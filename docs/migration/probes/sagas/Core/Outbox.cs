using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas;

/// <summary>A claimed row, store-agnostic (the port the worker runs on; the real one would live in Tarol.Persistence).</summary>
public sealed record ClaimedRow(long Id, Guid ClaimId, string Reaction, int Attempts, string? LastError, string StreamType,
    RecordedEvent Event, string? Traceparent);

/// <summary>A dead letter, for the human who re-drives it.</summary>
public sealed record DeadLetter(long Id, string Reaction, Guid EventId, Guid StreamId, int Attempts, string? LastError, DateTimeOffset DeadAt);

/// <summary>The outbox port: both stores implement it (in-memory parity, question 5).</summary>
public interface IReactionOutbox
{
    Task<IReadOnlyList<ClaimedRow>> Claim(int max, TimeSpan lease, CancellationToken ct);
    Task<bool> Complete(ClaimedRow row, CancellationToken ct);
    Task<bool> Retry(ClaimedRow row, DateTimeOffset at, string error, CancellationToken ct);
    Task<bool> Bury(ClaimedRow row, string error, CancellationToken ct);
    Task<IReadOnlyList<DeadLetter>> DeadLetters(CancellationToken ct);
    Task<bool> Redrive(long id, CancellationToken ct);
    Task<long> Pending(CancellationToken ct);
}

/// <summary>DL-A: a <c>dead_at</c> column on the outbox row; DL-B: the row moves to an <c>outbox_dead</c> table.</summary>
public enum DeadLetterDesign { Column, Table }

/// <summary>
/// The probe's outbox on the real step 8 tables. The claim is a copy of <c>NpgsqlOutbox.Claim</c> plus the
/// <c>traceparent</c> column (8C does not read it) and, for DL-A, the dead filter. <see cref="Install"/> adds what step 9
/// would add to the schema script. The schedule trigger stands in for the store writing <c>due_at</c> from the generated
/// schedule (the real store would take it from <see cref="ReactionSet.DueAt"/> at commit).
/// </summary>
public sealed class PgOutbox : IReactionOutbox
{
    private readonly NpgsqlDataSource _db;
    private readonly EventCodec _codec;
    private readonly TimeProvider _time;
    private readonly string _s;
    private readonly DeadLetterDesign _design;
    private readonly string[] _unordered;

    /// <param name="unordered">Scheduled reactions (timeouts): ordered by due time only, exempt from the head-per-stream rule (T-2).</param>
    public PgOutbox(NpgsqlDataSource db, EventCodec codec, TimeProvider time, string schema, DeadLetterDesign design, string[]? unordered = null)
        => (_db, _codec, _time, _s, _design, _unordered) = (db, codec, time, schema, design, unordered ?? []);

    public DeadLetterDesign Design => _design;

