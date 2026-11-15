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
    private readonly StoreLookups _lookupDefinitions;
    private readonly Lock _gate = new();
    private readonly Dictionary<StreamKey, Stream> _streams = [];
    private readonly Dictionary<IdempotencyKey, StoredOutcome> _keys = [];
    private readonly Dictionary<(Guid Tenant, string Type, string Lookup, string Key), Guid> _lookups = [];
    private readonly List<PositionedEvent> _recorded = [];
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
        _stateOptions = options.StateOptions ?? options.Codec?.Options;
        _lookupDefinitions = options.Lookups ?? new StoreLookups();
    }

    /// <summary>Every recorded event of every stream, in commit order.</summary>
    public IReadOnlyList<RecordedEvent> Recorded
    {
        get
        {
            lock (_gate)
                return [.. _recorded.Select(static e => e.Event)];
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
        lock (_gate)
            return _streams.TryGetValue(new StreamKey(TenantOf(tenant), T.StreamType, id.Value), out var stream) ? [.. stream.Events] : [];
    }

    /// <inheritdoc />
    public ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _loads);
        byte[]? state;
        lock (_gate)
        {
            if (!_streams.TryGetValue(new StreamKey(TenantOf(context.Tenant), T.StreamType, id.Value), out var stream))
                return ValueTask.FromResult<Option<T>>(Option.None);
            state = stream.State;
        }
        if (state is not null && _stateOptions is not null && ReadState(id, state, _stateOptions).TryGetValue(out T? stored))
        {
            Interlocked.Increment(ref _stateLoads);
            return ValueTask.FromResult<Option<T>>(stored);
        }
        StreamEvent[] history;
        lock (_gate)
            history = [.. _streams[new StreamKey(TenantOf(context.Tenant), T.StreamType, id.Value)].Events];   // copied only to replay
        return ValueTask.FromResult<Option<T>>(T.Rehydrate(id, history));
    }

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
    public ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        var changes = aggregate.UncommittedChanges.ToArray();
        if (changes.Length == 0)
            return ValueTask.FromResult(Result.Success());
        if (changes[0].Version != expectedVersion + 1)
            throw new ArgumentException(
                $"The uncommitted changes of {typeof(T).Name} start at version {changes[0].Version}, not at expected version {expectedVersion} + 1.",
                nameof(expectedVersion));
        if (_codec is not null)
            changes = RoundTrip<T>(_codec, changes);
        var state = _stateOptions is not null && T.StateSchema != 0 ? AggregateState.Write(aggregate, _stateOptions) : null;
        var tenant = TenantOf(context.Tenant);
        var type = T.StreamType;
        var keys = _lookupDefinitions.KeysOf(aggregate);

        lock (_gate)
        {
            var streamKey = new StreamKey(tenant, type, aggregate.Id.Value);
            _streams.TryGetValue(streamKey, out var stream);
            var actual = stream?.Events.Count ?? 0;
            if (actual != expectedVersion)
                return ValueTask.FromResult<Result>(StoreErrors.VersionConflict(expectedVersion, actual));
            if (key.TryGetValue(out IdempotencyKey idempotency) && _keys.ContainsKey(idempotency))
                return ValueTask.FromResult<Result>(StoreErrors.DuplicateKey(idempotency));
            foreach (var (lookup, newKey) in keys)
            {
                if (newKey is not null && newKey != stream?.Lookups.GetValueOrDefault(lookup)
                    && _lookups.TryGetValue((tenant, type, lookup, newKey), out var owner) && owner != aggregate.Id.Value)
                    return ValueTask.FromResult<Result>(StoreErrors.LookupTaken(lookup, newKey));
            }

            // everything checked: commit atomically
            stream ??= _streams[streamKey] = new Stream();
            var at = _time.GetUtcNow();
            var commit = ++_commits;
            foreach (var change in changes)
            {
                stream.Events.Add(change);
                var recorded = new RecordedEvent(aggregate.Id.Value, change, Guid.CreateVersion7(at), at, context);
                _recorded.Add(new PositionedEvent(new GlobalPosition(commit, _recorded.Count + 1), type, recorded));
            }
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
                _keys[idempotency] = new StoredOutcome(aggregate.Id.Value, changes[^1].Version, changes.Length);
        }
        return ValueTask.FromResult(Result.Success());
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
            return ValueTask.FromResult<Option<StoredOutcome>>(_keys.TryGetValue(key, out var outcome) ? outcome : Option.None);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        PositionedEvent[] page;
        lock (_gate)
            page = [.. _recorded.Where(e => e.Position > after).Take(limit)];
        foreach (var e in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }
        await Task.CompletedTask.ConfigureAwait(false);
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

    private sealed class Stream
    {
        public List<StreamEvent> Events { get; } = [];

        public byte[]? State { get; set; }

        public Dictionary<string, string> Lookups { get; } = [];
    }
}
