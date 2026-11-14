using System.Diagnostics.CodeAnalysis;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>
/// Read access to the aggregate's identity map of one entity type, at any depth. The generator implements it on the root
/// for every entity type; only generated code adds or removes entries.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IRegistry<TEntity>
    where TEntity : IEntity<TEntity>
{
    /// <summary>Finds an entity of the aggregate by id, wherever it sits in the tree.</summary>
    /// <param name="id">The id.</param>
    /// <param name="entity">The entity, when found.</param>
    /// <returns><see langword="true"/> when the aggregate holds an entity with that id.</returns>
    bool TryFind(Id<TEntity> id, [MaybeNullWhen(false)] out TEntity entity);
}
