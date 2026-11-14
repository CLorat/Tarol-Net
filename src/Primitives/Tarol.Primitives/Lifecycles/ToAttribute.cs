namespace Tarol.Primitives;

/// <summary>
/// The states a lifecycle state may change to, on a member of a <see cref="LifecycleAttribute"/> enum:
/// <c>[To(Closed, Cancelled)] Open</c>. A member without it is terminal.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class ToAttribute : Attribute
{
    /// <summary>Declares the target states.</summary>
    /// <param name="states">Members of the same enum (attribute arguments cannot be typed by the enum, so TPRM016 checks them).</param>
    public ToAttribute(params object[] states) => States = states ?? [];

    /// <summary>The target states.</summary>
    public IReadOnlyList<object> States { get; }
}
