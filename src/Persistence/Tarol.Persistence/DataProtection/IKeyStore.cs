namespace Tarol.Persistence;

/// <summary>
/// Where the data keys live (DP-1, DP-4): a data key per (tenant, subject), wrapped by its tenant's key, wrapped by the master
/// key (<see cref="IKeyEncryptionKey"/>). Erasing a subject turns its key into a tombstone; erasing a tenant blanks the tenant
/// key, which erases every subject of the tenant in one row. Step 16 ships it on tables in the store's database
/// (<c>NpgsqlKeyStore</c>) and in memory (<see cref="InMemoryKeyStore"/>); a KMS-backed store follows behind this port.
/// </summary>
public interface IKeyStore
{
    /// <summary>Raised in every process that shares the store when a subject or tenant is erased (key caches evict on it).</summary>
    event EventHandler<KeyErasedEventArgs>? Erased;

    /// <summary>The keys of <paramref name="subjects"/> in <paramref name="tenant"/>: a key, or a tombstone for an erased subject.</summary>
    /// <param name="tenant">The tenant (<see cref="Guid.Empty"/> without one).</param>
    /// <param name="subjects">The subjects.</param>
    /// <param name="create">Whether subjects without a key get one (sealing); without it they read as erased (opening).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A ring holding every subject.</returns>
    ValueTask<KeyRing> Keys(Guid tenant, IReadOnlyCollection<Guid> subjects, bool create, CancellationToken cancellationToken);

    /// <summary>Erases a subject: its key becomes a tombstone (no key, the erasure time), here and, through <see cref="Erased"/>, in every process.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when it was erased already.</returns>
    ValueTask<bool> EraseSubject(Guid tenant, Guid subject, CancellationToken cancellationToken);

    /// <summary>Erases a tenant: its tenant key is blanked, so no data key of the tenant opens any more (offboarding).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when it was erased already.</returns>
    ValueTask<bool> EraseTenant(Guid tenant, CancellationToken cancellationToken);

    /// <summary>Re-wraps every tenant key that is not wrapped under the master key's current <see cref="IKeyEncryptionKey.KeyId"/> (rotation).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many tenant keys were re-wrapped.</returns>
    ValueTask<int> Rewrap(CancellationToken cancellationToken);
}

/// <summary>A subject (or a whole tenant) was erased.</summary>
/// <param name="tenant">The tenant.</param>
/// <param name="subject">The subject; <see langword="null"/> when the whole tenant was erased.</param>
public sealed class KeyErasedEventArgs(Guid tenant, Guid? subject) : EventArgs
{
    /// <summary>The tenant.</summary>
    public Guid Tenant { get; } = tenant;

    /// <summary>The subject; <see langword="null"/> when the whole tenant was erased.</summary>
    public Guid? Subject { get; } = subject;
}
