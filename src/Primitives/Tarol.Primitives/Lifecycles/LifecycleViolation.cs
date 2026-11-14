using Tarol.Results;

namespace Tarol.Primitives;

/// <summary>
/// A step of a lifecycle field that is not a declared edge, found on the live path (a behaviour raised an event without its
/// guard). The aggregate keeps the first one of a commit and <see cref="AggregateChanges.Prepare{TSelf}"/> returns
/// <see cref="ToError"/>. Replay never records one: history that today's lifecycle forbids still loads.
/// </summary>
/// <param name="Field">The owner type and field (<c>Auction.Status</c>).</param>
/// <param name="From">The state before the event, or <c>(new)</c> for a root or entity that was just created.</param>
/// <param name="To">The state after the event.</param>
/// <param name="Event">The event's type name.</param>
public sealed record LifecycleViolation(string Field, string From, string To, string Event)
{
    /// <summary>The code of <see cref="ToError"/>.</summary>
    public const string Code = "Lifecycle.ForbiddenStep";

    /// <summary>The <c>From</c> of a step that creates a root or entity in a state other than its lifecycle's initial state.</summary>
    public const string New = "(new)";

    /// <summary>The failure <see cref="AggregateChanges.Prepare{TSelf}"/> returns.</summary>
    /// <returns><c>Error.Internal("Lifecycle.ForbiddenStep", "Auction.Status: Settled -&gt; Open by Reopened")</c>: a bug, not a conflict.</returns>
    public Error ToError() => Error.Internal(Code, $"{Field}: {From} -> {To} by {Event}");
}
