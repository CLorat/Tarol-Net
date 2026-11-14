namespace Tarol.Primitives;

/// <summary>
/// An event at its position in an aggregate's stream: the routing envelope beside the payload. Raising an event records
/// one; replay consumes them. The fields map one to one to the store's columns.
/// </summary>
/// <param name="Version">The stream position, starting at 1, gapless.</param>
/// <param name="EntityId">
/// The entity the event targets: the root's id for a root event, the child's id for an entity event (for a creation,
/// the new child's id).
/// </param>
/// <param name="ParentId">For a creation, the id of the entity (or root) that owns the new child; otherwise <see cref="Guid.Empty"/>.</param>
/// <param name="Event">The payload.</param>
public readonly record struct StreamEvent(long Version, Guid EntityId, Guid ParentId, DomainEvent Event);
