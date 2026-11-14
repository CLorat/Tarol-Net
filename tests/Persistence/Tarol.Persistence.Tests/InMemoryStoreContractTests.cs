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
