using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>
/// The reactions to an event: one outbox row per (event, reaction) is written in the event's commit, so a reaction runs
/// after its event is durable (docs/architecture/changes.md §7). Step 9 generates this table (<c>ReactionsTo</c>); return
/// spans over static arrays, never a collection expression built per call.
/// <code>
/// static readonly string[] OnBidPlaced = ["NotifySeller"];
/// ReactionTable reactions = e => e is Bid.Placed ? OnBidPlaced : ReadOnlySpan&lt;string&gt;.Empty;
/// </code>
/// </summary>
/// <param name="domainEvent">A committed event.</param>
/// <returns>The names of the reactions that must run for it; empty for none.</returns>
public delegate ReadOnlySpan<string> ReactionTable(DomainEvent domainEvent);
