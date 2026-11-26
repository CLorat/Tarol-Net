using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;

namespace Tarol.Queries;

/// <summary>
/// A projection that writes its own SQL (QR-2): tables keyed by something other than one stream (totals per seller). The
/// host hands it each batch of events inside the batch transaction, after the guarded checkpoint move, so its writes and
/// the checkpoint commit together (exactly once). Run by <see cref="SqlProjectionHost"/> on a store that implements
/// <see cref="ISqlProjectionStore"/> (<c>Tarol.Queries.Npgsql</c>). Tenant-scoped data must carry the tenant in its keys.
/// </summary>
public interface ISqlProjection
{
    /// <summary>The projection's name (lowercase letters, digits and hyphens): the key of its checkpoint.</summary>
    string Name { get; }

    /// <summary>Its version; a new version starts from the beginning of the feed with a new checkpoint (and, by convention, new tables).</summary>
    int Version { get; }

    /// <summary>The stream types it reads; <see langword="null"/> or empty: every event of the feed.</summary>
    IReadOnlyCollection<string>? StreamTypes { get; }

    /// <summary>Creates its tables (idempotent: <c>CREATE TABLE IF NOT EXISTS</c>), in a transaction of its own.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction to run the statements in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Applies a batch (only events of <see cref="StreamTypes"/>, in feed order) within the batch transaction.</summary>
    /// <param name="events">The events; may be empty when only the state changes.</param>
    /// <param name="connection">The batch's connection.</param>
    /// <param name="transaction">The batch transaction: run every statement in it.</param>
    /// <param name="cancellationToken">Cancels the batch (it rolls back).</param>
    /// <returns>The number of rows written (for the trace).</returns>
    ValueTask<int> Apply(IReadOnlyList<PositionedEvent> events, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="ISqlProjection"/> whose rows are the <c>[Projection(…, Sql = true)]</c> record <typeparamref name="TRow"/> (TN-54,
/// owner decision P-2): its name and version are the row's, its table is the row's read table (<c>rm_&lt;name&gt;_v&lt;version&gt;</c>), so
/// <see cref="IQuery{TRow}"/> and <see cref="IRowQuery{TRow}"/> endpoints list and read what it writes, with the read-after and
/// building answers of a row projection. With PostgreSQL, <c>NpgsqlRows&lt;TRow&gt;</c> creates the table and writes rows in the batch
/// transaction.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
public interface ISqlProjection<TRow> : ISqlProjection
    where TRow : class, IProjection<TRow>
{
    /// <inheritdoc />
    string ISqlProjection.Name => TRow.Name;

    /// <inheritdoc />
    int ISqlProjection.Version => TRow.Version;
}

/// <summary>A store that runs <see cref="ISqlProjection"/>s: checkpoints in its projections table and the batch transaction.</summary>
public interface ISqlProjectionStore
{
    /// <summary>Creates the checkpoint row and runs <see cref="ISqlProjection.Prepare"/>.</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Prepare(ISqlProjection projection, CancellationToken cancellationToken);

    /// <summary>The stored checkpoint of the projection version.</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> Checkpoint(ISqlProjection projection, CancellationToken cancellationToken);

    /// <summary>In one transaction: moves the checkpoint from <paramref name="from"/> to <paramref name="through"/> (guarded), then
    /// calls <see cref="ISqlProjection.Apply"/>; commits both or nothing.</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="events">Its events of the batch.</param>
    /// <param name="from">The checkpoint the batch read after.</param>
    /// <param name="through">The position of the batch's last feed event.</param>
    /// <param name="state">The state after the batch.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>Whether it was written (<see langword="false"/>: another runner moved the checkpoint) and the rows the projection reported.</returns>
    ValueTask<(bool Written, int Rows)> Write(ISqlProjection projection, IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition through,
        ProjectionState state, CancellationToken cancellationToken);
}

/// <summary>Runs one <see cref="ISqlProjection"/> on an <see cref="ISqlProjectionStore"/>.</summary>
public sealed class SqlProjectionHost : ProjectionHost
{
    private readonly ISqlProjection _projection;
    private readonly ISqlProjectionStore _store;
    private readonly HashSet<string>? _types;

    /// <summary>Creates the host.</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="feed">The store's feed.</param>
    /// <param name="store">The store that runs SQL projections.</param>
    /// <param name="options">Batch, poll and backoff.</param>
    /// <param name="time">The clock of waits.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="signal">Wakes the host after a commit.</param>
    /// <param name="gate">Lets one runner of the version work (step 15); <see langword="null"/> = every host runs.</param>
    /// <param name="meters">Creates the meter <see cref="ProjectionHost.MeterName"/>; <see langword="null"/> = a shared meter.</param>
    public SqlProjectionHost(ISqlProjection projection, IEventFeed feed, ISqlProjectionStore store, ProjectionOptions? options = null,
        TimeProvider? time = null, ILogger<SqlProjectionHost>? logger = null, IFeedSignal? signal = null, IProjectionGate? gate = null,
        IMeterFactory? meters = null)
        : base(ArgumentNullThen(projection).Name, projection.Version, feed, options, time, logger, signal, gate, meters)
    {
        ArgumentNullException.ThrowIfNull(store);
        _projection = projection;
        _store = store;
        _types = projection.StreamTypes is { Count: > 0 } types ? new HashSet<string>(types, StringComparer.Ordinal) : null;
    }

    /// <summary>The projection.</summary>
    public ISqlProjection Projection => _projection;

    private static ISqlProjection ArgumentNullThen(ISqlProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return projection;
    }

    /// <inheritdoc />
    protected override ValueTask Prepare(CancellationToken cancellationToken) => _store.Prepare(_projection, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken) => _store.Checkpoint(_projection, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition through,
        ProjectionState state, CancellationToken cancellationToken)
    {
        IReadOnlyList<PositionedEvent> mine = _types is null ? events : [.. events.Where(e => _types.Contains(e.StreamType))];
        return _store.Write(_projection, mine, from, through, state, cancellationToken);
    }
}
