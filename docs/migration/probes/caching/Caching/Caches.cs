using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Caching;

// ───────────────────────────────────────────── the would-be runtime: nothing here is package code ─────────────

/// <summary>An in-process cache with TimeProvider-driven expiry and a hard entry cap (no eviction policy: full = no insert).</summary>
internal sealed class LocalCache<TKey, TValue>(TimeProvider time, TimeSpan ttl, int maxEntries = 1_000_000) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TValue Value, long Expires)> _entries = new();
    public long Hits, Misses;
    public int Count => _entries.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var e) && time.GetTimestamp() < e.Expires) { Interlocked.Increment(ref Hits); value = e.Value; return true; }
        Interlocked.Increment(ref Misses); value = default!; return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_entries.Count >= maxEntries && !_entries.ContainsKey(key)) return;
        _entries[key] = (value, time.GetTimestamp() + (long)(ttl.TotalSeconds * time.TimestampFrequency));
    }

    public void Remove(TKey key) => _entries.TryRemove(key, out _);
    public void Clear() => _entries.Clear();
}

internal static class Projection
{
    public static string Name<T>() where T : class, IProjection<T> => T.Name;
    public static int Version<T>() where T : class, IProjection<T> => T.Version;
}

internal readonly record struct StreamKey(Guid Tenant, string Type, Guid Id);
internal readonly record struct LookupKey(Guid Tenant, string Type, string Lookup, string Key);
internal sealed record CachedState(long Version, byte[] State);

/// <summary>
/// Candidate (b): an aggregate cache as a decorator of the store port. It caches the stored state's bytes (never the
/// mutable instance: every hit is a fresh aggregate), keyed by (tenant, stream type, id) and stamped with the version.
/// Write-through after a successful Append, evicted on a VersionConflict. <paramref name="cacheFind"/> adds candidate (c).
/// </summary>
internal sealed class CachingStore(IAggregateStore inner, EventCodec codec, TimeProvider time, TimeSpan ttl, bool cacheFind = false, bool evictOnConflict = true) : IAggregateStore
{
    public LocalCache<StreamKey, CachedState> States { get; } = new(time, ttl);
    public LocalCache<LookupKey, Guid> Lookups { get; } = new(time, ttl);
    public long Conflicts;
    private static Guid TenantOf(OperationContext c) => c.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty;

    public async ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var key = new StreamKey(TenantOf(context), codec.StreamTypeOf<T>(), id.Value);
        if (T.StateSchema != 0 && States.TryGet(key, out var hit) && AggregateState.Read(id, hit.State, Lab.Json).TryGetValue(out T? fresh))
            return fresh;                                                             // a new instance per hit
        var loaded = await inner.Load(id, context, cancellationToken);
        if (T.StateSchema != 0 && loaded.TryGetValue(out T? aggregate))
            States.Set(key, new CachedState(aggregate.Version, AggregateState.Write(aggregate, Lab.Json)));
        return loaded;
    }

    public async ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var k = new LookupKey(TenantOf(context), codec.StreamTypeOf<T>(), lookup, key);
        if (cacheFind && Lookups.TryGet(k, out var id)) return (Id<T>)id;
        var found = await inner.Find<T>(lookup, key, context, cancellationToken);
        if (cacheFind && found.TryGetValue(out Id<T> f)) Lookups.Set(k, f.Value);
        return found;
    }

    public async ValueTask<Result<GlobalPosition>> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken) where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var skey = new StreamKey(TenantOf(context), codec.StreamTypeOf<T>(), aggregate.Id.Value);
        var appended = await inner.Append(aggregate, expectedVersion, context, key, cancellationToken);
        if (appended.IsSuccess && T.StateSchema != 0)
            States.Set(skey, new CachedState(aggregate.Version, AggregateState.Write(aggregate, Lab.Json)));   // write-through
        else if (StoreErrors.IsVersionConflict(appended))
        {
            Interlocked.Increment(ref Conflicts);
            if (evictOnConflict) States.Remove(skey);                                 // the next load reads the store
        }
        return appended;
    }

    public ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken) => inner.Seen(key, cancellationToken);
}

