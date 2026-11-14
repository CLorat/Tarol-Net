using System.Text.Json;
using Tarol.Options;
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

    /// <summary>
    /// The shape of the stored state: a stable hash of the captured state names and types (the whole tree, nesting
    /// included). It changes when the shape changes; a stored state with another value is not read (the load replays).
    /// <c>0</c> when the aggregate loads by replay (<see cref="LoadByReplayAttribute"/>).
    /// </summary>
    static abstract int StateSchema { get; }

    /// <summary>
    /// Writes the aggregate's current state (root, every entity, <c>id</c>, <c>version</c>, <c>schema</c>) as one JSON object.
    /// Member values go through <paramref name="options"/> (its resolver chain); names are fixed at compile time.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="options">The JSON options of the state (value objects need <c>ValueObjectJson.Resolver</c> first).</param>
    /// <exception cref="InvalidOperationException">The aggregate is broken, or it loads by replay (<see cref="StateSchema"/> is 0).</exception>
    static abstract void WriteState(Utf8JsonWriter writer, TSelf aggregate, JsonSerializerOptions options);

    /// <summary>
    /// Restores the aggregate from a state written by <see cref="WriteState"/>, without events: no <c>On</c> handler,
    /// invariant or rule runs. Returns <see cref="Tarol.Options.None"/> (and skips the object) when the state has another
    /// <c>schema</c>, so the caller replays the events instead.
    /// </summary>
    /// <param name="id">The aggregate's id (the stream); the state's <c>id</c> must match.</param>
    /// <param name="reader">A reader on, or before, the state object.</param>
    /// <param name="options">The JSON options the state was written with.</param>
    /// <returns>The aggregate, with no uncommitted changes, or <see cref="Tarol.Options.None"/> for another shape.</returns>
    /// <exception cref="JsonException">The state is malformed (a missing or unknown member, a wrong token).</exception>
    /// <exception cref="InvalidOperationException">The state belongs to another aggregate or holds an entity twice.</exception>
    static abstract Option<TSelf> ReadState(Id<TSelf> id, ref Utf8JsonReader reader, JsonSerializerOptions options);
}
