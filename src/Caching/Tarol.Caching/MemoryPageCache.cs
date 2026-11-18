using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Caching;

/// <summary>
/// The in-process <see cref="IPageCache"/>: a concurrent dictionary bounded by <see cref="CacheOptions.MaxEntries"/>,
/// <see cref="CacheOptions.MaxBytes"/> and <see cref="CacheOptions.IdleExpiry"/> on a <see cref="TimeProvider"/>. There is
/// no eviction thread: the idle expiry is checked on read, and a write that passes a cap trims the cache (idle entries
/// first, then the least recently used, down to 90 % of the caps). Entries are immutable, so a reader never sees a torn one.
/// Reports on the meter <see cref="MeterName"/>.
/// </summary>
public sealed class MemoryPageCache : IPageCache, IDisposable
{
    /// <summary>The meter (<c>Tarol.Caching</c>): <c>tarol.caching.hits</c>, <c>tarol.caching.misses</c>, <c>tarol.caching.evictions</c>, <c>tarol.caching.entries</c>, <c>tarol.caching.bytes</c>.</summary>
    public const string MeterName = "Tarol.Caching";

    private readonly ConcurrentDictionary<PageKey, Entry> _entries = new();
    private readonly CacheOptions _options;
    private readonly TimeProvider _time;
    private readonly long _idle;
    private readonly Lock _trim = new();
    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _evictions;
    private int _count;
    private long _bytes;
    private long _clears;

    /// <summary>Creates the cache.</summary>
    /// <param name="options">The bounds (default <see cref="CacheOptions"/>); copied.</param>
    /// <param name="time">The clock of the idle expiry.</param>
    /// <param name="meters">Creates the meter (the host's <see cref="IMeterFactory"/>); <see langword="null"/>: a meter of its own.</param>
    public MemoryPageCache(CacheOptions? options = null, TimeProvider? time = null, IMeterFactory? meters = null)
    {
        _options = (options ?? new CacheOptions()).Copy();
        _options.Validate();
        _time = time ?? TimeProvider.System;
        _idle = (long)(_options.IdleExpiry.TotalSeconds * _time.TimestampFrequency);
        _ownsMeter = meters is null;
        _meter = meters?.Create(MeterName) ?? new Meter(MeterName);
        _hits = _meter.CreateCounter<long>("tarol.caching.hits", "{page}", "List pages answered from the cache.");
        _misses = _meter.CreateCounter<long>("tarol.caching.misses", "{page}", "List pages read from the store (not cached, or the checkpoint moved).");
        _evictions = _meter.CreateCounter<long>("tarol.caching.evictions", "{page}", "Entries removed by the idle expiry or a cap.");
        _meter.CreateObservableGauge("tarol.caching.entries", () => Count, "{page}", "Cached list pages.");
        _meter.CreateObservableGauge("tarol.caching.bytes", () => Bytes, "By", "The estimated size of the cached pages.");
    }

    /// <summary>The number of entries.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The estimated size of all entries in bytes.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <inheritdoc />
    public bool TryGet<TRow>(PageKey key, GlobalPosition position, [NotNullWhen(true)] out Page<TRow>? page)
        where TRow : class
    {
        page = null;
        var tag = new KeyValuePair<string, object?>("tarol.projection", key.Projection);
        if (!_entries.TryGetValue(key, out var entry))
        {
            _misses.Add(1, tag);
            return false;
        }
        var now = _time.GetTimestamp();
        if (now - entry.Touched >= _idle)
        {
            Remove(key, entry, "idle");
            _misses.Add(1, tag);
            return false;
        }
        if (entry.Position != position || entry.Page is not Page<TRow> typed)
        {
            _misses.Add(1, tag);                                            // the checkpoint moved: the next Put replaces it
            return false;
        }
        entry.Touched = now;
        _hits.Add(1, tag);
        page = typed;
        return true;
    }

    /// <inheritdoc />
    public void Put<TRow>(PageKey key, GlobalPosition position, Page<TRow> page, long size)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size > _options.MaxBytes)
            return;
        var entry = new Entry(position, page, size, _time.GetTimestamp());
        while (true)
        {
            if (_entries.TryGetValue(key, out var old))
            {
                if (old.Position > position)
                    return;                                                  // a slower search read an older checkpoint
                if (_entries.TryUpdate(key, entry, old))
                {
                    Interlocked.Add(ref _bytes, size - old.Size);
                    break;
                }
            }
            else if (_entries.TryAdd(key, entry))
            {
                Interlocked.Increment(ref _count);
                Interlocked.Add(ref _bytes, size);
                break;
            }
        }
        if (Count > _options.MaxEntries || Bytes > _options.MaxBytes)
            Trim();
    }

    /// <inheritdoc />
    public long Clears => Interlocked.Read(ref _clears);

    /// <inheritdoc />
    public void Clear()
    {
        Interlocked.Increment(ref _clears);
        foreach (var (key, entry) in _entries)
            Remove(key, entry, null);
    }

    /// <inheritdoc />
    public void Clear(Guid tenant)
    {
        Interlocked.Increment(ref _clears);
        foreach (var (key, entry) in _entries)
            if (key.Tenant == tenant)
                Remove(key, entry, null);
    }

    /// <summary>Disposes the meter when the cache created it.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }

    private void Trim()
    {
        if (!_trim.TryEnter())
            return;                                                          // another writer is trimming
        try
        {
            var now = _time.GetTimestamp();
            foreach (var (key, entry) in _entries)
                if (now - entry.Touched >= _idle)
                    Remove(key, entry, "idle");
            var entries = (int)(_options.MaxEntries * 0.9);
            var bytes = (long)(_options.MaxBytes * 0.9);
            if (Count <= _options.MaxEntries && Bytes <= _options.MaxBytes)
                return;
            var reason = Count > _options.MaxEntries ? "entries" : "bytes";
            // the last use as of now: readers keep moving it, and a sort needs keys that hold still
            var oldest = _entries.Select(static e => (e.Key, e.Value, Touched: e.Value.Touched)).ToArray();
            Array.Sort(oldest, static (a, b) => a.Touched.CompareTo(b.Touched));
            foreach (var (key, entry, _) in oldest)
            {
                if (Count <= entries && Bytes <= bytes)
                    break;
                Remove(key, entry, reason);
            }
        }
        finally
        {
            _trim.Exit();
        }
    }

    private void Remove(PageKey key, Entry entry, string? reason)
    {
        if (!_entries.TryRemove(new KeyValuePair<PageKey, Entry>(key, entry)))
            return;                                                          // replaced or removed meanwhile
        Interlocked.Decrement(ref _count);
        Interlocked.Add(ref _bytes, -entry.Size);
        if (reason is not null)
            _evictions.Add(1, new KeyValuePair<string, object?>("tarol.projection", key.Projection), new KeyValuePair<string, object?>("reason", reason));
    }

    // immutable but for the last use; a reader sees either the old entry or the new one
    private sealed class Entry(GlobalPosition position, object page, long size, long touched)
    {
        private long _touched = touched;

        public GlobalPosition Position { get; } = position;

        public object Page { get; } = page;

        public long Size { get; } = size;

        public long Touched
        {
            get => Volatile.Read(ref _touched);
            set => Volatile.Write(ref _touched, value);
        }
    }
}
