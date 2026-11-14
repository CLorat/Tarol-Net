using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>
/// The children of one type that an aggregate root or entity owns: ordered, read-only, with O(1) lookup by id.
/// Membership changes only through the children's <c>Created</c> and <c>Deleted</c> events; the generated dispatch is the
/// only code that reaches the mutators.
/// <code>public EntitySet&lt;Room&gt; Rooms { get; } = [];</code>
/// </summary>
/// <typeparam name="TEntity">The child type.</typeparam>
[CollectionBuilder(typeof(EntitySet), nameof(EntitySet.Create))]
public sealed class EntitySet<TEntity> : IReadOnlyList<TEntity>
    where TEntity : IEntity<TEntity>
{
    private readonly List<TEntity> _items = [];
    private readonly Dictionary<Id<TEntity>, TEntity> _byId = [];

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public TEntity this[int index] => _items[index];

    /// <summary>The child with the given id.</summary>
    /// <param name="id">The id.</param>
    /// <exception cref="KeyNotFoundException">No child has that id.</exception>
    public TEntity this[Id<TEntity> id] => _byId[id];

    /// <summary>Whether a child with the given id is in the set.</summary>
    /// <param name="id">The id.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool Contains(Id<TEntity> id) => _byId.ContainsKey(id);

    /// <summary>Looks a child up by id without allocating.</summary>
    /// <param name="id">The id.</param>
    /// <param name="entity">The child, when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGet(Id<TEntity> id, [MaybeNullWhen(false)] out TEntity entity) => _byId.TryGetValue(id, out entity);

    /// <summary>An allocation-free enumerator over the children in creation order.</summary>
    /// <returns>The enumerator.</returns>
    public List<TEntity>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<TEntity> IEnumerable<TEntity>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    // Reached only by generated code, through [UnsafeAccessor].
    internal void AddInternal(TEntity entity)
    {
        _byId.Add(entity.Id, entity);
        _items.Add(entity);
    }

    internal void RemoveInternal(TEntity entity)
    {
        if (_byId.Remove(entity.Id))
            _items.Remove(entity);
    }

    internal void ClearInternal()
    {
        _byId.Clear();
        _items.Clear();
    }
}

/// <summary>Collection-expression support for <see cref="EntitySet{TEntity}"/>: only the empty expression <c>[]</c> is allowed.</summary>
public static class EntitySet
{
    /// <summary>Creates an empty set. Children are added by their creation events, never by an initializer.</summary>
    /// <typeparam name="TEntity">The child type.</typeparam>
    /// <param name="items">Must be empty.</param>
    /// <returns>An empty set.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="items"/> is not empty.</exception>
    public static EntitySet<TEntity> Create<TEntity>(ReadOnlySpan<TEntity> items)
        where TEntity : IEntity<TEntity>
    {
        if (!items.IsEmpty) ThrowHelper.EntitySetInitializer(typeof(TEntity).Name);
        return new EntitySet<TEntity>();
    }
}
