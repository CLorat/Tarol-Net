using System.Diagnostics;
using Npgsql;

namespace Probe.Operations;

/// <summary>One upgrade step: the SQL that takes the schema from <c>Version - 1</c> to <see cref="Version"/>.</summary>
/// <param name="Version">The version after the step.</param>
/// <param name="CompatibleFrom">The oldest code version that still runs on the schema after this step (expand: unchanged; contract: raised).</param>
/// <param name="Sql">The step; idempotent (IF NOT EXISTS), so a step that ran but whose version bump was lost runs again harmlessly.</param>
/// <param name="Transactional">False for <c>CREATE INDEX CONCURRENTLY</c>: runs outside a transaction, an invalid leftover index is dropped first.</param>
/// <param name="Index">For a non-transactional step: the index it builds (checked valid afterwards).</param>
internal sealed record Step(int Version, int CompatibleFrom, string Sql, bool Transactional = true, string? Index = null);

/// <summary>The outcome of <see cref="Upgrader.Ensure"/>.</summary>
internal sealed record Ensured(int From, int To, int Ran, int Compatible, TimeSpan Waited);

/// <summary>
/// The would-be <c>EnsureSchema</c> of step 15: the version 1 script, then the steps after the stored version, under a
/// session-level advisory lock (one process upgrades, the others wait and then see the new version). Every step runs with a
/// <c>lock_timeout</c> and is retried on SQLSTATE 55P03, so a long reader never makes the writers queue behind the DDL.
/// <c>schema_version</c> gains <c>compatible_from</c>: code that knows version K runs on a schema of version V &gt; K while
/// <c>compatible_from</c> &lt;= K (rolling deploys); it refuses only a schema that dropped what it needs.
/// </summary>
internal sealed class Upgrader(NpgsqlDataSource db, string schema, string v1Script, IReadOnlyList<Step> steps, int knows)
{
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int Attempts { get; init; } = 5;
    public bool Lock { get; init; } = true;
    /// <summary>Wait with a blocking pg_advisory_lock instead of polling pg_try_advisory_lock (deadlocks with CONCURRENTLY: §5).</summary>
    public bool Blocking { get; init; }
    public int LockTimeoutsHit { get; private set; }

    public static string Prepare(string s) => $"""
        ALTER TABLE {s}.schema_version ADD COLUMN IF NOT EXISTS compatible_from integer NOT NULL DEFAULT 1;
        CREATE TABLE IF NOT EXISTS {s}.upgrade_log (version integer NOT NULL, by_pid integer NOT NULL, at timestamptz NOT NULL DEFAULT clock_timestamp());
        """;

