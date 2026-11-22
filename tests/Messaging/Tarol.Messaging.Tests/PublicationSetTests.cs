using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

public sealed class PublicationSetTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static RecordedEvent Recorded(DomainEvent e, OperationContext context)
        => new(Guid.Parse("0193a000-0000-7000-8000-0000000000c1"), new StreamEvent(3, Guid.Empty, Guid.Empty, e), Guid.Parse("0193a000-0000-7000-8000-0000000000e1"), Start, context);

    [Fact]
    public void The_generated_set_names_its_rows_and_topics()
    {
        var set = GeneratedPublications.Set;
        set.Names.ShouldBe(["publish:artflow.auction-noted@1", "publish:artflow.auction-settled@1", "publish:artflow.payment-recorded@1"]);
        set.Topics.ShouldBe(["artflow.auction-noted", "artflow.auction-settled", "artflow.payment-recorded"]);
        set.ReactionsTo(new Auction.Settled(1m)).ToArray().ShouldBe(["publish:artflow.auction-settled@1"]);
        set.ReactionsTo(new Auction.Opened()).IsEmpty.ShouldBeTrue();
        set.DueAt(new Auction.Settled(1m), "publish:artflow.auction-settled@1").ShouldBeNull();
        set.IsScheduled("publish:artflow.auction-settled@1").ShouldBeFalse();
        set.Find("publish:artflow.auction-settled@1").ShouldBe(new PublicationInfo("publish:artflow.auction-settled@1", "artflow.auction-settled", 1)
        {
            ContractType = typeof(AuctionSettledV1),                                                                 // BC-25
            EventType = typeof(Auction.Settled),
            Stream = "Auction",
        });
        set.Find("publish:artflow.unknown@1").ShouldBeNull();
        PublicationInfo.NameOf("billing.invoice-paid", 2).ShouldBe("publish:billing.invoice-paid@2");
    }

    [Fact]
    public void The_map_serializes_through_the_applications_JSON_and_a_null_mapping_is_nothing()
    {
        var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var json = GeneratedPublications.Set.Map("publish:artflow.auction-settled@1", Recorded(new Auction.Settled(150m), context), ShopJson.Default.Options);
        System.Text.Encoding.UTF8.GetString(json!).ShouldBe("{\"auction\":\"0193a000-0000-7000-8000-0000000000c1\",\"price\":150}");
        GeneratedPublications.Set.Map("publish:artflow.payment-recorded@1", Recorded(new Auction.PaymentRecorded("internal-1", 1m), context), ShopJson.Default.Options)
            .ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => GeneratedPublications.Set.Map("publish:artflow.unknown@1", Recorded(new Auction.Opened(), context), ShopJson.Default.Options));
        Should.Throw<InvalidOperationException>(() => PublicationSet.Serialize(new AuctionSettledV1(Guid.Empty, 1m), new System.Text.Json.JsonSerializerOptions
        {
            TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(),
        })).Message.ShouldContain("[JsonSerializable(typeof(AuctionSettledV1))]");
    }

    [Fact]
    public void Sets_combine_and_refuse_a_contract_version_twice_or_a_malformed_name()
    {
        var billing = PublicationTests.BillingPublications();
        var combined = PublicationSet.Combine(GeneratedPublications.Set, billing);
        combined.Names.Count.ShouldBe(5);
        combined.ReactionsTo(new Invoice.Issued(Guid.Empty, 1m)).ToArray().ShouldBe(["publish:billing.invoice-paid@1"]);
        combined.ReactionsTo(new Auction.Noted(1)).ToArray().ShouldBe(["publish:artflow.auction-noted@1"]);
        Should.Throw<InvalidOperationException>(() => PublicationSet.Combine(billing, PublicationTests.BillingPublications()))
            .Message.ShouldContain("billing.invoice-paid v1 is published by two assemblies");
        Should.Throw<ArgumentException>(() => new PublicationSet([new("publish:x@1", "billing.paid", 1)], static _ => ReadOnlySpan<string>.Empty, static (_, _, _) => null));
        Should.Throw<ArgumentException>(() => new PublicationSet([new("publish:paid@1", "paid", 1)], static _ => ReadOnlySpan<string>.Empty, static (_, _, _) => null));
        PublicationSet.Empty.Names.ShouldBeEmpty();
    }

    [Fact]
    public void Message_Of_builds_the_envelope_of_a_committed_event()
    {
        var tenant = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
        var causation = (Id<ICausation>)Guid.Parse("0193a000-0000-7000-8000-0000000000ca");
        var context = OperationContext.Start(Actor.User("alice"), tenant, TimeProvider.System).AsSystem("Reactions.Remind").CausedBy(causation);

        var message = Message.Of(Recorded(new Auction.Settled(1m), context), "artflow.auction-settled", 2, new byte[] { 1 }, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        message.Id.ShouldBe(Guid.Parse("0193a000-0000-7000-8000-0000000000e1"));
        message.Source.ShouldBe("artflow");
        message.Type.ShouldBe("artflow.auction-settled");
        message.Version.ShouldBe(2);
        message.Time.ShouldBe(Start);
        message.Subject.ShouldBe("0193a000-0000-7000-8000-0000000000c1");
        message.Tenant.ShouldBe((Option<Id<ITenant>>)tenant);
        message.Correlation.ShouldBe(context.CorrelationId);
        message.Causation.ShouldBe((Option<Id<ICausation>>)causation);
        message.Actor.ShouldBe("System:Reactions.Remind:alice");
        message.ActorSubject.ShouldBe("alice");
        message.Traceparent.ShouldBe("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        Message.From(h => message.Headers().FirstOrDefault(p => p.Key == h).Value, message.Data).ShouldBe(message with { Data = message.Data });
        Should.Throw<ArgumentException>(() => Message.Of(Recorded(new Auction.Opened(), context), "settled", 1, Array.Empty<byte>(), null));
        Should.Throw<ArgumentOutOfRangeException>(() => Message.Of(Recorded(new Auction.Opened(), context), "artflow.settled", 0, Array.Empty<byte>(), null));
    }

    [Fact]
    public void Effects_validate_their_names_and_match_derived_events()
    {
        var mailbox = new Mailbox();
        var effects = new EffectSet().Add<Auction.PaymentRecorded>("send-receipt", _ => new SendReceipt(mailbox)).Add<DomainEvent>("audit.all", _ => new Audit());

        effects.Names.ShouldBe(["effect:send-receipt", "effect:audit.all"]);
        effects.ReactionsTo(new Auction.PaymentRecorded("i", 1m)).ToArray().ShouldBe(["effect:send-receipt", "effect:audit.all"]);
        effects.ReactionsTo(new Auction.Opened()).ToArray().ShouldBe(["effect:audit.all"]);
        effects.ActivityName("effect:send-receipt").ShouldBe("effect send-receipt");
        EffectSet.RowOf("x").ShouldBe("effect:x");
        Should.Throw<ArgumentException>(() => effects.Add<Auction.Opened>("send-receipt", _ => throw new InvalidOperationException()));
        Should.Throw<ArgumentException>(() => effects.Add<Auction.Opened>("send receipt", _ => throw new InvalidOperationException()));
        new EffectSet().ReactionsTo(new Auction.Opened()).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task OutboxRows_join_handlers_and_refuse_a_name_twice()
    {
        var mailbox = new Mailbox();
        var effects = new EffectSet().Add<Auction.PaymentRecorded>("send-receipt", _ => new SendReceipt(mailbox));
        var publisher = new MessagePublisher(GeneratedPublications.Set, new InMemoryTransport(TimeProvider.System), ShopJson.Default.Options);
        var rows = OutboxRows.Combine(ReactionSet.Empty, publisher, effects);

        rows.Parts.Count.ShouldBe(3);
        rows.Names.Count.ShouldBe(4);
        rows.ReactionsTo(new Auction.PaymentRecorded("i", 1m)).ToArray().ShouldBe(["publish:artflow.payment-recorded@1", "effect:send-receipt"]);
        rows.ActivityName("publish:artflow.payment-recorded@1").ShouldBe("publish artflow.payment-recorded");
        rows.ActivityName("effect:send-receipt").ShouldBe("effect send-receipt");
        rows.ActivityName("gone").ShouldBe("outbox gone");
        rows.IsScheduled("effect:send-receipt").ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => OutboxRows.Combine(effects, effects)).Message.ShouldContain("effect:send-receipt");
        await Should.ThrowAsync<InvalidOperationException>(async () => _ = await rows.Run(null!, "gone", null!, CancellationToken.None));
    }

    [Fact]
    public async Task AddMessaging_publishes_and_runs_effects_on_the_reaction_worker()
    {
        var activities = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name == ReactionWorker.ActivitySourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                    activities.Add(a.OperationName);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Mailbox>();
        builder.Services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>())
            .UseInMemoryStore()
            .AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(10))                    // no reactions: publications and effects only
            .AddMessaging("artflow", m => m
                .Publish(GeneratedPublications.Set)
                .Effect<Auction.PaymentRecorded, SendReceipt>("send-receipt")
                .UseInMemoryTransport());                                                            // no consumers: no dead-letter store needed
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var pipeline = host.Services.GetRequiredService<ChangePipeline>();
        var transport = (InMemoryTransport)host.Services.GetRequiredService<IMessageTransport>();
        var store = host.Services.GetRequiredService<IAggregateStore>();
        var auction = Auction.Open(Start);
        _ = auction.Settle();
        var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        (await store.Append(auction, 0, context, Option.None, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        (await pipeline.Run(new RecordPayment(auction.Id, "inv-9", 5m), context, default, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var mailbox = host.Services.GetRequiredService<Mailbox>();
        var deadline = Stopwatch.StartNew();
        while ((transport.Published.Count < 2 || mailbox.Mails.Count < 1) && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        transport.Published.Select(m => m.Type).ShouldBe(["artflow.auction-settled", "artflow.payment-recorded"]);
        mailbox.Mails.ShouldBe(["receipt inv-9"]);
        lock (activities)
            activities.ShouldContain("effect send-receipt");
        host.Services.GetServices<IOutboxRowHandler>().Count().ShouldBe(2);
    }

    private sealed class Audit : IEffect<DomainEvent>
    {
        public ValueTask<Results.Result> Run(DomainEvent domainEvent, EffectContext context, CancellationToken cancellationToken) => ValueTask.FromResult(Results.Result.Success());
    }
}
