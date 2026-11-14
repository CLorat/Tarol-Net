using Tarol.Results;

namespace Tarol.Primitives;

/// <summary>
/// Declares an enum as the lifecycle of a state field: its members are the states, <see cref="ToAttribute"/> on a member
/// lists the states it may change to. The generator writes <c>CanTransitionTo</c>, <c>RequireTransitionTo</c>, the edge
/// table, a Mermaid diagram, and, for every aggregate or entity field of this type, a live-only check of every step.
/// </summary>
[AttributeUsage(AttributeTargets.Enum)]
public sealed class LifecycleAttribute : Attribute
{
    /// <summary>The initial state's name when it is not the member with value 0.</summary>
    public string? Initial { get; set; }
}

/// <summary>The states a lifecycle state may change to: <c>[To(Closed, Cancelled)] Open</c>.</summary>
/// <param name="states">Members of the same enum.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ToAttribute(params object[] states) : Attribute
{
    /// <summary>The target states.</summary>
    public IReadOnlyList<object> States { get; } = states;
}

/// <summary>A step of a lifecycle state field that is not a declared edge, found on the live path.</summary>
/// <param name="Field">The owner type and field (<c>Auction.Status</c>).</param>
/// <param name="From">The state before the event.</param>
/// <param name="To">The state after the event.</param>
/// <param name="Event">The event's type name.</param>
public sealed record LifecycleViolation(string Field, string From, string To, string Event)
{
    /// <summary>The failure <c>Prepare</c> returns.</summary>
    /// <returns><c>Error.Internal("Lifecycle.ForbiddenStep", …)</c>: a behaviour raised it without its guard.</returns>
    public Error ToError() => Error.Internal("Lifecycle.ForbiddenStep", $"{Field}: {From} -> {To} by {Event}");
}
