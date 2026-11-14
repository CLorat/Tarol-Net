using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// A claimed outbox row: run <see cref="Reaction"/> for <see cref="Event"/>, then <see cref="NpgsqlOutbox.Complete"/> it,
/// or <see cref="NpgsqlOutbox.Fail"/> it to retry later.
/// </summary>
/// <param name="Id">The row.</param>
/// <param name="ClaimId">This claim; complete and fail must present it.</param>
/// <param name="Reaction">The reaction's name (from the <see cref="ReactionTable"/>).</param>
/// <param name="Tenant">The tenant of the event.</param>
/// <param name="Attempts">How many times the row was claimed, this claim included.</param>
/// <param name="LastError">The error of the previous attempt.</param>
/// <param name="Event">The event with its commit facts.</param>
public sealed record OutboxRow(long Id, Guid ClaimId, string Reaction, Option<Id<ITenant>> Tenant, int Attempts, Option<string> LastError, PositionedEvent Event);

/// <summary>
/// The outbox of an <see cref="NpgsqlAggregateStore"/> (P-1, P-10): workers claim due rows with <c>FOR UPDATE SKIP
/// LOCKED</c>, so concurrent workers never claim one row twice. A row is claimable only when it is the oldest of its
/// (tenant, stream, reaction): one stream's events reach a reaction in version order, one at a time. The worker loop,
/// retries with backoff and dead-lettering belong to step 9.
/// <code>
/// foreach (var row in await store.Outbox.Claim(50, TimeSpan.FromMinutes(1), ct))
///     _ = await Run(row) ? await store.Outbox.Complete(row, ct) : await store.Outbox.Fail(row, now + backoff, error, ct);
/// </code>
/// </summary>
/// <remarks>
/// A row whose lease expired is claimable again (at least once); a worker that lost its lease gets <see langword="false"/>
/// from <see cref="Complete"/>. A row that keeps failing holds back its stream's later rows for that reaction only.
/// </remarks>
public sealed class NpgsqlOutbox
{
    private readonly NpgsqlDataSource _db;
    private readonly EventCodec _codec;
    private readonly TimeProvider _time;
    private readonly string _claim;
    private readonly string _complete;
    private readonly string _fail;
    private readonly string _pending;

    internal NpgsqlOutbox(NpgsqlDataSource dataSource, EventCodec codec, TimeProvider time, string schema)
    {
        _db = dataSource;
        _codec = codec;
        _time = time;
        _claim = $"""
            WITH c AS (
                SELECT o.id FROM {schema}.outbox o
                WHERE o.due_at <= $1 AND (o.lease_until IS NULL OR o.lease_until <= $1)
                  AND NOT EXISTS (SELECT 1 FROM {schema}.outbox p
                                  WHERE p.tenant = o.tenant AND p.type = o.type AND p.stream_id = o.stream_id
                                    AND p.reaction = o.reaction AND p.id < o.id)
                ORDER BY o.id
                LIMIT $2
                FOR UPDATE SKIP LOCKED),
            u AS (
                UPDATE {schema}.outbox o SET claim_id = gen_random_uuid(), lease_until = $3, attempts = o.attempts + 1
                FROM c WHERE o.id = c.id
                RETURNING o.id, o.claim_id, o.reaction, o.attempts, o.last_error, o.event_id)
            SELECT u.id, u.claim_id, u.reaction, u.attempts, u.last_error, {RecordedEvents.Columns("e")}
            FROM u JOIN {schema}.events e ON e.event_id = u.event_id
            ORDER BY u.id
            """;
        _complete = $"DELETE FROM {schema}.outbox WHERE id = $1 AND claim_id = $2";
        _fail = $"UPDATE {schema}.outbox SET due_at = $3, claim_id = NULL, lease_until = NULL, last_error = $4 WHERE id = $1 AND claim_id = $2";
        _pending = $"SELECT count(*) FROM {schema}.outbox";
    }

    /// <summary>Claims up to <paramref name="max"/> due rows for <paramref name="lease"/>.</summary>
    /// <param name="max">The most rows.</param>
    /// <param name="lease">How long the rows stay with this worker; after it they are claimable again.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The claimed rows, oldest first.</returns>
    public async Task<IReadOnlyList<OutboxRow>> Claim(int max, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        var now = _time.GetUtcNow();
        await using var command = _db.CreateCommand(_claim);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = max });
        command.Parameters.Add(new NpgsqlParameter { Value = now + lease });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var rows = new List<OutboxRow>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var recorded = RecordedEvents.Read(reader, 5, _codec);
                rows.Add(new OutboxRow(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), recorded.Event.Context.Tenant,
                    reader.GetInt32(3), reader.IsDBNull(4) ? Option.None : reader.GetString(4), recorded));
            }
            return rows;
        }
    }

    /// <summary>Removes a row whose reaction completed.</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost (another worker claimed the row again).</returns>
    public async Task<bool> Complete(OutboxRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = _db.CreateCommand(_complete);
        command.Parameters.Add(new NpgsqlParameter { Value = row.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = row.ClaimId });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Releases a row to be claimed again at <paramref name="retryAt"/>, remembering the error.</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="retryAt">When it is due again.</param>
    /// <param name="error">What went wrong (stored as <see cref="OutboxRow.LastError"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost.</returns>
    public async Task<bool> Fail(OutboxRow row, DateTimeOffset retryAt, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(error);
        await using var command = _db.CreateCommand(_fail);
        command.Parameters.Add(new NpgsqlParameter { Value = row.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = row.ClaimId });
        command.Parameters.Add(new NpgsqlParameter { Value = retryAt });
        command.Parameters.Add(new NpgsqlParameter { Value = error });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>How many rows wait (claimed or not); for health checks.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count.</returns>
    public async Task<long> Pending(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_pending);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long n ? n : 0;
    }
}
