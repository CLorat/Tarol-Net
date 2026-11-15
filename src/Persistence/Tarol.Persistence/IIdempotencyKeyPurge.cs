namespace Tarol.Persistence;

/// <summary>A store that can delete old idempotency keys (P-11). The reaction worker purges through it when one is
/// registered (<c>TarolBuilder.UseNpgsqlStore</c> registers the PostgreSQL store's).</summary>
public interface IIdempotencyKeyPurge
{
    /// <summary>Deletes keys stored before <paramref name="olderThan"/>, at most <paramref name="batch"/> per call.</summary>
    /// <param name="olderThan">The end of the retention window, e.g. now − 7 days.</param>
    /// <param name="batch">The most keys to delete in one call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many keys were deleted; call again while it equals <paramref name="batch"/>.</returns>
    Task<int> PurgeKeys(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken);
}
