using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// Applies an <see cref="NpgsqlSchemaDefinition"/> (docs/architecture/operations.md §4, O-4); what <c>EnsureSchema</c> of the
/// store, the bus and <c>messages_dead</c> runs:
/// <list type="number">
/// <item>it takes a <b>session</b> advisory lock per PostgreSQL schema and <b>polls</b> <c>pg_try_advisory_lock</c> every 100 ms
/// (a session blocked in <c>pg_advisory_lock</c> is a transaction <c>CREATE INDEX CONCURRENTLY</c> waits for: a deadlock);</item>
/// <item>it runs the version 1 script only on a schema without a versioned version table (empty, or from before step 15);</item>
/// <item>it runs the missing steps in order, each with <see cref="LockTimeout"/> and up to <see cref="Attempts"/> attempts, so a
/// long reader never makes the commits queue behind the DDL;</item>
/// <item>a <c>CONCURRENTLY</c> step runs outside a transaction and first drops an invalid index left by an interrupted build;</item>
/// <item>a version is stored in its last step's transaction.</item>
/// </list>
/// Code that knows version K runs on a schema of version V &gt; K while <c>compatible_from</c> ≤ K, and refuses otherwise.
/// </summary>
public sealed partial class NpgsqlSchemaUpgrader
{
    private readonly NpgsqlDataSource _db;
    private readonly NpgsqlSchemaDefinition _definition;
    private readonly ILogger _log;

