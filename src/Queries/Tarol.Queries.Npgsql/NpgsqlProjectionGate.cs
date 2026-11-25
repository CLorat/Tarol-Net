using Npgsql;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// The single-runner gate of the PostgreSQL read stores (step 15, O-8): a <b>session</b> advisory lock per projection version,
/// <c>pg_try_advisory_lock(hashtext('tarol.projection:&lt;schema&gt;:&lt;name&gt;:&lt;version&gt;'))</c>, held while the host runs.
/// Other hosts never block: they ask again every <see cref="RetryEvery"/> and take over when the runner's session ends (it
/// stopped, crashed, or its connection was terminated). Registered by <c>UseNpgsqlReadModels</c> unless
/// <see cref="NpgsqlReadModelOptions.SingleRunner"/> is off.
/// <para>
/// All locks of one gate share <b>one</b> pooled connection (a session holds any number of advisory locks): a process with
/// 42 projections keeps 1 connection for its gates, not 42 (Tatalu FB-INT3-3, "too many clients"). A projection version is
/// held at most once per gate (session locks are re-entrant, the gate is not). When that session breaks, every lease of the
/// gate reports <see cref="IProjectionGateLease.Held"/> <see langword="false"/>, and the hosts ask again on a new session.
/// </para>
/// </summary>
public sealed class NpgsqlProjectionGate : IProjectionGate, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly string _schema;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private NpgsqlConnection? _session;
    private long _generation;
    private long _checked;

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

    /// <summary>How often the gate checks that its session, and so its locks, is still there (default 5 s).</summary>
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many projection versions this gate holds now (all on one session).</summary>
    public int Locks
    {
        get
        {
            _sessionLock.Wait();
            try
            {
                return _held.Count;
            }
            finally
            {
                _sessionLock.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IProjectionGateLease?> TryEnter(string name, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = Key(name, version);
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_held.Contains(key))
                return null;                                                       // another host of this process runs it
            var session = await Session(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var attempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1))", session);
                attempt.Parameters.Add(new NpgsqlParameter { Value = key });
                if (await attempt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    return null;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                await Drop().ConfigureAwait(false);
                throw;
            }
            _held.Add(key);
            return new Lease(this, key, _generation);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>The lock key of a projection version (<c>tarol.projection:&lt;schema&gt;:&lt;name&gt;:&lt;version&gt;</c>).</summary>
    /// <param name="name">The projection.</param>
    /// <param name="version">Its version.</param>
    /// <returns>The text hashed by <c>hashtext</c>.</returns>
    public string Key(string name, int version) => $"tarol.projection:{_schema}:{name}:{version}";

    /// <summary>Closes the session: every lock of this gate is released.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Drop().ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>The open session, opened on demand; a new session is a new generation (no lock of the old one is held).</summary>
    private async ValueTask<NpgsqlConnection> Session(CancellationToken cancellationToken)
    {
        if (_session is { State: System.Data.ConnectionState.Open } open)
            return open;
        await Drop().ConfigureAwait(false);
        _session = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _checked = _time.GetTimestamp();
        return _session;
    }

    /// <summary>Ends the session (closing it resets it, DISCARD ALL, which releases its locks) and forgets every lease.</summary>
    private async ValueTask Drop()
    {
        _generation++;
        _held.Clear();
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> Check(long generation, CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != _generation || _session is not { State: System.Data.ConnectionState.Open } session)
                return false;
            if (_time.GetElapsedTime(_checked) < CheckEvery)
                return true;
            try
            {
                await using var ping = new NpgsqlCommand("SELECT 1", session);
                _ = await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                _checked = _time.GetTimestamp();
                return true;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                await Drop().ConfigureAwait(false);                                    // the session (and every lock) is gone
                return false;
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async ValueTask Release(string key, long generation)
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation || !_held.Remove(key) || _session is not { } session)
                return;
            if (_held.Count == 0)
            {
                await Drop().ConfigureAwait(false);                                    // nothing held: give the connection back
                return;
            }
            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext($1))", session);
                unlock.Parameters.Add(new NpgsqlParameter { Value = key });
                _ = await unlock.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                await Drop().ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private sealed class Lease(NpgsqlProjectionGate gate, string key, long generation) : IProjectionGateLease
    {
        private int _disposed;

        public ValueTask<bool> Held(CancellationToken cancellationToken)
            => Volatile.Read(ref _disposed) == 1 ? ValueTask.FromResult(false) : gate.Check(generation, cancellationToken);

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 1 ? ValueTask.CompletedTask : gate.Release(key, generation);
    }
}
