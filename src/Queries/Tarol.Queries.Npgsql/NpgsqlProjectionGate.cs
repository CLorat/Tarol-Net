using Npgsql;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// The single-runner gate of the PostgreSQL read stores (step 15, O-8): a <b>session</b> advisory lock per projection version,
/// <c>pg_try_advisory_lock(hashtext('tarol.projection:&lt;schema&gt;:&lt;name&gt;:&lt;version&gt;'))</c>, held on a connection of
/// its own while the host runs. Other hosts never block: they ask again every <see cref="RetryEvery"/> and take over when the
/// runner's session ends (it stopped, crashed, or its connection was terminated). Registered by <c>UseNpgsqlReadModels</c>
/// unless <see cref="NpgsqlReadModelOptions.SingleRunner"/> is off. Each runner keeps one pooled connection per projection.
/// </summary>
public sealed class NpgsqlProjectionGate : IProjectionGate
{
    private readonly NpgsqlDataSource _db;
    private readonly string _schema;
    private readonly TimeProvider _time;

    /// <summary>Creates the gate.</summary>
    /// <param name="dataSource">The database of the read tables.</param>
    /// <param name="schema">The read tables' schema (part of the lock key).</param>
    /// <param name="time">The clock of the connection checks; default <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public NpgsqlProjectionGate(NpgsqlDataSource dataSource, string schema = NpgsqlReadModelSchema.DefaultSchema, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        NpgsqlReadModelSchema.Validate(schema);
        _db = dataSource;
        _schema = schema;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public TimeSpan RetryEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often a runner checks that its connection, and so its lock, is still there (default 5 s).</summary>
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async ValueTask<IProjectionGateLease?> TryEnter(string name, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var attempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1))", connection);
            attempt.Parameters.Add(new NpgsqlParameter { Value = Key(name, version) });
            if (await attempt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                return new Lease(connection, this);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>The lock key of a projection version (<c>tarol.projection:&lt;schema&gt;:&lt;name&gt;:&lt;version&gt;</c>).</summary>
    /// <param name="name">The projection.</param>
    /// <param name="version">Its version.</param>
    /// <returns>The text hashed by <c>hashtext</c>.</returns>
    public string Key(string name, int version) => $"tarol.projection:{_schema}:{name}:{version}";

    private sealed class Lease(NpgsqlConnection connection, NpgsqlProjectionGate gate) : IProjectionGateLease
    {
        private long _checked = gate._time.GetTimestamp();

        public async ValueTask<bool> Held(CancellationToken cancellationToken)
        {
            if (gate._time.GetElapsedTime(_checked) < gate.CheckEvery)
                return connection.State == System.Data.ConnectionState.Open;
            try
            {
                await using var ping = new NpgsqlCommand("SELECT 1", connection);
                _ = await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                _checked = gate._time.GetTimestamp();
                return true;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                return false;                                                      // the session (and its lock) is gone
            }
        }

        // closing the connection resets the session (DISCARD ALL), which releases the lock
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
