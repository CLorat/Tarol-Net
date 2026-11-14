namespace Tarol.Primitives;

/// <summary>
/// Declares an enum as a lifecycle: its members are the states, <see cref="ToAttribute"/> on a member lists the states it
/// may change to. The generator writes <c>&lt;Enum&gt;Lifecycle</c> (<c>Initial</c>, <c>Diagram</c>, <c>IsEdge</c>,
/// <c>IsTerminal</c>, <c>Edges</c>, and the extension members <c>CanTransitionTo</c> and <c>RequireTransitionTo</c>), and,
/// for every field of this type on an aggregate root or entity, a live-only check of every step that
/// <see cref="AggregateChanges.Prepare{TSelf}"/> reports.
/// </summary>
/// <remarks>
/// <code>
/// [Lifecycle]
/// public enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, Settled }
/// </code>
/// For an enum nested in a type, the generated class is prefixed with the containing types' names
/// (<c>Auction.Status</c> → <c>AuctionStatusLifecycle</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
public sealed class LifecycleAttribute : Attribute
{
    /// <summary>The initial state's member name when it is not the member with value 0: <c>Initial = nameof(Status.Draft)</c>.</summary>
    public string? Initial { get; set; }
}
