using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Queries;

/// <summary>
/// Runs the row projection <typeparamref name="TRow"/>: per batch, the events of its stream type, the stored rows of
/// their streams, <c>TRow.Apply</c> per event (events at or below a row's version are skipped, so a replay changes
/// nothing), and one guarded write of the changed rows and the checkpoint.
/// </summary>
/// <typeparam name="TRow">The <see cref="ProjectionAttribute"/> row record.</typeparam>
public sealed class ProjectionHost<TRow> : ProjectionHost
    where TRow : class, IProjection<TRow>
{
    private readonly IReadModelStore<TRow> _store;

    /// <summary>Creates the host.</summary>
    /// <param name="feed">The store's feed.</param>
    /// <param name="store">The read store of <typeparamref name="TRow"/>.</param>
    /// <param name="options">Batch, poll and backoff.</param>
    /// <param name="time">The clock of waits; default <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Logs failed and refused batches; <see langword="null"/> = none.</param>
    /// <param name="signal">Wakes the host after a commit; <see langword="null"/> = poll.</param>
    /// <param name="gate">Lets one runner of the version work (step 15); <see langword="null"/> = every host runs.</param>
    /// <param name="meters">Creates the meter <see cref="ProjectionHost.MeterName"/>; <see langword="null"/> = a shared meter.</param>
    public ProjectionHost(IEventFeed feed, IReadModelStore<TRow> store, ProjectionOptions? options = null, TimeProvider? time = null,
        ILogger<ProjectionHost<TRow>>? logger = null, IFeedSignal? signal = null, IProjectionGate? gate = null, IMeterFactory? meters = null)
        : base(TRow.Name, TRow.Version, feed, options, time, logger, signal, gate, meters)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>The read store.</summary>
    public IReadModelStore<TRow> Store => _store;

    /// <summary>Test hook: called with the number of changed rows after they were computed and before the write; true throws (a crash).</summary>
    internal Func<int, bool>? CrashBeforeWrite { get; set; }

    private protected override ValueTask Prepare(CancellationToken cancellationToken) => _store.Prepare(cancellationToken);

    private protected override ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken) => _store.Checkpoint(cancellationToken);

    private protected override async ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from,
        GlobalPosition to, ProjectionState state, CancellationToken cancellationToken)
    {
        var streamType = TRow.StreamType;
        var keys = new HashSet<RowKey>();
        foreach (var e in events)
            if (e.StreamType == streamType)
                keys.Add(KeyOf(e));
        var stored = keys.Count == 0
            ? (IReadOnlyDictionary<RowKey, Versioned<TRow>>)new Dictionary<RowKey, Versioned<TRow>>()
            : await _store.Load(keys, cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<RowKey, Versioned<TRow>>(stored);
        var changed = new Dictionary<RowKey, Versioned<TRow>>();
        foreach (var e in events)
        {
            if (e.StreamType != streamType)
                continue;
            var key = KeyOf(e);
            rows.TryGetValue(key, out var current);
            var version = e.Event.Event.Version;
            if (current is not null && version <= current.Version)
                continue;                                                              // applied before (a replay): idempotent by stream version
            var next = TRow.Apply(current?.Row, e.Event);
            if (next is null || ReferenceEquals(next, current?.Row))
                continue;                                                              // no row yet, or an event the projection ignores
            var versioned = new Versioned<TRow>(key, version, next);
            rows[key] = versioned;
            changed[key] = versioned;
        }
        if (CrashBeforeWrite?.Invoke(changed.Count) == true)
            throw new InvalidOperationException("Test: crash before the write.");
        var written = await _store.Write(new ProjectionBatch<TRow>([.. changed.Values], from, to, state), cancellationToken).ConfigureAwait(false);
        return (written, changed.Count);
    }

    internal static RowKey KeyOf(PositionedEvent e)
        => new(e.Event.Context.Tenant.TryGetValue(out Id<ITenant> tenant) ? tenant.Value : Guid.Empty, e.Event.StreamId);
}
