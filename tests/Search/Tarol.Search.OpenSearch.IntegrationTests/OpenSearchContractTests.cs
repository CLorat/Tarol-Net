using Tarol.Search.Contract;
using Tarol.Search.Npgsql;

namespace Tarol.Search.OpenSearch.IntegrationTests;

/// <summary>The OpenSearch index passes the contract every implementation passes (checkpoints in PostgreSQL).</summary>
[Trait("Category", "Integration")]
public sealed class OpenSearchContractTests(Engine engine) : SearchContract
{
    protected override Task<ISearchIndex> NewIndex()
    {
        var n = engine.Next();
        return Task.FromResult<ISearchIndex>(new OpenSearchIndex(engine.Client(), new NpgsqlSearchCheckpoints(engine.DataSource, $"os{n}_{Environment.ProcessId}"),
            new OpenSearchOptions { IndexPrefix = $"contract-{n}-{Environment.ProcessId}", Replicas = 0 }));
    }

    protected override async Task Settle(ISearchIndex index)
    {
        using var client = engine.Client();
        using var _ = await client.PostAsync("/_refresh", null, TestContext.Current.CancellationToken);
    }
}
