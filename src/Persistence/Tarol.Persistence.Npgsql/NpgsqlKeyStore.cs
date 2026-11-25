using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Tarol.Persistence.Npgsql;

/// <summary>How an <see cref="NpgsqlKeyStore"/> behaves.</summary>
public sealed record NpgsqlKeyStoreOptions
{
    private readonly string _schema = NpgsqlSchema.DefaultSchema;

    /// <summary>The schema of <c>tenant_keys</c> and <c>subject_keys</c> (the store's schema, version 3).</summary>
    public string Schema
    {
        get => _schema;
        init
        {
            NpgsqlSchema.Validate(value);
            _schema = value;
        }
    }

    /// <summary>The most unwrapped data keys kept per process (default 100,000); beyond it the least recently used go.</summary>
    public int MaxCachedKeys { get; init; } = 100_000;

    /// <summary>How long an unused key stays cached (default 1 hour).</summary>
    public TimeSpan IdleTime { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The <see cref="IKeyStore"/> on tables in the store's database (DP-1, schema version 3): <c>tenant_keys</c> holds each
/// tenant's key wrapped by the master key, <c>subject_keys</c> each subject's data key wrapped by its tenant key. Keys are
/// cached per process, unwrapped (bounded, with an idle time); a load that finds its keys cached adds no round trip
/// (data-protection.md §4.3). An erasure writes the tombstone and a <c>NOTIFY</c> in one statement, and every process that
/// listens evicts the key (<see cref="StartListening"/>; a lost connection clears the whole cache).
/// </summary>
public sealed partial class NpgsqlKeyStore : IKeyStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly IKeyEncryptionKey _master;
    private readonly TimeProvider _time;
    private readonly NpgsqlKeyStoreOptions _options;
    private readonly ILogger? _log;
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Subject), Cached<SubjectKey>> _keys = new();
    private readonly ConcurrentDictionary<Guid, Cached<byte[]?>> _tenants = new();

    /// <summary>Tests: runs between reading keys from the table and caching them (where an erasure can come in).</summary>
    internal Func<Task>? AfterKeyRead { get; set; }

