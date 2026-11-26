using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Audit;

/// <summary>The last value of one field of one object: the "before" of its next change.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Target">The object.</param>
/// <param name="Field">The field.</param>
public readonly record struct AuditValueKey(Guid Tenant, Guid Target, string Field);

/// <summary>
/// What one batch of the feed writes, together with the checkpoint (exactly once): the rows, the new last values (a <see langword="null"/>
/// value is a field set to null), and the objects removed (their last values go).
/// </summary>
/// <param name="Entries">The rows.</param>
/// <param name="Values">The new last values.</param>
/// <param name="Removed">The removed objects.</param>
/// <param name="From">The checkpoint the batch starts after (the write is refused when the stored one differs).</param>
/// <param name="Through">The checkpoint after the batch.</param>
/// <param name="State">The host's state after the batch.</param>
public sealed record AuditBatch(IReadOnlyList<AuditEntry> Entries, IReadOnlyDictionary<AuditValueKey, string?> Values,
    IReadOnlyCollection<(Guid Tenant, Guid Target)> Removed, GlobalPosition From, GlobalPosition Through, ProjectionState State);

/// <summary>
/// Where the change log lives (TN-64): <see cref="InMemoryAuditStore"/>, or PostgreSQL with <c>Tarol.Audit.Npgsql</c>. Rows from the feed
/// belong to a projection version (a new version rebuilds them from the feed); reads and denials are kept apart from the versions,
/// since the feed cannot rebuild them.
/// </summary>
public interface IAuditStore
{
    /// <summary>Creates the tables of <paramref name="version"/> if they are missing.</summary>
    /// <param name="version">The projection version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Prepare(int version, CancellationToken cancellationToken);

    /// <summary>The checkpoint of <paramref name="version"/>.</summary>
    /// <param name="version">The projection version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> Checkpoint(int version, CancellationToken cancellationToken);

    /// <summary>The last values of every field of <paramref name="objects"/>.</summary>
    /// <param name="version">The projection version.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values found.</returns>
    ValueTask<IReadOnlyDictionary<AuditValueKey, string?>> Values(int version, IReadOnlyCollection<(Guid Tenant, Guid Target)> objects, CancellationToken cancellationToken);

    /// <summary>Writes a batch and moves the checkpoint, atomically; refused when the stored checkpoint is not <see cref="AuditBatch.From"/>.</summary>
    /// <param name="version">The projection version.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was written.</returns>
    ValueTask<bool> Write(int version, AuditBatch batch, CancellationToken cancellationToken);

    /// <summary>Writes a read or denial (outside the versions).</summary>
    /// <param name="entry">The row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Record(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>The rows of <paramref name="tenant"/> matching <paramref name="query"/>, newest first (recorded time, then id), from <paramref name="version"/> and the reads and denials.</summary>
    /// <param name="version">The projection version.</param>
    /// <param name="tenant">The caller's tenant.</param>
    /// <param name="query">The filters.</param>
    /// <param name="after">The last row of the previous page, or <see langword="null"/>.</param>
    /// <param name="take">How many rows.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rows, values as stored (sealed).</returns>
    ValueTask<IReadOnlyList<AuditEntry>> Query(int version, Guid tenant, AuditQuery query, (DateTimeOffset At, Guid Id)? after, int take, CancellationToken cancellationToken);

    /// <summary>Deletes every row and value of a tenant (tenant erasure).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows went.</returns>
    ValueTask<int> ForgetTenant(Guid tenant, CancellationToken cancellationToken);

    /// <summary>Deletes rows recorded before <paramref name="olderThan"/> (AU-4: the log's own retention), at most <paramref name="batch"/>.</summary>
    /// <param name="olderThan">The end of the retention window.</param>
    /// <param name="batch">The most rows per call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows went; call again while it equals <paramref name="batch"/>.</returns>
    ValueTask<int> Purge(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken);
}
