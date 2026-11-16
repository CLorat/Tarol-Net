using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Queries;

/// <summary>The key of a per-stream row: the tenant (<see cref="Guid.Empty"/> for none) and the stream id.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Id">The stream id.</param>
public readonly record struct RowKey(Guid Tenant, Guid Id);

/// <summary>A row with its key and the stream version it reflects (events at or below it are skipped on a replay).</summary>
/// <typeparam name="TRow">The row record.</typeparam>
/// <param name="Key">The key.</param>
/// <param name="Version">The version of the last event applied.</param>
/// <param name="Row">The row.</param>
public sealed record Versioned<TRow>(RowKey Key, long Version, TRow Row);

/// <summary>Whether a projection version is still catching up with the feed (QR-3: lists answer 503 while it builds).</summary>
public enum ProjectionState
{
    /// <summary>Reading the feed from the start; its rows are incomplete.</summary>
    Building,

    /// <summary>Has reached the head of the feed once; its rows lag the commits by the projection's lag only.</summary>
    Current,
}

/// <summary>The stored progress of a projection version.</summary>
/// <param name="Position">The position of the last event the projection handled.</param>
/// <param name="State">Building or current.</param>
public readonly record struct ProjectionCheckpoint(GlobalPosition Position, ProjectionState State);

/// <summary>One write of the host: the changed rows and the checkpoint move, applied together or not at all.</summary>
/// <typeparam name="TRow">The row record.</typeparam>
/// <param name="Rows">The rows that changed in the batch (one per stream, at its last version).</param>
/// <param name="From">The checkpoint the batch read after; the write is refused when the stored one differs (another runner).</param>
/// <param name="To">The position of the batch's last event (equal to <paramref name="From"/> when only the state changes).</param>
/// <param name="State">The state after the batch.</param>
public sealed record ProjectionBatch<TRow>(IReadOnlyList<Versioned<TRow>> Rows, GlobalPosition From, GlobalPosition To, ProjectionState State);

/// <summary>
/// Where the rows and the checkpoint of one projection version live: <see cref="InMemoryReadModelStore{TRow}"/>, or the
/// read table of <c>Tarol.Queries.Npgsql</c>. Every read is tenant-scoped.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
public interface IReadModelStore<TRow>
    where TRow : class, IProjection<TRow>
{
    /// <summary>Creates the table and the checkpoint row if they do not exist (idempotent).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Prepare(CancellationToken cancellationToken);

    /// <summary>The stored checkpoint (<see cref="GlobalPosition.Start"/> and building before the first batch).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken);

    /// <summary>The stored rows of <paramref name="keys"/> (missing keys are absent).</summary>
    /// <param name="keys">The keys.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows by key.</returns>
    ValueTask<IReadOnlyDictionary<RowKey, Versioned<TRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a batch: first moves the checkpoint from <see cref="ProjectionBatch{TRow}.From"/> to
    /// <see cref="ProjectionBatch{TRow}.To"/> (guarded: it must still be at <c>From</c>), then upserts the rows whose version
    /// is above the stored one, in one transaction.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="cancellationToken">Cancels the write (nothing is written).</param>
    /// <returns><see langword="false"/> when another runner moved the checkpoint: nothing was written.</returns>
    ValueTask<bool> Write(ProjectionBatch<TRow> batch, CancellationToken cancellationToken);

    /// <summary>One row by key, within its tenant.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    ValueTask<Versioned<TRow>?> Find(RowKey key, CancellationToken cancellationToken);

    /// <summary>A page of a list query (<see cref="ListQuery"/>: tenant, equality filters, sort, cursor or offset, limit).</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page, or <see cref="Invalid"/> for a bad cursor, limit or offset (HTTP 400).</returns>
    /// <exception cref="ArgumentException">A filter or the sort names no column of the projection (a programming error).</exception>
    ValueTask<Result<Page<TRow>>> Search(ListQuery query, CancellationToken cancellationToken);
}

/// <summary>The read stores of an application, one per projection version (<c>UseInMemoryReadModels</c>, <c>UseNpgsqlReadModels</c>).</summary>
public interface IReadModels
{
    /// <summary>The store of <typeparamref name="TRow"/>'s current version.</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <returns>The store (the same instance on every call).</returns>
    IReadModelStore<TRow> Store<TRow>()
        where TRow : class, IProjection<TRow>;
}
