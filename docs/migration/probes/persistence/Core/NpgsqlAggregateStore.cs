using System.Buffers;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>How the expected version is enforced (question 4).</summary>
public enum Guard
{
    /// <summary>G-1: conditional UPDATE of the stream row (INSERT … ON CONFLICT DO NOTHING for a new stream).</summary>
    StreamRow,
    /// <summary>G-2: only the events' primary key (tenant, type, stream, version); 23505 → conflict.</summary>
    UniqueEvents,
    /// <summary>G-3: SELECT … FOR UPDATE of the stream row, compare in C#, then write (two round trips).</summary>
    SelectForUpdate,
    /// <summary>G-4: G-1, but a stream row locked by another commit is skipped (FOR UPDATE SKIP LOCKED in the same statement) → conflict at once.</summary>
    FailFast,
}

/// <summary>A unique lookup: L-A (key function at commit) or L-B (expression index on the state JSON).</summary>
public abstract record LookupDefinition(string StreamType, string Name);
public sealed record KeyLookup(string StreamType, string Name, Func<object, string?> KeyOf) : LookupDefinition(StreamType, Name);
public sealed record StateLookup(string StreamType, string Name, string JsonProperty) : LookupDefinition(StreamType, Name);

/// <summary>Which reactions an event has (step 9's generated <c>ReactionsTo</c>; a fake in tests).</summary>
public delegate ReadOnlySpan<string> ReactionTable(DomainEvent domainEvent);

public sealed record NpgsqlStoreOptions
{
    public string Schema { get; init; } = "tarol";
    public Guard Guard { get; init; } = Guard.StreamRow;
    public JsonSerializerOptions? StateOptions { get; init; }
    public ReactionTable? Reactions { get; init; }
    public IReadOnlyList<LookupDefinition> Lookups { get; init; } = [];
}

/// <summary>A recorded event with its global position (ReadAll).</summary>
public readonly record struct PositionedEvent(GlobalPosition Position, string StreamType, RecordedEvent Event);

/// <summary>The checkpoint of a catch-up reader: (transaction id, position), totally ordered.</summary>
public readonly record struct GlobalPosition(ulong Transaction, long Position)
{
    public static GlobalPosition Start => default;
}

public sealed class NpgsqlAggregateStore : IAggregateStore
{
    private readonly NpgsqlDataSource _db;
    private readonly EventCodec _codec;
    private readonly TimeProvider _time;
    private readonly NpgsqlStoreOptions _o;
    private readonly string _s;
    private int _stateLoads;
    private int _replays;

    public NpgsqlAggregateStore(NpgsqlDataSource db, EventCodec codec, TimeProvider time, NpgsqlStoreOptions? options = null)
    {
        _db = db;
        _codec = codec;
        _time = time;
        _o = options ?? new NpgsqlStoreOptions();
        _s = _o.Schema;
    }

    public int StateLoads => Volatile.Read(ref _stateLoads);
    public int Replays => Volatile.Read(ref _replays);

    public async Task EnsureSchema(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(Schema.Sql(_s));
        await cmd.ExecuteNonQueryAsync(ct);
        foreach (var l in _o.Lookups.OfType<StateLookup>())
        {
            // L-B: one partial unique expression index per lookup
            await using var ix = _db.CreateCommand(
                $"CREATE UNIQUE INDEX IF NOT EXISTS lookup_{l.StreamType}_{l.Name} ON {_s}.streams (tenant, (state->>'{l.JsonProperty}')) WHERE type = '{l.StreamType}'");
            await ix.ExecuteNonQueryAsync(ct);
        }
    }

    private static Guid TenantOf(Option<Id<ITenant>> tenant) => tenant.TryGetValue(out Id<ITenant> id) ? id.Value : Guid.Empty;

    // ------------------------------------------------------------------ load

