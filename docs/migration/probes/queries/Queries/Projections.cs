using System.Diagnostics;
using Tarol.Persistence;
using Tarol.Primitives;

namespace Probe.Queries;

// ── The would-be Tarol.Queries runtime: projection port, host, checkpoints (candidate P-A: a loop per projection on the feed) ──

/// <summary>A row of a read model with the stream version it reflects (per-stream documents: key = (tenant, stream id)).</summary>
public sealed record Versioned<TRow>(Guid Tenant, Guid Id, long Version, TRow Row);

/// <summary>What the generator would write for a declared projection: the dispatch switch over its events (no reflection).</summary>
public interface IProjection<TRow> where TRow : class
{
    string Name { get; }
    int Version { get; }
    /// <summary>The stream type the projection reads (the feed carries every type; filtered here, in memory).</summary>
    string StreamType { get; }
    /// <summary>The new row, the same row (event not handled), or null (no row: e.g. an event before the creation).</summary>
    TRow? Apply(TRow? row, PositionedEvent e);
}

/// <summary>Where the rows and the checkpoint live; Write stores both atomically, guarded by the checkpoint it started from.</summary>
public interface IReadModelStore<TRow> where TRow : class
{
    Task Ensure(CancellationToken ct);
    Task<GlobalPosition> Checkpoint(CancellationToken ct);
    Task<Dictionary<(Guid, Guid), Versioned<TRow>>> Load(IReadOnlyCollection<(Guid Tenant, Guid Id)> keys, CancellationToken ct);
    /// <returns>false when another runner moved the checkpoint (nothing written).</returns>
    Task<bool> Write(IReadOnlyList<Versioned<TRow>> rows, GlobalPosition from, GlobalPosition to, CancellationToken ct);
}

public sealed class ProjectionOptions
{
    public int Batch { get; init; } = 500;
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>A wake-up source (LISTEN/NOTIFY); the poll stays as the fallback (xmin can hold a commit back after its NOTIFY).</summary>
    public Func<TimeSpan, CancellationToken, Task>? Wait { get; init; }
}

/// <summary>Runs one projection: read the feed after the checkpoint, apply per stream, write rows + checkpoint in one transaction.</summary>
public sealed class ProjectionHost<TRow> where TRow : class
{
    private readonly IEventFeed _feed;
    private readonly IProjection<TRow> _projection;
    private readonly IReadModelStore<TRow> _store;
    private readonly ProjectionOptions _options;
    private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GlobalPosition _position;
    private bool _loaded;

    public ProjectionHost(IEventFeed feed, IProjection<TRow> projection, IReadModelStore<TRow> store, ProjectionOptions? options = null)
        => (_feed, _projection, _store, _options) = (feed, projection, store, options ?? new());

    public GlobalPosition Position => _position;
    public long Conflicts { get; private set; }
    public long Applied { get; private set; }
    /// <summary>(checkpoint, when) after each batch, for the lag measurement.</summary>
    public List<(GlobalPosition Position, long Ticks)>? Trace { get; set; }
    /// <summary>Test hook: throws after the rows were computed and before the write (a crash).</summary>
    public Func<int, bool>? CrashBeforeWrite { get; set; }

    /// <summary>One batch; returns the number of feed events read (0 = caught up).</summary>
    public async Task<int> RunOnce(CancellationToken ct = default)
    {
        if (!_loaded) { _position = await _store.Checkpoint(ct); _loaded = true; }
        var from = _position;
        var events = new List<PositionedEvent>(_options.Batch);
        await foreach (var e in _feed.ReadAll(from, _options.Batch, ct))
            events.Add(e);
        if (events.Count == 0)
            return 0;
        var mine = events.Where(e => e.StreamType == _projection.StreamType).ToList();
        var keys = mine.Select(e => (Tenant(e), e.Event.StreamId)).Distinct().ToList();
        var rows = keys.Count == 0 ? [] : await _store.Load(keys, ct);
        var changed = new Dictionary<(Guid, Guid), Versioned<TRow>>();
        foreach (var e in mine)
        {
            var key = (Tenant(e), e.Event.StreamId);
            rows.TryGetValue(key, out var current);
            if (current is not null && e.Event.Event.Version <= current.Version)
                continue;                                              // already applied (at-least-once replay): idempotent by stream version
            var next = _projection.Apply(current?.Row, e);
            if (next is null)
                continue;
            var v = new Versioned<TRow>(key.Item1, key.Item2, e.Event.Event.Version, next);
            rows[key] = v;
            changed[key] = v;
        }
        var to = events[^1].Position;
        if (CrashBeforeWrite?.Invoke(changed.Count) == true)
            throw new InvalidOperationException("probe: crash before the write");
        if (!await _store.Write([.. changed.Values], from, to, ct))
        {
            Conflicts++;                                               // another runner moved the checkpoint: reload and re-read
            _loaded = false;
            return events.Count;
        }
        Applied += mine.Count;
        _position = to;
        Trace?.Add((to, Stopwatch.GetTimestamp()));
        Interlocked.Exchange(ref _moved, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return events.Count;
    }

    public async Task<long> CatchUp(CancellationToken ct = default)
    {
        long n = 0;
        int read;
        while ((read = await RunOnce(ct)) > 0)
            n += read;
        return n;
    }

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int read;
            try { read = await RunOnce(ct); }
            catch (OperationCanceledException) { return; }
            if (read == _options.Batch)
                continue;
            try
            {
                if (_options.Wait is { } wait) await wait(_options.Poll, ct);
                else await Task.Delay(_options.Poll, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Read-your-writes in process: waits until the checkpoint passed <paramref name="token"/>; false on timeout.</summary>
    public async Task<bool> WaitFor(GlobalPosition token, TimeSpan timeout, CancellationToken ct = default)
    {
        var end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (_position < token)
        {
            var left = end - Stopwatch.GetTimestamp();
            if (left <= 0)
                return false;
            try { await _moved.Task.WaitAsync(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency), ct); }
            catch (TimeoutException) { return _position >= token; }
        }
        return true;
    }

    internal static Guid Tenant(PositionedEvent e) => e.Event.Context.Tenant.TryGetValue(out Tarol.StronglyTypedId.Id<Tarol.Kernel.ITenant> t) ? t.Value : Guid.Empty;
}
