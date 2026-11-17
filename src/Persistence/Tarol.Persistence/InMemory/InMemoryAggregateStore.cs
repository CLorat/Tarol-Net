using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// An <see cref="IAggregateStore"/> in memory, for tests and samples. It keeps the store contract (tenant isolation,
/// atomic append with expected version, idempotency key and unique lookups), records every event as a
/// <see cref="RecordedEvent"/> with its operation context and the commit time from the injected <see cref="TimeProvider"/>,
/// and serves the <see cref="IEventFeed"/>.
/// </summary>
/// <remarks>
/// With state options, every commit also writes the aggregate's stored state (<c>T.WriteState</c>) and loads read it back
/// (<c>T.ReadState</c>), falling back to a replay when the state is missing, of another schema or unreadable, as the Npgsql
/// store does. Without them, and for <c>[LoadByReplay]</c> aggregates, loads replay the history. With a codec, events go
/// through JSON on every append. Streams are keyed by (tenant, <c>T.StreamType</c>, id). Thread-safe.
/// </remarks>
public sealed class InMemoryAggregateStore : IAggregateStore, IEventFeed
{
    private readonly TimeProvider _time;
    private readonly JsonSerializerOptions? _stateOptions;
    private readonly EventCodec? _codec;
    private readonly IKeyStore? _keyStore;
    private readonly StoreLookups _lookupDefinitions;
    private readonly InMemoryOutbox? _outbox;
    private readonly IOutbox _outboxPort;
    private readonly Lock _gate = new();
    private readonly Dictionary<StreamKey, Stream> _streams = [];
    private readonly Dictionary<(Option<Id<ITenant>> Tenant, string Scope, string Key), StoredOutcome> _keys = [];
    private readonly Dictionary<(Guid Tenant, string Type, string Lookup, string Key), Guid> _lookups = [];
    private readonly List<Entry> _recorded = [];
    private ulong _commits;
    private int _loads;
    private int _stateLoads;

    /// <summary>Creates a store.</summary>
    /// <param name="time">The clock for <see cref="RecordedEvent.RecordedAt"/> and event ids.</param>
    /// <param name="stateOptions">JSON options for the stored state (with <c>ValueObjectJson.Resolver</c> first when the state holds value objects); <see langword="null"/> = load by replay.</param>
    public InMemoryAggregateStore(TimeProvider time, JsonSerializerOptions? stateOptions = null)
        : this(time, new InMemoryStoreOptions { StateOptions = stateOptions })
    {
    }

    /// <summary>Creates a store.</summary>
    /// <param name="time">The clock for <see cref="RecordedEvent.RecordedAt"/> and event ids.</param>
    /// <param name="options">State options, codec and lookups.</param>
    public InMemoryAggregateStore(TimeProvider time, InMemoryStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        _time = time;
        _codec = options.Codec;
        _keyStore = options.Keys;
        if (_keyStore is not null && _codec is null)
            throw new ArgumentException("A key store needs a codec: sealing happens in the event codec.", nameof(options));
        _stateOptions = options.StateOptions ?? options.Codec?.Options;
        _lookupDefinitions = options.Lookups ?? new StoreLookups();
        _outbox = options.Reactions is { } reactions ? new InMemoryOutbox(time, reactions) : null;
        _outboxPort = _outbox ?? new InMemoryOutbox(time, OutboxReactions.From(static _ => ReadOnlySpan<string>.Empty));
        Jobs = new InMemoryJobRows(time);
    }

    /// <summary>The outbox: rows of <see cref="InMemoryStoreOptions.Reactions"/>, written in each commit (empty without reactions).</summary>
    public IOutbox Outbox => _outboxPort;

    /// <summary>The store's job rows (step 14, J-4): one row per (tenant, job), claimed and leased on the store's clock.</summary>
    public IJobRows Jobs { get; }

    /// <summary>Every recorded event of every stream, in commit order.</summary>
    public IReadOnlyList<RecordedEvent> Recorded
    {
        get
        {
            Entry[] entries;
            lock (_gate)
                entries = [.. _recorded];
            return [.. entries.Select(e => Opened(e).AsTask().GetAwaiter().GetResult().Event)];
        }
    }

    /// <summary>How many loads the store served (a test can prove that invalid input never reached the store).</summary>
    public int Loads => Volatile.Read(ref _loads);

    /// <summary>How many loads came from the stored state rather than a replay (tests).</summary>
    internal int StateLoads => Volatile.Read(ref _stateLoads);

