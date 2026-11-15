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
