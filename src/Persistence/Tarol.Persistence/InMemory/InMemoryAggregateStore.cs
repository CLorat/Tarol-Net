using System.Text.Json;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// An <see cref="IAggregateStore"/> in memory, for tests and samples. It keeps the store contract (tenant isolation,
/// atomic append with expected version, idempotency key and unique lookups) and records every event as a
/// <see cref="RecordedEvent"/> with its operation context and the commit time from the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// With <c>stateOptions</c>, every commit also writes the aggregate's stored state (<c>T.WriteState</c>) and loads read
/// it back (<c>T.ReadState</c>), falling back to a replay when the state is missing or of another schema, as the Npgsql
/// store will. Without them, and for <c>[LoadByReplay]</c> aggregates, loads replay the history. Thread-safe.
/// </remarks>
/// <param name="time">The clock for <see cref="RecordedEvent.RecordedAt"/> and event ids.</param>
/// <param name="stateOptions">JSON options for the stored state (with <c>ValueObjectJson.Resolver</c> first when the state holds value objects); <see langword="null"/> = load by replay.</param>
public sealed class InMemoryAggregateStore(TimeProvider time, JsonSerializerOptions? stateOptions = null) : IAggregateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<StreamKey, Stream> _streams = [];
    private readonly Dictionary<IdempotencyKey, StoredOutcome> _keys = [];
    private readonly Dictionary<(string Type, string Lookup), Func<object, string?>> _lookupDefinitions = [];
    private readonly Dictionary<(Guid Tenant, string Type, string Lookup, string Key), Guid> _lookups = [];
    private readonly List<RecordedEvent> _recorded = [];
    private int _loads;
    private int _stateLoads;

    /// <summary>Every recorded event of every stream, in commit order.</summary>
    public IReadOnlyList<RecordedEvent> Recorded
    {
        get
        {
            lock (_gate)
                return [.. _recorded];
        }
    }

    /// <summary>How many loads the store served (a test can prove that invalid input never reached the store).</summary>
    public int Loads => Volatile.Read(ref _loads);

    /// <summary>How many loads came from the stored state rather than a replay (tests).</summary>
    internal int StateLoads => Volatile.Read(ref _stateLoads);

    /// <summary>Defines a unique lookup: after every commit of a <typeparamref name="T"/>, <paramref name="key"/> of the aggregate is indexed.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="lookup">The lookup's name, used by <see cref="Find{T}"/>.</param>
    /// <param name="key">The key of an aggregate (after its events), or <see langword="null"/> for none.</param>
    /// <returns>This store.</returns>
    public InMemoryAggregateStore DefineLookup<T>(string lookup, Func<T, string?> key)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookup);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
            _lookupDefinitions[(typeof(T).FullName!, lookup)] = aggregate => key((T)aggregate);
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
            return _streams.TryGetValue(new StreamKey(TenantOf(tenant), typeof(T).FullName!, id.Value), out var stream) ? [.. stream.Events] : [];
    }

    /// <inheritdoc />
    public ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _loads);
        StreamEvent[] history;
        byte[]? state;
        lock (_gate)
        {
            if (!_streams.TryGetValue(new StreamKey(TenantOf(context.Tenant), typeof(T).FullName!, id.Value), out var stream))
                return ValueTask.FromResult<Option<T>>(Option.None);
            history = [.. stream.Events];
            state = stream.State;
        }
        if (state is not null && stateOptions is not null && AggregateState.Read(id, state, stateOptions).TryGetValue(out T? stored))
        {
            Interlocked.Increment(ref _stateLoads);
            return ValueTask.FromResult<Option<T>>(stored);
        }
        return ValueTask.FromResult<Option<T>>(T.Rehydrate(id, history));
    }

    /// <inheritdoc />
    public ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookup);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_lookupDefinitions.ContainsKey((typeof(T).FullName!, lookup)))
                throw new InvalidOperationException($"No lookup '{lookup}' is defined for {typeof(T).Name}; call DefineLookup<{typeof(T).Name}>(\"{lookup}\", …).");
            return ValueTask.FromResult<Option<Id<T>>>(
                _lookups.TryGetValue((TenantOf(context.Tenant), typeof(T).FullName!, lookup, key), out var id) ? (Id<T>)id : Option.None);
        }
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
        var state = stateOptions is not null && T.StateSchema != 0 ? AggregateState.Write(aggregate, stateOptions) : null;
        var tenant = TenantOf(context.Tenant);
        var type = typeof(T).FullName!;

        lock (_gate)
        {
            var streamKey = new StreamKey(tenant, type, aggregate.Id.Value);
            _streams.TryGetValue(streamKey, out var stream);
            var actual = stream?.Events.Count ?? 0;
            if (actual != expectedVersion)
                return ValueTask.FromResult<Result>(StoreErrors.VersionConflict(expectedVersion, actual));
            if (key.TryGetValue(out IdempotencyKey idempotency) && _keys.ContainsKey(idempotency))
                return ValueTask.FromResult<Result>(StoreErrors.DuplicateKey(idempotency));

            var lookups = new List<((Guid, string, string, string) Slot, string? Old)>();
            foreach (var ((definedType, lookup), keyOf) in _lookupDefinitions)
            {
                if (definedType != type)
                    continue;
                var newKey = keyOf(aggregate) is { Length: > 0 } k ? k : null;
                var oldKey = stream?.Lookups.GetValueOrDefault(lookup);
                if (newKey == oldKey)
                    continue;
                if (newKey is not null && _lookups.TryGetValue((tenant, type, lookup, newKey), out var owner) && owner != aggregate.Id.Value)
                    return ValueTask.FromResult<Result>(StoreErrors.LookupTaken(lookup, newKey));
                lookups.Add(((tenant, type, lookup, newKey ?? ""), oldKey));
            }

            // everything checked: commit atomically
            stream ??= _streams[streamKey] = new Stream();
            var at = time.GetUtcNow();
            foreach (var change in changes)
            {
                stream.Events.Add(change);
                _recorded.Add(new RecordedEvent(aggregate.Id.Value, change, Guid.CreateVersion7(at), at, context));
            }
            stream.State = state;
            foreach (var (slot, old) in lookups)
            {
                var lookup = slot.Item3;
                if (old is not null)
                    _lookups.Remove((tenant, type, lookup, old));
                if (slot.Item4.Length > 0)
                {
                    _lookups[slot] = aggregate.Id.Value;
                    stream.Lookups[lookup] = slot.Item4;
                }
                else
                {
                    stream.Lookups.Remove(lookup);
                }
            }
            if (key.TryGetValue(out idempotency))
                _keys[idempotency] = new StoredOutcome(aggregate.Id.Value, changes[^1].Version, changes.Length);
        }
        return ValueTask.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<Option<StoredOutcome>>(_keys.TryGetValue(key, out var outcome) ? outcome : Option.None);
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
