using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>
/// In-memory store for tests and the probe: per-tenant streams, expected version, idempotency keys stored with the
/// append, <see cref="RecordedEvent"/> with the operation context. Loads by replay (the Npgsql store loads state).
/// </summary>
public sealed class InMemoryAggregateStore(TimeProvider time) : IAggregateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Tenant, string Type, Guid Id), List<StreamEvent>> _streams = [];
    private readonly Dictionary<IdempotencyKey, ChangeOutcome> _keys = [];
    private readonly List<RecordedEvent> _log = [];
    private readonly Dictionary<(Guid Tenant, string Type, string Lookup, string Key), Guid> _lookups = [];

    /// <summary>Test helper: a unique lookup entry (the Npgsql store derives these from the stored state).</summary>
    public void Index<T>(string lookup, string key, Id<T> id, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate) _lookups[(context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty, typeof(T).Name, lookup, key)] = id.Value;
    }

    public ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate)
            return ValueTask.FromResult<Option<Id<T>>>(
                _lookups.TryGetValue((context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty, typeof(T).Name, lookup, key), out var id)
                    ? (Id<T>)id : Option.None);
    }

    /// <summary>Runs once before the next append: simulates a concurrent writer.</summary>
    public Action? BeforeNextAppend { get; set; }

    public int Loads { get; private set; }

    public IReadOnlyList<RecordedEvent> Log { get { lock (_gate) return [.. _log]; } }

    public ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        StreamEvent[] history;
        lock (_gate)
        {
            Loads++;
            if (!_streams.TryGetValue(Key<T>(id.Value, context), out var stream) || stream.Count == 0)
                return ValueTask.FromResult<Option<T>>(Option.None);
            history = [.. stream];
        }
        return ValueTask.FromResult<Option<T>>(T.Rehydrate(id, history));
    }

    public ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key,
        CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var before = BeforeNextAppend;
        BeforeNextAppend = null;
        before?.Invoke();
        lock (_gate)
        {
            var k = Key<T>(aggregate.Id.Value, context);
            _streams.TryGetValue(k, out var stream);
            var actual = stream?.Count ?? 0;
            if (actual != expectedVersion)
                return ValueTask.FromResult<Result>(Error.Conflict("Stream.VersionConflict", $"expected {expectedVersion}, found {actual}"));
            if (key.TryGetValue(out IdempotencyKey idem) && _keys.ContainsKey(idem))
                return ValueTask.FromResult<Result>(Error.Conflict("Change.Duplicate"));
            stream ??= _streams[k] = [];
            var at = time.GetUtcNow();
            foreach (var e in aggregate.UncommittedChanges)
            {
                stream.Add(e);
                _log.Add(new RecordedEvent(aggregate.Id.Value, e, Guid.CreateVersion7(at), at, context));
            }
            if (key.TryGetValue(out idem))
                _keys[idem] = new ChangeOutcome(aggregate.Id.Value, aggregate.Version, aggregate.UncommittedChanges.Length);
        }
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Option<ChangeOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult<Option<ChangeOutcome>>(_keys.TryGetValue(key, out var o) ? o : Option.None);
    }

    /// <summary>Test helper: commit events written by "someone else".</summary>
    public void CommitBehindTheBack<T>(T aggregate, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate)
        {
            var k = Key<T>(aggregate.Id.Value, context);
            if (!_streams.TryGetValue(k, out var stream))
                stream = _streams[k] = [];
            var at = time.GetUtcNow();
            foreach (var e in aggregate.UncommittedChanges)
            {
                stream.Add(e);
                _log.Add(new RecordedEvent(aggregate.Id.Value, e, Guid.CreateVersion7(at), at, context));
            }
        }
        AggregateChanges.Committed(aggregate);
    }

    private static (Guid, string, Guid) Key<T>(Guid id, OperationContext context)
        => (context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty, typeof(T).Name, id);
}
