using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Persistence;

namespace Tarol.Operations;

/// <summary>
/// Polls the registered <see cref="OperationsSignal"/>s into an <see cref="OperationsSnapshot"/> every
/// <see cref="OperationsOptions.PollEvery"/> (O-1) and publishes each signal as an observable gauge on its meter (normally
/// <see cref="OperationsSignals.Meter"/>). A gauge callback reads the snapshot, never the database: a scrape costs about a
/// microsecond and never blocks on PostgreSQL. The health checks read the same snapshot.
/// </summary>
public sealed partial class OperationsHost : BackgroundService
{
    private readonly IReadOnlyList<OperationsSignal> _signals;
    private readonly IServiceProvider _services;
    private readonly OperationsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly List<Meter> _ownMeters = [];
    private volatile OperationsSnapshot _snapshot = OperationsSnapshot.Empty;

    /// <summary>Creates the host and its gauges.</summary>
    /// <param name="signals">The registered signals.</param>
    /// <param name="services">The services the signals read with.</param>
    /// <param name="options">Poll interval and thresholds.</param>
    /// <param name="time">The clock of the poll.</param>
    /// <param name="logger">Logs failed polls (15005); <see langword="null"/> = none.</param>
    /// <param name="meters">Creates the gauges' meters (the host's <see cref="IMeterFactory"/>); <see langword="null"/> = meters of its own.</param>
    public OperationsHost(IEnumerable<OperationsSignal> signals, IServiceProvider services, OperationsOptions options, TimeProvider time,
        ILogger<OperationsHost>? logger = null, IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        options.Validate();
        _signals = [.. signals];
        _services = services;
        _options = options;
        _time = time;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        var byMeter = new Dictionary<string, Meter>(StringComparer.Ordinal);
        foreach (var signal in _signals.DistinctBy(static s => (s.Meter, s.Name)))
        {
            if (!byMeter.TryGetValue(signal.Meter, out var meter))
            {
                meter = meters?.Create(signal.Meter) ?? new Meter(signal.Meter);
                if (meters is null)
                    _ownMeters.Add(meter);
                byMeter[signal.Meter] = meter;
            }
            var name = signal.Name;
            meter.CreateObservableGauge(name, () => Observe(name), signal.Unit, signal.Description);
        }
    }

    /// <summary>The last snapshot.</summary>
    public OperationsSnapshot Snapshot => _snapshot;

    /// <summary>The signals it polls.</summary>
    public IReadOnlyList<OperationsSignal> Signals => _signals;

    /// <summary>Reads every signal once; a signal that throws keeps its last values and fails the poll.</summary>
    /// <param name="cancellationToken">Cancels the poll.</param>
    /// <returns>The new snapshot.</returns>
    public async Task<OperationsSnapshot> PollOnce(CancellationToken cancellationToken = default)
    {
        var previous = _snapshot;
        var values = new Dictionary<string, IReadOnlyList<SignalValue>>(previous.Values, StringComparer.Ordinal);
        string? error = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.PollEvery);
        foreach (var signal in _signals)
        {
            try
            {
                values[signal.Name] = await signal.Read(_services, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error ??= $"{signal.Name}: {e.GetType().Name}: {e.Message}";
            }
        }
        var now = _time.GetUtcNow();
        var snapshot = error is null
            ? new OperationsSnapshot(values, now, now, 0, null)
            : new OperationsSnapshot(values, previous.LastSuccess, now, previous.Failures + 1, error);
        _snapshot = snapshot;
        if (error is not null)
            PollFailed(_log, snapshot.Failures, error);
        return snapshot;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await PollOnce(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_options.PollEvery, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var meter in _ownMeters)
            meter.Dispose();
        base.Dispose();
    }

    private IEnumerable<Measurement<double>> Observe(string name)
    {
        if (!_snapshot.Values.TryGetValue(name, out var values))
            yield break;
        foreach (var value in values)
            yield return value.TagName is { } tag
                ? new Measurement<double>(value.Value, new KeyValuePair<string, object?>(tag, value.TagValue))
                : new Measurement<double>(value.Value);
    }

    [LoggerMessage(EventId = 15005, Level = LogLevel.Warning, Message = "Operations snapshot: poll {Failures} in a row failed, the last values are kept: {Error}")]
    private static partial void PollFailed(ILogger logger, int failures, string error);
}
