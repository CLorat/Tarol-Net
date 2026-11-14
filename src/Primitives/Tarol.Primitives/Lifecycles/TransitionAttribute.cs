namespace Tarol.Primitives;

/// <summary>
/// Labels an edge of a lifecycle with the event that performs it, for the diagram (<c>Open --&gt; Closed : Closed</c>):
/// <c>[Transition&lt;AuctionStatus&gt;(AuctionStatus.Open, AuctionStatus.Closed)] public sealed record Closed : Event;</c>.
/// The edge must be declared with <see cref="ToAttribute"/> (TPRM021), and the event's <c>On</c> handler should assign
/// <paramref name="to"/> (TPRM023). It adds no check of its own: <see cref="ToAttribute"/> stays the graph.
/// </summary>
/// <typeparam name="TEnum">The <see cref="LifecycleAttribute"/> enum.</typeparam>
/// <param name="from">The state the event leaves.</param>
/// <param name="to">The state the event enters.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class TransitionAttribute<TEnum>(TEnum from, TEnum to) : Attribute
    where TEnum : struct, Enum
{
    /// <summary>The state the event leaves.</summary>
    public TEnum From { get; } = from;

    /// <summary>The state the event enters.</summary>
    public TEnum To { get; } = to;
}
