using Tarol.Persistence;

namespace Tarol.Caching;

/// <summary>
/// The erasure participant of the page cache (DP-5, step 16C): a page holds opened rows, and an erasure's re-fold does not
/// move the checkpoint the pages are keyed on, so the erased tenant's pages are dropped here and, on the key store's
/// signal, in every other process (<see cref="ErasureParticipant.Evict"/>).
/// </summary>
/// <param name="cache">The cache.</param>
public sealed class PageCacheEraser(IPageCache cache) : ErasureParticipant
{
    /// <inheritdoc />
    public override string Name => "page-cache";

    /// <inheritdoc />
    public override ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        cache.Clear(tenant);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask ForgetTenant(Guid tenant, CancellationToken cancellationToken)
    {
        cache.Clear(tenant);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask Deleted(IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        foreach (var tenant in streams.Select(static s => s.Tenant).Distinct())
            cache.Clear(tenant);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override void Evict(Guid tenant, Guid? subject) => cache.Clear(tenant);
}