    /// <summary>Creates an upgrader.</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="definition">The schema.</param>
    /// <param name="logger">Logs applied and retried steps (event ids 8001–8005); <see langword="null"/> = none.</param>
    public NpgsqlSchemaUpgrader(NpgsqlDataSource dataSource, NpgsqlSchemaDefinition definition, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(definition);
        _db = dataSource;
        _definition = definition;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>The <c>lock_timeout</c> of every step (default 2 s): a step that waits longer gives up and is retried.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not positive.</exception>
    public TimeSpan LockTimeout
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromSeconds(2);

    /// <summary>How often a step is tried before its lock timeout is an exception (default 5); the waits grow by 200 ms.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int Attempts
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 5;

    /// <summary>Upgrades the schema, or verifies it.</summary>
    /// <param name="mode"><see cref="SchemaMode.Upgrade"/> or <see cref="SchemaMode.VerifyOnly"/>.</param>
    /// <param name="cancellationToken">Cancels the call, also while it waits for another upgrader.</param>
    /// <returns>The schema's version afterwards.</returns>
    /// <exception cref="InvalidOperationException">
    /// The schema needs a newer code version (<c>compatible_from</c> above this code's version), or, in
    /// <see cref="SchemaMode.VerifyOnly"/>, it is missing or older than this code's version. The message names both versions.
    /// </exception>
    public async Task<SchemaState> Ensure(SchemaMode mode, CancellationToken cancellationToken)
    {
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (mode == SchemaMode.VerifyOnly)
                return await Verify(connection, cancellationToken).ConfigureAwait(false);
            await Lock(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                return await Upgrade(connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // the pool resets the session (DISCARD ALL) as well; unlocking here frees the next upgrader at once
                if (connection.State == System.Data.ConnectionState.Open)
                    await Execute(connection, "SELECT pg_advisory_unlock(hashtext($1))", CancellationToken.None, _definition.LockKey).ConfigureAwait(false);
            }
        }
    }

    private async Task Lock(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        // poll: a session blocked in pg_advisory_lock is a running transaction, and CREATE INDEX CONCURRENTLY waits for it
        while (true)
        {
            await using (var attempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1))", connection))
            {
                attempt.Parameters.Add(new() { Value = _definition.LockKey });
                if (await attempt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                    return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SchemaState> Upgrade(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var d = _definition;
        var ran = 0;
        if (!await IsVersioned(connection, cancellationToken).ConfigureAwait(false))
        {
            // empty, or version 1 from before step 15: the baseline (idempotent: it adds what version 1 gained before it was
            // versioned) and compatible_from, in one implicit transaction. Never re-run on a versioned schema: an
            // ALTER TABLE … IF NOT EXISTS takes an ACCESS EXCLUSIVE lock even when it changes nothing.
            await Execute(connection, d.Baseline + "\n" + d.Prepare, cancellationToken).ConfigureAwait(false);
        }
        var (version, compatibleFrom) = await Read(connection, cancellationToken).ConfigureAwait(false);
        Check(version, compatibleFrom);
        for (var i = 0; i < d.Steps.Count; i++)
        {
            if (d.Steps[i].Version <= version)
                continue;
            await Run(connection, i, cancellationToken).ConfigureAwait(false);
            ran++;
        }
        (version, compatibleFrom) = await Read(connection, cancellationToken).ConfigureAwait(false);
        if (version > d.Version)
            NewerCompatible(_log, d.Schema, d.Owner, version, compatibleFrom, d.Version);
        return new SchemaState(version, compatibleFrom, ran);
    }

    private async Task<SchemaState> Verify(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var d = _definition;
        if (!await IsVersioned(connection, cancellationToken).ConfigureAwait(false))
        {
            var message = $"The schema '{d.Schema}' ({d.Owner}) has no versioned {d.VersionTable} (it is missing or at version 1); this code needs version {d.Version}. "
                + "Run the definition's UpgradeScript with your migration tool, or use SchemaMode.Upgrade.";
            Refused(_log, d.Schema, d.Owner, 0, 0, d.Version);
            throw new InvalidOperationException(message);
        }
        var (version, compatibleFrom) = await Read(connection, cancellationToken).ConfigureAwait(false);
        Check(version, compatibleFrom);
        if (version < d.Version)
        {
            Refused(_log, d.Schema, d.Owner, version, compatibleFrom, d.Version);
            throw new InvalidOperationException($"The schema '{d.Schema}' ({d.Owner}) has version {version}; this code needs version {d.Version} (SchemaMode.VerifyOnly). "
                + $"Run UpgradeScript(from: {version}) with your migration tool, or use SchemaMode.Upgrade.");
        }
        if (version > d.Version)
            NewerCompatible(_log, d.Schema, d.Owner, version, compatibleFrom, d.Version);
        return new SchemaState(version, compatibleFrom, 0);
    }

    private void Check(int version, int compatibleFrom)
    {
        var d = _definition;
        if (compatibleFrom <= d.Version)
            return;
        Refused(_log, d.Schema, d.Owner, version, compatibleFrom, d.Version);
        throw new InvalidOperationException($"The schema '{d.Schema}' has version {version}, compatible from version {compatibleFrom}; this {d.Owner} knows version {d.Version}. "
            + "Deploy the newer version of the package.");
    }

    private async Task Run(NpgsqlConnection connection, int index, CancellationToken cancellationToken)
    {
        var d = _definition;
        var step = d.Steps[index];
        var timeout = string.Create(CultureInfo.InvariantCulture, $"{(long)LockTimeout.TotalMilliseconds}ms");
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!step.Concurrently)
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        await Execute(connection, $"SET LOCAL lock_timeout = '{timeout}'", cancellationToken, transaction: transaction).ConfigureAwait(false);
                        await Execute(connection, step.Sql, cancellationToken, transaction: transaction).ConfigureAwait(false);
                        if (d.Closes(index))
                            await Execute(connection, d.Bump(step.Version), cancellationToken, transaction: transaction).ConfigureAwait(false);
                        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else
                {
                    // CREATE INDEX CONCURRENTLY cannot run in a transaction (25001), and an interrupted build leaves an INVALID
                    // index that IF NOT EXISTS would keep for ever: drop it first
                    await Execute(connection, $"SET lock_timeout = '{timeout}'", cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (await IsInvalid(connection, step.Index!, cancellationToken).ConfigureAwait(false))
                        {
                            await Execute(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {d.Schema}.{step.Index}", cancellationToken).ConfigureAwait(false);
                            InvalidIndexDropped(_log, d.Schema, step.Index!, step.Version);
                        }
                        await Execute(connection, step.Sql, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await Execute(connection, "RESET lock_timeout", CancellationToken.None).ConfigureAwait(false);
                    }
                    if (d.Closes(index))
                        await Execute(connection, d.Bump(step.Version), cancellationToken).ConfigureAwait(false);
                }
                StepApplied(_log, d.Schema, d.Owner, step.Version, index + 1, attempt, (long)clock.Elapsed.TotalMilliseconds);
                return;
            }
            catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected && attempt < Attempts)
            {
                StepRetried(_log, d.Schema, step.Version, index + 1, attempt, Attempts, e.SqlState);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsVersioned(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var probe = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2 AND column_name = 'compatible_from')", connection);
        probe.Parameters.Add(new() { Value = _definition.Schema });
        probe.Parameters.Add(new() { Value = _definition.VersionTable });
        return await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private async Task<bool> IsInvalid(NpgsqlConnection connection, string index, CancellationToken cancellationToken)
    {
        await using var probe = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                           WHERE n.nspname = $1 AND c.relname = $2 AND NOT i.indisvalid)
            """, connection);
        probe.Parameters.Add(new() { Value = _definition.Schema });
        probe.Parameters.Add(new() { Value = index });
        return await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private async Task<(int Version, int CompatibleFrom)> Read(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand(
            $"SELECT version, compatible_from FROM {_definition.Schema}.{_definition.VersionTable} ORDER BY version DESC LIMIT 1", connection);
        var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"The schema '{_definition.Schema}' has an empty {_definition.VersionTable}.");
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, string? argument = null,
        NpgsqlTransaction? transaction = null)
    {
        // no command timeout: a CONCURRENTLY build on a large table may take minutes; the lock timeout bounds the waiting
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        if (argument is not null)
            command.Parameters.Add(new() { Value = argument });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information,
        Message = "Schema {Schema} ({Owner}): applied step {Step} of version {Version} (attempt {Attempt}, {ElapsedMs} ms)")]
    private static partial void StepApplied(ILogger logger, string schema, string owner, int version, int step, int attempt, long elapsedMs);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning,
        Message = "Schema {Schema}: step {Step} of version {Version} timed out waiting for a lock (attempt {Attempt} of {Attempts}, SQLSTATE {SqlState}); retrying")]
    private static partial void StepRetried(ILogger logger, string schema, int version, int step, int attempt, int attempts, string sqlState);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Warning,
        Message = "Schema {Schema}: dropped the invalid index {Index} left by an interrupted build; version {Version} builds it again")]
    private static partial void InvalidIndexDropped(ILogger logger, string schema, string index, int version);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Information,
        Message = "Schema {Schema} has version {Version}, compatible from {CompatibleFrom}; this {Owner} knows version {Known} and runs on it")]
    private static partial void NewerCompatible(ILogger logger, string schema, string owner, int version, int compatibleFrom, int known);

    [LoggerMessage(EventId = 8005, Level = LogLevel.Error,
        Message = "Schema {Schema} ({Owner}) refused: version {Version}, compatible from {CompatibleFrom}; this code knows version {Known}")]
    private static partial void Refused(ILogger logger, string schema, string owner, int version, int compatibleFrom, int known);
}
