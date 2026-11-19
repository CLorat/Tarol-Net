using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Contract;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>
/// Versioned schema upgrades (docs/architecture/operations.md §4, O-4; migration/operations.md §11, PR 15A): concurrent first
/// starts, a populated version 1 database upgraded while writers commit, old code on a newer schema, an interrupted
/// <c>CONCURRENTLY</c> build, a step behind a held lock, <see cref="SchemaMode.VerifyOnly"/> and <c>UpgradeScript</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaUpgradeTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NpgsqlSchemaUpgrader Upgrader(string schema, ILogger? logger = null, NpgsqlSchemaDefinition? definition = null, TimeSpan? lockTimeout = null)
        => new(postgres.DataSource, definition ?? NpgsqlSchema.Definition(schema), logger) { LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2) };

    /// <summary>Code of step 14: knows version 1 only (the baseline, no steps).</summary>
    private static NpgsqlSchemaDefinition KnowsVersion1(string schema)
        => new("Tarol.Persistence.Npgsql", schema, "schema_version", NpgsqlSchema.Script(schema), []);

    private async Task<(int Version, int CompatibleFrom)> Stored(string schema, string table = "schema_version")
    {
        await using var command = postgres.DataSource.CreateCommand($"SELECT version, compatible_from FROM {schema}.{table}");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task<bool?> Valid(string schema, string index) => await postgres.Scalar($"""
        SELECT i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = '{schema}' AND c.relname = '{index}'
        """) as bool?;

    [Fact]
    public async Task Six_processes_starting_at_once_on_an_empty_schema_all_succeed_and_one_runs_the_steps()
    {
        for (var round = 0; round < 5; round++)
        {
            var schema = postgres.NewSchema();
            // six data sources: six processes, each with its own pool
            var sources = Enumerable.Range(0, 6).Select(_ => new NpgsqlSlimDataSourceBuilder(postgres.ConnectionString).EnableArrays().Build()).ToList();
            try
            {
                var states = await Task.WhenAll(sources.Select(db => new NpgsqlAggregateStore(db, Postgres.Codec(), TimeProvider.System, new() { Schema = schema })
                    .EnsureSchema(SchemaMode.Upgrade, Ct)));

                states.ShouldAllBe(s => s.Version == NpgsqlSchema.Version && s.CompatibleFrom == 1);
                states.Sum(s => s.StepsRun).ShouldBe(NpgsqlSchema.Steps(schema).Count);                      // one process ran them
                (await Stored(schema)).ShouldBe((NpgsqlSchema.Version, 1));
                (await postgres.Scalar($"SELECT count(*) FROM {schema}.schema_version")).ShouldBe(1L);
                (await Valid(schema, "outbox_dead_at")).ShouldBe(true);
                (await Valid(schema, "jobs_dead_at")).ShouldBe(true);
            }
            finally
            {
                foreach (var db in sources)
                    await db.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_populated_version_1_database_is_upgraded_while_writers_commit()
    {
        var schema = postgres.NewSchema();
        await postgres.Execute(NpgsqlSchema.Script(schema));                                                // what step 14 created
        var store = postgres.Upgraded(schema);
        var tenant = Id<ITenant>.New(TimeProvider.System);
        var context = OperationContext.Start(Actor.User("alice"), tenant, TimeProvider.System);
        for (var i = 0; i < 200; i++)
            (await store.Append(Member.Join(Id<Member>.New(TimeProvider.System), $"m{i}@x"), 0, context, Option.None, Ct)).ToResult().ShouldBe(Result.Success());
        await postgres.Execute($"""
            INSERT INTO {schema}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, last_error, dead_at)
            SELECT g, gen_random_uuid(), 'r', 'member', gen_random_uuid(), 1, gen_random_uuid(), 10, 'boom', now() - g * interval '1 second' FROM generate_series(1, 500) g;
            INSERT INTO {schema}.jobs_dead (tenant, name, occurrence, attempts, last_error, dead_at)
            SELECT gen_random_uuid(), 'j', now(), 3, 'boom', now() FROM generate_series(1, 500) g;
            """);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var failures = new ConcurrentQueue<Exception>();
        var commits = 0;
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                try
                {
                    var appended = await store.Append(Member.Join(Id<Member>.New(TimeProvider.System), "w@x"), 0, context, Option.None, CancellationToken.None);
                    appended.ToResult().ShouldBe(Result.Success());
                    Interlocked.Increment(ref commits);
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                }
        }, CancellationToken.None)).ToList();
        while (Volatile.Read(ref commits) < 20)                                                             // the writers run
            await Task.Delay(10, Ct);

        var states = await Task.WhenAll(store.EnsureSchema(SchemaMode.Upgrade, Ct), postgres.Upgraded(schema).EnsureSchema(SchemaMode.Upgrade, Ct));
        var before = Volatile.Read(ref commits);
        while (Volatile.Read(ref commits) < before + 20)                                                    // and keep running after it
            await Task.Delay(10, Ct);
        await stop.CancelAsync();
        await Task.WhenAll(writers);

        failures.ShouldBeEmpty();
        states.Sum(s => s.StepsRun).ShouldBe(NpgsqlSchema.Steps(schema).Count);
        (await Stored(schema)).ShouldBe((NpgsqlSchema.Version, 1));
        (await Valid(schema, "outbox_dead_at")).ShouldBe(true);
        (await Valid(schema, "jobs_dead_at")).ShouldBe(true);
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.operations_audit")).ShouldBe(0L);
        (await store.Outbox.DeadLetters(1000, Ct)).Count.ShouldBe(500);                                    // the rows survived
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.events")).ShouldBe(200L + Volatile.Read(ref commits));
    }

    [Fact]
    public async Task Old_code_runs_on_a_newer_compatible_schema_and_refuses_an_incompatible_one()
    {
        var store = await postgres.IsolatedStore(TimeProvider.System);
        var old = Upgrader(store.Schema, definition: KnowsVersion1(store.Schema));

        (await old.Ensure(SchemaMode.Upgrade, Ct)).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, 0));                    // a rolling deploy: old pods start
        (await old.Ensure(SchemaMode.VerifyOnly, Ct)).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, 0));

        var newer = NpgsqlSchema.Version + 1;
        await postgres.Execute($"UPDATE {store.Schema}.schema_version SET version = {newer}, compatible_from = 1");
        (await store.EnsureSchema(SchemaMode.Upgrade, Ct)).ShouldBe(new SchemaState(newer, 1, 0));        // no step runs

        await postgres.Execute($"UPDATE {store.Schema}.schema_version SET version = {newer}, compatible_from = {newer}");
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => store.EnsureSchema(SchemaMode.Upgrade, Ct));
        refused.Message.ShouldContain($"has version {newer}, compatible from version {newer}; this Tarol.Persistence.Npgsql knows version {NpgsqlSchema.Version}");
        (await Should.ThrowAsync<InvalidOperationException>(() => old.Ensure(SchemaMode.Upgrade, Ct))).Message.ShouldContain("knows version 1");
    }

    [Fact]
    public async Task An_interrupted_concurrent_index_build_is_dropped_and_built_again()
    {
        var schema = postgres.NewSchema();
        await postgres.Execute(NpgsqlSchema.Script(schema));
        await postgres.Execute($"""
            INSERT INTO {schema}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, dead_at)
            SELECT g, gen_random_uuid(), 'r', 'member', gen_random_uuid(), 1, gen_random_uuid(), 10, timestamptz '2026-11-17 12:00:00Z' FROM generate_series(1, 2) g
            """);
        // a build that fails half-way leaves an INVALID index of the step's name, which IF NOT EXISTS would keep for ever
        (await Should.ThrowAsync<PostgresException>(() => postgres.Execute($"CREATE UNIQUE INDEX CONCURRENTLY outbox_dead_at ON {schema}.outbox_dead (dead_at)")))
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        (await Valid(schema, "outbox_dead_at")).ShouldBe(false);
        var log = new EventLog();

        (await Upgrader(schema, log).Ensure(SchemaMode.Upgrade, Ct)).Version.ShouldBe(NpgsqlSchema.Version);

        (await Valid(schema, "outbox_dead_at")).ShouldBe(true);
        (await postgres.Scalar($"SELECT indisunique FROM pg_index WHERE indexrelid = '{schema}.outbox_dead_at'::regclass")).ShouldBe(false);
        log.Ids.ShouldContain(8003);
        log.Ids.Count(id => id == 8001).ShouldBe(NpgsqlSchema.Steps(schema).Count);
    }

    [Fact]
    public async Task VerifyOnly_fails_fast_on_a_missing_or_older_schema_and_changes_nothing()
    {
        var schema = postgres.NewSchema();
        var missing = await Should.ThrowAsync<InvalidOperationException>(() => Upgrader(schema).Ensure(SchemaMode.VerifyOnly, Ct));
        missing.Message.ShouldContain($"this code needs version {NpgsqlSchema.Version}");
        (await postgres.Scalar($"SELECT to_regnamespace('{schema}') IS NULL")).ShouldBe(true);               // nothing created

        await postgres.Execute(NpgsqlSchema.Script(schema));                                                // version 1 of step 14
        (await Should.ThrowAsync<InvalidOperationException>(() => Upgrader(schema).Ensure(SchemaMode.VerifyOnly, Ct))).Message.ShouldContain($"needs version {NpgsqlSchema.Version}");

        await postgres.Execute(NpgsqlSchema.Definition(schema).Prepare);                                     // versioned, still version 1
        var older = await Should.ThrowAsync<InvalidOperationException>(() => postgres.Upgraded(schema).EnsureSchema(SchemaMode.VerifyOnly, Ct));
        older.Message.ShouldContain($"has version 1; this code needs version {NpgsqlSchema.Version}");
        (await Stored(schema)).ShouldBe((1, 1));

        (await Upgrader(schema).Ensure(SchemaMode.Upgrade, Ct)).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, NpgsqlSchema.Steps(schema).Count));
        (await Upgrader(schema).Ensure(SchemaMode.VerifyOnly, Ct)).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, 0));
    }

    [Fact]
    public async Task UpgradeScript_run_statement_by_statement_gives_the_same_schema_and_verifies()
    {
        var fresh = postgres.NewSchema();
        foreach (var statement in Statements(NpgsqlSchema.UpgradeScript(fresh, 0)))
            await postgres.Execute(statement);                                                               // as psql would: autocommit
        (await Upgrader(fresh).Ensure(SchemaMode.VerifyOnly, Ct)).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, 0));
        (await Valid(fresh, "jobs_dead_at")).ShouldBe(true);

        var old = postgres.NewSchema();
        await postgres.Execute(NpgsqlSchema.Script(old));
        foreach (var statement in Statements(NpgsqlSchema.UpgradeScript(old, 1)))
            await postgres.Execute(statement);
        (await Upgrader(old).Ensure(SchemaMode.VerifyOnly, Ct)).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, 0));

        NpgsqlSchema.UpgradeScript(old, NpgsqlSchema.Version).ShouldBeEmpty();
        NpgsqlSchema.UpgradeScript(old, 1).ShouldNotContain("CREATE TABLE IF NOT EXISTS " + old + ".streams");
        NpgsqlSchema.UpgradeScript(old, 1).ShouldContain($"CREATE INDEX CONCURRENTLY IF NOT EXISTS outbox_dead_at ON {old}.outbox_dead (dead_at)");
    }

    [Fact]
    public void A_definition_refuses_steps_out_of_order_and_a_concurrent_step_without_its_index()
    {
        Should.Throw<ArgumentException>(() => new NpgsqlSchemaDefinition("x", "s", "v", "", [new(3, 1, "a"), new(2, 1, "b")]));
        Should.Throw<ArgumentException>(() => new NpgsqlSchemaDefinition("x", "s", "v", "", [new(1, 1, "a")]));
        Should.Throw<ArgumentException>(() => new NpgsqlSchemaDefinition("x", "s", "v", "", [new(2, 3, "a")]));
        Should.Throw<ArgumentException>(() => new NpgsqlSchemaDefinition("x", "s", "v", "", [new(2, 1, "CREATE INDEX CONCURRENTLY …", Concurrently: true)]));
        Should.Throw<ArgumentException>(() => new NpgsqlSchemaDefinition("x", "S", "v", "", []));
        new NpgsqlSchemaDefinition("x", "s", "v", "", [new(2, 1, "a"), new(2, 1, "b"), new(3, 2, "c")]).Version.ShouldBe(3);
        Should.Throw<ArgumentOutOfRangeException>(() => new NpgsqlSchemaUpgrader(postgres.DataSource, NpgsqlSchema.Definition()) { LockTimeout = TimeSpan.Zero });
    }

    /// <summary>Splits a script into statements at top-level semicolons (a <c>DO $$ … $$</c> block stays one statement).</summary>
    internal static IEnumerable<string> Statements(string script)
    {
        var current = new System.Text.StringBuilder();
        var dollar = false;
        foreach (var line in script.Split('\n'))
        {
            if (line.TrimStart().StartsWith("--", StringComparison.Ordinal) && current.Length == 0)
                continue;
            current.Append(line).Append('\n');
            if (line.Split("$$").Length % 2 == 0)
                dollar = !dollar;
            if (!dollar && line.TrimEnd().EndsWith(';'))
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.ToString().Trim().Length > 0)
            yield return current.ToString();
    }
}

