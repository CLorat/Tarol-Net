namespace Tarol.Primitives;

/// <summary>
/// The base of every domain event. An event is a fact in the past tense, as data: a <c>sealed record</c> whose members
/// are the facts its <c>On</c> handlers need. It carries no envelope (version, ids, time, actor); those travel beside it
/// in a <see cref="StreamEvent"/> and, once stored, a <see cref="RecordedEvent"/>.
/// </summary>
/// <remarks>
/// Do not derive from this type directly. Derive from the base of the type the event targets, which you can write
/// unqualified inside that type: <c>Event</c> (the root or an entity), <c>Created</c>, <c>CreatedIn&lt;TParent&gt;</c> or
/// <c>Deleted</c> (an entity). <code>public sealed record Repriced(decimal Price) : Event;</code>
/// </remarks>
public abstract record DomainEvent;
