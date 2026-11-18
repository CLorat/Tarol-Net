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
        await Refold([.. streams.Where(static s => s.StreamType == TRow.StreamType)], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask ForgetTenant(Guid tenant, CancellationToken cancellationToken)
        => await Refold(await Store.Streams(tenant, TRow.StreamType, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override async ValueTask Deleted(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var keys = streams.Where(static s => s.StreamType == TRow.StreamType).Select(static s => new RowKey(s.Tenant, s.Id)).ToArray();
        if (keys.Length > 0)
            await Rows.Remove(keys, cancellationToken).ConfigureAwait(false);
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
}
