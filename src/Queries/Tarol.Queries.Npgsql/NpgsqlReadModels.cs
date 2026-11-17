using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Tarol.Persistence;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// The PostgreSQL read stores of an application (<see cref="IReadModels"/>: one <see cref="NpgsqlReadModelStore{TRow}"/>
/// per row type) and the store of SQL projections (<see cref="ISqlProjectionStore"/>), in one schema next to the event
/// store's tables. Old projection versions stay until <see cref="DropVersion"/> drops them (QR-3: never automatically).
/// </summary>
public sealed partial class NpgsqlReadModels : IReadModels, ISqlProjectionStore
{
    private readonly NpgsqlDataSource _db;
    private readonly JsonSerializerOptions _json;
    private readonly ConcurrentDictionary<Type, object> _stores = new();
    private readonly ConcurrentDictionary<(string, int), byte> _inUse = new();
    private readonly string? _channel;

    /// <summary>Creates the read stores.</summary>
    /// <param name="dataSource">The data source (arrays enabled; the event store's, or another database).</param>
    /// <param name="json">The application's JSON options (the rows are stored as JSON through them).</param>
    /// <param name="schema">The schema (default <c>tarol</c>).</param>
    /// <param name="pushCheckpoints">
    /// Whether every batch (row and SQL projections) sends the checkpoint <c>NOTIFY &lt;schema&gt;_checkpoint</c> in its
    /// transaction (<see cref="NpgsqlReadModelOptions.PushCheckpoints"/>; read by <see cref="NpgsqlProjectionPositions"/>).
    /// </param>
    public NpgsqlReadModels(NpgsqlDataSource dataSource, JsonSerializerOptions json, string schema = NpgsqlReadModelSchema.DefaultSchema,
        bool pushCheckpoints = false)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(json);
        NpgsqlReadModelSchema.Validate(schema);
        _db = dataSource;
        _json = json;
        Schema = schema;
        _channel = pushCheckpoints ? CheckpointSql.Channel(schema) : null;
    }

    /// <summary>The schema of the read tables and the projections table.</summary>
    public string Schema { get; }

    /// <summary>The data source (for the lag signal of step 15).</summary>
    internal NpgsqlDataSource DataSource => _db;

    /// <summary>Whether every batch sends the checkpoint <c>NOTIFY</c> (<see cref="NpgsqlReadModelOptions.PushCheckpoints"/>).</summary>
    public bool PushesCheckpoints => _channel is not null;

    /// <inheritdoc />
    public IReadModelStore<TRow> Store<TRow>()
        where TRow : class, IProjection<TRow>
        => Typed<TRow>();

    /// <summary>The typed store of <typeparamref name="TRow"/>.</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <returns>The store.</returns>
    public NpgsqlReadModelStore<TRow> Typed<TRow>()
        where TRow : class, IProjection<TRow>
        => (NpgsqlReadModelStore<TRow>)_stores.GetOrAdd(typeof(TRow), _ =>
        {
            _inUse[(TRow.Name, TRow.Version)] = 0;
            return new NpgsqlReadModelStore<TRow>(_db, _json, Schema, PushesCheckpoints);
        });

    /// <summary>
    /// Drops the read table and the checkpoint of an old projection version (the explicit operation of QR-3, once no
    /// running code reads it). Refuses a version this process uses. Each attempt has <see cref="DropLockTimeout"/>, so a reader
    /// of the old table delays the drop, never the readers behind it (step 15: without it a reader arriving meanwhile waited
    /// 2.6 s and then failed with <c>42P01</c>).
    /// </summary>
    /// <param name="name">The projection's name.</param>
    /// <param name="version">The old version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">This process reads or hosts that version.</exception>
    /// <exception cref="TimeoutException">Another session still reads the table after <see cref="DropAttempts"/> attempts.</exception>
    public async Task DropVersion(string name, int version, CancellationToken cancellationToken)
    {
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (_inUse.ContainsKey((name, version)))
            throw new InvalidOperationException($"The projection '{name}' v{version} is in use in this process; drop only versions no running code reads.");
        // step 15 (O-5): with lock_timeout, a drop behind a reader gives up instead of making every new reader wait behind it
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        await using (var timeout = new NpgsqlCommand(
                            string.Create(CultureInfo.InvariantCulture, $"SET LOCAL lock_timeout = '{(long)DropLockTimeout.TotalMilliseconds}ms'"), connection, transaction))
                            await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {Schema}.rm_{name.Replace('-', '_')}_v{version}", connection, transaction))
                            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        await using (var forget = new NpgsqlCommand($"DELETE FROM {Schema}.projections WHERE name = $1 AND version = $2", connection, transaction))
                        {
                            forget.Parameters.Add(new NpgsqlParameter { Value = name });
                            forget.Parameters.Add(new NpgsqlParameter { Value = version });
                            await forget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        }
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                if (attempt >= DropAttempts)
                    throw new TimeoutException($"The table of '{name}' v{version} is still read after {DropAttempts} attempts of {DropLockTimeout.TotalMilliseconds} ms; "
                        + "drop it when no process reads it.", e);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The <c>lock_timeout</c> of <see cref="DropVersion"/> (default 200 ms; step 15).</summary>
    public TimeSpan DropLockTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How often <see cref="DropVersion"/> tries before it gives up with a <see cref="TimeoutException"/> (default 5).</summary>
    public int DropAttempts { get; init; } = 5;

    /// <inheritdoc />
    public async ValueTask Prepare(ISqlProjection projection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ValidateName(projection.Name);
        _inUse[(projection.Name, projection.Version)] = 0;
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", connection, transaction))
                {
                    lockCommand.Parameters.Add(new NpgsqlParameter { Value = "tarol.read-models:" + Schema });
                    await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var table = new NpgsqlCommand(NpgsqlReadModelSchema.ProjectionsTable(Schema), connection, transaction))
                    await table.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await using (var create = new NpgsqlCommand($"INSERT INTO {Schema}.projections (name, version) VALUES ($1, $2) ON CONFLICT DO NOTHING", connection, transaction))
                {
                    create.Parameters.Add(new NpgsqlParameter { Value = projection.Name });
                    create.Parameters.Add(new NpgsqlParameter { Value = projection.Version });
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await projection.Prepare(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> Checkpoint(ISqlProjection projection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        await using var command = _db.CreateCommand($"SELECT tx::text, pos, state FROM {Schema}.projections WHERE name = $1 AND version = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = projection.Name });
        command.Parameters.Add(new NpgsqlParameter { Value = projection.Version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? CheckpointSql.Read(reader, 0) : default;
    }

    /// <inheritdoc />
    public async ValueTask<(bool Written, int Rows)> Write(ISqlProjection projection, IReadOnlyList<PositionedEvent> events, GlobalPosition from,
        GlobalPosition through, ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(events);
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var advance = new NpgsqlCommand($"""
                    UPDATE {Schema}.projections SET tx = $3::text::xid8, pos = $4, state = $5, updated_at = now()
                    WHERE name = $1 AND version = $2 AND tx = $6::text::xid8 AND pos = $7
                    """, connection, transaction))
                {
                    CheckpointSql.Add(advance.Parameters, projection.Name, projection.Version, from, through, state);
                    if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return (false, 0);
                    }
                }
                if (_channel is not null)
                    await CheckpointSql.Notify(connection, transaction, _channel, projection.Name, projection.Version, through, state, cancellationToken).ConfigureAwait(false);
                var rows = events.Count == 0 ? 0 : await projection.Apply(events, connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (true, rows);
            }
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ProjectionName().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a projection name (lowercase letters, digits and hyphens, at most 40, starting with a letter).", nameof(name));
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex ProjectionName();
}
