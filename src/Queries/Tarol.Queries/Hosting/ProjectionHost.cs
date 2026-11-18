using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    /// <summary>Creates a host. A host outside Tarol.Queries (search, step 18) derives from this class and implements
    /// <see cref="Prepare"/>, <see cref="LoadCheckpoint"/> and <see cref="Write"/>; the loop, gate, meters and waits are the same.</summary>
    /// <param name="name">The projection's name: the key of its checkpoint and of its gate.</param>
    /// <param name="version">Its version (≥ 1).</param>
    /// <param name="feed">The store's feed.</param>
    /// <param name="options">Batch, poll and backoff; <see langword="null"/> = the defaults.</param>
    /// <param name="time">The clock of waits; <see langword="null"/> = <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">The logger; <see langword="null"/> = none.</param>
    /// <param name="signal">Wakes the host after a commit; <see langword="null"/> = poll only.</param>
    /// <param name="gate">Lets one runner of the version work (step 15); <see langword="null"/> = every host runs.</param>
    /// <param name="meters">Creates the meter <see cref="MeterName"/>; <see langword="null"/> = a shared meter.</param>
    protected ProjectionHost(string name, int version, IEventFeed feed, ProjectionOptions? options, TimeProvider? time, ILogger? logger,
        IFeedSignal? signal, IProjectionGate? gate, IMeterFactory? meters)
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
        _runnerGate = gate;
        var meter = meters?.Create(MeterName) ?? SharedMeter;
        _events = meter.CreateCounter<long>("tarol.projections.events", "{event}", "Feed events in projection batches, by outcome (written, refused).");
        _batches = meter.CreateCounter<long>("tarol.projections.batches", "{batch}", "Projection batches, by outcome (written, refused).");
        _batchDuration = meter.CreateHistogram<double>("tarol.projections.batch.duration", "s", "Duration of a projection batch: read, apply, write.");
    }

    /// <summary>The name of the projections' meter (step 15).</summary>
    public const string MeterName = "Tarol.Queries";

    private static readonly Meter SharedMeter = new(MeterName);
    private readonly IProjectionGate? _runnerGate;
    private readonly Counter<long> _events;
    private readonly Counter<long> _batches;
    private readonly Histogram<double> _batchDuration;
    private IProjectionGateLease? _lease;

    /// <summary>
    /// Whether this host runs batches: it holds the projection's gate (<see cref="IProjectionGate"/>, step 15), or there is no
    /// gate. A host without the gate waits and asks again every <see cref="IProjectionGate.RetryEvery"/>.
    /// </summary>
    public bool IsRunner => _runnerGate is null || Volatile.Read(ref _lease) is not null;

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
        var started = Stopwatch.GetTimestamp();
        using var activity = Source.StartActivity(ActivityKind.Internal, default(ActivityContext), name: "project " + Name);
        activity?.SetTag("tarol.projection", Name);
        activity?.SetTag("tarol.projection.version", Version);
        activity?.SetTag("tarol.events", events.Count);
        var (written, rows) = await Write(events, current.Position, to, state, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("tarol.rows", rows);
        var tags = new TagList { { "tarol.projection", Name }, { "tarol.outcome", written ? "written" : "refused" } };
        _batches.Add(1, tags);
        _events.Add(events.Count, tags);
        _batchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
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
        try
        {
            await Loop(poll, failures, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _lease, null) is { } lease)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Holds the gate before a batch (step 15, O-8): false = another runner works, wait and ask again.</summary>
    private async ValueTask<bool> Gate(CancellationToken cancellationToken)
    {
        if (_runnerGate is null)
            return true;
        if (_lease is { } held)
        {
            if (await held.Held(cancellationToken).ConfigureAwait(false))
                return true;
            Volatile.Write(ref _lease, null);
            await held.DisposeAsync().ConfigureAwait(false);
            GateLost(_log, Name, Version);
        }
        var lease = await _runnerGate.TryEnter(Name, Version, cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return false;
        _loaded = false;                                                               // the previous runner moved the checkpoint
        Volatile.Write(ref _lease, lease);
        GateTaken(_log, Name, Version);
        return true;
    }

    private async Task Loop(TimeSpan poll, int failures, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await Gate(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(_runnerGate!.RetryEvery, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }
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

    /// <summary>Creates what the projection stores into (idempotent); called once before the first batch.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    protected abstract ValueTask Prepare(CancellationToken cancellationToken);

    /// <summary>Reads the stored checkpoint (<see cref="GlobalPosition.Start"/> and building before the first batch).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint.</returns>
    protected abstract ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken);

    /// <summary>
    /// Applies and writes a batch with the guarded checkpoint move: the checkpoint moves from <paramref name="from"/> to
    /// <paramref name="through"/> only while it is still at <paramref name="from"/>, together with what the batch wrote.
    /// </summary>
    /// <param name="events">The feed events of the batch, in order (every stream type); empty when only the state changes.</param>
    /// <param name="from">The checkpoint the batch read after.</param>
    /// <param name="through">The position of the batch's last event (equal to <paramref name="from"/> when only the state changes).</param>
    /// <param name="state">The state after the batch.</param>
    /// <param name="cancellationToken">Cancels the batch (nothing is written).</param>
    /// <returns>Whether it was written (<see langword="false"/>: another runner moved the checkpoint, the host reads again) and how many rows changed.</returns>
    protected abstract ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition through,
        ProjectionState state, CancellationToken cancellationToken);

    [LoggerMessage(EventId = 12010, Level = LogLevel.Information, Message = "Projection {Projection} v{Version}: this host is the runner")]
    private static partial void GateTaken(ILogger logger, string projection, int version);

    [LoggerMessage(EventId = 12011, Level = LogLevel.Warning, Message = "Projection {Projection} v{Version}: the runner's gate was lost; asking again")]
    private static partial void GateLost(ILogger logger, string projection, int version);

    [LoggerMessage(EventId = 12001, Level = LogLevel.Warning, Message = "Projection {Projection} v{Version}: batch {Failures} failed; trying again in {Wait}")]
    private static partial void BatchFailed(ILogger logger, Exception exception, string projection, int version, int failures, TimeSpan wait);

    [LoggerMessage(EventId = 12002, Level = LogLevel.Information, Message = "Projection {Projection} v{Version}: another runner moved the checkpoint from {Transaction}.{Position}; reading again")]
    private static partial void Refused(ILogger logger, string projection, int version, ulong transaction, long position);

    [LoggerMessage(EventId = 12003, Level = LogLevel.Information, Message = "Projection {Projection} v{Version} is current at {Transaction}.{Position}")]
    private static partial void BecameCurrent(ILogger logger, string projection, int version, ulong transaction, long position);
}
