using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
public sealed class NpgsqlAggregateStore : IAggregateStore, IEventFeed, IIdempotencyKeyPurge
{
    private static readonly Guid NoTenant = Guid.Empty;
    private readonly NpgsqlDataSource _db;
    private readonly EventCodec _codec;
    private readonly TimeProvider _time;
    private readonly JsonSerializerOptions _stateOptions;
    private readonly StoreLookups _lookups;
    private readonly IOutboxReactions? _reactions;
    private readonly bool _notify;
    private readonly bool _notifyFeed;
    private readonly Sql _sql;
    private readonly ILogger? _log;
    private readonly SchemaMode _schemaMode;
    private readonly TimeSpan _schemaLockTimeout;
    private readonly IKeyStore? _keys;

    /// <summary>Creates a store.</summary>
    /// <param name="dataSource">The data source (with arrays enabled).</param>
    /// <param name="codec">The event codec with every aggregate registered.</param>
    /// <param name="time">The clock of <see cref="RecordedEvent.RecordedAt"/> and of the event ids.</param>
    /// <param name="options">Schema name, state options and lookups.</param>
    /// <param name="logger">Logs the schema upgrades (event ids 8001–8005); <see langword="null"/> = none.</param>
    public NpgsqlAggregateStore(NpgsqlDataSource dataSource, EventCodec codec, TimeProvider time, NpgsqlStoreOptions? options = null, ILogger? logger = null)
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
        _sql = new Sql(options.Schema, sealing: options.Keys is not null);
        _reactions = options.Reactions;
        _notify = options.Notify;
        _notifyFeed = options.NotifyFeed;
        _log = logger;
        _schemaMode = options.SchemaMode;
        _schemaLockTimeout = options.SchemaLockTimeout;
        _keys = options.Keys;
        FeedChannel = options.Schema + "_feed";
        Outbox = new NpgsqlOutbox(dataSource, codec, time, options.Schema, options.Reactions, options.Notify, options.Keys);
        Jobs = new NpgsqlJobRows(dataSource, time, options.Schema);
    }

    /// <summary>The job rows (<c>jobs</c>, <c>jobs_dead</c>; step 14): one row per (tenant, job), claimed by the job host.</summary>
    public NpgsqlJobRows Jobs { get; }

    /// <summary>Claims, completes, retries and buries the outbox rows the commits wrote (<see cref="NpgsqlStoreOptions.Reactions"/>).</summary>
    public NpgsqlOutbox Outbox { get; }

    /// <summary>The PostgreSQL schema of the tables.</summary>
    public string Schema => _sql.Schema;

    /// <summary>The data source (for the snapshot signals of step 15).</summary>
    internal NpgsqlDataSource DataSource => _db;

    /// <summary>
    /// The channel of the feed's <c>NOTIFY</c> (<c>&lt;schema&gt;_feed</c>), sent with every commit when
    /// <see cref="NpgsqlStoreOptions.NotifyFeed"/> is on: projection hosts <c>LISTEN</c> on it to wake at once.
    /// </summary>
    public string FeedChannel { get; }

    /// <summary>Whether every commit sends the feed's <c>NOTIFY</c> (<see cref="NpgsqlStoreOptions.NotifyFeed"/>).</summary>
    public bool NotifiesFeed => _notifyFeed;

    /// <summary>
    /// Creates or upgrades the schema (<see cref="NpgsqlSchema.Definition"/>) under an advisory lock, or only verifies it
    /// (<see cref="NpgsqlStoreOptions.SchemaMode"/>; docs/architecture/operations.md §4).
    /// </summary>
    /// <param name="cancellationToken">Cancels the call, also while it waits for another process's upgrade.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">
    /// The schema is compatible only from a newer version than this package's (<see cref="NpgsqlSchema.Version"/>); a newer
    /// schema that is still compatible is accepted. In <see cref="SchemaMode.VerifyOnly"/>: the schema is missing or older.
    /// </exception>
    public Task EnsureSchema(CancellationToken cancellationToken) => EnsureSchema(_schemaMode, cancellationToken);

    /// <summary>Creates or upgrades the schema, or only verifies it, as <paramref name="mode"/> says.</summary>
    /// <param name="mode">Upgrade or verify.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The schema's version afterwards.</returns>
    /// <exception cref="InvalidOperationException">As <see cref="EnsureSchema(CancellationToken)"/>.</exception>
    public async Task<SchemaState> EnsureSchema(SchemaMode mode, CancellationToken cancellationToken)
    {
        var state = await new NpgsqlSchemaUpgrader(_db, NpgsqlSchema.Definition(Schema), _log) { LockTimeout = _schemaLockTimeout }
            .Ensure(mode, cancellationToken).ConfigureAwait(false);
        if (_keys is not null && mode == SchemaMode.Upgrade && state.CompatibleFrom < NpgsqlSchema.SealingVersion)
        {
            // a store that seals: code from before sealing (version 2) would read envelopes as values, so it must refuse (§14 item 1)
            await using var raise = _db.CreateCommand(_sql.RaiseCompatibility);
            raise.Parameters.Add(new NpgsqlParameter { Value = NpgsqlSchema.SealingVersion });
            await raise.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            state = state with { CompatibleFrom = NpgsqlSchema.SealingVersion };
        }
        Ensured = state;
        return state;
    }

    /// <summary>The schema's state after the last successful <c>EnsureSchema</c> of this instance (the readiness of step 15).</summary>
    public SchemaState? Ensured { get; private set; }

    // ------------------------------------------------------------------------------------------------------------ reads

    /// <inheritdoc />
    public async ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var type = _codec.StreamTypeOf<T>();
        var tenant = TenantOf(context.Tenant);
        byte[]? state = null;
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
                    if (T.StateSchema != 0 && reader.GetInt32(0) == T.StateSchema && !reader.IsDBNull(1))
                        state = reader.GetFieldValue<byte[]>(1);
                }
            }

            if (state is not null)
            {
                if (_keys is not null && EventCodec.IsSealed(state))
                    state = await OpenState(tenant, state, cancellationToken).ConfigureAwait(false);
                if (ReadState(id, state).TryGetValue(out T? stored))
                    return stored;
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

    private async Task<byte[]> OpenState(Guid tenant, byte[] state, CancellationToken cancellationToken)
    {
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(state, subjects);
        var keys = await _keys!.Keys(tenant, subjects, create: false, cancellationToken).ConfigureAwait(false);
        var opened = new ArrayBufferWriter<byte>(state.Length);
        EventCodec.OpenState(state, keys, opened);
        return opened.WrittenSpan.ToArray();
    }

    private async Task<StreamEvent[]> ReadStream(NpgsqlConnection connection, Guid tenant, string type, Guid id, CancellationToken cancellationToken)
    {
        var rows = new List<(long Version, Guid Entity, Guid Parent, EventType Type, byte[] Payload)>();
        await using (var command = new NpgsqlCommand(_sql.LoadEvents, connection))
        {
            Add(command, tenant, type, id);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetInt64(0), reader.GetGuid(1), reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2),
                        new EventType(reader.GetString(3), reader.GetInt32(4)), reader.GetFieldValue<byte[]>(5)));
                }
            }
        }

        var rings = await RecordedEvents.Rings(rows.Select(r => (tenant, r.Payload)), _keys, cancellationToken).ConfigureAwait(false);
        rings.TryGetValue(tenant, out var ring);
        var events = new StreamEvent[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var domainEvent = ring is not null && EventCodec.IsSealed(row.Payload)
                ? _codec.Decode(type, row.Type, row.Payload, ring)
                : _codec.Decode(type, row.Type, row.Payload);
            events[i] = new StreamEvent(row.Version, row.Entity, row.Parent, domainEvent);
        }

        return events;
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
        if (_lookups.IsPersonal<T>(lookup))
        {
            var keys = _keys ?? throw new InvalidOperationException($"The personal lookup '{lookup}' needs a key store: set NpgsqlStoreOptions.Keys.");
            if (await keys.BlindIndex(TenantOf(context.Tenant), lookup, key, create: false, cancellationToken).ConfigureAwait(false) is not { } index)
                return Option.None;
            key = index;
        }

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
                ? new StoredOutcome(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? Option.None : reader.GetString(3))
                : Option.None;
    }

    // ----------------------------------------------------------------------------------------------------------- append

    /// <inheritdoc />
    public async ValueTask<Result<GlobalPosition>> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        var changes = aggregate.UncommittedChanges.ToArray();
        if (changes.Length == 0)
            return GlobalPosition.Start;
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
        var stateBytes = schema != 0 ? AggregateState.Write(aggregate, _stateOptions) : null;
        var sealing = _keys is not null && _codec.HasPersonalData<T>();

        var versions = new long[changes.Length];
        var eventIds = new Guid[changes.Length];
        var aliases = new string[changes.Length];
        var aliasVersions = new int[changes.Length];
        var entities = new Guid[changes.Length];
        var parents = new Guid[changes.Length];
        var payloads = new string[changes.Length];
        var eventSubjects = new string?[changes.Length];
        Guid[]? stateSubjects = null;
        var plain = sealing ? new byte[changes.Length][] : null;
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
            if (plain is not null)
                plain[i] = buffer.WrittenSpan.ToArray();
            else
                payloads[i] = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        if (plain is not null)
        {
            // DP-2: seal after the serializer, with the keys of every subject (created when new: before the commit, so a
            // failed commit leaves an unused key, never an event without one)
            var subjects = new List<Guid>[changes.Length];
            var all = new List<Guid>();
            for (var i = 0; i < changes.Length; i++)
            {
                subjects[i] = [];
                _codec.SubjectsOf<T>(new EventType(aliases[i], aliasVersions[i]), plain[i], id, subjects[i]);
                all.AddRange(subjects[i]);
            }
            var stateList = new List<Guid>();
            if (stateBytes is not null)
                _codec.StateSubjectsOf<T>(stateBytes, id, stateList);
            all.AddRange(stateList);
            var ring = await _keys!.Keys(tenant, [.. all.Distinct()], create: true, cancellationToken).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < changes.Length; i++)
                {
                    buffer.ResetWrittenCount();
                    payloads[i] = _codec.Seal<T>(new EventType(aliases[i], aliasVersions[i]), plain[i], id, ring, buffer)
                        ? Encoding.UTF8.GetString(buffer.WrittenSpan) : Encoding.UTF8.GetString(plain[i]);
                    eventSubjects[i] = subjects[i].Count == 0 ? null : "{" + string.Join(",", subjects[i].Distinct()) + "}";
                }
                if (stateBytes is not null && stateList.Count > 0)
                {
                    buffer.ResetWrittenCount();
                    if (_codec.SealState<T>(stateBytes, id, ring, buffer))
                        stateBytes = buffer.WrittenSpan.ToArray();
                    stateSubjects = [.. stateList.Distinct()];
                }
            }
            catch (SubjectErasedException e)
            {
                return DataProtectionErrors.SubjectErased(e.Subject);
            }
        }

        var state = stateBytes is null ? null : Encoding.UTF8.GetString(stateBytes);
        var lookupKeys = await LookupKeys(aggregate, tenant, cancellationToken).ConfigureAwait(false);

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
            if (_keys is not null)
            {
                events.Parameters.Add(Jsonb(eventSubjects, NpgsqlDbType.Array | NpgsqlDbType.Text));
                events.Parameters.Add(Jsonb(stateSubjects, NpgsqlDbType.Array | NpgsqlDbType.Uuid));
            }

            // 2. everything else only if this commit's first event exists
            NpgsqlBatchCommand? keyCommand = null;
            if (key.TryGetValue(out IdempotencyKey idempotency))
                keyCommand = Command(batch, _sql.StoreKey, TenantOf(idempotency.Tenant), idempotency.Scope, idempotency.Key, id, version, changes.Length, at, eventIds[0],
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = idempotency.Fingerprint.TryGetValue(out string? fingerprint) ? fingerprint : DBNull.Value });
            var lookupCommands = new List<(NpgsqlBatchCommand Command, string Lookup, string Key)>();
            foreach (var (lookup, lookupKey) in lookupKeys)
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

            // the feed's wake-up (opt-in, QR-5): one notification per commit, sent at commit
            if (_notifyFeed)
                _ = Command(batch, _sql.NotifyOutbox, FeedChannel, eventIds[0]);

            // 3. the version after these statements (ours, or the winner's after a lost race: read committed), and this
            //    commit's position: its transaction and the position of its last event (read-your-writes, QR-5)
            _ = Command(batch, _sql.CommitPosition, tenant, type, id, expectedVersion);

            var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    batch.Connection = connection;
                    batch.Transaction = transaction;
                    long actual = 0;
                    var position = GlobalPosition.Start;
                    try
                    {
                        var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false))
                        {
                            do
                            {
                                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                {
                                    actual = reader.GetInt64(0);
                                    if (reader.FieldCount == 3 && !reader.IsDBNull(2))
                                        position = new GlobalPosition(ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(2));
                                }
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
                    return position;
                }
            }
        }
    }

    /// <summary>The lookup keys of a commit; a personal lookup's key is its blind index (DP-5).</summary>
    private async ValueTask<IReadOnlyList<(string Lookup, string? Key)>> LookupKeys<T>(T aggregate, Guid tenant, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var keys = _lookups.KeysOf(aggregate);
        if (!_lookups.HasPersonal<T>())
            return keys;
        var store = _keys ?? throw new InvalidOperationException($"A personal lookup of {typeof(T).Name} needs a key store: set NpgsqlStoreOptions.Keys.");
        var hashed = new (string, string?)[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var (lookup, value) = keys[i];
            hashed[i] = (lookup, value is not null && _lookups.IsPersonal<T>(lookup)
                ? await store.BlindIndex(tenant, lookup, value, create: true, cancellationToken).ConfigureAwait(false)
                : value);
        }
        return hashed;
    }

    // Defensive: the guarded statements avoid these, but a constraint is the last word.
    private async Task<Result<GlobalPosition>> UniqueViolation(PostgresException e, long expected, Option<IdempotencyKey> key, Guid tenant, string type, Guid id,
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
        var rows = new List<RecordedEvents.Raw>(Math.Min(limit, 1024));
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(RecordedEvents.ReadRaw(reader, 0));
        }

        // one key query per tenant of the page, for the keys the cache lacks (data-protection.md §6.3)
        foreach (var e in await RecordedEvents.Decode(rows, _codec, _keys, cancellationToken).ConfigureAwait(false))
            yield return e;
    }

    // ------------------------------------------------------------------------------------------------- data protection

    /// <summary>
    /// One stream's recorded events, opened with the current keys (an erased subject's fields read as <c>null</c>): what an
    /// erasure re-folds into a projection (DP-5). Positions are the feed's.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events from version 1; empty when the stream does not exist.</returns>
    public async Task<IReadOnlyList<PositionedEvent>> ReadStream(StreamRef stream, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.ReadStream);
        Add(command, stream.Tenant, stream.StreamType, stream.Id);
        return await ReadPage(command, _keys, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The streams whose events hold sealed data of a subject (through the GIN index on <c>events.subjects</c>).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The streams, ordered by type and id.</returns>
    public async Task<IReadOnlyList<StreamRef>> StreamsOf(Guid tenant, Guid subject, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.StreamsOf);
        Add(command, tenant, new[] { subject });
        var streams = new List<StreamRef>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                streams.Add(new StreamRef(tenant, reader.GetString(0), reader.GetGuid(1)));
        return streams;
    }

    /// <summary>
    /// Every event that holds sealed data of a subject, in feed order, opened with <b>that subject's key only</b>: other
    /// subjects' fields in the same events read as <c>null</c> (an export, DP-6).
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events.</returns>
    public async Task<IReadOnlyList<PositionedEvent>> EventsOf(Guid tenant, Guid subject, CancellationToken cancellationToken)
    {
        var keys = _keys ?? throw new InvalidOperationException("An export needs a key store: set NpgsqlStoreOptions.Keys.");
        await using var command = _db.CreateCommand(_sql.EventsOf);
        Add(command, tenant, new[] { subject });
        return await ReadPage(command, new OnlySubject(keys, subject), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The current stored state of a stream, opened with that subject's key only (an export); <see langword="null"/> without one.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="subject">The subject whose fields are opened.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state's JSON.</returns>
    public async Task<byte[]?> StateOf(StreamRef stream, Guid subject, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_sql.LoadState);
        Add(command, stream.Tenant, stream.StreamType, stream.Id);
        byte[]? state = null;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(1))
                state = reader.GetFieldValue<byte[]>(1);
        if (state is null || !EventCodec.IsSealed(state) || _keys is null)
            return state;
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(state, subjects);
        var ring = await new OnlySubject(_keys, subject).Keys(stream.Tenant, subjects, create: false, cancellationToken).ConfigureAwait(false);
        var opened = new ArrayBufferWriter<byte>(state.Length);
        EventCodec.OpenState(state, ring, opened);
        return opened.WrittenSpan.ToArray();
    }

    /// <summary>Deletes the personal lookups (<see cref="StoreLookups.DefinePersonal{T}"/>) of the given streams (an erasure, DP-5).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="streams">The streams (of <paramref name="tenant"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many lookup rows were deleted.</returns>
    public async Task<int> DeletePersonalLookups(Guid tenant, IReadOnlyCollection<StreamRef> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var deleted = 0;
        foreach (var group in streams.Where(s => s.Tenant == tenant).GroupBy(static s => s.StreamType))
        {
            var personal = _lookups.PersonalOf(group.Key);
            if (personal.Count == 0)
                continue;
            await using var command = _db.CreateCommand(_sql.DeleteLookups);
            Add(command, tenant, group.Key, personal.ToArray(), group.Select(static s => s.Id).ToArray());
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return deleted;
    }

    /// <summary>
    /// Deletes whole streams of a type whose last commit is older than <paramref name="olderThan"/>: events, state and
    /// lookups, at most <paramref name="batch"/> per call, skipping streams with pending outbox rows (DP-7, the retention).
    /// </summary>
    /// <param name="streamType">The stream type.</param>
    /// <param name="olderThan">The cut-off (now − the retention).</param>
    /// <param name="batch">The most streams per statement (500 in <c>tarol.retention</c>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The deleted streams; call again while it returns <paramref name="batch"/> streams.</returns>
    public async Task<IReadOnlyList<StreamRef>> DeleteStreams(string streamType, DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        await using var command = _db.CreateCommand(_sql.DeleteStreams);
        Add(command, streamType, olderThan, batch);
        var deleted = new List<StreamRef>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                deleted.Add(new StreamRef(reader.GetGuid(0), streamType, reader.GetGuid(1)));
        return deleted;
    }

    /// <summary>
    /// Seals the personal fields of <typeparamref name="T"/>'s events and states written before they were marked (or
    /// before the store had keys): the one physical rewrite of <c>events</c> Tarol does, never automatic (§14 item 1).
    /// Pages of <paramref name="batch"/> events, one transaction each, in position order; a field of an already erased
    /// subject becomes <c>null</c>. Safe to rerun and to run while writers commit.
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="batch">Events per transaction (e.g. 500).</param>
    /// <param name="cancellationToken">Cancels the rewrite between pages.</param>
    /// <returns>How many events and states were rewritten.</returns>
    /// <exception cref="InvalidOperationException">The store has no key store.</exception>
    public async Task<(long Events, long States)> SealHistory<T>(int batch, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        var keys = _keys ?? throw new InvalidOperationException("Sealing the history needs a key store: set NpgsqlStoreOptions.Keys.");
        var type = _codec.StreamTypeOf<T>();
        var personal = EventCodec.PersonalEventTypes<T>();
        long events = 0, states = 0;
        if (personal.Count > 0)
        {
            var aliases = personal.Select(static t => t.Alias).Distinct().ToArray();
            var last = 0L;
            while (true)
            {
                var page = new List<(long Position, Guid Tenant, Guid Stream, EventType Type, byte[] Payload)>();
                await using (var select = _db.CreateCommand(_sql.UnsealedEvents))
                {
                    Add(select, type, aliases, last, batch);
                    var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            page.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), new EventType(reader.GetString(3), reader.GetInt32(4)), reader.GetFieldValue<byte[]>(5)));
                }
                if (page.Count == 0)
                    break;
                last = page[^1].Position;
                var positions = new List<long>();
                var payloads = new List<string>();
                var subjects = new List<string?>();
                var buffer = new ArrayBufferWriter<byte>(512);
                foreach (var group in page.Where(static r => !EventCodec.IsSealed(r.Payload)).GroupBy(static r => r.Tenant))
                {
                    var perEvent = group.Select(r =>
                    {
                        var list = new List<Guid>();
                        _codec.SubjectsOf<T>(r.Type, r.Payload, r.Stream, list);
                        return (Row: r, Subjects: list);
                    }).Where(static x => x.Subjects.Count > 0).ToArray();
                    if (perEvent.Length == 0)
                        continue;
                    var ring = await keys.Keys(group.Key, [.. perEvent.SelectMany(static x => x.Subjects).Distinct()], create: true, cancellationToken).ConfigureAwait(false);
                    foreach (var (row, list) in perEvent)
                    {
                        buffer.ResetWrittenCount();
                        _codec.SealStored<T>(row.Type, row.Payload, row.Stream, ring, buffer);
                        positions.Add(row.Position);
                        payloads.Add(Encoding.UTF8.GetString(buffer.WrittenSpan));
                        subjects.Add("{" + string.Join(",", list.Distinct()) + "}");
                    }
                }
                if (positions.Count > 0)
                {
                    await using var update = _db.CreateCommand(_sql.SealEvents);
                    Add(update, type, positions.ToArray(), Jsonb(payloads.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text), Jsonb(subjects.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
                    events += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (T.StateSchema != 0 && _codec.HasPersonalData<T>())
        {
            var (lastTenant, lastId) = (Guid.Empty, Guid.Empty);
            while (true)
            {
                var page = new List<(Guid Tenant, Guid Id, long Version, byte[] State)>();
                await using (var select = _db.CreateCommand(_sql.UnsealedStates))
                {
                    Add(select, type, lastTenant, lastId, batch);
                    var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            page.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3)));
                }
                if (page.Count == 0)
                    break;
                (lastTenant, lastId, _, _) = page[^1];
                foreach (var (tenant, id, version, state) in page)
                {
                    if (EventCodec.IsSealed(state))
                        continue;
                    var list = new List<Guid>();
                    _codec.StateSubjectsOf<T>(state, id, list);
                    if (list.Count == 0)
                        continue;
                    var ring = await keys.Keys(tenant, list, create: true, cancellationToken).ConfigureAwait(false);
                    var buffer = new ArrayBufferWriter<byte>(state.Length * 2);
                    if (!SealStoredState<T>(state, id, ring, buffer))
                        continue;
                    await using var update = _db.CreateCommand(_sql.SealState);
                    Add(update, tenant, type, id, Jsonb(Encoding.UTF8.GetString(buffer.WrittenSpan), NpgsqlDbType.Jsonb), version, list.Distinct().ToArray());
                    states += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return (events, states);
    }

    // a state whose subject is erased has nothing to keep: its personal members become null, as the events' do
    private bool SealStoredState<T>(byte[] state, Guid id, KeyRing ring, ArrayBufferWriter<byte> output)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        try
        {
            return _codec.SealState<T>(state, id, ring, output);
        }
        catch (SubjectErasedException)
        {
            return false; // replayed on the next load (the events read null), rewritten by the next commit
        }
    }

    private async Task<IReadOnlyList<PositionedEvent>> ReadPage(NpgsqlCommand command, IKeyStore? keys, CancellationToken cancellationToken)
    {
        var rows = new List<RecordedEvents.Raw>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(RecordedEvents.ReadRaw(reader, 0));
        return await RecordedEvents.Decode(rows, _codec, keys, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A key store that hands out only one subject's key; every other subject reads as erased (exports).</summary>
    private sealed class OnlySubject(IKeyStore inner, Guid subject) : IKeyStore
    {
        public event EventHandler<KeyErasedEventArgs>? Erased
        {
            add => inner.Erased += value;
            remove => inner.Erased -= value;
        }

        public async ValueTask<KeyRing> Keys(Guid tenant, IReadOnlyCollection<Guid> subjects, bool create, CancellationToken cancellationToken)
        {
            var found = await inner.Keys(tenant, subjects.Contains(subject) ? [subject] : [], create: false, cancellationToken).ConfigureAwait(false);
            var ring = new KeyRing(tenant);
            foreach (var s in subjects)
                ring.Add(s == subject && found.TryGet(s, out var key) ? key : SubjectKey.Erased(tenant, s));
            return ring;
        }

        public ValueTask<bool> EraseSubject(Guid tenant, Guid subject, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> EraseTenant(Guid tenant, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<string?> BlindIndex(Guid tenant, string lookup, string value, bool create, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<int> Rewrap(CancellationToken cancellationToken) => throw new NotSupportedException();
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
                    var (rewritten, subjects) = await SealedState(aggregate, tenant, cancellationToken).ConfigureAwait(false);
                    await using var update = new NpgsqlCommand(_sql.RewriteState, connection);
                    Add(update, tenant, type, id, T.StateSchema, Jsonb(Encoding.UTF8.GetString(rewritten), NpgsqlDbType.Jsonb), version);
                    if (_keys is not null)
                        update.Parameters.Add(Jsonb(subjects, NpgsqlDbType.Array | NpgsqlDbType.Uuid));
                    rebuilt += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                (lastTenant, lastId, _) = stale[^1];
            }
        }
    }

    /// <summary>An aggregate's state as it is stored: sealed when it holds personal data and the store has keys.</summary>
    private async Task<(byte[] State, Guid[]? Subjects)> SealedState<T>(T aggregate, Guid tenant, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var state = AggregateState.Write(aggregate, _stateOptions);
        if (_keys is null || !_codec.HasPersonalData<T>())
            return (state, null);
        var subjects = new List<Guid>();
        _codec.StateSubjectsOf<T>(state, aggregate.Id.Value, subjects);
        if (subjects.Count == 0)
            return (state, null);
        var ring = await _keys.Keys(tenant, subjects, create: false, cancellationToken).ConfigureAwait(false);
        var sealedState = new ArrayBufferWriter<byte>(state.Length * 2);
        return _codec.SealState<T>(state, aggregate.Id.Value, ring, sealedState) ? (sealedState.WrittenSpan.ToArray(), [.. subjects]) : (state, null);
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
    /// <remarks>Only a sealing store writes the <c>subjects</c> columns (version 3): a store without keys runs, as before, on older schemas.</remarks>
    private sealed class Sql(string schema, bool sealing)
    {
        public string Schema => schema;

        // $22: per event, its sealed subjects as a uuid[] literal (NULL when nothing of it is sealed); $23: the state's
        private string Events => $"""
            INSERT INTO {schema}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, parent_id, payload,
                                         recorded_at, actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent{(sealing ? ", subjects" : "")})
            SELECT $1, $2, $3, v, e, a, av, en, NULLIF(p, '00000000-0000-0000-0000-000000000000'::uuid), pl, $4, $5, $6, $7, $8, $9, $10{(sealing ? ", sj::uuid[]" : "")}
            FROM unnest($11::bigint[], $12::uuid[], $13::text[], $14::int[], $15::uuid[], $16::uuid[], $17::jsonb[]{(sealing ? ", $22::text[]" : "")}) AS u(v, e, a, av, en, p, pl{(sealing ? ", sj" : "")})
            WHERE EXISTS (SELECT 1 FROM g)
            """;

        private string Ours(string eventId) => $"EXISTS (SELECT 1 FROM {schema}.events WHERE event_id = {eventId})";

        public string CreateStream => $"""
            WITH g AS (INSERT INTO {schema}.streams (tenant, type, id, version, schema, state, updated_at{(sealing ? ", subjects" : "")})
                       VALUES ($1, $2, $3, $18, $19, $20, $4{(sealing ? ", $23::uuid[]" : "")}) ON CONFLICT (tenant, type, id) DO NOTHING
                       RETURNING 1)
            {Events} AND $21::bigint = 0
            """;

        public string AppendStream => $"""
            WITH g AS (UPDATE {schema}.streams SET version = $18, schema = $19, state = $20, updated_at = $4{(sealing ? ", subjects = $23::uuid[]" : "")}
                       WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $21
                       RETURNING 1)
            {Events}
            """;

        public string StoreKey => $"""
            INSERT INTO {schema}.idempotency_keys (tenant, scope, key, stream_id, version, events, stored_at, fingerprint)
            SELECT $1, $2, $3, $4, $5, $6, $7, $9 WHERE {Ours("$8")}
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

        // the stream's version (column 0, as StreamVersion) plus this commit's transaction and the position of its last event
        public string CommitPosition => $"""
            SELECT s.version, pg_current_xact_id()::text,
                   (SELECT max(e.position) FROM {schema}.events e
                    WHERE e.tenant = $1 AND e.type = $2 AND e.stream_id = $3 AND e.version > $4 AND e.tx_id = pg_current_xact_id())
            FROM {schema}.streams s WHERE s.tenant = $1 AND s.type = $2 AND s.id = $3
            """;

        public string LoadState => $"SELECT schema, state FROM {schema}.streams WHERE tenant = $1 AND type = $2 AND id = $3";

        public string LoadEvents => $"""
            SELECT version, entity_id, parent_id, alias, alias_version, payload FROM {schema}.events
            WHERE tenant = $1 AND type = $2 AND stream_id = $3 ORDER BY version
            """;

        public string Find => $"SELECT stream_id FROM {schema}.lookups WHERE tenant = $1 AND type = $2 AND lookup = $3 AND key = $4";

        public string Seen => $"SELECT stream_id, version, events, fingerprint FROM {schema}.idempotency_keys WHERE tenant = $1 AND scope = $2 AND key = $3";

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
            UPDATE {schema}.streams SET schema = $4, state = $5{(sealing ? ", subjects = $7::uuid[]" : "")}
            WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $6
            """;

        public string ReadStream => $"""
            SELECT {RecordedEvents.Columns("events")} FROM {schema}.events
            WHERE tenant = $1 AND type = $2 AND stream_id = $3 ORDER BY version
            """;

        public string StreamsOf => $"""
            SELECT DISTINCT type, stream_id FROM {schema}.events WHERE tenant = $1 AND subjects @> $2::uuid[] ORDER BY type, stream_id
            """;

        public string EventsOf => $"""
            SELECT {RecordedEvents.Columns("events")} FROM {schema}.events
            WHERE tenant = $1 AND subjects @> $2::uuid[] ORDER BY events.tx_id, events.position
            """;

        public string DeleteLookups => $"""
            DELETE FROM {schema}.lookups WHERE tenant = $1 AND type = $2 AND lookup = ANY($3::text[]) AND stream_id = ANY($4::uuid[])
            """;

        // the oldest expired streams without pending reactions, locked (a racing commit waits, then finds no stream row);
        // their events, lookups and the stream rows go in one statement
        public string DeleteStreams => $"""
            WITH d AS (SELECT s.tenant, s.id FROM {schema}.streams s
                       WHERE s.type = $1 AND s.updated_at < $2
                         AND NOT EXISTS (SELECT 1 FROM {schema}.outbox o WHERE o.tenant = s.tenant AND o.type = s.type AND o.stream_id = s.id)
                       ORDER BY s.updated_at LIMIT $3
                       FOR UPDATE SKIP LOCKED),
                 e AS (DELETE FROM {schema}.events x USING d WHERE x.tenant = d.tenant AND x.type = $1 AND x.stream_id = d.id),
                 l AS (DELETE FROM {schema}.lookups x USING d WHERE x.tenant = d.tenant AND x.type = $1 AND x.stream_id = d.id)
            DELETE FROM {schema}.streams x USING d WHERE x.tenant = d.tenant AND x.type = $1 AND x.id = d.id
            RETURNING x.tenant, x.id
            """;

        public string UnsealedEvents => $"""
            SELECT position, tenant, stream_id, alias, alias_version, payload FROM {schema}.events
            WHERE type = $1 AND subjects IS NULL AND alias = ANY($2::text[]) AND position > $3
            ORDER BY position LIMIT $4
            """;

        public string SealEvents => $"""
            UPDATE {schema}.events e SET payload = u.p::jsonb, subjects = u.s::uuid[]
            FROM unnest($2::bigint[], $3::text[], $4::text[]) AS u(pos, p, s)
            WHERE e.type = $1 AND e.position = u.pos AND e.subjects IS NULL
            """;

        public string UnsealedStates => $"""
            SELECT tenant, id, version, state FROM {schema}.streams
            WHERE type = $1 AND subjects IS NULL AND state IS NOT NULL AND (tenant, id) > ($2, $3)
            ORDER BY tenant, id LIMIT $4
            """;

        public string SealState => $"""
            UPDATE {schema}.streams SET state = $4, subjects = $6::uuid[]
            WHERE tenant = $1 AND type = $2 AND id = $3 AND version = $5 AND subjects IS NULL
            """;

        public string RaiseCompatibility => $"UPDATE {schema}.schema_version SET compatible_from = greatest(compatible_from, $1)";

        public string PurgeKeys => $"""
            DELETE FROM {schema}.idempotency_keys k USING (
                SELECT tenant, scope, key FROM {schema}.idempotency_keys WHERE stored_at < $1 ORDER BY stored_at LIMIT $2) d
            WHERE (k.tenant, k.scope, k.key) = (d.tenant, d.scope, d.key)
            """;
    }
}