internal enum Validation { Checkpoint, Pushed, Ttl, TenantTag }

/// <summary>
/// Candidate (a): a list-page cache. The entry is the answer's bytes + ETag, keyed by (tenant, projection, version, the
/// canonical query) and stamped with what validates it: the projection's checkpoint (read from the database, or pushed by
/// NOTIFY), a TTL, or a per-tenant generation bumped by the projection's batches.
/// </summary>
internal sealed class PageCache(IReadModelStore<AuctionRow> store, Validation mode, TimeProvider time, TimeSpan ttl,
    CheckpointWatch? watch = null, TenantGenerations? tenants = null)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    public long Hits, Misses;
    private sealed record Entry(GlobalPosition At, long Generation, long Stored, byte[] Body, string ETag);
    public sealed record Answer(byte[] Body, string ETag, GlobalPosition Position, bool Hit);

    public static string Key(ListQuery q)
    {
        var sb = new StringBuilder(128).Append(q.Tenant.ToString("N")).Append('|').Append(Projection.Name<AuctionRow>()).Append("|v").Append(Projection.Version<AuctionRow>())
            .Append('|').Append(q.Sort).Append(q.Descending ? "-" : "+").Append('|').Append(q.After).Append('|').Append(q.Offset)
            .Append('|').Append(q.Limit).Append('|').Append(q.Count ? 'c' : '-');
        foreach (var f in q.Filters.OrderBy(f => f.Column, StringComparer.Ordinal))
            sb.Append('|').Append(f.Column).Append('=').Append(Convert.ToString(f.Value, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public async ValueTask<Answer> Get(ListQuery q, CancellationToken ct = default)
    {
        var key = Key(q);
        var current = GlobalPosition.Start;
        long generation = 0;
        switch (mode)
        {
            case Validation.Checkpoint: current = (await store.Checkpoint(ct)).Position; break;   // one round trip
            case Validation.Pushed: current = watch!.Position; break;                         // none
            case Validation.TenantTag: generation = tenants!.Of(q.Tenant); break;             // none
        }
        if (_entries.TryGetValue(key, out var e))
        {
            var valid = mode switch
            {
                Validation.Checkpoint or Validation.Pushed => e.At == current,
                Validation.TenantTag => e.Generation == generation,
                _ => time.GetTimestamp() - e.Stored < (long)(ttl.TotalSeconds * time.TimestampFrequency),
            };
            if (valid) { Interlocked.Increment(ref Hits); return new Answer(e.Body, e.ETag, e.At, true); }
        }
        Interlocked.Increment(ref Misses);
        var page = (await store.Search(q, ct)).GetValueOrThrow();
        var body = Serialize(page);
        // stamped with what was read BEFORE the search (pushed/tag) or the page's own checkpoint: a batch during the search makes it miss next time
        var at = mode == Validation.Pushed ? current : page.Position;
        _entries[key] = new Entry(at, generation, time.GetTimestamp(), body, page.ETag);
        return new Answer(body, page.ETag, page.Position, false);
    }

    public static byte[] Serialize(Page<AuctionRow> page) => JsonSerializer.SerializeToUtf8Bytes(new PageBody(page.Items, page.Next), ProbeJson.Default.PageBody);
}

/// <summary>Keeps a projection's checkpoint pushed into the API process: LISTEN on the checkpoint trigger, poll as a fallback.</summary>
internal sealed class CheckpointWatch : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private GlobalPosition _position;
    public long Notifications, Polls;
    public GlobalPosition Position { get { lock (_stop) return _position; } }

    public CheckpointWatch(NpgsqlDataSource db, string schema, IReadModelStore<AuctionRow> store, TimeSpan poll)
    {
        _loop = Task.Run(async () =>
        {
            await using var conn = await db.OpenConnectionAsync(_stop.Token);
            conn.Notification += (_, n) =>
            {
                var p = n.Payload.Split(':');
                if (p[0] == Projection.Name<AuctionRow>() && p[1] == Projection.Version<AuctionRow>().ToString(CultureInfo.InvariantCulture))
                { Interlocked.Increment(ref Notifications); Advance(new GlobalPosition(ulong.Parse(p[2], CultureInfo.InvariantCulture), long.Parse(p[3], CultureInfo.InvariantCulture))); }
            };
            await using (var listen = new NpgsqlCommand($"LISTEN {schema}_checkpoint", conn)) await listen.ExecuteNonQueryAsync(_stop.Token);
            Advance((await store.Checkpoint(_stop.Token)).Position);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (!await conn.WaitAsync(poll, _stop.Token)) { Interlocked.Increment(ref Polls); Advance((await store.Checkpoint(_stop.Token)).Position); }
                }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private void Advance(GlobalPosition p) { lock (_stop) if (p > _position) _position = p; }
    public async ValueTask DisposeAsync() { await _stop.CancelAsync(); try { await _loop; } catch (OperationCanceledException) { } }
}

/// <summary>A generation per tenant, bumped for every tenant a projection batch wrote rows of (in process, and by NOTIFY across processes).</summary>
internal sealed class TenantGenerations
{
    private readonly ConcurrentDictionary<Guid, long> _gen = new();
    public long Of(Guid tenant) => _gen.GetValueOrDefault(tenant);
    public void Bump(Guid tenant) => _gen.AddOrUpdate(tenant, 1, static (_, g) => g + 1);
}

/// <summary>Decorates the projection's store: after a written batch, bumps the generation of each tenant it touched (+ NOTIFY for other processes).</summary>
internal sealed class TaggingStore(IReadModelStore<AuctionRow> inner, TenantGenerations local, NpgsqlDataSource? db = null, string? channel = null,
    Func<Guid[], Task>? after = null) : IReadModelStore<AuctionRow>
{
    public ValueTask Prepare(CancellationToken ct) => inner.Prepare(ct);
    public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken ct) => inner.Checkpoint(ct);
    public ValueTask<IReadOnlyDictionary<RowKey, Versioned<AuctionRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken ct) => inner.Load(keys, ct);
    public ValueTask<Versioned<AuctionRow>?> Find(RowKey key, CancellationToken ct) => inner.Find(key, ct);
    public ValueTask<Result<Page<AuctionRow>>> Search(ListQuery query, CancellationToken ct) => inner.Search(query, ct);

    public async ValueTask<bool> Write(ProjectionBatch<AuctionRow> batch, CancellationToken ct)
    {
        if (!await inner.Write(batch, ct)) return false;
        var touched = batch.Rows.Select(r => r.Key.Tenant).Distinct().ToArray();
        foreach (var t in touched) local.Bump(t);
        if (db is not null && touched.Length > 0)
        {
            await using var n = db.CreateCommand("SELECT pg_notify($1, $2)");
            n.Parameters.Add(new() { Value = channel! });
            n.Parameters.Add(new() { Value = string.Join(',', touched) });
            await n.ExecuteNonQueryAsync(ct);                                          // after the commit: visible when it arrives
        }
        if (after is not null && touched.Length > 0) await after(touched);
        return true;
    }
}

/// <summary>Receives the tenant tags in another process.</summary>
internal sealed class TenantWatch : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public TenantWatch(NpgsqlDataSource db, string channel, TenantGenerations generations)
    {
        var ready = new TaskCompletionSource();
        _loop = Task.Run(async () =>
        {
            await using var conn = await db.OpenConnectionAsync(_stop.Token);
            conn.Notification += (_, n) => { foreach (var t in n.Payload.Split(',')) generations.Bump(Guid.Parse(t)); };
            await using (var listen = new NpgsqlCommand($"LISTEN {channel}", conn)) await listen.ExecuteNonQueryAsync(_stop.Token);
            ready.SetResult();
            while (!_stop.IsCancellationRequested)
                try { await conn.WaitAsync(_stop.Token); } catch (OperationCanceledException) { break; }
        });
        ready.Task.Wait(TimeSpan.FromSeconds(10));
    }
    public async ValueTask DisposeAsync() { await _stop.CancelAsync(); try { await _loop; } catch (OperationCanceledException) { } }
}
