using Shop;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;
using Tarol.Testing.Metrics;

namespace Tarol.Messaging.Tests;

/// <summary>Dead-letter paging, discard and the consumers' meter (step 15B; docs/migration/operations.md §11).</summary>
public sealed class OperationsTests
{
    private static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    private static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000b");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Incoming Poison(string topic, Option<Id<ITenant>> tenant)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(), Type = topic, Version = 1, Source = "billing", Time = DateTimeOffset.UnixEpoch, Tenant = tenant, Data = "{}"u8.ToArray(),
            Correlation = Id<ICorrelation>.New(),
        };
        return new Incoming(message, 3, "billing", message.Data, topic);
    }

    [Fact]
    public async Task Dead_messages_page_by_id_filter_by_subscription_and_envelope_tenant_and_are_discarded()
    {
        var dead = new InMemoryMessageDeadLetters(TimeProvider.System);
        await dead.Add("shop.A", Poison("billing.x", TenantA), "Error NotFound", Ct);
        await dead.Add("shop.A", Poison("billing.x", TenantB), "Error NotFound", Ct);
        await dead.Add("shop.B", Poison("billing.y", TenantA), "Error NotFound", Ct);
        await dead.Add("shop.B", new Incoming(null, 1, null, "raw"u8.ToArray(), "billing.y"), "Messaging.Malformed", Ct);

        var first = await dead.List(new DeadLetterQuery { Limit = 3 }, Ct);
        var rest = await dead.List(new DeadLetterQuery { Limit = 3, After = first[^1].Id }, Ct);
        (first.Count, rest.Count).ShouldBe((3, 1));
        (await dead.List(new DeadLetterQuery { Name = "shop.A" }, Ct)).Count.ShouldBe(2);
        (await dead.List(new DeadLetterQuery { Tenant = TenantA }, Ct)).Count.ShouldBe(2);
        (await dead.List(new DeadLetterQuery { Tenant = TenantA, Name = "shop.B" }, Ct)).ShouldHaveSingleItem().Tenant.ShouldBe(Option.Some(TenantA));
        rest[0].ToItem().Subject.ShouldBe("billing.y/-");
        first[0].ToItem().ShouldBe(new DeadLetterItem(first[0].Id, DeadLetterKinds.Messages, "shop.A", TenantA, $"billing.x/{first[0].MessageId}", 3,
            "Error NotFound", first[0].DeadAt));

        (await dead.Discard(rest[0].Id, Ct)).ShouldBeTrue();
        (await dead.Discard(rest[0].Id, Ct)).ShouldBeFalse();
        (await ((IMessageDeadLetters)dead).Discard(first[0].Id, "ops-alice", Ct)).ShouldBeTrue();             // the default: unaudited in memory
        dead.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Handled_deliveries_are_counted_by_consumer_and_outcome_and_timed()
    {
        using var meters = new Measurements();
        var consumers = new MessageConsumers(GeneratedConsumers.Set, new ChangePipeline(new InMemoryAggregateStore(TimeProvider.System), TimeProvider.System),
            new InMemoryMessageDeadLetters(TimeProvider.System), new MessagingOptions { Application = "shop" }, ShopJson.Default.Options, meters: meters);

        _ = await consumers.Handle("RecordPayment", new Incoming(null, 1, null, "x"u8.ToArray(), "billing.invoice-paid"), Ct);   // poison: buried

        var consumed = meters.Of("tarol.messages.consumed").ShouldHaveSingleItem();
        (consumed.Unit, consumed.Tags["tarol.consumer"], consumed.Tags["tarol.outcome"]).ShouldBe(("{message}", (object?)"RecordPayment", (object?)"buried"));
        meters.Of("tarol.messages.duration").ShouldHaveSingleItem().Unit.ShouldBe("s");
        meters.All.ShouldAllBe(m => m.Meter == MessageConsumers.MeterName);
    }
}
