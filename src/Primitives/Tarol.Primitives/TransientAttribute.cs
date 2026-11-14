namespace Tarol.Primitives;

/// <summary>
/// Marks a field or property of an aggregate or entity as derived data that is not state: a cache that any method may
/// fill (TPRM013 does not report assignments to it) and that is not stored with the aggregate's state.
/// <code>[Transient] private string? _summary;</code>
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class TransientAttribute : Attribute;
