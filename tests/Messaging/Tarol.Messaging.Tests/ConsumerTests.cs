using System.Text;
using Shop;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

/// <summary>The scenarios of docs/migration/messaging.md §3.1 on the in-memory transport, through <see cref="MessageTest"/>.</summary>
public sealed class ConsumerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    private static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000b");
    private static readonly DeliveryPolicy Quick = new() { MaxAttempts = 3, FirstDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(4) };

    private static MessageTest Test() => MessageTest.At(Start, GeneratedConsumers.Set, ShopJson.Default.Options, "shop", policy: Quick)
        .Trust("billing", t => t.Tenants(TenantA))
        .Trust("artflow", t => t.AnyTenant())
        .InTenant(TenantA);

    private static Auction Settled(MessageTest test)
    {
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        test.Given(auction);
        return auction;
    }

    private static Message Paid(MessageTest test, Auction auction, decimal amount = 150m)
        => test.Message("billing.invoice-paid", 1, new InvoicePaidV1(Guid.NewGuid(), auction.Id.Value, amount), ShopJson.Default.InvoicePaidV1);

    [Fact]
    public async Task A_valid_message_commits_its_change()
    {
        var test = Test();
        var auction = Settled(test);
        await test.Send(Paid(test, auction));

        var attempts = await test.Drain();

        attempts.ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Done);
        attempts[0].Outcome!.Value.Events.ShouldBe(1);
        test.Load(auction.Id).Paid.ShouldBe(150m);
    }

    [Fact]
    public async Task The_context_is_the_consumer_acting_for_the_senders_actor_in_the_envelopes_tenant_and_conversation()
    {
        var test = Test().From(Actor.User("alice"));
        var auction = Settled(test);
        var message = Paid(test, auction);
        await test.Send(message);
        await test.Drain();

        var recorded = test.Store.Recorded.Single(r => r.Event.Event is Auction.PaymentRecorded);
        recorded.Context.Actor.ToString().ShouldBe("System:Consumers.RecordPayment (for alice)");          // M-6
        recorded.Context.Tenant.ShouldBe((Option<Id<ITenant>>)TenantA);
        recorded.Context.CorrelationId.ShouldBe(message.Correlation);
        recorded.Context.CausationId.ShouldBe((Option<Id<ICausation>>)(Id<ICausation>)message.Id);
    }

    [Fact]
    public async Task A_redelivered_message_replays_by_its_key_and_writes_nothing()
    {
        var test = Test();
        var auction = Settled(test);
        var message = Paid(test, auction);
        await test.Send(message);
        await test.Send(message);                                                // the same message id again (a lost ack, a republish)

        var attempts = await test.Drain();

        attempts.Select(a => a.Outcome!.Value.Replayed).ShouldBe([false, true]);
        test.Store.Recorded.Count(r => r.Event.Event is Auction.PaymentRecorded).ShouldBe(1);
    }

    [Fact]
    public async Task Two_consumers_of_one_contract_both_commit_with_their_own_keys()
    {
        var test = Test();
        await test.Send(test.Message("artflow.auction-settled", 1, new AuctionSettledV1(Guid.NewGuid(), 70m), ShopJson.Default.AuctionSettledV1));

        var attempts = await test.Drain();

        attempts.Select(a => (a.Consumer, a.Verdict, a.Outcome!.Value.Replayed)).Order()
            .ShouldBe([("IssueInvoice", Verdict.Done, false), ("Sales", Verdict.Done, false)]);   // M-12: consumer:<name>, not consumer:<contract>
        test.Load((Id<Ledger>)RecordSale.TheLedger).Sales.ShouldBe(1);
    }

    [Fact]
    public async Task One_event_published_as_v1_and_v2_commits_once()
    {
        var test = Test();
        var auction = Settled(test);
        var v1 = Paid(test, auction);
        var v2 = v1 with { Version = 2, Data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new InvoicePaidV2(Guid.NewGuid(), auction.Id.Value, new MoneyV2(150m, "EUR")), ShopJson.Default.InvoicePaidV2) };
        await test.Send(v1);
        await test.Send(v2);

        var attempts = await test.Drain();

        attempts.Select(a => a.Outcome!.Value.Replayed).ShouldBe([false, true]);    // the message id is the event id, for every version
        test.Load(auction.Id).Paid.ShouldBe(150m);
    }

    [Fact]
    public async Task An_unknown_version_of_a_known_contract_is_buried()
    {
        var test = Test();
        var auction = Settled(test);
        await test.Send(Paid(test, auction) with { Version = 3 });

        var attempt = (await test.Drain()).ShouldHaveSingleItem();

        attempt.Verdict.ShouldBe(Verdict.Bury);
        attempt.Reason.ShouldBe((Option<string>)"Messaging.UnknownVersion: billing.invoice-paid v3");
        (await test.DeadMessages()).ShouldHaveSingleItem().Subscription.ShouldBe("shop.RecordPayment");
    }

    [Fact]
    public async Task A_contract_the_consumer_does_not_read_is_dropped()
    {
        var test = Test();
        var other = test.Message("billing.invoice-voided", 1, new InvoicePaidV1(Guid.NewGuid(), Guid.NewGuid(), 1m), ShopJson.Default.InvoicePaidV1);
        await test.Send(other);
        (await test.Drain()).ShouldBeEmpty();                                    // no subscription has it: never delivered

        var handled = await test.Consumers.Handle("RecordPayment", new Incoming(other, 1, null, other.Data, other.Type), TestContext.Current.CancellationToken);

        handled.Ack.ShouldBe(Ack.Ack);
        test.Attempts.ShouldHaveSingleItem().Reason.ShouldBe((Option<string>)"Messaging.UnknownContract");
        (await test.DeadMessages()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_mapping_is_buried_at_the_first_attempt()
    {
        var test = Test();
        await test.Send(test.Message("artflow.auction-settled", 1, new AuctionSettledV1(Guid.NewGuid(), 0m), ShopJson.Default.AuctionSettledV1));

        var attempts = await test.Drain();

        var invoice = attempts.Single(a => a.Consumer == "IssueInvoice");
        invoice.Verdict.ShouldBe(Verdict.Bury);
        invoice.Incoming.Attempt.ShouldBe(1);
        invoice.Reason.ShouldBe((Option<string>)"Invalid Amount Range");
        attempts.Single(a => a.Consumer == "Sales").Verdict.ShouldBe(Verdict.Done);   // the other consumer is unaffected
    }

    [Fact]
    public async Task A_conflict_is_retried_until_the_state_allows_it()
    {
        var test = Test();
        var auction = Auction.Open(Start);
        test.Given(auction);                                                     // not settled yet: the payment arrived first
        await test.Send(Paid(test, auction));

        (await test.Drain()).ShouldHaveSingleItem().Reason.ShouldBe((Option<string>)"Conflict Auction.NotSettled");
        (await test.Drain()).ShouldBeEmpty();                                    // not due before its backoff
        var settled = test.Load(auction.Id);
        _ = settled.Settle();
        test.Given(settled);

        var retried = (await test.Later(TimeSpan.FromSeconds(2)).Drain()).ShouldHaveSingleItem();

        retried.Verdict.ShouldBe(Verdict.Done);
        retried.Incoming.Attempt.ShouldBe(2);
        test.Load(auction.Id).Paid.ShouldBe(150m);
    }

    [Fact]
    public async Task A_missing_target_is_retried_then_dead_lettered()
    {
        var test = Test();
        await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(Guid.NewGuid(), Guid.CreateVersion7(), 5m), ShopJson.Default.InvoicePaidV1));

        var verdicts = new List<Verdict>();
        for (var i = 0; i < 4; i++)
            verdicts.AddRange((await test.Later(TimeSpan.FromSeconds(5)).Drain()).Select(a => a.Verdict));

        verdicts.ShouldBe([Verdict.Retry, Verdict.Retry, Verdict.Bury]);         // MaxAttempts 3
        (await test.DeadMessages()).ShouldHaveSingleItem().Reason.ShouldStartWith("NotFound Auction.NotFound");
    }

    [Fact]
    public async Task A_poison_message_is_buried_with_its_bytes()
    {
        var test = Test();
        var auction = Settled(test);
        await test.Send(Paid(test, auction) with { Data = Encoding.UTF8.GetBytes("{not json") });

        var attempt = (await test.Drain()).ShouldHaveSingleItem();

        attempt.Verdict.ShouldBe(Verdict.Bury);
        attempt.Reason.ShouldBe((Option<string>)"Messaging.Malformed: the data does not match the contract");
        var letter = (await test.DeadMessages()).ShouldHaveSingleItem();
        Encoding.UTF8.GetString(letter.Data.Span).ShouldBe("{not json");
        letter.Headers[MessageHeaders.Type].ShouldBe("billing.invoice-paid");
    }

    [Fact]
    public async Task A_delivery_without_an_envelope_is_buried()
    {
        var test = Test();

        var handled = await test.Consumers.Handle("RecordPayment", new Incoming(null, 1, null, "raw"u8.ToArray(), "billing.invoice-paid"),
            TestContext.Current.CancellationToken);

        handled.Ack.ShouldBe(Ack.Ack);
        (await test.DeadMessages()).ShouldHaveSingleItem().Reason.ShouldStartWith("Messaging.Malformed: the envelope");
    }

    [Theory]
    [InlineData("tenant", "Messaging.TenantNotAllowed")]
    [InlineData("source", "Messaging.SourceMismatch")]
    [InlineData("authenticated", "Messaging.SourceMismatch")]
    [InlineData("unknown", "Messaging.UnknownSource")]
    public async Task A_forged_or_unknown_sender_is_buried(string forged, string reason)
    {
        var test = Test();
        var auction = Settled(test);
        var message = Paid(test, auction);
        var incoming = forged switch
        {
            "tenant" => new Incoming(message with { Tenant = TenantB }, 1, null, message.Data, message.Type),          // billing may act for A only
            "source" => new Incoming(message with { Source = "artflow" }, 1, null, message.Data, message.Type),       // ce-source ≠ the contract's prefix
            "authenticated" => new Incoming(message, 1, "artflow", message.Data, message.Type),                       // the transport saw artflow publish it
            _ => new Incoming(message with { Type = "payments.invoice-paid", Source = "payments" }, 1, null, message.Data, "payments.invoice-paid"),
        };

        var handled = await test.Consumers.Handle("RecordPayment", incoming, TestContext.Current.CancellationToken);

        handled.Ack.ShouldBe(Ack.Ack);
        (await test.DeadMessages()).ShouldHaveSingleItem().Reason.ShouldStartWith(reason);
        test.Load(auction.Id).Paid.ShouldBe(0m);
    }

    [Fact]
    public async Task A_forged_actor_is_recorded_only_as_on_behalf_of()
    {
        var test = Test().From(Actor.User("admin"));
        var auction = Settled(test);
        await test.Send(Paid(test, auction));
        await test.Drain();

        var actor = test.Store.Recorded.Single(r => r.Event.Event is Auction.PaymentRecorded).Context.Actor;

        actor.Kind.ShouldBe(ActorKind.System);
        actor.Id.ShouldBe("Consumers.RecordPayment");
        actor.OnBehalfOf.ShouldBe((Option<string>)"admin");
    }

    [Fact]
    public async Task A_dead_letter_is_redriven_after_the_cause_is_fixed()
    {
        var test = Test();
        var auction = Auction.Open(Start);
        test.Given(auction);
        await test.Send(Paid(test, auction, 33m));
        for (var i = 0; i < 3; i++)
            await test.Later(TimeSpan.FromSeconds(5)).Drain();                  // Conflict × 3: buried
        var letter = (await test.DeadMessages()).ShouldHaveSingleItem();
        var settled = test.Load(auction.Id);
        _ = settled.Settle();
        test.Given(settled);

        var redriven = await test.Redrive(letter.Id);

        redriven!.Verdict.ShouldBe(Verdict.Done);
        test.Load(auction.Id).Paid.ShouldBe(33m);
        (await test.DeadMessages()).ShouldBeEmpty();
        (await test.Redrive(letter.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task An_exception_in_a_factory_is_a_retry_never_a_crash()
    {
        var throwing = new ConsumerSet([new ConsumerInfo("Broken", ["billing.invoice-paid"])],
            static (_, _, _, _, _) => throw new InvalidOperationException("a bug in the factory"));
        var test = MessageTest.At(Start, throwing, ShopJson.Default.Options, "shop", policy: Quick).Trust("billing", t => t.AnyTenant());
        await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(Guid.NewGuid(), Guid.NewGuid(), 1m), ShopJson.Default.InvoicePaidV1));

        var attempt = (await test.Drain()).ShouldHaveSingleItem();

        attempt.Verdict.ShouldBe(Verdict.Retry);
        attempt.Reason.ShouldBe((Option<string>)"InvalidOperationException: a bug in the factory");
        test.Transport.Pending.ShouldBe(1);
    }

    [Fact]
    public async Task A_dead_letter_store_that_fails_makes_the_delivery_come_back()
    {
        var consumers = new MessageConsumers(GeneratedConsumers.Set, new ChangePipeline(new Tarol.Persistence.InMemoryAggregateStore(TimeProvider.System), TimeProvider.System),
            new FailingDeadLetters(), new MessagingOptions { Application = "shop" }, ShopJson.Default.Options);

        var handled = await consumers.Handle("RecordPayment", new Incoming(null, 1, null, "x"u8.ToArray(), "billing.invoice-paid"), TestContext.Current.CancellationToken);

        handled.Ack.ShouldBe(Ack.Retry);
        handled.RetryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    private sealed class FailingDeadLetters : IMessageDeadLetters
    {
        public Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken) => throw new IOException("the database is down");
        public Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeadMessage>>([]);
        public Task<DeadMessage?> Take(long id, CancellationToken cancellationToken) => Task.FromResult<DeadMessage?>(null);
    }
}
