using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;

namespace Tarol.Search;

/// <summary>
/// The search index in erasure and retention (GS-8, DP-5, DP-7): after a subject's tombstone it re-indexes the subject's streams
/// (their personal fields now read <see langword="null"/>, so their blind tokens go), after a tenant's erasure every stream of the
/// tenant's searchable types, and after retention it removes the documents of the deleted streams. The checkpoint does not move.
/// </summary>
public sealed class SearchEraser : ErasureParticipant
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the participant.</summary>
    /// <param name="services">Resolves the host and the store when an erasure runs.</param>
    public SearchEraser(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public override string Name => "search";

    private SearchHost Host => _services.GetRequiredService<SearchHost>();

    /// <inheritdoc />
    public override async ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
        => await Host.Reindex(streams, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override async ValueTask ForgetTenant(Guid tenant, CancellationToken cancellationToken)
    {
        var store = _services.GetRequiredService<IDataProtectionStore>();
        var registry = _services.GetRequiredService<SearchRegistry>();
        foreach (var type in registry.Sources.Keys)
            await Host.Reindex(await store.Streams(tenant, type, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask Deleted(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
        => await Host.Remove(streams, cancellationToken).ConfigureAwait(false);

    /// <summary>What the index holds of a subject (an export lists it): the documents of the subject's streams.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The documents (type, id, aggregate, title).</returns>
    public async Task<IReadOnlyList<SearchHit>> Documents(Guid tenant, Guid subject, CancellationToken cancellationToken)
    {
        var streams = await _services.GetRequiredService<IDataProtectionStore>().StreamsOf(tenant, subject, cancellationToken).ConfigureAwait(false);
        return await Host.Index.Documents(tenant, [.. streams.Select(static s => s.Id)], cancellationToken).ConfigureAwait(false);
    }
}
