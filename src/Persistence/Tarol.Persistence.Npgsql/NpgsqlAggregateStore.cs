using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// The <see cref="IAggregateStore"/> and <see cref="IEventFeed"/> on PostgreSQL, with plain Npgsql
/// (docs/architecture/persistence.md). Every commit writes, in one transaction and one round trip, the events with their
/// metadata, the aggregate's current state as queryable JSON, the unique lookups and the idempotency key; the tenant of
/// the <see cref="OperationContext"/> is the first column of every key.
/// <code>
/// var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
/// var codec = new EventCodec(ValueObjectJson.Options(AppJson.Default)).Add&lt;Auction&gt;();
/// var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new() { Lookups = lookups });
/// await store.EnsureSchema(ct);
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// A lost race is a row count, not an exception: the stream row is updated only at the expected version, the events are
/// inserted only if that update happened, and every later statement only if the events went in; the store rolls back and
/// returns <see cref="StoreErrors.VersionConflict"/> with the version it found. Database errors other than the mapped
/// unique violations are exceptions.
/// </para>
/// <para>
/// Loads read the stored state when its schema is the aggregate's <c>StateSchema</c>; otherwise, without a state, for
/// <c>[LoadByReplay]</c> aggregates, or when the state does not parse, they replay the events, and the next commit
/// rewrites the state. Register every aggregate with the codec. The data source needs arrays
/// (<see cref="NpgsqlSlimDataSourceBuilder"/>: <c>EnableArrays()</c>).
/// </para>
/// </remarks>
public sealed class NpgsqlAggregateStore : IAggregateStore, IEventFeed
{
    private static readonly Guid NoTenant = Guid.Empty;
    private readonly NpgsqlDataSource _db;
    private readonly EventCodec _codec;
    private readonly TimeProvider _time;
    private readonly JsonSerializerOptions _stateOptions;
    private readonly StoreLookups _lookups;
    private readonly IOutboxReactions? _reactions;
    private readonly bool _notify;
    private readonly Sql _sql;

    /// <summary>Creates a store.</summary>
    /// <param name="dataSource">The data source (with arrays enabled).</param>
    /// <param name="codec">The event codec with every aggregate registered.</param>
    /// <param name="time">The clock of <see cref="RecordedEvent.RecordedAt"/> and of the event ids.</param>
    /// <param name="options">Schema name, state options and lookups.</param>
    public NpgsqlAggregateStore(NpgsqlDataSource dataSource, EventCodec codec, TimeProvider time, NpgsqlStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(time);
        options ??= new NpgsqlStoreOptions();
        _db = dataSource;
        _codec = codec;
        _time = time;
        _stateOptions = options.StateOptions ?? codec.Options;
        _lookups = options.Lookups ?? new StoreLookups();
        _sql = new Sql(options.Schema);
        _reactions = options.Reactions;
        _notify = options.Notify;
        Outbox = new NpgsqlOutbox(dataSource, codec, time, options.Schema, options.Reactions, options.Notify);
    }

    /// <summary>Claims, completes, retries and buries the outbox rows the commits wrote (<see cref="NpgsqlStoreOptions.Reactions"/>).</summary>
    public NpgsqlOutbox Outbox { get; }

    /// <summary>The PostgreSQL schema of the tables.</summary>
    public string Schema => _sql.Schema;

    /// <summary>Creates the tables and indexes that are missing (<see cref="NpgsqlSchema.Script"/>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">The database holds a newer schema version than this package.</exception>
    public async Task EnsureSchema(CancellationToken cancellationToken)
    {
        await using (var create = _db.CreateCommand(NpgsqlSchema.Script(Schema)))
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var version = _db.CreateCommand($"SELECT max(version) FROM {Schema}.schema_version");
        if (await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int stored && stored > NpgsqlSchema.Version)
            throw new InvalidOperationException($"The schema '{Schema}' has version {stored}; this Tarol.Persistence.Npgsql knows version {NpgsqlSchema.Version}.");
    }

    // ------------------------------------------------------------------------------------------------------------ reads

