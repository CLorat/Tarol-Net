using System.Text.Json;
using Npgsql;
using Tarol.Persistence.Contract;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Persistence.Npgsql.IntegrationTests.Postgres))]

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container per test run. Tests share the default schema and stay independent through a fresh tenant
/// each; tests that read across tenants (the feed, rebuilds, purges) get a schema of their own.
/// </summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    /// <summary>The data source, as an application builds it for Native AOT.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString() + ";Maximum Pool Size=150").EnableArrays().Build();
        await Store(TimeProvider.System).EnsureSchema(default);
    }

    /// <summary>The codec of the contract models.</summary>
    public static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();

    /// <summary>A store on the default schema.</summary>
    public NpgsqlAggregateStore Store(TimeProvider time, StoreLookups? lookups = null, EventCodec? codec = null)
        => new(DataSource, codec ?? Codec(), time, new NpgsqlStoreOptions { Lookups = lookups ?? new StoreLookups() });

    /// <summary>A store on a fresh schema of its own, created.</summary>
    public async Task<NpgsqlAggregateStore> IsolatedStore(TimeProvider time, StoreLookups? lookups = null, EventCodec? codec = null)
    {
        var store = new NpgsqlAggregateStore(DataSource, codec ?? Codec(), time,
            new NpgsqlStoreOptions { Schema = $"t{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}", Lookups = lookups ?? new StoreLookups() });
        await store.EnsureSchema(default);
        return store;
    }

    public async Task<object?> Scalar(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    public async Task Execute(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>The store contract on PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlStoreContractTests(Postgres postgres) : StoreContract
{
    protected override IAggregateStore CreateStore(TimeProvider time, StoreLookups lookups) => postgres.Store(time, lookups);
}
