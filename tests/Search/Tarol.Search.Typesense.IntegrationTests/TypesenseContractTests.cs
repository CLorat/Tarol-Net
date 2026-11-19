using Tarol.Search.Contract;
using Tarol.Search.Npgsql;

namespace Tarol.Search.Typesense.IntegrationTests;

/// <summary>The Typesense index passes the contract every implementation passes (checkpoints in PostgreSQL).</summary>
[Trait("Category", "Integration")]
public sealed class TypesenseContractTests(Engine engine) : SearchContract
{
    protected override Task<ISearchIndex> NewIndex()
    {
        var n = engine.Next();
        return Task.FromResult<ISearchIndex>(new TypesenseIndex(engine.Client(), new NpgsqlSearchCheckpoints(engine.DataSource, $"ts{n}_{Environment.ProcessId}"),
            new TypesenseOptions { CollectionPrefix = $"contract_{n}_{Environment.ProcessId}" }));
    }
}