    /// <inheritdoc />
    public async ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var type = _codec.StreamTypeOf<T>();
        var tenant = TenantOf(context.Tenant);
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using (var stream = new NpgsqlCommand(_sql.LoadState, connection))
            {
                Add(stream, tenant, type, id.Value);
                var reader = await stream.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        return Option.None;
                    if (T.StateSchema != 0 && reader.GetInt32(0) == T.StateSchema && !reader.IsDBNull(1)
                        && ReadState(id, reader.GetFieldValue<byte[]>(1)).TryGetValue(out T? stored))
                        return stored;
                }
            }

            var history = await ReadStream(connection, tenant, type, id.Value, cancellationToken).ConfigureAwait(false);
            return Replay(id, history);
        }
    }

    // A state that does not parse is derived data: replay instead, the next commit rewrites it (P-12).
    private Option<T> ReadState<T>(Id<T> id, byte[] state)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        try
        {
            return AggregateState.Read(id, state, _stateOptions);
        }
        catch (JsonException)
        {
            return Option.None;
        }
    }

    private async Task<StreamEvent[]> ReadStream(NpgsqlConnection connection, Guid tenant, string type, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_sql.LoadEvents, connection);
        Add(command, tenant, type, id);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var events = new List<StreamEvent>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var domainEvent = _codec.Decode(type, new EventType(reader.GetString(3), reader.GetInt32(4)), reader.GetFieldValue<byte[]>(5));
                events.Add(new StreamEvent(reader.GetInt64(0), reader.GetGuid(1), reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2), domainEvent));
            }
            return [.. events];
        }
    }

    /// <inheritdoc />
    public async ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookup);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_lookups.IsDefined<T>(lookup))
            throw new InvalidOperationException($"No lookup '{lookup}' is defined for {typeof(T).Name}; define it on the StoreLookups of the store's options.");
        await using var command = _db.CreateCommand(_sql.Find);
        Add(command, TenantOf(context.Tenant), _codec.StreamTypeOf<T>(), lookup, key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id ? (Id<T>)id : Option.None;
    }

    /// <inheritdoc />
    public async ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = _db.CreateCommand(_sql.Seen);
        Add(command, TenantOf(key.Tenant), key.Scope, key.Key);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new StoredOutcome(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2))
                : Option.None;
    }

    // ----------------------------------------------------------------------------------------------------------- append

    /// <inheritdoc />
    public async ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        var changes = aggregate.UncommittedChanges.ToArray();
        if (changes.Length == 0)
            return Result.Success();
        if (changes[0].Version != expectedVersion + 1)
            throw new ArgumentException(
                $"The uncommitted changes of {typeof(T).Name} start at version {changes[0].Version}, not at expected version {expectedVersion} + 1.",
                nameof(expectedVersion));

        var type = _codec.StreamTypeOf<T>();
        var tenant = TenantOf(context.Tenant);
        var id = aggregate.Id.Value;
        var version = changes[^1].Version;
        var at = _time.GetUtcNow();
        var schema = T.StateSchema;
        var state = schema != 0 ? Encoding.UTF8.GetString(AggregateState.Write(aggregate, _stateOptions)) : null;

        var versions = new long[changes.Length];
        var eventIds = new Guid[changes.Length];
        var aliases = new string[changes.Length];
        var aliasVersions = new int[changes.Length];
        var entities = new Guid[changes.Length];
        var parents = new Guid[changes.Length];
        var payloads = new string[changes.Length];
        var buffer = new ArrayBufferWriter<byte>(256);
        for (var i = 0; i < changes.Length; i++)
        {
            buffer.ResetWrittenCount();
            var stored = _codec.Encode<T>(changes[i].Event, buffer);
            versions[i] = changes[i].Version;
            eventIds[i] = Guid.CreateVersion7(at);
            aliases[i] = stored.Alias;
            aliasVersions[i] = stored.Version;
            entities[i] = changes[i].EntityId;
            parents[i] = changes[i].ParentId;
            payloads[i] = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        var actor = context.Actor;
        var batch = new NpgsqlBatch();
        await using (batch.ConfigureAwait(false))
        {
            // 1. guard + state + events, one statement: the events go in only if the stream row was at the expected version
            var events = Command(batch, expectedVersion == 0 ? _sql.CreateStream : _sql.AppendStream,
                tenant, type, id, at, actor.Kind.ToString(), actor.Id, actor.OnBehalfOf.TryGetValue(out string? onBehalfOf) ? onBehalfOf : null,
                context.CorrelationId.Value, context.CausationId.TryGetValue(out Id<ICausation> causation) ? causation.Value : null,
                Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null,
                versions, eventIds, aliases, aliasVersions, entities, parents, Jsonb(payloads, NpgsqlDbType.Array | NpgsqlDbType.Jsonb),
                version, schema, Jsonb(state, NpgsqlDbType.Jsonb), expectedVersion);

            // 2. everything else only if this commit's first event exists
            NpgsqlBatchCommand? keyCommand = null;
            if (key.TryGetValue(out IdempotencyKey idempotency))
                keyCommand = Command(batch, _sql.StoreKey, TenantOf(idempotency.Tenant), idempotency.Scope, idempotency.Key, id, version, changes.Length, at, eventIds[0]);
            var lookupCommands = new List<(NpgsqlBatchCommand Command, string Lookup, string Key)>();
            foreach (var (lookup, lookupKey) in _lookups.KeysOf(aggregate))
            {
                _ = Command(batch, _sql.ReleaseLookup, tenant, type, lookup, id, lookupKey, eventIds[0]);
                if (lookupKey is not null)
                    lookupCommands.Add((Command(batch, _sql.ClaimLookup, tenant, type, lookup, lookupKey, id, eventIds[0]), lookup, lookupKey));
            }

            // outbox rows: one per (event, reaction), in this commit (C-2, P-1)
            if (_reactions is not null)
            {
                var (reactions, rowVersions, rowEvents, rowDue, rowScheduled) = OutboxRows(_reactions, changes, eventIds, at);
                if (reactions.Length > 0)
                {
                    _ = Command(batch, _sql.WriteOutbox, tenant, type, id, reactions, rowVersions, rowEvents, rowDue, rowScheduled, eventIds[0]);
                    if (_notify)
                        _ = Command(batch, _sql.NotifyOutbox, Outbox.Channel, eventIds[0]);
                }
            }

            // 3. the version after these statements: ours, or the winner's after a lost race (read committed)
            _ = Command(batch, _sql.StreamVersion, tenant, type, id);

            var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    batch.Connection = connection;
                    batch.Transaction = transaction;
                    long actual = 0;
                    try
                    {
                        var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false))
                        {
                            do
                            {
                                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                    actual = reader.GetInt64(0);
                            }
                            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                        }
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return await UniqueViolation(e, expectedVersion, key, tenant, type, id, cancellationToken).ConfigureAwait(false);
                    }

                    Violation? refused = events.RecordsAffected != changes.Length ? StoreErrors.VersionConflict(expectedVersion, actual)
                        : keyCommand is { RecordsAffected: not 1 } ? StoreErrors.DuplicateKey(idempotency)
                        : lookupCommands.FirstOrDefault(static l => l.Command.RecordsAffected != 1) is { Command: not null } taken
                            ? StoreErrors.LookupTaken(taken.Lookup, taken.Key)
                        : null;
                    if (refused is not null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return refused;
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Result.Success();
                }
            }
        }
    }

    // Defensive: the guarded statements avoid these, but a constraint is the last word.
    private async Task<Result> UniqueViolation(PostgresException e, long expected, Option<IdempotencyKey> key, Guid tenant, string type, Guid id,
        CancellationToken cancellationToken)
    {
        switch (e.ConstraintName)
        {
            case "events_pkey" or "streams_pkey":
                await using (var command = _db.CreateCommand(_sql.StreamVersion))
                {
                    Add(command, tenant, type, id);
                    var actual = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long v ? v : 0;
                    return StoreErrors.VersionConflict(expected, actual);
                }
            case "idempotency_keys_pkey" when key.TryGetValue(out IdempotencyKey k):
                return StoreErrors.DuplicateKey(k);
            default:
                throw new InvalidOperationException($"Unexpected unique violation of '{e.ConstraintName}' in schema '{Schema}'.", e);
        }
    }

    // -------------------------------------------------------------------------------------------------------- the feed

    /// <inheritdoc />
    public async IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var command = _db.CreateCommand(_sql.ReadAll);
        Add(command, after.Transaction.ToString(CultureInfo.InvariantCulture), after.Position, limit);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                yield return RecordedEvents.Read(reader, 0, _codec);
        }
    }

    // ------------------------------------------------------------------------------------------------------ maintenance

    /// <summary>
    /// Rewrites the stored state of every <typeparamref name="T"/> stream whose schema differs from <c>T.StateSchema</c>
    /// (after a model change), by replaying its events. Optional: loads replay such streams anyway. A stream that a commit
    /// moved meanwhile is skipped (that commit wrote a current state).
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="cancellationToken">Cancels the rebuild between streams.</param>
    /// <returns>How many states were rewritten.</returns>
    public async Task<int> RebuildStates<T>(CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (T.StateSchema == 0)
            return 0;
        var type = _codec.StreamTypeOf<T>();
        var rebuilt = 0;
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var (lastTenant, lastId) = (Guid.Empty, Guid.Empty);
            while (true)
            {
                var stale = new List<(Guid Tenant, Guid Id, long Version)>();
                await using (var page = new NpgsqlCommand(_sql.StaleStates, connection))
                {
                    Add(page, type, T.StateSchema, lastTenant, lastId, 500);
                    var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            stale.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)));
                }
                if (stale.Count == 0)
                    return rebuilt;
                foreach (var (tenant, id, version) in stale)
                {
                    var history = await ReadStream(connection, tenant, type, id, cancellationToken).ConfigureAwait(false);
                    var aggregate = Replay((Id<T>)id, history);
                    await using var update = new NpgsqlCommand(_sql.RewriteState, connection);
                    Add(update, tenant, type, id, T.StateSchema, Jsonb(Encoding.UTF8.GetString(AggregateState.Write(aggregate, _stateOptions)), NpgsqlDbType.Jsonb), version);
                    rebuilt += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                (lastTenant, lastId, _) = stale[^1];
            }
        }
    }

    /// <summary>Deletes idempotency keys stored before <paramref name="olderThan"/>, at most <paramref name="batch"/> per call (P-11).</summary>
    /// <param name="olderThan">The end of the retention window, e.g. now − 7 days.</param>
    /// <param name="batch">The most keys to delete in one statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many keys were deleted; call again while it equals <paramref name="batch"/>.</returns>
    /// <remarks>A redelivery after its key was purged commits again. The store never purges on its own.</remarks>
    public async Task<int> PurgeKeys(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        await using var command = _db.CreateCommand(_sql.PurgeKeys);
        Add(command, olderThan, batch);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------------- helpers

    private static (string[] Reactions, long[] Versions, Guid[] Events, DateTimeOffset[] Due, bool[] Scheduled) OutboxRows(
        IOutboxReactions table, StreamEvent[] changes, Guid[] eventIds, DateTimeOffset at)
    {
        var reactions = new List<string>();
        var versions = new List<long>();
        var events = new List<Guid>();
        var due = new List<DateTimeOffset>();
        var scheduled = new List<bool>();
        for (var i = 0; i < changes.Length; i++)
        {
            var first = reactions.Count;
            foreach (var reaction in table.ReactionsTo(changes[i].Event))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
                for (var j = first; j < reactions.Count; j++)
                    if (reactions[j] == reaction)
                        throw new InvalidOperationException($"The reaction '{reaction}' is listed twice for one event.");
                reactions.Add(reaction);
                versions.Add(changes[i].Version);
                events.Add(eventIds[i]);
                due.Add(table.DueAt(changes[i].Event, reaction) is { } dueAt && dueAt > at ? dueAt : at);
                scheduled.Add(table.IsScheduled(reaction));
            }
        }
        return ([.. reactions], [.. versions], [.. events], [.. due], [.. scheduled]);
    }

    private static T Replay<T>(Id<T> id, StreamEvent[] history)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => T.Rehydrate(id, history);   // outside the async methods: a span cannot live across an await

    private static Guid TenantOf(Option<Id<ITenant>> tenant) => tenant.TryGetValue(out Id<ITenant> id) ? id.Value : NoTenant;

    private static NpgsqlParameter Jsonb(object? value, NpgsqlDbType type) => new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };

    private static NpgsqlParameter Parameter(object? value) => value as NpgsqlParameter ?? new NpgsqlParameter { Value = value ?? DBNull.Value };

    private static void Add(NpgsqlCommand command, params object?[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(Parameter(value));
    }

    private static NpgsqlBatchCommand Command(NpgsqlBatch batch, string sql, params object?[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values)
            command.Parameters.Add(Parameter(value));
        batch.BatchCommands.Add(command);
        return command;
    }

    /// <summary>Every statement, built once per schema name.</summary>
    private sealed class Sql(string schema)
    {
        public string Schema => schema;

        private string Events => $"""
            INSERT INTO {schema}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, parent_id, payload,
                                         recorded_at, actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent)
            SELECT $1, $2, $3, v, e, a, av, en, NULLIF(p, '00000000-0000-0000-0000-000000000000'::uuid), pl, $4, $5, $6, $7, $8, $9, $10
            FROM unnest($11::bigint[], $12::uuid[], $13::text[], $14::int[], $15::uuid[], $16::uuid[], $17::jsonb[]) AS u(v, e, a, av, en, p, pl)
            WHERE EXISTS (SELECT 1 FROM g)
            """;

        private string Ours(string eventId) => $"EXISTS (SELECT 1 FROM {schema}.events WHERE event_id = {eventId})";

        public string CreateStream => $"""
            WITH g AS (INSERT INTO {schema}.streams (tenant, type, id, version, schema, state, updated_at)
                       VALUES ($1, $2, $3, $18, $19, $20, $4) ON CONFLICT (tenant, type, id) DO NOTHING
                       RETURNING 1)
            {Events} AND $21::bigint = 0
            """;

        public string AppendStream => $"""
            WITH g AS (UPDATE {schema}.streams SET version = $18, schema = $19, state = $20, updated_at = $4
                       WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $21
                       RETURNING 1)
            {Events}
            """;

        public string StoreKey => $"""
            INSERT INTO {schema}.idempotency_keys (tenant, scope, key, stream_id, version, events, stored_at)
            SELECT $1, $2, $3, $4, $5, $6, $7 WHERE {Ours("$8")}
            ON CONFLICT (tenant, scope, key) DO NOTHING
            """;

        public string ReleaseLookup => $"""
            DELETE FROM {schema}.lookups
            WHERE tenant = $1 AND type = $2 AND lookup = $3 AND stream_id = $4 AND key IS DISTINCT FROM $5::text AND {Ours("$6")}
            """;

        public string ClaimLookup => $"""
            INSERT INTO {schema}.lookups (tenant, type, lookup, key, stream_id)
            SELECT $1, $2, $3, $4, $5 WHERE {Ours("$6")}
            ON CONFLICT (tenant, type, lookup, key) DO UPDATE SET stream_id = EXCLUDED.stream_id WHERE lookups.stream_id = EXCLUDED.stream_id
            """;

        // head: a scheduled reaction's row always; else the first row per reaction of this commit when no live row of the
        // stream and reaction waits. The guarded UPDATE of the stream row (statement 1) holds its lock, so a promotion in
        // Complete/Bury (FOR SHARE on that row) either committed before this snapshot or waits for this commit.
        public string WriteOutbox => $"""
            INSERT INTO {schema}.outbox (tenant, reaction, type, stream_id, version, event_id, due_at, head)
            SELECT $1, u.r, $2, $3, u.v, u.e, u.d,
                   u.s OR (row_number() OVER (PARTITION BY u.r ORDER BY u.v) = 1
                           AND NOT EXISTS (SELECT 1 FROM {schema}.outbox p
                                           WHERE p.tenant = $1 AND p.type = $2 AND p.stream_id = $3 AND p.reaction = u.r))
            FROM unnest($4::text[], $5::bigint[], $6::uuid[], $7::timestamptz[], $8::boolean[]) AS u(r, v, e, d, s)
            WHERE {Ours("$9")}
            """;

        // one notification per commit with rows; returns a count so the reader loop's last row stays the stream version
        public string NotifyOutbox => $"""
            WITH n AS (SELECT pg_notify($1, '') WHERE {Ours("$2")})
            SELECT count(*) FROM n
            """;

        public string StreamVersion => $"SELECT version FROM {schema}.streams WHERE tenant = $1 AND type = $2 AND id = $3";

        public string LoadState => $"SELECT schema, state FROM {schema}.streams WHERE tenant = $1 AND type = $2 AND id = $3";

        public string LoadEvents => $"""
            SELECT version, entity_id, parent_id, alias, alias_version, payload FROM {schema}.events
            WHERE tenant = $1 AND type = $2 AND stream_id = $3 ORDER BY version
            """;

        public string Find => $"SELECT stream_id FROM {schema}.lookups WHERE tenant = $1 AND type = $2 AND lookup = $3 AND key = $4";

        public string Seen => $"SELECT stream_id, version, events FROM {schema}.idempotency_keys WHERE tenant = $1 AND scope = $2 AND key = $3";

        // (tx_id, position) is the order; only transactions older than the oldest running one are final (P-14).
        // The text column needs its own name: ORDER BY tx_id would otherwise sort by the text output column.
        public string ReadAll => $"""
            SELECT {RecordedEvents.Columns("events")}
            FROM {schema}.events
            WHERE (tx_id, position) > ($1::text::xid8, $2) AND tx_id < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY events.tx_id, events.position
            LIMIT $3
            """;

        public string StaleStates => $"""
            SELECT tenant, id, version FROM {schema}.streams
            WHERE type = $1 AND schema <> $2 AND (tenant, id) > ($3, $4)
            ORDER BY tenant, id LIMIT $5
            """;

        public string RewriteState => $"""
            UPDATE {schema}.streams SET schema = $4, state = $5
            WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $6
            """;

        public string PurgeKeys => $"""
            DELETE FROM {schema}.idempotency_keys k USING (
                SELECT tenant, scope, key FROM {schema}.idempotency_keys WHERE stored_at < $1 ORDER BY stored_at LIMIT $2) d
            WHERE (k.tenant, k.scope, k.key) = (d.tenant, d.scope, d.key)
            """;
    }
}
