using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;

namespace Tarol.Queries;

/// <summary>
/// The erasure participant of a row projection (DP-5, step 16C): after a subject's key became a tombstone it re-folds the
/// subject's streams of the projection's stream type, which now read the subject's fields as <c>null</c>, and replaces their
/// rows (or removes them when the fold yields none). Retention removes the rows of deleted streams (DP-7). Registered for
/// every projection <see cref="ProjectionsBuilder.Add{TRow}"/> hosts; it reads the store through <see cref="IDataProtectionStore"/>.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
/// <remarks>The checkpoint does not move; a page cache in front of the store drops the tenant's pages.</remarks>
public sealed class ProjectionEraser<TRow> : ErasureParticipant
    where TRow : class, IProjection<TRow>
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the participant.</summary>
    /// <param name="services">Resolves the read models and the store when an erasure runs.</param>
    public ProjectionEraser(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public override string Name => $"projection:{TRow.Name}:v{TRow.Version}";

    private IReadModelStore<TRow> Rows => _services.GetRequiredService<IReadModels>().Store<TRow>();

    private IDataProtectionStore Store => _services.GetRequiredService<IDataProtectionStore>();

    /// <inheritdoc />
    public override async ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        await UntilBuilt(cancellationToken).ConfigureAwait(false);
        await Refold([.. streams.Where(static s => s.StreamType == TRow.StreamType)], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask ForgetTenant(Guid tenant, CancellationToken cancellationToken)
    {
        await UntilBuilt(cancellationToken).ConfigureAwait(false);
        await Refold(await Store.Streams(tenant, TRow.StreamType, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// TN-25 (Tatalu FB-F17.5-4, FB-INT2-4): while this process's host of the projection builds, a batch it read before the key
    /// was erased may still be written after the re-fold, with the old values. Wait until it is current first. A read table
    /// no host has prepared yet holds no rows: the re-fold has nothing to replace (the store answers that without failing).
    /// </summary>
    private async ValueTask UntilBuilt(CancellationToken cancellationToken)
    {
        foreach (var host in _services.GetServices<ProjectionHost>())
        {
            if (host.Name != TRow.Name || host.Version != TRow.Version || host.State == ProjectionState.Current)
                continue;
            if (!await host.WaitUntilCurrent(host.ErasureWait, cancellationToken).ConfigureAwait(false))
                throw new TimeoutException($"The projection {TRow.Name} v{TRow.Version} is still building after {host.ErasureWait}; its rows are re-folded once it is current: run the erasure again.");
        }
    }

    /// <inheritdoc />
    public override async ValueTask Deleted(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var keys = streams.Where(static s => s.StreamType == TRow.StreamType).Select(static s => new RowKey(s.Tenant, s.Id)).ToArray();
        if (keys.Length > 0)
            await Rows.RemoveStreams(keys, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Folds each stream from its first event, as the host does, and replaces or removes its row.</summary>
    /// <param name="streams">The streams (of <c>TRow.StreamType</c>).</param>
    /// <param name="cancellationToken">Cancels between streams.</param>
    /// <returns>How many rows were replaced and removed.</returns>
    public async Task<(int Replaced, int Removed)> Refold(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var replaced = new List<Versioned<TRow>>();
        var removed = new List<RowKey>();
        if (TRow.PerEntity)
            return await RefoldPerEntity(streams, cancellationToken).ConfigureAwait(false);
        foreach (var stream in streams)
        {
            var events = await Store.ReadStream(stream, cancellationToken).ConfigureAwait(false);
            TRow? row = null;
            long version = 0;
            foreach (var e in events)
            {
                var next = TRow.Apply(row, e.Event);
                if (next is not null && !ReferenceEquals(next, row))
                    version = e.Event.Event.Version;
                row = next;
            }

            var key = new RowKey(stream.Tenant, stream.Id);
            if (row is null)
                removed.Add(key);
            else
                replaced.Add(new Versioned<TRow>(key, version, row));
        }

        if (replaced.Count > 0)
            await Rows.Replace(replaced, cancellationToken).ConfigureAwait(false);
        var gone = removed.Count > 0 ? await Rows.Remove(removed, cancellationToken).ConfigureAwait(false) : 0;
        return (replaced.Count, gone);
    }

    // TN-54 (P-1): the rows of a stream are its entities' rows: drop them, then write what the stream folds to now
    private async Task<(int Replaced, int Removed)> RefoldPerEntity(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        var replaced = new List<Versioned<TRow>>();
        foreach (var stream in streams)
        {
            var rows = new Dictionary<Guid, (TRow? Row, long Version)>();
            foreach (var e in await Store.ReadStream(stream, cancellationToken).ConfigureAwait(false))
            {
                var entity = e.Event.Event.EntityId;
                if (entity == Guid.Empty)
                    continue;
                rows.TryGetValue(entity, out var current);
                var next = TRow.Apply(current.Row, e.Event);
                rows[entity] = (next, next is not null && !ReferenceEquals(next, current.Row) ? e.Event.Event.Version : current.Version);
            }
            foreach (var (entity, (row, version)) in rows)
                if (row is not null)
                    replaced.Add(new Versioned<TRow>(new RowKey(stream.Tenant, entity), version, row) { Stream = stream.Id });
        }
        var gone = await Rows.RemoveStreams([.. streams.Select(static s => new RowKey(s.Tenant, s.Id))], cancellationToken).ConfigureAwait(false);
        if (replaced.Count > 0)
            await Rows.Replace(replaced, cancellationToken).ConfigureAwait(false);
        return (replaced.Count, Math.Max(0, gone - replaced.Count));
    }
}
