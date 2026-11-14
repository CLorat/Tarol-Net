using Npgsql;

namespace Tarol.Persistence.Npgsql;

/// <summary>A claimed outbox row (step 9's worker gets these).</summary>
public readonly record struct OutboxRow(long Id, Guid Tenant, string Reaction, string StreamType, Guid StreamId, long Version, Guid EventId, int Attempts);

/// <summary>How a claim treats one stream's rows (§13 open item).</summary>
public enum OutboxOrder
{
    /// <summary>O-1: any due row; two workers may run one stream's rows of one reaction concurrently or out of order.</summary>
    None,
    /// <summary>O-2: only the oldest row per (stream, reaction): the next one is claimable once the head is completed.</summary>
    HeadPerStream,
}

/// <summary>Claim, complete and fail outbox rows with FOR UPDATE SKIP LOCKED (P-1: step 8 builds and tests these).</summary>
public sealed class OutboxClaims(NpgsqlDataSource db, string schema = "tarol")
{
    public async Task<IReadOnlyList<OutboxRow>> Claim(int max, TimeSpan lease, DateTimeOffset now, OutboxOrder order, CancellationToken ct)
    {
        var head = order == OutboxOrder.HeadPerStream
            ? $" AND NOT EXISTS (SELECT 1 FROM {schema}.outbox p WHERE p.tenant = o.tenant AND p.type = o.type AND p.stream_id = o.stream_id AND p.reaction = o.reaction AND p.id < o.id)"
            : "";
        await using var cmd = db.CreateCommand($"""
            WITH c AS (
                SELECT o.id FROM {schema}.outbox o
                WHERE o.due_at <= $1 AND (o.lease_until IS NULL OR o.lease_until < $1){head}
                ORDER BY o.id LIMIT $2
                FOR UPDATE SKIP LOCKED)
            UPDATE {schema}.outbox o SET lease_until = $3, attempts = o.attempts + 1
            FROM c WHERE o.id = c.id
            RETURNING o.id, o.tenant, o.reaction, o.type, o.stream_id, o.version, o.event_id, o.attempts
            """);
        cmd.Parameters.Add(new() { Value = now });
        cmd.Parameters.Add(new() { Value = max });
        cmd.Parameters.Add(new() { Value = now + lease });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OutboxRow>();
        while (await r.ReadAsync(ct))
            rows.Add(new OutboxRow(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.GetInt64(5), r.GetGuid(6), r.GetInt32(7)));
        return rows;
    }

    public async Task Complete(long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"DELETE FROM {schema}.outbox WHERE id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task Fail(long id, DateTimeOffset retryAt, string error, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"UPDATE {schema}.outbox SET due_at = $2, lease_until = NULL, last_error = $3 WHERE id = $1");
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = retryAt });
        cmd.Parameters.Add(new() { Value = error });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Retention (C-8 open item): delete idempotency keys older than the window, in batches.</summary>
public static class Retention
{
    public static async Task<int> PurgeKeys(NpgsqlDataSource db, DateTimeOffset olderThan, int batch, CancellationToken ct, string schema = "tarol")
    {
        await using var cmd = db.CreateCommand($"""
            DELETE FROM {schema}.idempotency_keys k USING (
                SELECT tenant, scope, key FROM {schema}.idempotency_keys WHERE stored_at < $1 ORDER BY stored_at LIMIT $2) d
            WHERE (k.tenant, k.scope, k.key) = (d.tenant, d.scope, d.key)
            """);
        cmd.Parameters.Add(new() { Value = olderThan });
        cmd.Parameters.Add(new() { Value = batch });
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
