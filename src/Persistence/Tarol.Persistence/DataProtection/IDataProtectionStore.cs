namespace Tarol.Persistence;

/// <summary>
/// What erasure, export and retention need from a store (DP-5 … DP-7): the streams and events of a subject, a stream's
/// events for a re-fold, the deletion of personal lookups and of expired streams, and the audit. Implemented by the
/// in-memory and the Npgsql store; <c>Tarol.DataProtection</c> uses it.
/// </summary>
public interface IDataProtectionStore
{
    /// <summary>One stream's recorded events, opened with the current keys (an erased subject's fields read as <c>null</c>).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events from version 1; empty when the stream does not exist.</returns>
    Task<IReadOnlyList<PositionedEvent>> ReadStream(StreamRef stream, CancellationToken cancellationToken);

    /// <summary>The streams of a type in a tenant (a tenant's re-fold).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="streamType">The stream type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The streams.</returns>
    Task<IReadOnlyList<StreamRef>> Streams(Guid tenant, string streamType, CancellationToken cancellationToken);

    /// <summary>The streams whose events hold sealed data of a subject.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The streams.</returns>
    Task<IReadOnlyList<StreamRef>> StreamsOf(Guid tenant, Guid subject, CancellationToken cancellationToken);

    /// <summary>Every event holding sealed data of a subject, in feed order, opened with that subject's key only.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events.</returns>
    Task<IReadOnlyList<PositionedEvent>> EventsOf(Guid tenant, Guid subject, CancellationToken cancellationToken);

    /// <summary>A stream's stored state, opened with that subject's key only; <see langword="null"/> without one.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="subject">The subject whose fields are opened.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state's JSON.</returns>
    Task<byte[]?> StateOf(StreamRef stream, Guid subject, CancellationToken cancellationToken);

    /// <summary>Deletes the personal lookups of the given streams.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="streams">The streams.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many lookup rows were deleted.</returns>
    Task<int> DeletePersonalLookups(Guid tenant, IReadOnlyCollection<StreamRef> streams, CancellationToken cancellationToken);

    /// <summary>Deletes whole streams of a type whose last commit is older than <paramref name="olderThan"/>, skipping pending reactions.</summary>
    /// <param name="streamType">The stream type.</param>
    /// <param name="olderThan">The cut-off.</param>
    /// <param name="batch">The most streams per call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The deleted streams.</returns>
    Task<IReadOnlyList<StreamRef>> DeleteStreams(string streamType, DateTimeOffset olderThan, int batch, CancellationToken cancellationToken);

    /// <summary>Writes an audit row (<c>operations_audit</c> on PostgreSQL): an erasure, an export, a retention batch.</summary>
    /// <param name="actor">Who acted (a principal name, or a system job).</param>
    /// <param name="action">The act, e.g. <c>erase</c>.</param>
    /// <param name="target">What it acted on, e.g. <c>subject:&lt;id&gt;</c>.</param>
    /// <param name="detail">A JSON object with ids and counts (never personal values), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task Audit(string actor, string action, string target, string? detail, CancellationToken cancellationToken);

    /// <summary>Deletes audit rows written before <paramref name="olderThan"/>, at most <paramref name="batch"/> (DP-9, <c>tarol.purge-audit</c>).</summary>
    /// <param name="olderThan">The cut-off.</param>
    /// <param name="batch">The most rows per call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> PurgeAudit(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken);
}
