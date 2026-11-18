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

    /// <summary>The container's connection string.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>The data source, as an application builds it for Native AOT.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString() + ";Maximum Pool Size=150").EnableArrays().Build();
        await Store(TimeProvider.System).EnsureSchema(default);
        // the contract suites also run on a schema that a step-14 store created (the version 1 script) and this code upgraded
        await Execute(NpgsqlSchema.Script(UpgradedSchema));
        await Upgraded(UpgradedSchema).EnsureSchema(default);
    }

    /// <summary>A schema created by the version 1 script, then upgraded (operations.md §11, 15A).</summary>
    public const string UpgradedSchema = "upgraded_v1";

    /// <summary>A fresh schema name.</summary>
    public string NewSchema() => $"t{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";

    /// <summary>A store on <paramref name="schema"/>, not ensured.</summary>
    public NpgsqlAggregateStore Upgraded(string schema, TimeProvider? time = null, StoreLookups? lookups = null, IOutboxReactions? reactions = null)
        => new(DataSource, Codec(), time ?? TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Lookups = lookups ?? new StoreLookups(), Reactions = reactions });

    /// <summary>A store on a fresh schema created by the version 1 script and then upgraded by <c>EnsureSchema</c>.</summary>
    public async Task<NpgsqlAggregateStore> UpgradedIsolatedStore(TimeProvider time, IOutboxReactions? reactions = null)
    {
        var schema = NewSchema();
        await Execute(NpgsqlSchema.Script(schema));
        var store = Upgraded(schema, time, reactions: reactions);
        await store.EnsureSchema(default);
        return store;
    }

    /// <summary>The codec of the contract models.</summary>
    public static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();

    /// <summary>The master key of the tests' key stores (fixed, so every store of the run opens what another sealed).</summary>
    public static readonly ConfiguredKeyEncryptionKey Master = new("test-1", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>A key store on <paramref name="schema"/> (the store's, version 3).</summary>
    public NpgsqlKeyStore KeyStore(TimeProvider time, string schema = NpgsqlSchema.DefaultSchema, NpgsqlKeyStoreOptions? options = null)
        => new(DataSource, Master, time, (options ?? new NpgsqlKeyStoreOptions()) with { Schema = schema });

    private NpgsqlKeyStore? _defaultKeys;

    /// <summary>A sealing store on the default schema (step 16); the stores share one key store (one listener connection).</summary>
    public NpgsqlAggregateStore SealedStore(TimeProvider time, StoreLookups? lookups = null, EventCodec? codec = null, IKeyStore? keys = null)
        => new(DataSource, codec ?? Codec(), time, new NpgsqlStoreOptions
        {
            Lookups = lookups ?? new StoreLookups(),
            Keys = keys ?? LazyInitializer.EnsureInitialized(ref _defaultKeys, () => KeyStore(TimeProvider.System)),
        });

    /// <summary>A sealing store on a fresh schema of its own, created.</summary>
    public async Task<NpgsqlAggregateStore> SealedIsolatedStore(TimeProvider time, EventCodec codec, StoreLookups? lookups = null, IOutboxReactions? reactions = null,
        NpgsqlKeyStoreOptions? keyOptions = null)
    {
        var schema = NewSchema();
        var store = new NpgsqlAggregateStore(DataSource, codec, time, new NpgsqlStoreOptions
        {
            Schema = schema,
            Lookups = lookups ?? new StoreLookups(),
            Reactions = reactions,
            Keys = KeyStore(time, schema, keyOptions),
        });
        await store.EnsureSchema(default);
        return store;
    }

    /// <summary>A store on the default schema.</summary>
    public NpgsqlAggregateStore Store(TimeProvider time, StoreLookups? lookups = null, EventCodec? codec = null)
        => new(DataSource, codec ?? Codec(), time, new NpgsqlStoreOptions { Lookups = lookups ?? new StoreLookups() });

    /// <summary>A store on a fresh schema of its own, created.</summary>
    public Task<NpgsqlAggregateStore> IsolatedStore(TimeProvider time, StoreLookups? lookups = null, EventCodec? codec = null, ReactionTable? reactions = null)
        => IsolatedStore(time, reactions is null ? null : OutboxReactions.From(reactions), lookups, codec);

    /// <summary>A store on a fresh schema of its own, created, with outbox rows for <paramref name="reactions"/>.</summary>
    public async Task<NpgsqlAggregateStore> IsolatedStore(TimeProvider time, IOutboxReactions? reactions, StoreLookups? lookups = null, EventCodec? codec = null,
        bool notify = false)
    {
        var store = new NpgsqlAggregateStore(DataSource, codec ?? Codec(), time, new NpgsqlStoreOptions
        {
            Schema = NewSchema(),
            Lookups = lookups ?? new StoreLookups(),
            Reactions = reactions,
            Notify = notify,
        });
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
        if (_defaultKeys is not null)
            await _defaultKeys.DisposeAsync();
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

/// <summary>The store contract on a sealing store: <see cref="Member"/>'s badge names are sealed (step 16).</summary>
[Trait("Category", "Integration")]
public sealed class SealedStoreContractTests(Postgres postgres) : StoreContract
{
    protected override IAggregateStore CreateStore(TimeProvider time, StoreLookups lookups) => postgres.SealedStore(time, lookups);
}

/// <summary>The outbox contract on a sealing store: reactions see the events opened.</summary>
[Trait("Category", "Integration")]
public sealed class SealedOutboxContractTests(Postgres postgres) : OutboxContract
{
    protected override async Task<(IAggregateStore Store, IOutbox Outbox)> Create(TimeProvider time, IOutboxReactions reactions)
    {
        var store = await postgres.SealedIsolatedStore(time, Postgres.Codec(), reactions: reactions);
        return (store, store.Outbox);
    }
}

/// <summary>The store contract on a schema the version 1 script created and <c>EnsureSchema</c> upgraded.</summary>
[Trait("Category", "Integration")]
public sealed class UpgradedStoreContractTests(Postgres postgres) : StoreContract
{
    protected override IAggregateStore CreateStore(TimeProvider time, StoreLookups lookups) => postgres.Upgraded(Postgres.UpgradedSchema, time, lookups);
}

/// <summary>The outbox contract on upgraded version 1 schemas, a schema per test.</summary>
[Trait("Category", "Integration")]
public sealed class UpgradedOutboxContractTests(Postgres postgres) : OutboxContract
{
    protected override async Task<(IAggregateStore Store, IOutbox Outbox)> Create(TimeProvider time, IOutboxReactions reactions)
    {
        var store = await postgres.UpgradedIsolatedStore(time, reactions);
        return (store, store.Outbox);
    }
}

/// <summary>The outbox contract on PostgreSQL, a schema per test.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlOutboxContractTests(Postgres postgres) : OutboxContract
{
    protected override async Task<(IAggregateStore Store, IOutbox Outbox)> Create(TimeProvider time, IOutboxReactions reactions)
    {
        var store = await postgres.IsolatedStore(time, reactions);
        return (store, store.Outbox);
    }
}
