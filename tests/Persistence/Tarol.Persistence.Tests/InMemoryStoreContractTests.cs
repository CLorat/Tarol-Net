using Tarol.Persistence.Contract;

namespace Tarol.Persistence.Tests;

/// <summary>The store contract on the in-memory store, events kept as objects.</summary>
public sealed class InMemoryStoreContractTests : StoreContract
{
    protected override IAggregateStore CreateStore(TimeProvider time, StoreLookups lookups)
        => new InMemoryAggregateStore(time, new InMemoryStoreOptions { StateOptions = CreateCodec().Options, Lookups = lookups });
}

/// <summary>The store contract on the in-memory store, every event through the codec (P-15).</summary>
public sealed class InMemoryStoreWithCodecContractTests : StoreContract
{
    protected override IAggregateStore CreateStore(TimeProvider time, StoreLookups lookups)
        => new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = CreateCodec(), Lookups = lookups });
}

/// <summary>The outbox contract on the in-memory store.</summary>
public sealed class InMemoryOutboxContractTests : OutboxContract
{
    protected override Task<(IAggregateStore Store, IOutbox Outbox)> Create(TimeProvider time, IOutboxReactions reactions)
    {
        var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = CreateCodec(), Reactions = reactions });
        return Task.FromResult<(IAggregateStore, IOutbox)>((store, store.Outbox));
    }

    private static EventCodec CreateCodec() => new EventCodec(new System.Text.Json.JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();
}

/// <summary>The job rows contract on the in-memory store (step 14A).</summary>
public sealed class InMemoryJobRowsContractTests : JobRowsContract
{
    protected override Task<IJobRows> Create(TimeProvider time) => Task.FromResult(new InMemoryAggregateStore(time).Jobs);
}