    /// <summary>Defines a unique lookup (a shortcut for <see cref="StoreLookups.Define{T}"/> on this store's lookups).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="lookup">The lookup's name, used by <see cref="Find{T}"/>.</param>
    /// <param name="key">The key of an aggregate (after its events), or <see langword="null"/> for none.</param>
    /// <returns>This store.</returns>
    public InMemoryAggregateStore DefineLookup<T>(string lookup, Func<T, string?> key)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        _lookupDefinitions.Define(lookup, key);
        return this;
    }

    /// <summary>The committed history of one stream (empty when it does not exist in the tenant).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="id">The stream id.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The stream's events from version 1.</returns>
    public StreamEvent[] History<T>(Id<T> id, Option<Id<ITenant>> tenant)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        Stream? stream;
        lock (_gate)
            _streams.TryGetValue(new StreamKey(TenantOf(tenant), T.StreamType, id.Value), out stream);
        return stream is null ? [] : Events(stream, TenantOf(tenant), T.StreamType).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _loads);
        var tenant = TenantOf(context.Tenant);
        byte[]? state;
        Stream? stream;
        lock (_gate)
        {
            if (!_streams.TryGetValue(new StreamKey(tenant, T.StreamType, id.Value), out stream))
                return Option.None;
            state = stream.State;
        }
        if (state is not null && _stateOptions is not null)
        {
            if (_keyStore is not null && EventCodec.IsSealed(state))
                state = await OpenState(tenant, state, cancellationToken).ConfigureAwait(false);
            if (ReadState(id, state, _stateOptions).TryGetValue(out T? stored))
            {
                Interlocked.Increment(ref _stateLoads);
                return stored;
            }
        }
        return T.Rehydrate(id, await Events(stream, tenant, T.StreamType).ConfigureAwait(false));
    }

    private async ValueTask<byte[]> OpenState(Guid tenant, byte[] state, CancellationToken cancellationToken)
    {
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(state, subjects);
        var keys = await _keyStore!.Keys(tenant, subjects, create: false, cancellationToken).ConfigureAwait(false);
        var opened = new ArrayBufferWriter<byte>(state.Length);
        EventCodec.OpenState(state, keys, opened);
        return opened.WrittenSpan.ToArray();
    }

    /// <summary>A copy of a stream's events, opened with the current keys when they are sealed.</summary>
    private async ValueTask<StreamEvent[]> Events(Stream stream, Guid tenant, string streamType)
    {
        StreamEvent[] events;
        SealedEvent?[]? payloads;
        lock (_gate)
        {
            events = [.. stream.Events];
            payloads = stream.Sealed is null ? null : [.. stream.Sealed];
        }
        if (payloads is null)
            return events;
        for (var i = 0; i < events.Length; i++)
        {
            if (payloads[i] is { } payload)
                events[i] = events[i] with { Event = await Decode(tenant, streamType, payload).ConfigureAwait(false) };
        }
        return events;
    }

    private async ValueTask<DomainEvent> Decode(Guid tenant, string streamType, SealedEvent payload)
    {
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(payload.Payload, subjects);
        var keys = await _keyStore!.Keys(tenant, subjects, create: false, CancellationToken.None).ConfigureAwait(false);
        return _codec!.Decode(streamType, payload.Type, payload.Payload, keys);
    }

    private async ValueTask<PositionedEvent> Opened(Entry entry)
        => entry.Payload is not { } payload ? entry.Event
            : entry.Event with { Event = entry.Event.Event with { Event = entry.Event.Event.Event with { Event = await Decode(entry.Tenant, entry.Event.StreamType, payload).ConfigureAwait(false) } } };

    // A state that fails to parse is derived data: replay instead (P-12).
    private static Option<T> ReadState<T>(Id<T> id, byte[] state, JsonSerializerOptions options)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        try
        {
            return AggregateState.Read(id, state, options);
        }
        catch (JsonException)
        {
            return Option.None;
        }
    }

    /// <inheritdoc />
    public ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookup);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_lookupDefinitions.IsDefined<T>(lookup))
            throw new InvalidOperationException($"No lookup '{lookup}' is defined for {typeof(T).Name}; call DefineLookup<{typeof(T).Name}>(\"{lookup}\", …).");
        lock (_gate)
            return ValueTask.FromResult<Option<Id<T>>>(
                _lookups.TryGetValue((TenantOf(context.Tenant), T.StreamType, lookup, key), out var id) ? (Id<T>)id : Option.None);
    }

    /// <inheritdoc />
    public async ValueTask<Result<GlobalPosition>> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(context);
        if (_keyStore is null || !_codec!.HasPersonalData<T>() || aggregate.UncommittedChanges.Length == 0)
            return Commit(aggregate, expectedVersion, context, key, null, cancellationToken);
        var sealing = await Seal(aggregate, TenantOf(context.Tenant), cancellationToken).ConfigureAwait(false);
        return sealing.TryGetValue(out Sealing? sealedData) ? Commit(aggregate, expectedVersion, context, key, sealedData, cancellationToken)
            : sealing.TryGetValue(out Violation? violation) ? violation : throw new InvalidOperationException("unreachable");
    }

    /// <summary>The sealed payloads and state of a commit, or <see cref="DataProtectionErrors.SubjectErased"/>.</summary>
    private async ValueTask<Result<Sealing>> Seal<T>(T aggregate, Guid tenant, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var codec = _codec!;
        var changes = aggregate.UncommittedChanges.ToArray();
        var plain = new (EventType Type, byte[] Payload)[changes.Length];
        var subjects = new List<Guid>();
        var buffer = new ArrayBufferWriter<byte>(256);
        for (var i = 0; i < changes.Length; i++)
        {
            buffer.ResetWrittenCount();
            var type = codec.Encode<T>(changes[i].Event, buffer);
            plain[i] = (type, buffer.WrittenSpan.ToArray());
            codec.SubjectsOf<T>(type, plain[i].Payload, aggregate.Id.Value, subjects);
        }
        var state = _stateOptions is not null && T.StateSchema != 0 ? AggregateState.Write(aggregate, _stateOptions) : null;
        if (state is not null)
            codec.StateSubjectsOf<T>(state, aggregate.Id.Value, subjects);
        var keys = await _keyStore!.Keys(tenant, subjects, create: true, cancellationToken).ConfigureAwait(false);
        try
        {
            var payloads = new SealedEvent?[changes.Length];
            for (var i = 0; i < changes.Length; i++)
            {
                buffer.ResetWrittenCount();
                payloads[i] = new SealedEvent(plain[i].Type, codec.Seal<T>(plain[i].Type, plain[i].Payload, aggregate.Id.Value, keys, buffer) ? buffer.WrittenSpan.ToArray() : plain[i].Payload);
            }
            byte[]? sealedState = null;
            if (state is not null)
            {
                buffer.ResetWrittenCount();
                sealedState = codec.SealState<T>(state, aggregate.Id.Value, keys, buffer) ? buffer.WrittenSpan.ToArray() : state;
            }
            return new Sealing(payloads, sealedState) { Keys = keys };
        }
        catch (SubjectErasedException e)
        {
            return DataProtectionErrors.SubjectErased(e.Subject);
        }
    }

    private Result<GlobalPosition> Commit<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key, Sealing? sealing,
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
        if (sealing is not null)
            changes = Opened<T>(_codec!, changes, sealing);
        else if (_codec is not null)
            changes = RoundTrip<T>(_codec, changes);
        var state = sealing is not null ? sealing.State
            : _stateOptions is not null && T.StateSchema != 0 ? AggregateState.Write(aggregate, _stateOptions) : null;
        var tenant = TenantOf(context.Tenant);
        var type = T.StreamType;
        var keys = _lookupDefinitions.KeysOf(aggregate);
        _outbox?.Check(changes);

        lock (_gate)
        {
            var streamKey = new StreamKey(tenant, type, aggregate.Id.Value);
            _streams.TryGetValue(streamKey, out var stream);
            var actual = stream?.Events.Count ?? 0;
            if (actual != expectedVersion)
                return StoreErrors.VersionConflict(expectedVersion, actual);
            if (key.TryGetValue(out IdempotencyKey idempotency) && _keys.ContainsKey(Identity(idempotency)))
                return StoreErrors.DuplicateKey(idempotency);
            foreach (var (lookup, newKey) in keys)
            {
                if (newKey is not null && newKey != stream?.Lookups.GetValueOrDefault(lookup)
                    && _lookups.TryGetValue((tenant, type, lookup, newKey), out var owner) && owner != aggregate.Id.Value)
                    return StoreErrors.LookupTaken(lookup, newKey);
            }

            // everything checked: commit atomically
            stream ??= _streams[streamKey] = new Stream();
            var at = _time.GetUtcNow();
            var commit = ++_commits;
            var first = _recorded.Count;
            if (sealing is not null)
                stream.Sealed ??= [.. Enumerable.Repeat<SealedEvent?>(null, stream.Events.Count)];
            for (var i = 0; i < changes.Length; i++)
            {
                var change = changes[i];
                stream.Events.Add(change);
                stream.Sealed?.Add(sealing?.Payloads[i]);
                var recorded = new RecordedEvent(aggregate.Id.Value, change, Guid.CreateVersion7(at), at, context);
                _recorded.Add(new Entry(new PositionedEvent(new GlobalPosition(commit, _recorded.Count + 1), type, recorded), tenant, sealing?.Payloads[i]));
            }
            _outbox?.Write([.. _recorded.GetRange(first, changes.Length).Select(static e => e.Event)]);
            stream.State = state;
            foreach (var (lookup, newKey) in keys)
            {
                if (stream.Lookups.Remove(lookup, out var old))
                    _lookups.Remove((tenant, type, lookup, old));
                if (newKey is null)
                    continue;
                _lookups[(tenant, type, lookup, newKey)] = aggregate.Id.Value;
                stream.Lookups[lookup] = newKey;
            }
            if (key.TryGetValue(out idempotency))
                _keys[Identity(idempotency)] = new StoredOutcome(aggregate.Id.Value, changes[^1].Version, changes.Length, idempotency.Fingerprint);
            return _recorded[^1].Event.Position;
        }
    }

    /// <summary>The committed events as the sealed payloads open now (a test sees what a load will see).</summary>
    private static StreamEvent[] Opened<T>(EventCodec codec, StreamEvent[] changes, Sealing sealing)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var streamType = codec.StreamTypeOf<T>();
        var copies = new StreamEvent[changes.Length];
        for (var i = 0; i < changes.Length; i++)
            copies[i] = changes[i] with { Event = codec.Decode(streamType, sealing.Payloads[i]!.Type, sealing.Payloads[i]!.Payload, sealing.Keys) };
        return copies;
    }

    private static StreamEvent[] RoundTrip<T>(EventCodec codec, StreamEvent[] changes)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var streamType = codec.StreamTypeOf<T>();
        var copies = new StreamEvent[changes.Length];
        for (var i = 0; i < changes.Length; i++)
        {
            buffer.ResetWrittenCount();
            var stored = codec.Encode<T>(changes[i].Event, buffer);
            copies[i] = changes[i] with { Event = codec.Decode(streamType, stored, buffer.WrittenSpan) };
        }
        return copies;
    }

    /// <inheritdoc />
    public ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<Option<StoredOutcome>>(_keys.TryGetValue(Identity(key), out var outcome) ? outcome : Option.None);
    }

    /// <summary>A key is (tenant, scope, key); its fingerprint is stored with the outcome, not part of the identity.</summary>
    private static (Option<Id<ITenant>>, string, string) Identity(IdempotencyKey key) => (key.Tenant, key.Scope, key.Key);

    /// <inheritdoc />
    public async IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Entry[] page;
        lock (_gate)
            page = [.. _recorded.Where(e => e.Event.Position > after).Take(limit)];
        foreach (var e in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Opened(e).ConfigureAwait(false);
        }
    }

    /// <summary>A stream's stored state bytes as committed (sealed fields stay sealed; tests).</summary>
    internal byte[]? StoredStateForTests<T>(Id<T> id, Option<Id<ITenant>> tenant)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate)
            return _streams[new StreamKey(TenantOf(tenant), T.StreamType, id.Value)].State;
    }

    /// <summary>Replaces a stream's stored state with malformed JSON (tests of the replay fallback).</summary>
    internal void CorruptStateForTests<T>(Id<T> id, Option<Id<ITenant>> tenant)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate)
            _streams[new StreamKey(TenantOf(tenant), T.StreamType, id.Value)].State = "{\"id\":"u8.ToArray();
    }

    private static Guid TenantOf(Option<Id<ITenant>> tenant) => tenant.TryGetValue(out Id<ITenant> id) ? id.Value : Guid.Empty;

    private readonly record struct StreamKey(Guid Tenant, string Type, Guid Id);

    /// <summary>A recorded event; <paramref name="Payload"/> is its sealed payload when its aggregate has personal data.</summary>
    private sealed record Entry(PositionedEvent Event, Guid Tenant, SealedEvent? Payload);

    private sealed record SealedEvent(EventType Type, byte[] Payload);

    private sealed record Sealing(SealedEvent?[] Payloads, byte[]? State)
    {
        public KeyRing Keys { get; init; } = null!;
    }

    private sealed class Stream
    {
        public List<StreamEvent> Events { get; } = [];

        /// <summary>The sealed payloads, parallel to <see cref="Events"/> (null for events committed before sealing; whole list null when never sealed).</summary>
        public List<SealedEvent?>? Sealed { get; set; }

        public byte[]? State { get; set; }

        public Dictionary<string, string> Lookups { get; } = [];
    }
}