    // moved by every eviction before it removes entries; a read that saw it move meanwhile does not keep what it cached, so an
    // erasure that commits between the read of a key and its caching cannot leave the old key in the cache
    private long _evictions;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _listenGate = new();
    private Task? _listener;
    private TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a key store.</summary>
    /// <param name="dataSource">The store's data source.</param>
    /// <param name="master">The master key.</param>
    /// <param name="time">The clock for creation and erasure times and the cache's idle time.</param>
    /// <param name="options">Schema and cache bounds.</param>
    /// <param name="logger">Logs 8006–8008 (Debug); <see langword="null"/> = none.</param>
    public NpgsqlKeyStore(NpgsqlDataSource dataSource, IKeyEncryptionKey master, TimeProvider time, NpgsqlKeyStoreOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(time);
        _db = dataSource;
        _master = master;
        _time = time;
        _options = options ?? new NpgsqlKeyStoreOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxCachedKeys);
        _log = logger;
        Channel = _options.Schema + "_erased";
    }

    /// <inheritdoc />
    public event EventHandler<KeyErasedEventArgs>? Erased;

    /// <summary>The <c>NOTIFY</c> channel of erasures (<c>&lt;schema&gt;_erased</c>).</summary>
    public string Channel { get; }

    /// <summary>How many data keys are cached (tests, metrics).</summary>
    public int CachedKeys => _keys.Count;

    /// <summary>How many key queries reached the database (tests: a cached load makes none).</summary>
    public int KeyQueries => Volatile.Read(ref _keyQueries);

    private int _keyQueries;

    private string S => _options.Schema;

    /// <summary>
    /// Starts listening for erasures of other processes (once; the first key access starts it too). Completes when the
    /// <c>LISTEN</c> is active.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait (not the listener).</param>
    /// <returns>A task.</returns>
    public Task StartListening(CancellationToken cancellationToken)
    {
        lock (_listenGate)
            _listener ??= Detached.Run(() => Listen(_stop.Token));
        return _listening.Task.WaitAsync(cancellationToken);
    }

    // ----------------------------------------------------------------------------------------------------------- keys

    /// <inheritdoc />
    public async ValueTask<KeyRing> Keys(Guid tenant, IReadOnlyCollection<Guid> subjects, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        _ = StartListening(CancellationToken.None);
        var ring = new KeyRing(tenant);
        var now = _time.GetTimestamp();
        List<Guid>? missing = null;
        foreach (var subject in subjects)
        {
            if (ring.Contains(subject))
                continue;
            if (Fresh(_keys, (tenant, subject), now) is { } cached)
                ring.Add(cached);
            else
                (missing ??= []).Add(subject);
        }

        if (missing is null)
            return ring;
        var evictions = Volatile.Read(ref _evictions);
        var tenantKey = await TenantKey(tenant, create, cancellationToken).ConfigureAwait(false);
        if (tenantKey is null)
        {
            foreach (var subject in missing)
                ring.Add(SubjectKey.Erased(tenant, subject)); // the tenant is erased, or never sealed anything
            return ring;
        }

        using var wrap = new KeyWrap(tenantKey);
        var found = await Read(tenant, missing, wrap, cancellationToken).ConfigureAwait(false);
        if (AfterKeyRead is { } afterKeyRead)
            await afterKeyRead().ConfigureAwait(false);
        if (create && found.Count < missing.Count)
        {
            await Create(tenant, [.. missing.Where(s => !found.ContainsKey(s))], wrap, cancellationToken).ConfigureAwait(false);
            foreach (var (subject, key) in await Read(tenant, [.. missing.Where(s => !found.ContainsKey(s))], wrap, cancellationToken).ConfigureAwait(false))
                found[subject] = key;
        }

        foreach (var subject in missing)
        {
            if (found.TryGetValue(subject, out var key))
            {
                ring.Add(key);
                Put(_keys, (tenant, subject), key, now);
                if (Volatile.Read(ref _evictions) != evictions)
                    _keys.TryRemove((tenant, subject), out _);                         // an erasure may have come in meanwhile
            }
            else
            {
                ring.Add(SubjectKey.Erased(tenant, subject)); // no row: nothing was sealed for it (not cached: it may be created later)
            }
        }

        return ring;
    }

    private async Task<Dictionary<Guid, SubjectKey>> Read(Guid tenant, List<Guid> subjects, KeyWrap wrap, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _keyQueries);
        var found = new Dictionary<Guid, SubjectKey>();
        await using var command = _db.CreateCommand($"SELECT subject, version, wrapped FROM {S}.subject_keys WHERE tenant = $1 AND subject = ANY($2)");
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        command.Parameters.Add(new NpgsqlParameter { Value = subjects.ToArray() });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var material = new byte[SubjectKey.KeySize];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var subject = reader.GetGuid(0);
                var version = reader.GetInt32(1);
                if (reader.IsDBNull(2))
                {
                    found[subject] = SubjectKey.Erased(tenant, subject);
                    continue;
                }

                wrap.Unwrap(reader.GetFieldValue<byte[]>(2), KeyBinding.Subject(tenant, subject, version), material);
                found[subject] = new SubjectKey(tenant, subject, version, material);
            }

            CryptographicOperations.ZeroMemory(material);
        }

        return found;
    }

    private async Task Create(Guid tenant, Guid[] subjects, KeyWrap wrap, CancellationToken cancellationToken)
    {
        var wrapped = new byte[subjects.Length][];
        var material = new byte[SubjectKey.KeySize];
        for (var i = 0; i < subjects.Length; i++)
        {
            RandomNumberGenerator.Fill(material);
            wrapped[i] = wrap.Wrap(material, KeyBinding.Subject(tenant, subjects[i], 1));
        }

        CryptographicOperations.ZeroMemory(material);
        await using var command = _db.CreateCommand($"""
            INSERT INTO {S}.subject_keys (tenant, subject, version, wrapped, created_at)
            SELECT $1, s, 1, w, $4 FROM unnest($2::uuid[], $3::bytea[]) AS u(s, w)
            ON CONFLICT (tenant, subject) DO NOTHING
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        command.Parameters.Add(new NpgsqlParameter { Value = subjects });
        command.Parameters.Add(new NpgsqlParameter { Value = wrapped, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter { Value = _time.GetUtcNow() });
        var created = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (created > 0 && _log is not null)
            LogCreated(_log, created, tenant);
    }

    /// <summary>The tenant's unwrapped key (created when <paramref name="create"/>), or <see langword="null"/> when erased or missing.</summary>
    private async Task<byte[]?> TenantKey(Guid tenant, bool create, CancellationToken cancellationToken)
    {
        var now = _time.GetTimestamp();
        if (_tenants.TryGetValue(tenant, out var cached) && !Idle(cached, now) && (cached.Value is not null || !create || cached.Erased))
        {
            cached.Touch(now);
            return cached.Value;
        }

        if (create)
        {
            var material = RandomNumberGenerator.GetBytes(SubjectKey.KeySize);
            await using var insert = _db.CreateCommand($"""
                INSERT INTO {S}.tenant_keys (tenant, key_id, wrapped, created_at) VALUES ($1, $2, $3, $4)
                ON CONFLICT (tenant) DO NOTHING
                """);
            insert.Parameters.Add(new NpgsqlParameter { Value = tenant });
            insert.Parameters.Add(new NpgsqlParameter { Value = _master.KeyId });
            insert.Parameters.Add(new NpgsqlParameter { Value = _master.Wrap(material, KeyBinding.Tenant(tenant)) });
            insert.Parameters.Add(new NpgsqlParameter { Value = _time.GetUtcNow() });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(material);
        }

        var evictions = Volatile.Read(ref _evictions);
        await using var select = _db.CreateCommand($"SELECT key_id, wrapped FROM {S}.tenant_keys WHERE tenant = $1");
        select.Parameters.Add(new NpgsqlParameter { Value = tenant });
        var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null; // never sealed anything (not cached: it may be created later)
            if (reader.IsDBNull(1))
            {
                _tenants[tenant] = new Cached<byte[]?>(null, now) { Erased = true };
                return null;
            }

            var key = new byte[SubjectKey.KeySize];
            _master.Unwrap(reader.GetString(0), reader.GetFieldValue<byte[]>(1), KeyBinding.Tenant(tenant), key);
            _tenants[tenant] = new Cached<byte[]?>(key, now);
            if (Volatile.Read(ref _evictions) != evictions)
                _tenants.TryRemove(tenant, out _);                                      // an erasure may have come in meanwhile
            return key;
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> BlindIndex(Guid tenant, string lookup, string value, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(value);
        _ = StartListening(CancellationToken.None);
        return await TenantKey(tenant, create, cancellationToken).ConfigureAwait(false) is { } key ? KeyBinding.BlindIndex(key, lookup, value) : null;
    }

    // -------------------------------------------------------------------------------------------------------- erasure

    /// <inheritdoc />
    public async ValueTask<bool> EraseSubject(Guid tenant, Guid subject, CancellationToken cancellationToken)
    {
        // the tombstone and the NOTIFY in one statement: no process can miss an erasure that committed
        await using var command = _db.CreateCommand($"""
            WITH e AS (INSERT INTO {S}.subject_keys AS k (tenant, subject, version, wrapped, created_at, erased_at) VALUES ($1, $2, 1, NULL, $3, $3)
                       ON CONFLICT (tenant, subject) DO UPDATE SET wrapped = NULL, erased_at = $3 WHERE k.erased_at IS NULL
                       RETURNING 1)
            SELECT pg_notify($4, $5) FROM e
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        command.Parameters.Add(new NpgsqlParameter { Value = subject });
        command.Parameters.Add(new NpgsqlParameter { Value = _time.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter { Value = Channel });
        command.Parameters.Add(new NpgsqlParameter { Value = $"{tenant:N}:{subject:N}" });
        var erased = await Rows(command, cancellationToken).ConfigureAwait(false) > 0;
        Evict(tenant, subject, "erased here");
        if (erased)
        {
            if (_log is not null)
                LogErased(_log, tenant);
            Erased?.Invoke(this, new KeyErasedEventArgs(tenant, subject));
        }

        return erased;
    }

    /// <inheritdoc />
    public async ValueTask<bool> EraseTenant(Guid tenant, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"""
            WITH e AS (INSERT INTO {S}.tenant_keys AS t (tenant, key_id, wrapped, created_at, erased_at) VALUES ($1, NULL, NULL, $2, $2)
                       ON CONFLICT (tenant) DO UPDATE SET key_id = NULL, wrapped = NULL, erased_at = $2 WHERE t.erased_at IS NULL
                       RETURNING 1)
            SELECT pg_notify($3, $4) FROM e
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        command.Parameters.Add(new NpgsqlParameter { Value = _time.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter { Value = Channel });
        command.Parameters.Add(new NpgsqlParameter { Value = $"{tenant:N}:" });
        var erased = await Rows(command, cancellationToken).ConfigureAwait(false) > 0;
        Evict(tenant, null, "erased here");
        if (erased)
        {
            if (_log is not null)
                LogErased(_log, tenant);
            Erased?.Invoke(this, new KeyErasedEventArgs(tenant, null));
        }

        return erased;
    }

    /// <inheritdoc />
    public async ValueTask<int> Rewrap(CancellationToken cancellationToken)
    {
        var stale = new List<(Guid Tenant, string KeyId, byte[] Wrapped)>();
        await using (var select = _db.CreateCommand($"SELECT tenant, key_id, wrapped FROM {S}.tenant_keys WHERE wrapped IS NOT NULL AND key_id <> $1"))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = _master.KeyId });
            var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    stale.Add((reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2)));
        }

        var count = 0;
        var material = new byte[SubjectKey.KeySize];
        foreach (var (tenant, keyId, wrapped) in stale)
        {
            _master.Unwrap(keyId, wrapped, KeyBinding.Tenant(tenant), material);
            await using var update = _db.CreateCommand($"UPDATE {S}.tenant_keys SET key_id = $2, wrapped = $3 WHERE tenant = $1 AND key_id = $4 AND wrapped IS NOT NULL");
            update.Parameters.Add(new NpgsqlParameter { Value = tenant });
            update.Parameters.Add(new NpgsqlParameter { Value = _master.KeyId });
            update.Parameters.Add(new NpgsqlParameter { Value = _master.Wrap(material, KeyBinding.Tenant(tenant)) });
            update.Parameters.Add(new NpgsqlParameter { Value = keyId });
            count += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        CryptographicOperations.ZeroMemory(material);
        return count;
    }

    /// <summary>Drops every cached key (tests; after a lost listener connection).</summary>
    public void ClearCache()
    {
        Interlocked.Increment(ref _evictions);
        var count = _keys.Count;
        _keys.Clear();
        _tenants.Clear();
        if (_log is not null && count > 0)
            LogEvicted(_log, count, "cache cleared");
    }

    private void Evict(Guid tenant, Guid? subject, string reason)
    {
        Interlocked.Increment(ref _evictions);
        var count = 0;
        if (subject is { } s)
        {
            count += _keys.TryRemove((tenant, s), out _) ? 1 : 0;
        }
        else
        {
            _tenants.TryRemove(tenant, out _);
            foreach (var key in _keys.Keys.Where(k => k.Tenant == tenant).ToArray())
                count += _keys.TryRemove(key, out _) ? 1 : 0;
        }

        if (_log is not null)
            LogEvicted(_log, count, reason);
    }

    // ------------------------------------------------------------------------------------------------------- listener

    private async Task Listen(CancellationToken stop)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var connection = await _db.OpenConnectionAsync(stop).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    connection.Notification += OnNotification;
                    await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                        await listen.ExecuteNonQueryAsync(stop).ConfigureAwait(false);
                    ClearCache(); // anything cached before this LISTEN may have missed an erasure
                    _listening.TrySetResult();
                    delay = TimeSpan.FromMilliseconds(250);
                    while (!stop.IsCancellationRequested)
                        await connection.WaitAsync(stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or IOException or TimeoutException)
            {
                ClearCache();
                if (_listening.Task.IsCompleted)
                    _listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    await Task.Delay(delay, _time, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        var parts = e.Payload.Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tenant))
            return;
        Guid? subject = Guid.TryParseExact(parts[1], "N", out var s) ? s : null;
        Evict(tenant, subject, "erased elsewhere");
        Erased?.Invoke(this, new KeyErasedEventArgs(tenant, subject));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is { } listener)
        {
            try
            {
                await listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        _stop.Dispose();
    }

    // ----------------------------------------------------------------------------------------------------------- cache

    private SubjectKey? Fresh(ConcurrentDictionary<(Guid, Guid), Cached<SubjectKey>> cache, (Guid, Guid) key, long now)
    {
        if (!cache.TryGetValue(key, out var entry))
            return null;
        if (Idle(entry, now))
        {
            cache.TryRemove(key, out _);
            return null;
        }

        entry.Touch(now);
        return entry.Value;
    }

    private void Put(ConcurrentDictionary<(Guid, Guid), Cached<SubjectKey>> cache, (Guid, Guid) key, SubjectKey value, long now)
    {
        cache[key] = new Cached<SubjectKey>(value, now);
        if (cache.Count <= _options.MaxCachedKeys)
            return;
        // over the bound: drop the least recently used tenth (of a snapshot: other threads keep adding meanwhile)
        var drop = cache.ToArray().OrderBy(static e => e.Value.LastUsed).Take(Math.Max(1, _options.MaxCachedKeys / 10)).Select(static e => e.Key).ToArray();
        foreach (var k in drop)
            cache.TryRemove(k, out _);
        if (_log is not null)
            LogEvicted(_log, drop.Length, "cache bound");
    }

    private bool Idle<T>(Cached<T> entry, long now) => _time.GetElapsedTime(entry.LastUsed, now) > _options.IdleTime;

    private static async Task<int> Rows(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = 0;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows++;
        return rows;
    }

    private sealed class Cached<T>(T value, long lastUsed)
    {
        private long _lastUsed = lastUsed;

        public T Value { get; } = value;

        public bool Erased { get; init; }

        public long LastUsed => Volatile.Read(ref _lastUsed);

        public void Touch(long now) => Volatile.Write(ref _lastUsed, now);
    }

    [LoggerMessage(EventId = 8006, Level = LogLevel.Debug, Message = "Created {Count} data key(s) in tenant {Tenant}")]
    private static partial void LogCreated(ILogger logger, int count, Guid tenant);

    [LoggerMessage(EventId = 8007, Level = LogLevel.Debug, Message = "Erased a key of tenant {Tenant}")]
    private static partial void LogErased(ILogger logger, Guid tenant);

    [LoggerMessage(EventId = 8008, Level = LogLevel.Debug, Message = "Evicted {Count} cached key(s): {Reason}")]
    private static partial void LogEvicted(ILogger logger, int count, string reason);
}
