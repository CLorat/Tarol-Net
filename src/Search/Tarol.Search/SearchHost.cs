using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.StronglyTypedId;

namespace Tarol.Search;

/// <summary>
/// Feeds the search index from the event feed (GS-4): a projection host named <c>search</c> at the registry's version. Per
/// batch it loads each changed stream of a searchable type once, maps it to documents and lets the index replace the stream's
/// documents and move the checkpoint, guarded. Gate, meters, backoff, <see cref="ProjectionHost.CatchUp"/> and
/// <see cref="ProjectionHost.WaitFor"/> as for every projection.
/// </summary>
public sealed class SearchHost : ProjectionHost
{
    /// <summary>The meter of search (<c>tarol.search.*</c>).</summary>
    public new const string MeterName = "Tarol.Search";

    private static readonly Meter SharedMeter = new(MeterName);
    private readonly SearchCollector _collector;
    private readonly ISearchIndex _index;
    private readonly Counter<long> _documents;

    /// <summary>Creates the host.</summary>
    /// <param name="registry">The searchable aggregates and the version.</param>
    /// <param name="index">The index.</param>
    /// <param name="feed">The store's feed.</param>
    /// <param name="reader">Loads the changed aggregates.</param>
    /// <param name="tokens">Blinds exact values; <see langword="null"/> = without a key store.</param>
    /// <param name="time">The clock; <see langword="null"/> = the system clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="signal">Wakes the host after a commit.</param>
    /// <param name="gate">Lets one runner of the version work.</param>
    /// <param name="meters">Creates the meters.</param>
    public SearchHost(SearchRegistry registry, ISearchIndex index, IEventFeed feed, IAggregateReader reader, SearchTokens? tokens = null, TimeProvider? time = null,
        ILogger<SearchHost>? logger = null, IFeedSignal? signal = null, IProjectionGate? gate = null, IMeterFactory? meters = null)
        : base(SearchIndexDefinition.Name, NotNull(registry).Version, feed, registry.Projection, time, logger, signal, gate, meters)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(reader);
        _index = index;
        _collector = new SearchCollector(registry, reader, tokens ?? new SearchTokens(), time ?? TimeProvider.System);
        var meter = meters?.Create(MeterName) ?? SharedMeter;
        _documents = meter.CreateCounter<long>("tarol.search.documents", "{document}", "Search documents written by the host.");
    }

    /// <summary>The index the host writes.</summary>
    public ISearchIndex Index => _index;

    private static SearchRegistry NotNull(SearchRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry;
    }

    /// <inheritdoc />
    protected override ValueTask Prepare(CancellationToken cancellationToken) => _index.Prepare(_collector.Registry.Definition(), cancellationToken);

    private Task? _prepared;

    /// <summary>
    /// Prepares the index once for writes outside the feed (erasure, retention). A host whose gate never opens (an API process:
    /// one runner per version, elsewhere) never ran <see cref="Prepare"/>, and an index resolves its version's table there; a
    /// failed preparation is retried by the next call.
    /// </summary>
    private async Task Prepared(CancellationToken cancellationToken)
    {
        var prepared = _prepared;
        if (prepared is null || prepared.IsFaulted || prepared.IsCanceled)
        {
            prepared = _index.Prepare(_collector.Registry.Definition(), cancellationToken).AsTask();
            _prepared = prepared;
        }
        await prepared.ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken) => _index.Checkpoint(cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition through,
        ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var streams = new List<(Guid, string, Guid)>();
        var seen = new HashSet<(Guid, Guid)>();
        foreach (var e in events)
        {
            if (!_collector.Covers(e.StreamType))
                continue;
            var tenant = e.Event.Context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty;
            if (seen.Add((tenant, e.Event.StreamId)))
                streams.Add((tenant, e.StreamType, e.Event.StreamId));
        }
        var collected = await _collector.Collect(streams, cancellationToken).ConfigureAwait(false);
        var written = await _index.Write(new SearchBatch(collected, from, through, state), cancellationToken).ConfigureAwait(false);
        var documents = collected.Sum(static s => s.Documents.Count);
        if (written && documents > 0)
            _documents.Add(documents);
        return (written, documents);
    }

    /// <summary>Re-indexes streams now, without moving the checkpoint (erasure, and an operator's repair).</summary>
    /// <param name="streams">The streams (others than searchable types are skipped).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many streams were re-indexed.</returns>
    public async Task<int> Reindex(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var mine = streams.Where(s => _collector.Covers(s.StreamType)).Select(static s => (s.Tenant, s.StreamType, s.Id)).ToList();
        if (mine.Count == 0)
            return 0;
        var collected = await _collector.Collect(mine, cancellationToken).ConfigureAwait(false);
        await Prepared(cancellationToken).ConfigureAwait(false);
        await _index.Replace(collected, cancellationToken).ConfigureAwait(false);
        return mine.Count;
    }

    /// <summary>Removes the documents of streams (retention deleted them).</summary>
    /// <param name="streams">The streams.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public async Task Remove(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var gone = streams.Where(s => _collector.Covers(s.StreamType)).Select(static s => new StreamDocuments(s.Tenant, s.Id, 0, [])).ToList();
        if (gone.Count > 0)
        {
            await Prepared(cancellationToken).ConfigureAwait(false);
            await _index.Replace(gone, cancellationToken).ConfigureAwait(false);
        }
    }
}
