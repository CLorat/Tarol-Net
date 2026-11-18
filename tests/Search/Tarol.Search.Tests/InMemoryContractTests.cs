using Tarol.Search.Contract;

namespace Tarol.Search.Tests;

/// <summary>The in-memory index passes the contract every implementation passes.</summary>
public sealed class InMemoryContractTests : SearchContract
{
    protected override Task<ISearchIndex> NewIndex() => Task.FromResult<ISearchIndex>(new InMemorySearchIndex());
}
