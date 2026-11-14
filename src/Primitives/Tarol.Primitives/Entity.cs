using System.Diagnostics.CodeAnalysis;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>
/// A child entity inside an aggregate: it has its own id, state and events, and is owned by the root or by another entity
/// through an <see cref="EntitySet{TEntity}"/> or <see cref="EntityRef{TEntity}"/>. Its events are routed by the root's
/// generated dispatch; it is created by its <c>Created</c> event and removed (with its own children) by its <c>Deleted</c> event.
/// </summary>
/// <typeparam name="TRoot">The aggregate root type.</typeparam>
/// <typeparam name="TSelf">The entity type itself (declare it <c>partial</c>).</typeparam>
/// <remarks>
/// <code>
/// public partial class Artwork : Entity&lt;Gallery, Artwork&gt;
/// {
///     public decimal Price { get; private set; }
///     public Success Reprice(decimal price) => Raise(new Repriced(price));
///     public Success Remove() => Raise(new Removed());
///     public sealed record Hung(string Name) : Created;
///     public sealed record Repriced(decimal Price) : Event;
///     public sealed record Removed : Deleted;
///     private void On(Repriced e) => Price = e.Price;
/// }
/// </code>
/// </remarks>
public abstract class Entity<TRoot, TSelf> : IEntity<TSelf>
    where TRoot : AggregateRoot<TRoot>
    where TSelf : Entity<TRoot, TSelf>
{
    /// <summary>Creates the entity. Only the generated dispatch calls it, when the creation event is applied.</summary>
    /// <param name="id">The id from the creation event's routing.</param>
    /// <param name="root">The aggregate root.</param>
    protected Entity(Id<TSelf> id, TRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Id = id;
        Root = root;
    }

    /// <summary>The base of the events that target an existing entity of this type.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The short base name is the user syntax (sealed record Repriced(...) : Event;), decided in docs/migration/primitives.md §9; Tarol targets C# only.")]
    public abstract record Event : DomainEvent;

    /// <summary>The base of the event that creates an entity of this type under its unique owner type.</summary>
    public abstract record Created : DomainEvent;

    /// <summary>
    /// The base of the event that creates an entity of this type under <typeparamref name="TParent"/>, for a child that
    /// has more than one owner type (TPRM007).
    /// </summary>
    /// <typeparam name="TParent">The owner type.</typeparam>
    public abstract record CreatedIn<TParent> : Created;

    /// <summary>The base of the event that removes an entity of this type, and everything it owns.</summary>
    public abstract record Deleted : DomainEvent;

    /// <inheritdoc />
    public Id<TSelf> Id { get; }

    /// <summary>The aggregate root that owns this entity.</summary>
    protected TRoot Root { get; }

    /// <summary>Raises an event that targets this entity.</summary>
    /// <param name="event">The event.</param>
    /// <returns><see cref="Success"/>, for <c>return Raise(…);</c> in a behaviour that returns a <c>Result</c>.</returns>
    protected Success Raise(Event @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Root.Publish(Id.Value, Guid.Empty, @event);
        return default;
    }

    /// <summary>Removes this entity: raises its deletion event; its own children are removed with it.</summary>
    /// <param name="event">The deletion event.</param>
    /// <returns><see cref="Success"/>.</returns>
    protected Success Raise(Deleted @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Root.Publish(Id.Value, Guid.Empty, @event);
        return default;
    }

    /// <summary>Creates a child of this entity with a new id and returns it.</summary>
    /// <typeparam name="TChild">The child type; this entity type must own it.</typeparam>
    /// <param name="event">The creation event.</param>
    /// <returns>The created child.</returns>
    protected TChild Raise<TChild>(Entity<TRoot, TChild>.Created @event)
        where TChild : Entity<TRoot, TChild>, IOwnedBy<TSelf>
        => Raise(@event, Id<TChild>.New());

    /// <summary>Creates a child of this entity with the given id and returns it.</summary>
    /// <typeparam name="TChild">The child type; this entity type must own it.</typeparam>
    /// <param name="event">The creation event.</param>
    /// <param name="id">The new child's id.</param>
    /// <returns>The created child.</returns>
    protected TChild Raise<TChild>(Entity<TRoot, TChild>.Created @event, Id<TChild> id)
        where TChild : Entity<TRoot, TChild>, IOwnedBy<TSelf>
    {
        ArgumentNullException.ThrowIfNull(@event);
        return Root.Create(@event, id, Id.Value);
    }
}
