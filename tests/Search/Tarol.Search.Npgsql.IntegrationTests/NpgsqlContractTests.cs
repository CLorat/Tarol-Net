using Tarol.Search.Contract;

namespace Tarol.Search.Npgsql.IntegrationTests;

/// <summary>The PostgreSQL index passes the contract every implementation passes.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlContractTests(Postgres postgres) : SearchContract
{
    protected override Task<ISearchIndex> NewIndex() => Task.FromResult<ISearchIndex>(new NpgsqlSearchIndex(postgres.DataSource, postgres.Schema()));
}
