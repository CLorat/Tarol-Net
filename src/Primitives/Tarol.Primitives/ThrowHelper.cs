using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Tarol.Primitives;

/// <summary>Cold-path throws used by the library and by generated code. Public because generated code lives in your assembly; do not call it.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ThrowHelper
{
    /// <summary>The next event's version is not the expected one.</summary>
    /// <param name="expected">The expected version.</param>
    /// <param name="actual">The event's version.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void OutOfOrderEvent(long expected, long actual)
        => throw new InvalidOperationException($"The event stream is out of order: expected version {expected}, but the next event has version {actual}. History must be replayed gapless, in stored order.");

    /// <summary>An event targets an entity that the aggregate does not hold.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="id">The id.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void UnknownEntity(string entityType, Guid id)
        => throw new InvalidOperationException($"An event targets {entityType} '{id}', but the aggregate holds no such entity: its creation is missing, it was deleted, or the event belongs to another aggregate.");

    /// <summary>A creation names an owner the aggregate does not hold.</summary>
    /// <param name="childType">The child type.</param>
    /// <param name="ownerType">The owner type.</param>
    /// <param name="ownerId">The owner's id.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void OrphanChild(string childType, string ownerType, Guid ownerId)
        => throw new InvalidOperationException($"Cannot create {childType}: its owner {ownerType} '{ownerId}' is not in the aggregate (or the event belongs to another aggregate).");

    /// <summary>A creation reuses an id that is already in the aggregate.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="id">The id.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void DuplicateEntity(string entityType, Guid id)
        => throw new InvalidOperationException($"{entityType} '{id}' is created a second time: the stream contains the same creation twice, or two creations share an id.");

    /// <summary>A creation targets an <see cref="EntityRef{TEntity}"/> that already holds a child.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="ownerType">The owner type.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void SlotOccupied(string entityType, string ownerType)
        => throw new InvalidOperationException($"Cannot create {entityType}: its {ownerType} already holds one. Delete it first.");

    /// <summary>The dispatch has no case for the event.</summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="aggregateType">The aggregate type.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void UnknownEvent(string eventType, string aggregateType)
        => throw new InvalidOperationException($"'{eventType}' is not an event of aggregate {aggregateType}.");

    /// <summary>The dispatch has no case for the event (expression form).</summary>
    /// <typeparam name="T">The expression type.</typeparam>
    /// <param name="eventType">The event type.</param>
    /// <param name="aggregateType">The aggregate type.</param>
    /// <returns>Never returns.</returns>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static T UnknownEvent<T>(string eventType, string aggregateType)
        => throw new InvalidOperationException($"'{eventType}' is not an event of aggregate {aggregateType}.");

    /// <summary>A root event carries another aggregate's id.</summary>
    /// <param name="aggregateType">The aggregate type.</param>
    /// <param name="expected">This aggregate's id.</param>
    /// <param name="actual">The event's entity id.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void ForeignEvent(string aggregateType, Guid expected, Guid actual)
        => throw new InvalidOperationException($"The event belongs to {aggregateType} '{actual}', not to '{expected}'. History passed to Rehydrate must come from this aggregate's own stream.");

    /// <summary>An event was raised from inside an <c>On</c> handler.</summary>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void RaiseDuringDispatch()
        => throw new InvalidOperationException("An event was raised while another event was being applied. On handlers only apply state; raise follow-up events from behaviour methods.");

    /// <summary>The aggregate is broken: an <c>On</c> handler threw.</summary>
    /// <param name="aggregateType">The aggregate type.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void Broken(string aggregateType)
        => throw new InvalidOperationException($"This {aggregateType} is broken: an On handler threw, so its state is half-applied. Discard the instance and load it again.");

    /// <summary>History was replayed onto an aggregate with uncommitted changes.</summary>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void ReplayWithUncommittedChanges()
        => throw new InvalidOperationException("Cannot replay history onto an aggregate that has uncommitted changes.");

    /// <summary>A creation was applied but did not register its child (stale generated code).</summary>
    /// <typeparam name="T">The child type.</typeparam>
    /// <param name="childType">The child type's name.</param>
    /// <returns>Never returns.</returns>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static T ChildNotCreated<T>(string childType)
        => throw new InvalidOperationException($"The creation event was applied, but no {childType} was registered. The generated code is out of date: rebuild, and make sure {childType} is a partial class owned by an EntitySet or EntityRef.");

    /// <summary>An <see cref="EntitySet{TEntity}"/> was initialized with elements.</summary>
    /// <param name="entityType">The child type.</param>
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    public static void EntitySetInitializer(string entityType)
        => throw new InvalidOperationException($"An EntitySet<{entityType}> can only be initialized empty ('= []' or '= new()'); children are added by their creation events.");
}
