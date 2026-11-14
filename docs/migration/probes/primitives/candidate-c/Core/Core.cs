// Candidate c runtime (prototype, stands in for Tarol.Primitives).
// Library-nested event bases, pure-payload events, routing envelope (StreamEvent) beside the event.
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace C.Core;

/// <summary>Marker base of every domain event. Events are pure payload records.</summary>
public abstract record DomainEvent;

/// <summary>An event in its stream: version and routing, beside the payload. Maps 1:1 to the store columns.</summary>
public readonly record struct StreamEvent(long Version, Guid EntityId, Guid ParentId, DomainEvent Event);

public interface IEntity<TSelf> where TSelf : IEntity<TSelf>
{
    Id<TSelf> Id { get; }
}

/// <summary>Generated on a child type: names its unique owner type (compile-time check of who may create it).</summary>
public interface IOwnedBy<TOwner>;

public abstract class AggregateRoot<TSelf> : IEntity<TSelf> where TSelf : AggregateRoot<TSelf>
{
    /// <summary>Base of every event that targets the root.</summary>
    public abstract record Event : DomainEvent;

    private List<StreamEvent>? _changes;
    private long _version;
    private bool _replaying;
    /// <summary>True while history is replayed (generated code skips live-only bookkeeping).</summary>
    protected bool IsReplaying => _replaying;
    private int _state; // 0 idle, 1 dispatching (stays 1 after a throw: poisoned)

    protected AggregateRoot(Id<TSelf> id) => Id = id;

    public Id<TSelf> Id { get; }
    public long Version => _version;
    public bool IsBroken => _state != 0;

    public ReadOnlySpan<StreamEvent> UncommittedChanges
        => _changes is null ? default : CollectionsMarshal.AsSpan(_changes);

    protected Success Raise(Event e) { Publish(Id.Value, Guid.Empty, e); return default; }

    protected TChild Raise<TChild>(Entity<TSelf, TChild>.Created e)
        where TChild : Entity<TSelf, TChild>, IOwnedBy<TSelf>
        => Create<TChild>(Guid.CreateVersion7(), Id.Value, e);

    internal TChild Create<TChild>(Guid id, Guid parent, DomainEvent e) where TChild : Entity<TSelf, TChild>
    {
        Publish(id, parent, e);
        return ((IRegistry<TChild>)this).Find((Id<TChild>)id)!;
    }

    internal void Publish(Guid entity, Guid parent, DomainEvent e)
    {
        if (_state != 0) Throw.Busy();
        var se = new StreamEvent(_version + 1, entity, parent, e);
        _state = 1;
        Dispatch(in se);           // a throw leaves _state == 1: the instance is poisoned
        _state = 0;
        _version = se.Version;
        (_changes ??= new List<StreamEvent>(8)).Add(se);
    }

    protected internal void ReplayHistory(ReadOnlySpan<StreamEvent> history)
    {
        _replaying = true;
        foreach (ref readonly var se in history)
        {
            if (se.Version != _version + 1) Throw.OutOfOrder(_version + 1, se.Version);
            _state = 1;
            Dispatch(in se);
            _state = 0;
            _version = se.Version;
        }
        _replaying = false;
    }

    protected abstract void Dispatch(in StreamEvent se);

    /// <summary>Generated when the aggregate declares invariants; default: none.</summary>
    protected virtual Result CheckInvariants() => Result.Success();

    internal Result PrepareCommit() => _state != 0 ? Throw.Broken<Result>() : CheckInvariants();
    internal void MarkCommitted() => _changes?.Clear();
}

public abstract class Entity<TRoot, TSelf> : IEntity<TSelf>
    where TRoot : AggregateRoot<TRoot>
    where TSelf : Entity<TRoot, TSelf>
{
    public abstract record Event : DomainEvent;
    public abstract record Created : DomainEvent;
    public abstract record Deleted : DomainEvent;

    protected Entity(Id<TSelf> id, TRoot root) { Id = id; Root = root; }

    protected TRoot Root { get; }
    public Id<TSelf> Id { get; }

    protected Success Raise(Event e) { Root.Publish(Id.Value, Guid.Empty, e); return default; }
    protected Success Raise(Deleted e) { Root.Publish(Id.Value, Guid.Empty, e); return default; }

    protected TChild Raise<TChild>(Entity<TRoot, TChild>.Created e)
        where TChild : Entity<TRoot, TChild>, IOwnedBy<TSelf>
        => Root.Create<TChild>(Guid.CreateVersion7(), Id.Value, e);
}

public interface IRegistry<TEntity> where TEntity : IEntity<TEntity>
{
    TEntity? Find(Id<TEntity> id);
}

/// <summary>Infrastructure hand-off: the only way to take events out runs the invariants first.</summary>
public static class AggregateChanges
{
    public static Result Prepare<TSelf>(AggregateRoot<TSelf> aggregate) where TSelf : AggregateRoot<TSelf>
        => aggregate.PrepareCommit();

    public static void Committed<TSelf>(AggregateRoot<TSelf> aggregate) where TSelf : AggregateRoot<TSelf>
        => aggregate.MarkCommitted();
}

[CollectionBuilder(typeof(EntitySet), nameof(EntitySet.Create))]
public sealed class EntitySet<T> : IReadOnlyList<T> where T : IEntity<T>
{
    private readonly List<T> _items = new();
    private readonly Dictionary<Id<T>, T> _byId = new();
    public int Count => _items.Count;
    public T this[int index] => _items[index];
    public bool TryGet(Id<T> id, [MaybeNullWhen(false)] out T entity) => _byId.TryGetValue(id, out entity);
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    internal void AddInternal(T e) { _byId.Add(e.Id, e); _items.Add(e); }
    internal void RemoveInternal(T e) { _byId.Remove(e.Id); _items.Remove(e); }
    internal void ClearInternal() { _byId.Clear(); _items.Clear(); }
}

public static class EntitySet
{
    public static EntitySet<T> Create<T>(ReadOnlySpan<T> items) where T : IEntity<T>
        => items.IsEmpty ? new EntitySet<T>() : throw new InvalidOperationException("only []");
}

public static class Throw
{
    [DoesNotReturn] public static void Busy() => throw new InvalidOperationException("Raise during dispatch, or the aggregate is broken by a throwing apply.");
    [DoesNotReturn] public static T Broken<T>() => throw new InvalidOperationException("The aggregate is broken: an apply threw. Discard it.");
    [DoesNotReturn] public static void OutOfOrder(long expected, long actual) => throw new InvalidOperationException($"expected version {expected}, got {actual}");
    [DoesNotReturn] public static void Unknown(string type, Guid id) => throw new InvalidOperationException($"unknown {type} {id}");
    [DoesNotReturn] public static void Duplicate(string type, Guid id) => throw new InvalidOperationException($"duplicate {type} {id}");
    [DoesNotReturn] public static void UnknownEvent(string type) => throw new InvalidOperationException($"unknown event {type}");
}
