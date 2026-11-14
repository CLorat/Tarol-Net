using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>Non-generic marker of an aggregate root, for constraints and infrastructure.</summary>
public interface IAggregateRoot;

/// <summary>
/// The static contract the generator implements on every aggregate root, so a generic store can rebuild and name events
/// with <c>T.Rehydrate(…)</c> and no reflection.
/// </summary>
/// <typeparam name="TSelf">The aggregate root type.</typeparam>
public interface IAggregateRoot<TSelf> : IAggregateRoot
    where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
{
    /// <summary>
    /// Rebuilds the aggregate and its whole entity tree by replaying <paramref name="history"/> through the same dispatch
    /// the live path uses. The history must start at version 1 and be gapless. No invariant, guard or rule runs.
    /// </summary>
    /// <param name="id">The aggregate's id (the stream).</param>
    /// <param name="history">The stored events, in order.</param>
    /// <returns>The aggregate, with no uncommitted changes.</returns>
    /// <exception cref="InvalidOperationException">The history is out of order, belongs to another aggregate, or targets an unknown entity.</exception>
    static abstract TSelf Rehydrate(Id<TSelf> id, ReadOnlySpan<StreamEvent> history);

    /// <summary>The stored alias and schema version of one of this aggregate's events.</summary>
    /// <param name="domainEvent">An event of this aggregate.</param>
    /// <returns>Its <see cref="Primitives.EventType"/>.</returns>
    static abstract EventType EventTypeOf(DomainEvent domainEvent);
}