/// <summary>
/// A step that waits behind another session's table lock (operations.md §4.3): with <c>lock_timeout</c> it gives up, retries
/// and completes, so commits never queue behind the DDL. It holds a transaction open, so it runs alone (P-14).
/// </summary>
[CollectionDefinition(nameof(HeldLocks), DisableParallelization = true)]
public sealed class HeldLocks;

[Collection(nameof(HeldLocks))]
[Trait("Category", "Integration")]
public sealed class SchemaUpgradeLockTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_step_behind_a_held_lock_times_out_retries_and_completes()
    {
        var schema = postgres.NewSchema();
        await postgres.Execute(NpgsqlSchema.Script(schema));
        await using var holder = await postgres.DataSource.OpenConnectionAsync(Ct);
        var held = await holder.BeginTransactionAsync(Ct);
        await using (var lockIt = new NpgsqlCommand($"LOCK TABLE {schema}.outbox_dead IN SHARE MODE", holder, held))   // conflicts with CREATE INDEX CONCURRENTLY
            await lockIt.ExecuteNonQueryAsync(Ct);
        var log = new EventLog();
        var upgrade = new NpgsqlSchemaUpgrader(postgres.DataSource, NpgsqlSchema.Definition(schema), log) { LockTimeout = TimeSpan.FromMilliseconds(200) }
            .Ensure(SchemaMode.Upgrade, Ct);

        var waited = Stopwatch.StartNew();
        while (!log.Ids.Contains(8002) && waited.Elapsed < TimeSpan.FromSeconds(20))                         // bounded by the event
            await Task.Delay(20, Ct);
        log.Ids.ShouldContain(8002);
        upgrade.IsCompleted.ShouldBeFalse();
        await held.CommitAsync(Ct);                                                                          // the reader ends

        (await upgrade).ShouldBe(new SchemaState(NpgsqlSchema.Version, 1, NpgsqlSchema.Steps(schema).Count));
        (await postgres.Scalar($"SELECT version FROM {schema}.schema_version")).ShouldBe(NpgsqlSchema.Version);
    }
}

/// <summary>Collects event ids.</summary>
internal sealed class EventLog : ILogger
{
    private readonly ConcurrentQueue<int> _ids = new();

    public IReadOnlyList<int> Ids => [.. _ids];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => _ids.Enqueue(eventId.Id);
}

/// <summary>The data sources Tarol builds prepare their statements unless the connection string says otherwise.</summary>
[Trait("Category", "Integration")]                                          // no database needed, but this assembly's fixture starts one
public sealed class AutoPrepareTests
{
    [Fact]
    public void Auto_prepare_is_set_unless_the_connection_string_sets_it()
    {
        new NpgsqlSlimDataSourceBuilder("Host=db").AutoPrepareUnlessSet().ConnectionStringBuilder.MaxAutoPrepare.ShouldBe(64);
        new NpgsqlSlimDataSourceBuilder("Host=db;Max Auto Prepare=0").AutoPrepareUnlessSet().ConnectionStringBuilder.MaxAutoPrepare.ShouldBe(0);
        new NpgsqlSlimDataSourceBuilder("Host=db;maxautoprepare=10").AutoPrepareUnlessSet(32).ConnectionStringBuilder.MaxAutoPrepare.ShouldBe(10);
    }
}
