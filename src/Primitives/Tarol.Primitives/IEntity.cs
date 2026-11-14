using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>An entity of an aggregate (the root or a child), identified by a strongly typed id.</summary>
/// <typeparam name="TSelf">The entity type.</typeparam>
public interface IEntity<TSelf>
    where TSelf : IEntity<TSelf>
{
    /// <summary>The id, unique within the aggregate (and, for UUIDv7 ids, globally).</summary>
    Id<TSelf> Id { get; }
}
