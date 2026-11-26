using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.Tests;

// ── reactions to own events (step 9A): changes with an [OnEvent<T>] factory ────────────────────────────────────────────

/// <summary>A creation reacting to a won bid: the factory makes the id (C-10), the key (reaction, event id) dedupes redelivery.</summary>
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);

    public Result<Payout> Execute(ChangeContext context) => Payout.Due(Id<Payout>.New(context.Now), Amount);
}

/// <summary>A validated reaction: constructing the value object in the factory is not TVAL006 (C-13); the pipeline validates.</summary>
public sealed partial record RecordWinningAmount(Id<Auction> Auction, Amount Amount) : IChange<Auction>, IValidatable
{
    [OnEvent<Bid.Won>(Name = "Auctions.RecordWinningAmount")]
    public static RecordWinningAmount From(Bid.Won e, RecordedEvent recorded) => new((Id<Auction>)recorded.StreamId, new Amount(e.Amount));

    public Result Execute(Auction auction, ChangeContext context) => auction.Touch();
}

/// <summary>A scheduled reaction: due at the closed event's time. The member "Auction" shadows the type, so nameof is qualified.</summary>
public sealed partial record SettleAfterClose(Id<Auction> Auction) : IChange<Auction>
{
    [OnEvent<Auction.Closed>(DueAt = nameof(Tests.Auction.Closed.At))]
    public static SettleAfterClose From(Auction.Closed e, RecordedEvent recorded) => new((Id<Auction>)recorded.StreamId);

    public Result Execute(Auction auction, ChangeContext context) => auction.Settle(context.Now);
}

// ── R-1/R-2 (TN-47): one event for several changes, and nothing to do ──────────────────────────────────────────────────

/// <summary>A fan-out: one settled auction, two payouts; each element has its own key (event id and position, a creation has no target).</summary>
public sealed partial record SplitPayout(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Auction.Settled>]
    public static IReadOnlyList<SplitPayout> From(Auction.Settled e, RecordedEvent recorded) => [new(recorded.StreamId, 60), new(recorded.StreamId, 40)];

    public Result<Payout> Execute(ChangeContext context) => Payout.Due(Id<Payout>.New(context.Now), Amount);
}

/// <summary>Nothing to do: the factory answers null for a settlement after 2000 (every test's), so no change runs.</summary>
public sealed partial record ArchiveOldAuction(Id<Auction> Auction) : IChange<Auction>
{
    [OnEvent<Auction.Settled>]
    public static ArchiveOldAuction? From(Auction.Settled e, RecordedEvent recorded) => e.At.Year < 2000 ? new((Id<Auction>)recorded.StreamId) : null;

    public Result Execute(Auction auction, ChangeContext context) => auction.Touch();
}

/// <summary>Nothing to do, found by Load: it answers ChangeOutcome.Skip instead of a NotFound the worker would retry.</summary>
public sealed partial record ForgetAuction(Guid Auction) : ILoads<Auction>
{
    [OnEvent<Auction.Settled>]
    public static ForgetAuction From(Auction.Settled e, RecordedEvent recorded) => new(recorded.StreamId);

    public ValueTask<Result<Auction>> Load(Tarol.Persistence.IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult<Result<Auction>>(ChangeOutcome.Skip("the auction is archived"));

    public Result Execute(Auction auction, ChangeContext context) => auction.Touch();
}
