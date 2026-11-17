using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Operations;

/// <summary>
/// The admin entry's library API (O-3): the registered <see cref="IDeadLetterSource"/>s (kinds <c>reactions</c>, <c>jobs</c>,
/// <c>messages</c>) and the read models' versions (<see cref="IReadModelAdmin"/>). Every act names its actor; a database
/// source writes the audit row in the same statement, and every act is logged (15001–15004).
/// </summary>
public interface IDeadLetterAdmin
{
    /// <summary>The registered kinds.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>One page of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="query">Paging and filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    /// <exception cref="KeyNotFoundException">No source of that kind is registered.</exception>
    Task<DeadLetterPage> List(string kind, DeadLetterQuery query, CancellationToken cancellationToken);

    /// <summary>Re-drives one dead letter.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The dead letter.</param>
    /// <param name="actor">Who asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is no such dead letter (or it is re-driven already).</returns>
    /// <exception cref="KeyNotFoundException">No source of that kind is registered.</exception>
    Task<bool> Redrive(string kind, long id, string actor, CancellationToken cancellationToken);

    /// <summary>Re-drives the dead letters a filter selects, at most <paramref name="max"/> (≤ <see cref="MaxRedrive"/>), oldest first.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="filter">Tenant and name (its <c>After</c> is the start; its <c>Limit</c> is ignored).</param>
    /// <param name="max">The most to re-drive.</param>
    /// <param name="actor">Who asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many were re-driven.</returns>
    /// <exception cref="KeyNotFoundException">No source of that kind is registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is outside 1 … <see cref="MaxRedrive"/>.</exception>
    Task<int> Redrive(string kind, DeadLetterQuery filter, int max, string actor, CancellationToken cancellationToken);

    /// <summary>Deletes one dead letter for good.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="id">The dead letter.</param>
    /// <param name="actor">Who asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is no such dead letter.</returns>
    /// <exception cref="KeyNotFoundException">No source of that kind is registered.</exception>
    Task<bool> Discard(string kind, long id, string actor, CancellationToken cancellationToken);

    /// <summary>The projection versions (empty without read models that support it).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The versions.</returns>
    Task<IReadOnlyList<ProjectionVersion>> Projections(CancellationToken cancellationToken);

    /// <summary>Drops a superseded projection version (QR-3: explicit, never scheduled).</summary>
    /// <param name="name">The projection.</param>
    /// <param name="version">The version.</param>
    /// <param name="actor">Who asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">No read models are registered, or the version is the newest or in use here.</exception>
    /// <exception cref="TimeoutException">Another session still reads its table.</exception>
    Task DropVersion(string name, int version, string actor, CancellationToken cancellationToken);

    /// <summary>The most dead letters one call of the filtered re-drive moves.</summary>
    const int MaxRedrive = 1000;
}

/// <summary>The <see cref="IDeadLetterAdmin"/> over the registered sources.</summary>
public sealed partial class DeadLetterAdmin : IDeadLetterAdmin
{
    private readonly Dictionary<string, IDeadLetterSource> _sources;
    private readonly IReadModelAdmin? _readModels;
    private readonly ILogger _log;

    /// <summary>Creates the admin.</summary>
    /// <param name="sources">The registered sources (one per kind; a later one of the same kind wins).</param>
    /// <param name="readModels">The read models' versions; <see langword="null"/> = none.</param>
    /// <param name="logger">Logs every act (15001–15004); <see langword="null"/> = none.</param>
    public DeadLetterAdmin(IEnumerable<IDeadLetterSource> sources, IReadModelAdmin? readModels = null, ILogger<DeadLetterAdmin>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = new Dictionary<string, IDeadLetterSource>(StringComparer.Ordinal);
        foreach (var source in sources)
            _sources[source.Kind] = source;
        _readModels = readModels;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        Kinds = [.. _sources.Keys.Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Kinds { get; }

    private IDeadLetterSource Source(string kind)
        => _sources.TryGetValue(kind, out var source) ? source : throw new KeyNotFoundException($"No dead letters of kind '{kind}' are registered ({string.Join(", ", Kinds)}).");

    /// <inheritdoc />
    public Task<DeadLetterPage> List(string kind, DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Source(kind).List(query, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> Redrive(string kind, long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var done = await Source(kind).Redrive(id, actor, cancellationToken).ConfigureAwait(false);
        if (done)
            Redriven(_log, kind, id, actor);
        return done;
    }

    /// <inheritdoc />
    public async Task<int> Redrive(string kind, DeadLetterQuery filter, int max, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, IDeadLetterAdmin.MaxRedrive);
        var source = Source(kind);
        var done = 0;
        var query = filter with { Limit = Math.Min(DeadLetterQuery.MaxLimit, max) };
        while (done < max)
        {
            var page = await source.List(query with { Limit = Math.Min(DeadLetterQuery.MaxLimit, max - done) }, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
                if (await source.Redrive(item.Id, actor, cancellationToken).ConfigureAwait(false))
                    done++;
            if (page.Next is not { } next || page.Items.Count == 0)
                break;
            query = query with { After = next };
        }
        RedrivenMany(_log, kind, done, filter.Tenant.TryGetValue(out Id<ITenant> tenant) ? tenant.ToString() : "*", filter.Name ?? "*", actor);
        return done;
    }

    /// <inheritdoc />
    public async Task<bool> Discard(string kind, long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var done = await Source(kind).Discard(id, actor, cancellationToken).ConfigureAwait(false);
        if (done)
            Discarded(_log, kind, id, actor);
        return done;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProjectionVersion>> Projections(CancellationToken cancellationToken)
        => _readModels?.Versions(cancellationToken) ?? Task.FromResult<IReadOnlyList<ProjectionVersion>>([]);

    /// <inheritdoc />
    public async Task DropVersion(string name, int version, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (_readModels is null)
            throw new InvalidOperationException("No read models with versions are registered (UseNpgsqlReadModels).");
        await _readModels.DropVersion(name, version, actor, cancellationToken).ConfigureAwait(false);
        Dropped(_log, name, version, actor);
    }

    [LoggerMessage(EventId = 15001, Level = LogLevel.Information, Message = "Dead letter {Kind}:{Id} re-driven by {Actor}")]
    private static partial void Redriven(ILogger logger, string kind, long id, string actor);

    [LoggerMessage(EventId = 15002, Level = LogLevel.Information, Message = "{Count} dead letters of {Kind} (tenant {Tenant}, name {Name}) re-driven by {Actor}")]
    private static partial void RedrivenMany(ILogger logger, string kind, int count, string tenant, string name, string actor);

    [LoggerMessage(EventId = 15003, Level = LogLevel.Information, Message = "Dead letter {Kind}:{Id} discarded by {Actor}")]
    private static partial void Discarded(ILogger logger, string kind, long id, string actor);

    [LoggerMessage(EventId = 15004, Level = LogLevel.Information, Message = "Projection {Projection} v{Version} dropped by {Actor}")]
    private static partial void Dropped(ILogger logger, string projection, int version, string actor);
}
