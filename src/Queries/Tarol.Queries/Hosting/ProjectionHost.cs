using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Persistence;

namespace Tarol.Queries;

/// <summary>
/// Runs one projection version (QR-1): reads the <see cref="IEventFeed"/> after its stored checkpoint, a batch at a time,
/// and writes what the batch changed together with the checkpoint, guarded by the position the batch read from (a second
/// runner of the same projection is refused and re-reads). A hosted service: start it in the worker process, next to the
/// reaction worker. <see cref="ProjectionHost{TRow}"/> projects row records, <see cref="SqlProjectionHost"/> runs an
/// <see cref="ISqlProjection"/>.
/// </summary>
/// <remarks>
/// Every batch runs in an <see cref="ActivityKind.Internal"/> activity <c>project &lt;name&gt;</c> of
/// <see cref="ActivitySourceName"/>, linked to nothing (a batch mixes many commits). A failing batch is logged and retried
/// after a backoff; the projection stops, the decisions go on.
/// </remarks>
public abstract partial class ProjectionHost : BackgroundService
{
    /// <summary>The name of the <see cref="ActivitySource"/> of projections: add it to your tracing.</summary>
    public const string ActivitySourceName = "Tarol.Queries";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private readonly IEventFeed _feed;
    private readonly IFeedSignal? _signal;
    private readonly ProjectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProjectionCheckpoint _checkpoint;
    private bool _loaded;
    private bool _prepared;
    private long _conflicts;

    private protected ProjectionHost(string name, int version, IEventFeed feed, ProjectionOptions? options, TimeProvider? time, ILogger? logger,
        IFeedSignal? signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(feed);
        options = (options ?? new ProjectionOptions()).Copy();
        options.Validate();
        Name = name;
        Version = version;
        _feed = feed;
        _options = options;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger.Instance;
        _signal = signal;
    }

    /// <summary>The projection's name.</summary>
    public string Name { get; }

    /// <summary>The projection's version.</summary>
    public int Version { get; }

    /// <summary>The position of the last event this host handled (as far as this process knows).</summary>
    public GlobalPosition Position
    {
        get
        {
            lock (_gate)
                return _checkpoint.Position;
        }
    }

    /// <summary>Building or current (as far as this process knows).</summary>
    public ProjectionState State
    {
        get
        {
            lock (_gate)
                return _checkpoint.State;
        }
    }

    /// <summary>How many batches the checkpoint guard refused because another runner moved it.</summary>
    public long Conflicts => Interlocked.Read(ref _conflicts);

