using System.Diagnostics.CodeAnalysis;

namespace Tarol.Primitives;

/// <summary>
/// A single, optional child entity: filled by the child's <c>Created</c> event (which throws if it is already filled) and
/// cleared by its <c>Deleted</c> event. Only the generated dispatch changes it.
/// <code>public EntityRef&lt;ShippingAddress&gt; Shipping { get; } = new();</code>
/// </summary>
/// <typeparam name="TEntity">The child type.</typeparam>
public sealed class EntityRef<TEntity>
    where TEntity : class, IEntity<TEntity>
{
    /// <summary>The child, or <see langword="null"/> when none exists.</summary>
    public TEntity? Value { get; private set; }

    /// <summary>Whether the child exists.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool HasValue => Value is not null;

    /// <inheritdoc />
    public override string ToString() => Value?.ToString() ?? "(none)";

    // Reached only by generated code, through [UnsafeAccessor].
    internal void SetInternal(TEntity entity) => Value = entity;

    internal void ClearInternal() => Value = null;
}