    public async Task<Ensured> Ensure(CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var sw = Stopwatch.StartNew();
        if (Lock && Blocking)
            await Exec(connection, "SELECT pg_advisory_lock(hashtext($1))", ct, "tarol.schema:" + schema);
        else if (Lock)
            // poll: a session blocked in pg_advisory_lock is a running transaction, and CREATE INDEX CONCURRENTLY waits for it
            while (true)
            {
                await using var t = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1))", connection);
                t.Parameters.Add(new() { Value = "tarol.schema:" + schema });
                if ((bool)(await t.ExecuteScalarAsync(ct))!) break;
                await Task.Delay(100, ct);
            }
        var waited = sw.Elapsed;
        try
        {
            // the version 1 script only on a database without it: re-running it takes table locks (ALTER TABLE … IF NOT EXISTS
            // takes ACCESS EXCLUSIVE even when nothing changes) and deadlocks with commits (selftest schema, check 4)
            await using (var probe = new NpgsqlCommand($"SELECT to_regclass('{schema}.schema_version') IS NOT NULL", connection))
                if (!(bool)(await probe.ExecuteScalarAsync(ct))!)
                    await Exec(connection, v1Script, ct);
            await using (var probe = new NpgsqlCommand($"SELECT count(*) FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = 'schema_version' AND column_name = 'compatible_from'", connection))
                if ((long)(await probe.ExecuteScalarAsync(ct))! == 0)
                    await Exec(connection, Prepare(schema), ct);
            var (from, compatible) = await Read(connection, ct);
            if (compatible > knows)
                throw new InvalidOperationException($"schema {schema} is version {from}, compatible from {compatible}; this code knows {knows}");
            var ran = 0;
            foreach (var step in steps.Where(s => s.Version > from && s.Version <= knows).OrderBy(s => s.Version))
            {
                await Run(connection, step, ct);
                ran++;
            }
            var (to, now) = await Read(connection, ct);
            return new Ensured(from, to, ran, now, waited);
        }
        finally
        {
            if (Lock)
                await Exec(connection, "SELECT pg_advisory_unlock(hashtext($1))", CancellationToken.None, "tarol.schema:" + schema);
        }
    }

    private async Task Run(NpgsqlConnection connection, Step step, CancellationToken ct)
    {
        var bump = $"UPDATE {schema}.schema_version SET version = {step.Version}, compatible_from = greatest(compatible_from, {step.CompatibleFrom}); INSERT INTO {schema}.upgrade_log (version, by_pid) VALUES ({step.Version}, pg_backend_pid());";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (step.Transactional)
                {
                    await using var tx = await connection.BeginTransactionAsync(ct);
                    await Exec(connection, $"SET LOCAL lock_timeout = '{(int)LockTimeout.TotalMilliseconds}ms'", ct, tx: tx);
                    await Exec(connection, step.Sql, ct, tx: tx);
                    await Exec(connection, bump, ct, tx: tx);
                    await tx.CommitAsync(ct);
                }
                else
                {
                    // CREATE INDEX CONCURRENTLY: no transaction; an interrupted build leaves an INVALID index that IF NOT EXISTS would keep
                    if (step.Index is { } index)
                        await Exec(connection, $"""
                            DO $$ BEGIN
                              IF EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                                         WHERE n.nspname = '{schema}' AND c.relname = '{index}' AND NOT i.indisvalid) THEN
                                EXECUTE 'DROP INDEX {schema}.{index}';
                              END IF; END $$
                            """, ct);
                    await Exec(connection, $"SET lock_timeout = '{(int)LockTimeout.TotalMilliseconds}ms'", ct);
                    await Exec(connection, step.Sql, ct);
                    await Exec(connection, "RESET lock_timeout", ct);
                    await Exec(connection, bump, ct);
                }
                return;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable && attempt < Attempts)
            {
                LockTimeoutsHit++;
                if (!step.Transactional) await Exec(connection, "RESET lock_timeout", ct);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }

    private async Task<(int Version, int Compatible)> Read(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand($"SELECT version, compatible_from FROM {schema}.schema_version LIMIT 1", connection);
        await using var r = await c.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt32(0), r.GetInt32(1));
    }

    private static async Task Exec(NpgsqlConnection connection, string sql, CancellationToken ct, string? arg = null, NpgsqlTransaction? tx = null)
    {
        await using var c = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 600 };
        if (arg is not null) c.Parameters.Add(new() { Value = arg });
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A plausible version 2 of the store script: indexes the retention of step 15 needs (dead_at), additive only.</summary>
    public static IReadOnlyList<Step> Version2(string s) =>
    [
        new(2, 1, $"CREATE INDEX CONCURRENTLY IF NOT EXISTS outbox_dead_at ON {s}.outbox_dead (dead_at)", Transactional: false, Index: "outbox_dead_at"),
        new(3, 1, $"CREATE INDEX CONCURRENTLY IF NOT EXISTS jobs_dead_at ON {s}.jobs_dead (dead_at)", Transactional: false, Index: "jobs_dead_at"),
        new(4, 1, $"ALTER TABLE {s}.outbox_dead ADD COLUMN IF NOT EXISTS redriven_by text NULL"),
    ];
}