    /// <summary>Reads and writes one batch.</summary>
    /// <param name="cancellationToken">Cancels the batch (nothing is written).</param>
    /// <returns>The number of feed events read; 0 when the projection was caught up.</returns>
    public async Task<int> RunOnce(CancellationToken cancellationToken = default)
    {
        if (!_prepared)
        {
            await Prepare(cancellationToken).ConfigureAwait(false);
            _prepared = true;
        }
        if (!_loaded)
        {
            var stored = await LoadCheckpoint(cancellationToken).ConfigureAwait(false);
            lock (_gate)
                _checkpoint = stored;
            _loaded = true;
        }
        ProjectionCheckpoint current;
        lock (_gate)
            current = _checkpoint;

        var events = new List<PositionedEvent>(Math.Min(_options.Batch, 4096));
        await foreach (var e in _feed.ReadAll(current.Position, _options.Batch, cancellationToken).ConfigureAwait(false))
            events.Add(e);
        var state = events.Count < _options.Batch ? ProjectionState.Current : current.State;
        if (events.Count == 0 && state == current.State)
            return 0;

        var to = events.Count > 0 ? events[^1].Position : current.Position;
        using var activity = Source.StartActivity(ActivityKind.Internal, default(ActivityContext), name: "project " + Name);
        activity?.SetTag("tarol.projection", Name);
        activity?.SetTag("tarol.projection.version", Version);
        activity?.SetTag("tarol.events", events.Count);
        var (written, rows) = await Write(events, current.Position, to, state, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("tarol.rows", rows);
        if (!written)
        {
            Interlocked.Increment(ref _conflicts);
            activity?.SetTag("tarol.conflict", true);
            _loaded = false;                                                           // another runner moved the checkpoint: re-read
            Refused(_log, Name, Version, current.Position.Transaction, current.Position.Position);
            return events.Count == 0 ? 1 : events.Count;
        }
        TaskCompletionSource moved;
        lock (_gate)
        {
            _checkpoint = new ProjectionCheckpoint(to, state);
            moved = _moved;
            _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        moved.TrySetResult();
        if (state != current.State)
            BecameCurrent(_log, Name, Version, to.Transaction, to.Position);
        return events.Count;
    }

    /// <summary>Runs batches until the projection has reached the head of the feed (tests, one-off rebuilds).</summary>
    /// <param name="cancellationToken">Cancels the catch-up.</param>
    /// <returns>The number of feed events read.</returns>
    public async Task<long> CatchUp(CancellationToken cancellationToken = default)
    {
        long total = 0;
        for (int read; (read = await RunOnce(cancellationToken).ConfigureAwait(false)) > 0;)
            total += read;
        return total;
    }

    /// <summary>
    /// Read-your-writes in this process: waits until the projection has handled <paramref name="position"/> (a commit's
    /// position, <c>Tarol-Position</c>), up to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="position">The position to pass.</param>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when the checkpoint passed it; <see langword="false"/> on timeout.</returns>
    public async Task<bool> WaitFor(GlobalPosition position, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var end = _time.GetTimestamp() + (long)(timeout.TotalSeconds * _time.TimestampFrequency);
        while (true)
        {
            Task moved;
            lock (_gate)
            {
                if (_checkpoint.Position >= position)
                    return true;
                moved = _moved.Task;
            }
            var left = end - _time.GetTimestamp();
            if (left <= 0)
                return false;
            try
            {
                await moved.WaitAsync(TimeSpan.FromSeconds((double)left / _time.TimestampFrequency), _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return Position >= position;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = _options.PollFor(_signal is not null);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var read = await RunOnce(stoppingToken).ConfigureAwait(false);
                failures = 0;
                if (read >= _options.Batch)
                    continue;
                if (read > 0)
                    continue;                                                          // a partial batch: one more read finds nothing or the next commits
                if (_signal is not null)
                    await _signal.Wait(poll, stoppingToken).ConfigureAwait(false);
                else
                    await Task.Delay(poll, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _loaded = false;
                failures++;
                var wait = TimeSpan.FromTicks(Math.Min(_options.MaxBackoff.Ticks, _options.Backoff.Ticks * (1L << Math.Min(failures - 1, 20))));
                BatchFailed(_log, e, Name, Version, failures, wait);
                try
                {
                    await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Creates what the projection stores into (idempotent).</summary>
    private protected abstract ValueTask Prepare(CancellationToken cancellationToken);

    /// <summary>Reads the stored checkpoint.</summary>
    private protected abstract ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken);

    /// <summary>Applies and writes a batch with the guarded checkpoint move; whether it was written and how many rows changed.</summary>
    private protected abstract ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition to,
        ProjectionState state, CancellationToken cancellationToken);

    [LoggerMessage(EventId = 12001, Level = LogLevel.Warning, Message = "Projection {Projection} v{Version}: batch {Failures} failed; trying again in {Wait}")]
    private static partial void BatchFailed(ILogger logger, Exception exception, string projection, int version, int failures, TimeSpan wait);

    [LoggerMessage(EventId = 12002, Level = LogLevel.Information, Message = "Projection {Projection} v{Version}: another runner moved the checkpoint from {Transaction}.{Position}; reading again")]
    private static partial void Refused(ILogger logger, string projection, int version, ulong transaction, long position);

    [LoggerMessage(EventId = 12003, Level = LogLevel.Information, Message = "Projection {Projection} v{Version} is current at {Transaction}.{Position}")]
    private static partial void BecameCurrent(ILogger logger, string projection, int version, ulong transaction, long position);
}