    /// <summary>Schema additions: DL-A column + index or DL-B table; due_at from the event's <c>dueAt</c>; NOTIFY on insert.</summary>
    public async Task Install(bool notify, CancellationToken ct)
    {
        var sql = _design == DeadLetterDesign.Column
            ? $"""
               ALTER TABLE {_s}.outbox ADD COLUMN IF NOT EXISTS dead_at timestamptz NULL;
               CREATE INDEX IF NOT EXISTS outbox_dead ON {_s}.outbox (dead_at) WHERE dead_at IS NOT NULL;
               """
            : $"""
               CREATE TABLE IF NOT EXISTS {_s}.outbox_dead (
                   id bigint PRIMARY KEY, tenant uuid NOT NULL, reaction text NOT NULL, type text NOT NULL, stream_id uuid NOT NULL,
                   version bigint NOT NULL, event_id uuid NOT NULL, attempts integer NOT NULL, last_error text NULL,
                   dead_at timestamptz NOT NULL);
               """;
        sql += $"""

            CREATE OR REPLACE FUNCTION {_s}.outbox_schedule() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN
              NEW.due_at := greatest(NEW.due_at, coalesce(
                (SELECT (e.payload->>'dueAt')::timestamptz FROM {_s}.events e WHERE e.event_id = NEW.event_id), NEW.due_at));
              RETURN NEW;
            END $f$;
            DROP TRIGGER IF EXISTS outbox_schedule ON {_s}.outbox;
            CREATE TRIGGER outbox_schedule BEFORE INSERT ON {_s}.outbox FOR EACH ROW EXECUTE FUNCTION {_s}.outbox_schedule();
            CREATE OR REPLACE FUNCTION {_s}.outbox_notify() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN PERFORM pg_notify('{_s}_outbox', ''); RETURN NULL; END $f$;
            DROP TRIGGER IF EXISTS outbox_notify ON {_s}.outbox;
            """;
        if (notify)
            sql += $"CREATE TRIGGER outbox_notify AFTER INSERT ON {_s}.outbox FOR EACH STATEMENT EXECUTE FUNCTION {_s}.outbox_notify();";
        await using var command = _db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    public string Channel => _s + "_outbox";

    private string Live(string alias) => _design == DeadLetterDesign.Column ? $"AND {alias}.dead_at IS NULL" : "";

    public async Task<IReadOnlyList<ClaimedRow>> Claim(int max, TimeSpan lease, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var sql = $"""
            WITH c AS (
                SELECT o.id FROM {_s}.outbox o
                WHERE o.due_at <= $1 AND (o.lease_until IS NULL OR o.lease_until <= $1) {Live("o")}
                  AND (o.reaction = ANY($4) OR NOT EXISTS (SELECT 1 FROM {_s}.outbox p
                                  WHERE p.tenant = o.tenant AND p.type = o.type AND p.stream_id = o.stream_id
                                    AND p.reaction = o.reaction AND p.id < o.id {Live("p")}))
                ORDER BY o.id LIMIT $2
                FOR UPDATE SKIP LOCKED),
            u AS (
                UPDATE {_s}.outbox o SET claim_id = gen_random_uuid(), lease_until = $3, attempts = o.attempts + 1
                FROM c WHERE o.id = c.id
                RETURNING o.id, o.claim_id, o.reaction, o.attempts, o.last_error, o.event_id)
            SELECT u.id, u.claim_id, u.reaction, u.attempts, u.last_error,
                   e.type, e.stream_id, e.version, e.entity_id, e.parent_id, e.alias, e.alias_version, e.payload, e.event_id,
                   e.recorded_at, e.actor_kind, e.actor_id, e.on_behalf_of, e.correlation_id, e.causation_id, e.tenant, e.traceparent
            FROM u JOIN {_s}.events e ON e.event_id = u.event_id
            ORDER BY u.id
            """;
        await using var command = _db.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = max });
        command.Parameters.Add(new NpgsqlParameter { Value = now + lease });
        command.Parameters.Add(new NpgsqlParameter { Value = _unordered });
        await using var r = await command.ExecuteReaderAsync(ct);
        var rows = new List<ClaimedRow>();
        while (await r.ReadAsync(ct))
        {
            var type = r.GetString(5);
            var payload = r.GetFieldValue<byte[]>(12);
            var domainEvent = _codec.Decode(type, new EventType(r.GetString(10), r.GetInt32(11)), payload);
            var tenant = r.GetGuid(20);
            var actor = new Actor(Enum.Parse<ActorKind>(r.GetString(15)), r.GetString(16), r.IsDBNull(17) ? Option.None : r.GetString(17));
            var context = new OperationContext(actor, tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant,
                (Id<ICorrelation>)r.GetGuid(18), r.IsDBNull(19) ? Option.None : (Id<ICausation>)r.GetGuid(19));
            var recorded = new RecordedEvent(r.GetGuid(6),
                new StreamEvent(r.GetInt64(7), r.GetGuid(8), r.IsDBNull(9) ? Guid.Empty : r.GetGuid(9), domainEvent),
                r.GetGuid(13), r.GetFieldValue<DateTimeOffset>(14), context);
            rows.Add(new ClaimedRow(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4),
                type, recorded, r.IsDBNull(21) ? null : r.GetString(21)));
        }
        return rows;
    }

    public Task<bool> Complete(ClaimedRow row, CancellationToken ct)
        => Exec($"DELETE FROM {_s}.outbox WHERE id = $1 AND claim_id = $2", ct, row.Id, row.ClaimId);

    public Task<bool> Retry(ClaimedRow row, DateTimeOffset at, string error, CancellationToken ct)
        => Exec($"UPDATE {_s}.outbox SET due_at = $3, claim_id = NULL, lease_until = NULL, last_error = $4 WHERE id = $1 AND claim_id = $2",
            ct, row.Id, row.ClaimId, at, error);

    public Task<bool> Bury(ClaimedRow row, string error, CancellationToken ct)
        => _design == DeadLetterDesign.Column
            ? Exec($"UPDATE {_s}.outbox SET dead_at = $3, claim_id = NULL, lease_until = NULL, last_error = $4 WHERE id = $1 AND claim_id = $2",
                ct, row.Id, row.ClaimId, _time.GetUtcNow(), error)
            : Exec($"""
                WITH d AS (DELETE FROM {_s}.outbox WHERE id = $1 AND claim_id = $2
                           RETURNING id, tenant, reaction, type, stream_id, version, event_id, attempts)
                INSERT INTO {_s}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, last_error, dead_at)
                SELECT id, tenant, reaction, type, stream_id, version, event_id, attempts, $4, $3 FROM d
                """, ct, row.Id, row.ClaimId, _time.GetUtcNow(), error);

    public async Task<IReadOnlyList<DeadLetter>> DeadLetters(CancellationToken ct)
    {
        var sql = _design == DeadLetterDesign.Column
            ? $"SELECT id, reaction, event_id, stream_id, attempts, last_error, dead_at FROM {_s}.outbox WHERE dead_at IS NOT NULL ORDER BY id"
            : $"SELECT id, reaction, event_id, stream_id, attempts, last_error, dead_at FROM {_s}.outbox_dead ORDER BY id";
        await using var command = _db.CreateCommand(sql);
        await using var r = await command.ExecuteReaderAsync(ct);
        var list = new List<DeadLetter>();
        while (await r.ReadAsync(ct))
            list.Add(new DeadLetter(r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    /// <summary>
    /// DL-A: the row is live again under its old id, so it is the head of its (stream, reaction) again.
    /// DL-B: a new row with a new id, behind the rows that waited meanwhile.
    /// </summary>
    public Task<bool> Redrive(long id, CancellationToken ct)
        => _design == DeadLetterDesign.Column
            ? Exec($"UPDATE {_s}.outbox SET dead_at = NULL, attempts = 0, last_error = NULL, due_at = $2 WHERE id = $1 AND dead_at IS NOT NULL",
                ct, id, _time.GetUtcNow())
            : Exec($"""
                WITH d AS (DELETE FROM {_s}.outbox_dead WHERE id = $1 RETURNING tenant, reaction, type, stream_id, version, event_id)
                INSERT INTO {_s}.outbox (tenant, reaction, type, stream_id, version, event_id, due_at)
                SELECT tenant, reaction, type, stream_id, version, event_id, $2 FROM d
                """, ct, id, _time.GetUtcNow());

    public async Task<long> Pending(CancellationToken ct)
    {
        await using var command = _db.CreateCommand($"SELECT count(*) FROM {_s}.outbox WHERE true {Live("outbox")}");
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Row ids of one stream's (live) rows, oldest first, for ordering checks.</summary>
    public async Task<IReadOnlyList<long>> Ids(CancellationToken ct)
    {
        await using var command = _db.CreateCommand($"SELECT id FROM {_s}.outbox WHERE true {Live("outbox")} ORDER BY id");
        await using var r = await command.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await r.ReadAsync(ct))
            ids.Add(r.GetInt64(0));
        return ids;
    }

    private async Task<bool> Exec(string sql, CancellationToken ct, params object[] values)
    {
        await using var command = _db.CreateCommand(sql);
        foreach (var v in values)
            command.Parameters.Add(new NpgsqlParameter { Value = v });
        return await command.ExecuteNonQueryAsync(ct) >= 1;
    }

}
