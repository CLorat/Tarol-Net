using System.Diagnostics;
using Shop;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

/// <summary>
/// Publications and effects from the outbox (docs/migration/messaging.md §11, PR 11B), two systems in one
/// <see cref="MessagingTest"/>: ArtFlow (this assembly's generated publications) and billing (a hand-written set, since one
/// assembly publishes under one prefix).
/// </summary>
public sealed class PublicationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    private static readonly DeliveryPolicy Quick = new() { MaxAttempts = 3, FirstDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(4) };

    private static readonly string[] OnIssued = [PublicationInfo.NameOf("billing.invoice-paid", 1)];
    private static readonly string[] OnIssuedDual = [PublicationInfo.NameOf("billing.invoice-paid", 1), PublicationInfo.NameOf("billing.invoice-paid", 2)];
    private static readonly string[] OnSettledDual = [PublicationInfo.NameOf("artflow.auction-settled", 1), PublicationInfo.NameOf("artflow.auction-settled", 2)];

    /// <summary>Billing pays every invoice it issues at once: billing.invoice-paid (v1, and v2 when dual).</summary>
    internal static PublicationSet BillingPublications(bool dual = false) => new(
        [new(OnIssuedDual[0], "billing.invoice-paid", 1), new(OnIssuedDual[1], "billing.invoice-paid", 2)],
        e => e is Invoice.Issued ? (dual ? OnIssuedDual : OnIssued) : ReadOnlySpan<string>.Empty,
        static (name, r, json) => r.Event.Event is Invoice.Issued issued
            ? name.EndsWith("@1", StringComparison.Ordinal)
                ? PublicationSet.Serialize(new InvoicePaidV1(r.StreamId, issued.Auction, issued.Amount), json)
                : PublicationSet.Serialize(new InvoicePaidV2(r.StreamId, issued.Auction, new MoneyV2(issued.Amount, "EUR")), json)
            : null);

    /// <summary>ArtFlow publishing auction-settled in v1 and v2 while its consumers migrate.</summary>
    private static PublicationSet DualSettled() => new(
        [new(OnSettledDual[0], "artflow.auction-settled", 1), new(OnSettledDual[1], "artflow.auction-settled", 2)],
        static e => e is Auction.Settled ? OnSettledDual : ReadOnlySpan<string>.Empty,
        static (name, r, json) => r.Event.Event is Auction.Settled s
            ? name.EndsWith("@1", StringComparison.Ordinal)
                ? PublicationSet.Serialize(new AuctionSettledV1(r.StreamId, s.Price), json)
                : PublicationSet.Serialize(new AuctionSettledV2(r.StreamId, new MoneyV2(s.Price, "EUR")), json)
            : null);

    internal static ConsumerSet Only(params string[] names)
        => new([.. GeneratedConsumers.Set.Consumers.Where(c => names.Contains(c.Name))], static (c, p, m, s, ct) => GeneratedConsumers.Set.Run(c, p, m, s, ct));

    private static MessagingTest Systems(Action<MessagingSystemOptions>? artflow = null, Action<MessagingSystemOptions>? billing = null)
        => MessagingTest.At(Start)
            .System("artflow", ShopJson.Default.Options, o =>
            {
                o.Publications = GeneratedPublications.Set;
                o.Consumers = Only("RecordPayment");
                o.Policy = Quick;
                o.Trust("billing", t => t.Tenants(TenantA));
                artflow?.Invoke(o);
            })
            .System("billing", ShopJson.Default.Options, o =>
            {
                o.Publications = BillingPublications();
                o.Consumers = Only("IssueInvoice", "Sales");
                o.Policy = Quick;
                o.Trust("artflow", t => t.AnyTenant());
                billing?.Invoke(o);
            });

    private static Auction Open(MessagingTest test)
    {
        var auction = Auction.Open(Start);
        test["artflow"].InTenant(TenantA).Given(auction);
        return auction;
    }

    [Fact]
    public async Task A_settlement_is_published_consumed_and_paid_back()
    {
        var test = Systems();
        var auction = Open(test);

        (await test["artflow"].When(new SettleAuction(auction.Id, 150m), Actor.User("alice"))).IsSuccess.ShouldBeTrue();
        var steps = await test.Drain();

        // ArtFlow → billing: the invoice and the sale; billing → ArtFlow: the payment; ArtFlow publishes the payment (no subscriber)
        test["billing"].Store.Recorded.Select(r => r.Event.Event.GetType().Name).Order().ShouldBe(["Issued", "SaleRecorded"]);
        test["artflow"].Load(auction.Id).Paid.ShouldBe(150m);
        test.Transport.Published.Select(m => m.Type).ShouldBe(["artflow.auction-settled", "billing.invoice-paid", "artflow.payment-recorded"]);
        steps.Where(s => s.Row is not null).ShouldAllBe(s => s.Row!.Verdict == Verdict.Done);
        steps.Where(s => s.Delivery is not null).ShouldAllBe(s => s.Delivery!.Verdict == Verdict.Done);
        (await test["artflow"].OutboxDead()).ShouldBeEmpty();
        test.Transport.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task The_envelope_is_the_committed_event_and_the_consumer_continues_its_conversation()
    {
        var test = Systems();
        var auction = Open(test);
        (await test["artflow"].When(new SettleAuction(auction.Id, 150m), Actor.User("alice"))).IsSuccess.ShouldBeTrue();
        await test.Drain();

        var settled = test["artflow"].Store.Recorded.Single(r => r.Event.Event is Auction.Settled);
        var message = test.Transport.Published[0];
        message.Id.ShouldBe(settled.EventId);                                                  // message id = event id
        message.DedupId.ShouldBe(settled.EventId.ToString("N") + ":artflow.auction-settled@1");
        message.Source.ShouldBe("artflow");
        message.Subject.ShouldBe(auction.Id.Value.ToString("D"));                               // ce-subject = stream id
        message.Time.ShouldBe(settled.RecordedAt);
        message.Tenant.ShouldBe((Option<Id<ITenant>>)TenantA);
        message.Correlation.ShouldBe(settled.Context.CorrelationId);
        message.Actor.ShouldBe("User:alice");
        message.StreamVersion.ShouldBe(settled.Event.Version);                                 // ce-streamversion = the view's ETag
        var feed = await test["artflow"].Store.ReadAll(Tarol.Persistence.GlobalPosition.Start, 100, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var at = feed.Single(e => e.Event.EventId == settled.EventId).Position;
        message.Position.ShouldBe($"{at.Transaction}.{at.Position}");                           // ce-position = Tarol-Position's text

        var invoice = test["billing"].Store.Recorded.Single(r => r.Event.Event is Invoice.Issued);
        invoice.Context.Actor.ToString().ShouldBe("System:Consumers.IssueInvoice (for alice)");      // M-6
        invoice.Context.CorrelationId.ShouldBe(settled.Context.CorrelationId);
        invoice.Context.CausationId.ShouldBe((Option<Id<ICausation>>)(Id<ICausation>)settled.EventId);

        // the next hop: billing publishes as the consumer acting for alice; ArtFlow's consumer records "for alice" again
        test.Transport.Published[1].Actor.ShouldBe("System:Consumers.IssueInvoice:alice");
        test["artflow"].Store.Recorded.Single(r => r.Event.Event is Auction.PaymentRecorded).Context.Actor.ToString()
            .ShouldBe("System:Consumers.RecordPayment (for alice)");
    }

    [Fact]
    public async Task The_publish_activity_is_the_traceparent_and_links_to_the_commit()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name is ReactionWorker.ActivitySourceName or MessageConsumers.ActivitySourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                    activities.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var test = Systems();
        var auction = Open(test);

        ActivitySpanId commitSpan;
        using (var commit = new Activity("settle").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            commitSpan = commit.SpanId;
            (await test["artflow"].When(new SettleAuction(auction.Id, 150m))).IsSuccess.ShouldBeTrue();
        }
        await test.Drain();

        Activity[] seen;
        lock (activities)
            seen = [.. activities];
        var message = test.Transport.Published[0];                          // listeners are process-wide: pick this test's spans
        var publish = seen.Single(a => a.Id == message.Traceparent);
        publish.OperationName.ShouldBe("publish artflow.auction-settled");
        publish.GetTagItem("tarol.reaction").ShouldBe("publish:artflow.auction-settled@1");
        publish.GetTagItem("messaging.destination.name").ShouldBe("artflow.auction-settled");
        publish.Links.ShouldHaveSingleItem().Context.SpanId.ShouldBe(commitSpan);                   // the row links to the commit
        var consume = seen.Single(a => a.OperationName == "consume artflow.auction-settled" && (string?)a.GetTagItem("tarol.consumer") == "IssueInvoice"
            && (string?)a.GetTagItem("messaging.message.id") == message.Id.ToString());
        consume.Links.ShouldHaveSingleItem().Context.SpanId.ShouldBe(publish.SpanId);             // a link, never a parent
        consume.ParentId.ShouldBeNull();
        seen.ShouldContain(a => a.OperationName == "publish billing.invoice-paid" && a.GetTagItem("messaging.destination.name") as string == "billing.invoice-paid");
    }

    [Fact]
    public async Task A_crash_after_publishing_publishes_again_and_the_consumer_commits_once()
    {
        var test = Systems(artflow: o => o.PublishThrough = inner => new CrashAfterPublish(inner, "artflow.auction-settled"));
        var auction = Open(test);
        (await test["artflow"].When(new SettleAuction(auction.Id, 150m))).IsSuccess.ShouldBeTrue();

        var first = await test.Drain();
        first.First(s => s.Row is not null).Row!.Verdict.ShouldBe(Verdict.Retry);
        test["billing"].Store.Recorded.Count(r => r.Event.Event is Invoice.Issued).ShouldBe(1);   // the first copy was consumed

        var second = await test.Later(TimeSpan.FromSeconds(2)).Drain();
        second.First(s => s.Row is not null).Row!.Verdict.ShouldBe(Verdict.Done);
        test.Transport.Published.Count(m => m.Type == "artflow.auction-settled").ShouldBe(2);
        test["billing"].Store.Recorded.Count(r => r.Event.Event is Invoice.Issued).ShouldBe(1);   // the key replays the copy
        second.Where(s => s.Delivery?.Consumer == "IssueInvoice").ShouldHaveSingleItem().Delivery!.Outcome!.Value.Replayed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_transport_retries_the_row_and_buries_it_at_the_limit()
    {
        var test = Systems(artflow: o => o.PublishThrough = static _ => new Unreachable());
        var auction = Open(test);
        (await test["artflow"].When(new SettleAuction(auction.Id, 150m))).IsSuccess.ShouldBeTrue();

        for (var i = 0; i < 3; i++)
            await test.Later(TimeSpan.FromSeconds(5)).Drain();

        test["artflow"].Rows.Select(r => r.Verdict).ShouldBe([Verdict.Retry, Verdict.Retry, Verdict.Bury]);
        var dead = (await test["artflow"].OutboxDead()).ShouldHaveSingleItem();
        dead.Reaction.ShouldBe("publish:artflow.auction-settled@1");
        dead.LastError.GetValueOrDefault("").ShouldContain("broker down");
    }

    [Fact]
    public async Task One_stream_publishes_in_version_order_with_two_worker_loops()
    {
        var test = MessagingTest.At(Start).System("artflow", ShopJson.Default.Options, o => o.Publications = GeneratedPublications.Set);
        var artflow = test["artflow"];
        var auctions = Enumerable.Range(0, 4).Select(_ => Auction.Open(Start)).ToArray();
        foreach (var auction in auctions)
            artflow.Given(auction);
        for (var n = 1; n <= 12; n++)
            foreach (var auction in auctions)
                (await artflow.When(new NoteAuction(auction.Id, n))).IsSuccess.ShouldBeTrue();

        var loops = Enumerable.Range(0, 2)
            .Select(_ => new ReactionWorker(artflow.Outbox, artflow.Publisher, artflow.Pipeline, test.Clock, new ReactionOptions { Batch = 3 }))
            .Select(worker => Task.Run(() => worker.Drain(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(loops);

        var notes = test.Transport.Published.Where(m => m.Type == "artflow.auction-noted").ToArray();
        notes.Length.ShouldBe(48);
        foreach (var stream in notes.GroupBy(m => m.Subject))
            stream.Select(m => System.Text.Json.JsonSerializer.Deserialize(m.Data.Span, ShopJson.Default.AuctionNotedV1)!.N).ShouldBe(Enumerable.Range(1, 12));
    }

    [Fact]
    public async Task A_dual_publish_commits_once_and_a_v1_only_consumer_buries_v2()
    {
        var test = Systems(artflow: o => o.Publications = DualSettled(), billing: o => o.Publications = BillingPublications(dual: true));
        var auction = Open(test);
        (await test["artflow"].When(new SettleAuction(auction.Id, 150m))).IsSuccess.ShouldBeTrue();

        var steps = await test.Drain();

        var settled = test.Transport.Published.Where(m => m.Type == "artflow.auction-settled").ToArray();
        settled.Select(m => m.Version).ShouldBe([1, 2]);
        settled.Select(m => m.Id).Distinct().ShouldHaveSingleItem();                             // one message id …
        settled.Select(m => m.DedupId).Distinct().Count().ShouldBe(2);                            // … two dedup ids
        test["billing"].Store.Recorded.Count(r => r.Event.Event is Invoice.Issued).ShouldBe(1);
        (await test["billing"].DeadLetters.List(10, CancellationToken.None)).Select(d => d.Reason)
            .ShouldBe(["Messaging.UnknownVersion: artflow.auction-settled v2", "Messaging.UnknownVersion: artflow.auction-settled v2"]);  // IssueInvoice and Sales read v1 only
        // RecordPayment reads v1 and v2 of invoice-paid: one payment, the second version replays the key
        test["artflow"].Load(auction.Id).Paid.ShouldBe(150m);
        steps.Where(s => s.Delivery?.Consumer == "RecordPayment").Select(s => s.Delivery!.Outcome!.Value.Replayed).ShouldBe([false, true]);
    }

    [Fact]
    public async Task A_contract_nobody_subscribes_is_published_and_a_null_mapping_publishes_nothing()
    {
        var test = MessagingTest.At(Start).System("artflow", ShopJson.Default.Options, o => o.Publications = GeneratedPublications.Set);
        var artflow = test["artflow"];
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        artflow.Given(auction);

        (await artflow.When(new RecordPayment(auction.Id, "inv-1", 10m))).IsSuccess.ShouldBeTrue();
        (await artflow.When(new RecordPayment(auction.Id, "internal-2", 5m))).IsSuccess.ShouldBeTrue();
        var steps = await test.Drain();

        steps.Where(s => s.Row!.Row.Reaction == "publish:artflow.payment-recorded@1").Select(s => s.Row!.Verdict).ShouldBe([Verdict.Done, Verdict.Done]);
        var published = test.Transport.Published.ShouldHaveSingleItem();                        // the internal one is filtered
        published.Type.ShouldBe("artflow.payment-recorded");
        System.Text.Json.JsonSerializer.Deserialize(published.Data.Span, ShopJson.Default.PaymentRecordedV1)!.Invoice.ShouldBe("inv-1");
        test.Transport.Pending.ShouldBe(0);                                                       // no subscription, no delivery
    }

    [Fact]
    public async Task An_effect_retries_and_after_a_crash_sends_one_mail_by_its_key()
    {
        var mailbox = new Mailbox { FailNext = 1, CrashAfterSendNext = true };
        var test = MessagingTest.At(Start).System("artflow", ShopJson.Default.Options, o =>
        {
            o.Effects = new EffectSet().Add<Auction.PaymentRecorded>("send-receipt", _ => new SendReceipt(mailbox));
            o.Policy = Quick;
        });
        var artflow = test["artflow"];
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        artflow.Given(auction);
        (await artflow.When(new RecordPayment(auction.Id, "inv-1", 10m))).IsSuccess.ShouldBeTrue();

        await test.Drain();                                                    // provider down: Unavailable → retry
        await test.Later(TimeSpan.FromSeconds(2)).Drain();                     // sent, then crashed → retry
        await test.Later(TimeSpan.FromSeconds(4)).Drain();                     // sent again with the same key → done

        artflow.Rows.Select(r => r.Verdict).ShouldBe([Verdict.Retry, Verdict.Retry, Verdict.Done]);
        artflow.Rows.ShouldAllBe(r => r.Row.Reaction == "effect:send-receipt");
        artflow.Rows[0].Reason.GetValueOrDefault("").ShouldContain("Mail.Down");
        artflow.Rows[1].Reason.GetValueOrDefault("").ShouldContain("crashed after sending");
        mailbox.Calls.ShouldBe(3);
        mailbox.Mails.ShouldBe(["receipt inv-1"]);
    }

    [Fact]
    public async Task An_effect_gets_the_event_id_as_its_key_and_an_Invalid_result_is_buried()
    {
        EffectContext? seen = null;
        var test = MessagingTest.At(Start).System("artflow", ShopJson.Default.Options, o => o.Effects = new EffectSet()
            .Add<Auction.PaymentRecorded>("reject", _ => new Effect(c =>
            {
                seen = c;
                return Results.Invalid.Field("Email", "Missing");
            })));
        var artflow = test["artflow"];
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        artflow.Given(auction);
        (await artflow.When(new RecordPayment(auction.Id, "inv-1", 10m))).IsSuccess.ShouldBeTrue();

        await test.Drain();

        var recorded = artflow.Store.Recorded.Single(r => r.Event.Event is Auction.PaymentRecorded);
        seen!.Name.ShouldBe("reject");
        seen.IdempotencyKey.ShouldBe(recorded.EventId.ToString("N"));
        seen.Recorded.EventId.ShouldBe(recorded.EventId);
        artflow.Rows.ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Bury);
        (await artflow.OutboxDead()).ShouldHaveSingleItem().Reaction.ShouldBe("effect:reject");
    }

    [Fact]
    public async Task An_effect_with_a_due_time_runs_once_it_is_due_not_at_the_commit()
    {
        // TN-63 (Tatalu FB-F17.4-3): quiet hours took Deferred → ReleaseNotification → Queued → the effect
        var runs = 0;
        var quietUntil = Start.AddHours(8);
        var test = MessagingTest.At(Start).System("artflow", ShopJson.Default.Options, o => o.Effects = new EffectSet()
            .Add<Auction.PaymentRecorded>("deliver", _ => new Effect(_ =>
            {
                runs++;
                return Results.Result.Success();
            }), e => e.Amount > 5m ? quietUntil : null));
        var artflow = test["artflow"];
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        artflow.Given(auction);
        (await artflow.When(new RecordPayment(auction.Id, "inv-1", 10m))).IsSuccess.ShouldBeTrue();

        await test.Drain();
        runs.ShouldBe(0);                                                                // not before 08:00

        await test.Later(TimeSpan.FromHours(9)).Drain();
        runs.ShouldBe(1);
        var set = new EffectSet().Add<Auction.PaymentRecorded>("deliver", _ => new Effect(_ => Results.Result.Success()), e => e.Amount > 5m ? quietUntil : null);
        set.IsScheduled("effect:deliver").ShouldBeTrue();
        set.DueAt(new Auction.PaymentRecorded("x", 1m), "effect:deliver").ShouldBeNull();      // null: at the commit
        new EffectSet().Add<Auction.PaymentRecorded>("now", _ => new Effect(_ => Results.Result.Success())).IsScheduled("effect:now").ShouldBeFalse();
    }

    [Fact]
    public async Task Given_history_is_neither_published_nor_reacted_to()
    {
        var test = Systems();
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        test["artflow"].Given(auction);

        (await test.Drain()).Where(s => s.Delivery is not null).ShouldBeEmpty();
        test.Transport.Published.ShouldBeEmpty();
    }

    private sealed class Effect(Func<EffectContext, Results.Result> run) : IEffect<Auction.PaymentRecorded>
    {
        public ValueTask<Results.Result> Run(Auction.PaymentRecorded domainEvent, EffectContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(run(context));
    }

    /// <summary>Publishes, then fails once per message of <paramref name="contract"/>: a crash between publish and complete.</summary>
    internal sealed class CrashAfterPublish(IMessageTransport inner, string contract) : IMessageTransport
    {
        private readonly HashSet<string> _crashed = [];

        public Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
            => inner.Prepare(topics, subscriptions, cancellationToken);

        public async Task Publish(Message message, CancellationToken cancellationToken)
        {
            await inner.Publish(message, cancellationToken);
            if (message.Type == contract && _crashed.Add(message.DedupId))
                throw new InvalidOperationException("crashed after publish, before complete");
        }

        public Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
            => inner.Consume(subscription, handler, cancellationToken);
    }

    private sealed class Unreachable : IMessageTransport
    {
        public Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Publish(Message message, CancellationToken cancellationToken) => throw new TimeoutException("broker down");

        public Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
