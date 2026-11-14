namespace Tarol.Primitives;

/// <summary>
/// Generated on a child entity for every type that owns it (holds an <see cref="EntitySet{TEntity}"/> or
/// <see cref="EntityRef{TEntity}"/> of it). It lets the compiler reject a creation raised by a type that does not own the
/// child (CS0311). Do not implement it yourself.
/// </summary>
/// <typeparam name="TOwner">The owning root or entity type.</typeparam>
public interface IOwnedBy<TOwner>;