    public async ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken ct)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var type = _codec.StreamTypeOf<T>();
        var tenant = TenantOf(context.Tenant);
        await using var conn = await _db.OpenConnectionAsync(ct);
        if (_o.StateOptions is { } so && T.StateSchema != 0)
        {
            await using var cmd = new NpgsqlCommand($"SELECT schema, state FROM {_s}.streams WHERE tenant = $1 AND type = $2 AND id = $3", conn)
            { Parameters = { new() { Value = tenant }, new() { Value = type }, new() { Value = id.Value } } };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Option.None;
            if (r.GetInt32(0) == T.StateSchema && !r.IsDBNull(1))
            {
                var bytes = r.GetFieldValue<byte[]>(1);
                if (AggregateState.Read(id, bytes, so).TryGetValue(out T? stored))
                {
                    Interlocked.Increment(ref _stateLoads);
                    return stored;
                }
            }
        }
        var history = await ReadStream(conn, tenant, type, id.Value, ct);
        if (history.Length == 0)
            return Option.None;
        Interlocked.Increment(ref _replays);
        return T.Rehydrate(id, history);
    }

    private async Task<StreamEvent[]> ReadStream(NpgsqlConnection conn, Guid tenant, string type, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT version, entity_id, parent_id, alias, alias_version, payload FROM {_s}.events WHERE tenant = $1 AND type = $2 AND stream_id = $3 ORDER BY version", conn)
        { Parameters = { new() { Value = tenant }, new() { Value = type }, new() { Value = id } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<StreamEvent>();
        while (await r.ReadAsync(ct))
        {
            var e = _codec.Decode(type, r.GetString(3), r.GetInt32(4), r.GetFieldValue<byte[]>(5));
            list.Add(new StreamEvent(r.GetInt64(0), r.GetGuid(1), r.IsDBNull(2) ? Guid.Empty : r.GetGuid(2), e));
        }
        return [.. list];
    }

    public async ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken ct)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var type = _codec.StreamTypeOf<T>();
        var def = _o.Lookups.FirstOrDefault(l => l.StreamType == type && l.Name == lookup)
            ?? throw new InvalidOperationException($"No lookup '{lookup}' is defined for {typeof(T).Name}.");
        var sql = def switch
        {
            KeyLookup => $"SELECT stream_id FROM {_s}.lookups WHERE tenant = $1 AND type = $2 AND lookup = $3 AND key = $4",
            StateLookup s => $"SELECT id FROM {_s}.streams WHERE tenant = $1 AND type = $2 AND $3 = $3 AND state->>'{s.JsonProperty}' = $4",
            _ => throw new InvalidOperationException(),
        };
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = TenantOf(context.Tenant) });
        cmd.Parameters.Add(new() { Value = type });
        cmd.Parameters.Add(new() { Value = lookup });
        cmd.Parameters.Add(new() { Value = key });
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? (Id<T>)g : Option.None;
    }

    public async ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT stream_id, version, events FROM {_s}.idempotency_keys WHERE tenant = $1 AND scope = $2 AND key = $3");
        cmd.Parameters.Add(new() { Value = TenantOf(key.Tenant) });
        cmd.Parameters.Add(new() { Value = key.Scope });
        cmd.Parameters.Add(new() { Value = key.Key });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new StoredOutcome(r.GetGuid(0), r.GetInt64(1), r.GetInt32(2)) : Option.None;
    }

    // ------------------------------------------------------------------ append

    public async ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key, CancellationToken ct)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var changes = aggregate.UncommittedChanges.ToArray();
        if (changes.Length == 0)
            return Result.Success();
        if (changes[0].Version != expectedVersion + 1)
            throw new ArgumentException($"The uncommitted changes start at {changes[0].Version}, not at {expectedVersion} + 1.", nameof(expectedVersion));

        var type = _codec.StreamTypeOf<T>();
        var tenant = TenantOf(context.Tenant);
        var id = aggregate.Id.Value;
        var newVersion = changes[^1].Version;
        var at = _time.GetUtcNow();
        var schema = _o.StateOptions is not null ? T.StateSchema : 0;
        byte[]? state = schema != 0 ? AggregateState.Write(aggregate, _o.StateOptions!) : null;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            if (_o.Guard == Guard.SelectForUpdate)
            {
                await using var sel = new NpgsqlCommand($"SELECT version FROM {_s}.streams WHERE tenant = $1 AND type = $2 AND id = $3 FOR UPDATE", conn, tx)
                { Parameters = { new() { Value = tenant }, new() { Value = type }, new() { Value = id } } };
                var actual = await sel.ExecuteScalarAsync(ct) is long v ? v : 0;
                if (actual != expectedVersion)
                {
                    await tx.RollbackAsync(ct);
                    return StoreErrors.VersionConflict(expectedVersion, actual);
                }
            }

            await using var batch = new NpgsqlBatch(conn, tx);
            var guardIndex = -1;
            // 1+2. one statement: the stream row (guard + state) in a data-modifying CTE, the events only if the guard
            //      passed. G-1 is exception-free: a lost race inserts 0 events. G-2 drops the version predicate and
            //      lets the events' primary key raise 23505.
            var buffer = new ArrayBufferWriter<byte>(256 * changes.Length);
            var encoded = new EncodedEvent[changes.Length];
            for (var i = 0; i < changes.Length; i++)
                encoded[i] = _codec.Encode<T>(changes[i].Event, buffer);
            var streamSql = expectedVersion == 0
                ? $"INSERT INTO {_s}.streams (tenant, type, id, version, schema, state, updated_at) VALUES ($1, $2, $3, $18, $19, $20, $4) ON CONFLICT (tenant, type, id) DO NOTHING RETURNING 1"
                : _o.Guard == Guard.FailFast
                ? $"WITH l AS (SELECT 1 FROM {_s}.streams WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $21 FOR UPDATE SKIP LOCKED) "
                  + $"UPDATE {_s}.streams SET version = $18, schema = $19, state = $20, updated_at = $4 WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $21 AND EXISTS (SELECT 1 FROM l) RETURNING 1"
                : $"UPDATE {_s}.streams SET version = $18, schema = $19, state = $20, updated_at = $4 WHERE tenant = $1 AND type = $2 AND id = $3"
                  + (_o.Guard == Guard.UniqueEvents ? " AND $21::bigint IS NOT NULL" : " AND version = $21") + " RETURNING 1";
            var eventsCmd = new NpgsqlBatchCommand(
                $"""
                WITH g AS ({streamSql})
                INSERT INTO {_s}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, parent_id, payload,
                                        recorded_at, actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent)
                SELECT $1, $2, $3, v, e, a, av, en, NULLIF(p, '00000000-0000-0000-0000-000000000000'::uuid), pl, $4, $5, $6, $7, $8, $9, $10
                FROM unnest($11::bigint[], $12::uuid[], $13::text[], $14::int[], $15::uuid[], $16::uuid[], $17::jsonb[]) AS u(v, e, a, av, en, p, pl)
                WHERE {(_o.Guard == Guard.UniqueEvents ? "true" : "EXISTS (SELECT 1 FROM g)")}
                """);
            guardIndex = 0;
            var eventIds = new Guid[changes.Length];
            for (var i = 0; i < changes.Length; i++)
                eventIds[i] = Guid.CreateVersion7(at);
            var actor = context.Actor;
            P(eventsCmd, tenant, type, id, at, actor.Kind.ToString(), actor.Id,
                actor.OnBehalfOf.TryGetValue(out string? obo) ? obo : DBNull.Value,
                context.CorrelationId.Value,
                context.CausationId.TryGetValue(out Id<ICausation> causation) ? causation.Value : DBNull.Value,
                System.Diagnostics.Activity.Current?.Id is { } tp ? tp : DBNull.Value,
                changes.Select(c => c.Version).ToArray(), eventIds,
                encoded.Select(e => e.Type.Alias).ToArray(), encoded.Select(e => e.Type.Version).ToArray(),
                changes.Select(c => c.EntityId).ToArray(), changes.Select(c => c.ParentId).ToArray(),
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb, Value = encoded.Select(e => System.Text.Encoding.UTF8.GetString(e.Payload.Span)).ToArray() },
                newVersion, schema, Jsonb(state), expectedVersion);
            batch.BatchCommands.Add(eventsCmd);

            if (_o.Reactions is { } reactions)
            {
                List<(string Reaction, long Version, Guid EventId)> rows = [];
                for (var i = 0; i < changes.Length; i++)
                    foreach (var reaction in reactions(changes[i].Event))
                        rows.Add((reaction, changes[i].Version, eventIds[i]));
                if (rows.Count > 0)
                    Add(batch, $"INSERT INTO {_s}.outbox (tenant, reaction, type, stream_id, version, event_id, due_at) SELECT $1, r, $2, $3, v, e, $4 FROM unnest($5::text[], $6::bigint[], $7::uuid[]) AS u(r, v, e) WHERE {Ours("$8")}",
                        tenant, type, id, at, rows.Select(r => r.Reaction).ToArray(), rows.Select(r => r.Version).ToArray(), rows.Select(r => r.EventId).ToArray(), eventIds[0]);
            }

            // 3. idempotency key (successes only: rolled back with everything else)
            var keyIndex = -1;
            if (key.TryGetValue(out IdempotencyKey k))
            {
                keyIndex = batch.BatchCommands.Count;
                Add(batch, $"INSERT INTO {_s}.idempotency_keys (tenant, scope, key, stream_id, version, events, stored_at) SELECT $1, $2, $3, $4, $5, $6, $7 WHERE {Ours("$8")} ON CONFLICT (tenant, scope, key) DO NOTHING",
                    TenantOf(k.Tenant), k.Scope, k.Key, id, newVersion, changes.Length, at, eventIds[0]);
            }

            // 4. L-A lookups: move or insert, refused when another stream owns the key
            List<(int Index, string Lookup, string Key)> lookupChecks = [];
            foreach (var def in _o.Lookups.OfType<KeyLookup>().Where(l => l.StreamType == type))
            {
                var newKey = def.KeyOf(aggregate) is { Length: > 0 } nk ? nk : null;
                Add(batch, $"DELETE FROM {_s}.lookups WHERE tenant = $1 AND type = $2 AND lookup = $3 AND stream_id = $4 AND key IS DISTINCT FROM $5 AND {Ours("$6")}",
                    tenant, type, def.Name, id, (object?)newKey ?? DBNull.Value, eventIds[0]);
                if (newKey is null)
                    continue;
                lookupChecks.Add((batch.BatchCommands.Count, def.Name, newKey));
                Add(batch, $"INSERT INTO {_s}.lookups (tenant, type, lookup, key, stream_id) SELECT $1, $2, $3, $4, $5 WHERE {Ours("$6")} ON CONFLICT (tenant, type, lookup, key) DO UPDATE SET stream_id = EXCLUDED.stream_id WHERE lookups.stream_id = EXCLUDED.stream_id",
                    tenant, type, def.Name, newKey, id, eventIds[0]);
            }

            // 5. the version after this statement list: ours on success, the winner's after a lost race (read committed)
            Add(batch, $"SELECT version FROM {_s}.streams WHERE tenant = $1 AND type = $2 AND id = $3", tenant, type, id);
            long actualAfter = 0;
            try
            {
                await using var r = await batch.ExecuteReaderAsync(ct);
                do
                    while (await r.ReadAsync(ct))
                        actualAfter = r.GetInt64(0);
                while (await r.NextResultAsync(ct));
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await tx.RollbackAsync(ct);
                return await MapUnique(e, expectedVersion, key, tenant, type, id, ct);
            }

            if (batch.BatchCommands[guardIndex].RecordsAffected != changes.Length)
            {
                await tx.RollbackAsync(ct);
                return StoreErrors.VersionConflict(expectedVersion, actualAfter);
            }
            if (keyIndex >= 0 && batch.BatchCommands[keyIndex].RecordsAffected != 1)
            {
                await tx.RollbackAsync(ct);
                return StoreErrors.DuplicateKey(k);
            }
            foreach (var (index, lookup, lookupKey) in lookupChecks)
                if (batch.BatchCommands[index].RecordsAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    return StoreErrors.LookupTaken(lookup, lookupKey);
                }
            await tx.CommitAsync(ct);
            return Result.Success();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return await MapUnique(e, expectedVersion, key, tenant, type, id, ct);
        }
    }

    private async Task<Result> MapUnique(PostgresException e, long expected, Option<IdempotencyKey> key, Guid tenant, string type, Guid id, CancellationToken ct) => e.TableName switch
    {
        "events" or "streams" when e.ConstraintName is "events_pkey" or "streams_pkey" => StoreErrors.VersionConflict(expected, await ActualVersion(tenant, type, id, ct)),
        "idempotency_keys" when key.TryGetValue(out IdempotencyKey k) => StoreErrors.DuplicateKey(k),
        "streams" when e.ConstraintName?.StartsWith("lookup_", StringComparison.Ordinal) == true
            => StoreErrors.LookupTaken(e.ConstraintName["lookup_".Length..], "?"),
        _ => throw new InvalidOperationException($"unexpected unique violation {e.ConstraintName}", e),
    };

    // a follow-up statement runs only if this transaction's events went in (G-1 does not abort on a lost race)
    private string Ours(string eventIdParameter) => $"EXISTS (SELECT 1 FROM {_s}.events WHERE event_id = {eventIdParameter})";

    private async Task<long> ActualVersion(Guid tenant, string type, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT version FROM {_s}.streams WHERE tenant = $1 AND type = $2 AND id = $3");
        cmd.Parameters.Add(new() { Value = tenant });
        cmd.Parameters.Add(new() { Value = type });
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteScalarAsync(ct) is long v ? v : 0;
    }

    private static NpgsqlParameter Jsonb(byte[]? state)
        => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = state is null ? DBNull.Value : System.Text.Encoding.UTF8.GetString(state) };

    private static void Add(NpgsqlBatch batch, string sql, params object?[] values)
    {
        var cmd = new NpgsqlBatchCommand(sql);
        P(cmd, values);
        batch.BatchCommands.Add(cmd);
    }

    private static void P(NpgsqlBatchCommand cmd, params object?[] values)
    {
        foreach (var v in values)
            cmd.Parameters.Add(v as NpgsqlParameter ?? new NpgsqlParameter { Value = v ?? DBNull.Value });
    }

    // ------------------------------------------------------------------ RebuildStates<T>()

    public async Task<int> RebuildStates<T>(OperationContext context, CancellationToken ct) where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (_o.StateOptions is not { } so || T.StateSchema == 0)
            return 0;
        var type = _codec.StreamTypeOf<T>();
        var rebuilt = 0;
        await using var conn = await _db.OpenConnectionAsync(ct);
        List<(Guid Tenant, Guid Id, long Version)> stale = [];
        await using (var cmd = new NpgsqlCommand($"SELECT tenant, id, version FROM {_s}.streams WHERE type = $1 AND schema <> $2", conn)
        { Parameters = { new() { Value = type }, new() { Value = T.StateSchema } } })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                stale.Add((r.GetGuid(0), r.GetGuid(1), r.GetInt64(2)));
        foreach (var (tenant, id, version) in stale)
        {
            var aggregate = T.Rehydrate((Id<T>)id, await ReadStream(conn, tenant, type, id, ct));
            await using var up = new NpgsqlCommand(
                $"UPDATE {_s}.streams SET schema = $4, state = $5 WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $6", conn)
            {
                Parameters = { new() { Value = tenant }, new() { Value = type }, new() { Value = id }, new() { Value = T.StateSchema },
                    new() { Value = System.Text.Encoding.UTF8.GetString(AggregateState.Write(aggregate, so)), NpgsqlDbType = NpgsqlDbType.Jsonb },
                    new() { Value = version } },
            };
            rebuilt += await up.ExecuteNonQueryAsync(ct);   // 0 when a commit moved the stream meanwhile (it wrote a current state)
        }
        _ = context;
        return rebuilt;
    }

    // ------------------------------------------------------------------ ReadAll (P-3)

    public async IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, bool naive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sql = naive
            ? $"SELECT tx_id::text AS tx_text, position, type, stream_id, version, entity_id, parent_id, alias, alias_version, payload, event_id, recorded_at, actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, tenant FROM {_s}.events WHERE position > $2 AND $1::xid8 IS NOT NULL ORDER BY events.position LIMIT $3"
            : $"SELECT tx_id::text AS tx_text, position, type, stream_id, version, entity_id, parent_id, alias, alias_version, payload, event_id, recorded_at, actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, tenant FROM {_s}.events WHERE (tx_id, position) > ($1::xid8, $2) AND tx_id < pg_snapshot_xmin(pg_current_snapshot()) ORDER BY events.tx_id, events.position LIMIT $3";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = after.Transaction.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new() { Value = after.Position });
        cmd.Parameters.Add(new() { Value = limit });
        cmd.CommandText = cmd.CommandText.Replace("$1::xid8", "$1::text::xid8", StringComparison.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var type = r.GetString(2);
            var e = _codec.Decode(type, r.GetString(7), r.GetInt32(8), r.GetFieldValue<byte[]>(9));
            var tenant = r.GetGuid(17);
            var kind = Enum.Parse<ActorKind>(r.GetString(12));
            var actor = kind == ActorKind.User ? Actor.User(r.GetString(13))
                : Actor.System(r.GetString(13), r.IsDBNull(14) ? default(Option<string>) : r.GetString(14));
            var context = new OperationContext(actor, tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant,
                (Id<ICorrelation>)r.GetGuid(15), r.IsDBNull(16) ? Option.None : (Id<ICausation>)r.GetGuid(16));
            var recorded = new RecordedEvent(r.GetGuid(3),
                new StreamEvent(r.GetInt64(4), r.GetGuid(5), r.IsDBNull(6) ? Guid.Empty : r.GetGuid(6), e),
                r.GetGuid(10), r.GetFieldValue<DateTimeOffset>(11), context);
            yield return new PositionedEvent(new GlobalPosition(ulong.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture), r.GetInt64(1)), type, recorded);
        }
    }
}
