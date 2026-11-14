using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Lifecycles;

public sealed class Bidder;

/// <summary>The auction's lifecycle (ArtFlow): open, closed, settled, never back.</summary>
[Lifecycle]
public enum AuctionStatus
{
    [To(Closed)] Open,
    [To(Settled)] Closed,
    Settled,
}

/// <summary>An entity lifecycle: a bid is withdrawn, won or lost.</summary>
[Lifecycle]
public enum BidStatus
{
    [To(Withdrawn, Won, Lost)] Active,
    Withdrawn,
    Won,
    Lost,
}

/// <summary>A second root lifecycle, entered by an ancestor handler (the root's On for a bid's event).</summary>
[Lifecycle]
public enum PayoutStatus
{
    [To(Due)] None,
    [To(Paid)] Due,
    Paid,
}

/// <summary>The ArtFlow auction with declared lifecycles, and behaviours that forgot their guards on purpose.</summary>
public partial class Auction : AggregateRoot<Auction>
{
    public AuctionStatus Status { get; private set; }
    public PayoutStatus Payout { get; private set; }
    public DateTimeOffset EndTime { get; private set; }
    public decimal CurrentPrice { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Id<Bidder>? Winner { get; private set; }
    public EntitySet<Bid> Bids { get; } = [];

    public static Auction Open(Id<Auction> id, decimal minimalPrice, DateTimeOffset endTime)
    {
        var auction = new Auction(id);
        auction.Raise(new Opened(minimalPrice, endTime));
        return auction;
    }

    public Result<Bid> PlaceBid(Id<Bidder> bidder, decimal amount, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Open || now > EndTime)
            return Error.Conflict("Auction.NotOpen");
        if (amount <= CurrentPrice)
            return Error.Conflict("Bid.TooLow");
        return Raise(new Bid.Placed(bidder, amount));
    }

    public Result Close(DateTimeOffset now)
    {
        var allowed = Status.RequireTransitionTo(AuctionStatus.Closed);
        if (!allowed.IsSuccess)
            return allowed;
        if (now < EndTime)
            return Error.Conflict("Auction.NotEnded");
        return Raise(new Closed(now));
    }

    public Result Settle()
    {
        if (!Status.CanTransitionTo(AuctionStatus.Settled))
            return Error.Conflict("Auction.NotClosed");
        Bid? winner = null;
        foreach (var bid in Bids)
            if (bid.Status == BidStatus.Active && (winner is null || bid.Amount > winner.Amount))
                winner = bid;
        foreach (var bid in Bids)
            if (bid.Status == BidStatus.Active)
                bid.Decide(won: bid == winner);
        return Raise(new Settled(winner?.BidderId, winner?.Amount ?? CurrentPrice));
    }

    /// <summary>Two steps in one behaviour and one commit: Open -> Closed -> Settled.</summary>
    public Result CloseAndSettle(DateTimeOffset now)
    {
        var closed = Close(now);
        return closed.IsSuccess ? Settle() : closed;
    }

    public Result Pay() => Payout.CanTransitionTo(PayoutStatus.Paid) ? Raise(new Paid()) : Error.Conflict("Auction.NothingDue");

    /// <summary>A behaviour that forgot its guard: Settled -> Open is not an edge.</summary>
    public Success ReopenWithoutGuard() => Raise(new Reopened());

    /// <summary>A behaviour that forgot its guard: from Open, Settled is not an edge (and a bid is still active).</summary>
    public Success SettleWithoutGuard() => Raise(new Settled(null, CurrentPrice));

    /// <summary>A bid created in a state other than its initial one (L-4).</summary>
    public Bid PlaceWinningBidWithoutGuard(Id<Bidder> bidder, decimal amount) => Raise(new Bid.PlacedAsWinner(bidder, amount));

    public sealed record Opened(decimal MinimalPrice, DateTimeOffset EndTime) : Event;

    [Transition<AuctionStatus>(AuctionStatus.Open, AuctionStatus.Closed)]
    public sealed record Closed(DateTimeOffset At) : Event;

    [Transition<AuctionStatus>(AuctionStatus.Closed, AuctionStatus.Settled)]
    public sealed record Settled(Id<Bidder>? Winner, decimal Price) : Event;

    /// <summary>Allowed in 2025, forbidden by today's lifecycle; old streams still contain it.</summary>
    public sealed record Reopened : Event;

    public sealed record Paid : Event;

    private void On(Opened e)
    {
        CurrentPrice = e.MinimalPrice;
        EndTime = e.EndTime;
    }

    private void On(Closed e)
    {
        Status = AuctionStatus.Closed;
        ClosedAt = e.At;
    }

    private void On(Settled e)
    {
        Status = AuctionStatus.Settled;
        Winner = e.Winner;
        CurrentPrice = e.Price;
    }

    private void On(Reopened e)
    {
        _ = e;
        Status = AuctionStatus.Open;
        ClosedAt = null;
    }

    private void On(Paid e)
    {
        _ = e;
        Payout = PayoutStatus.Paid;
    }

    private void On(Bid.Placed e) => CurrentPrice = e.Amount;

    private void On(Bid.Won e)
    {
        _ = e;
        Payout = PayoutStatus.Due;
    }

    [Invariant]
    private Result ClosedHasClosedAt()
        => Status == AuctionStatus.Open || ClosedAt is not null ? Result.Success() : Error.Conflict("Auction.ClosedWithoutTime");

    [Invariant]
    private Result SettledHasNoActiveBid()
        => Status != AuctionStatus.Settled || Bids.All(static b => b.Status != BidStatus.Active)
            ? Result.Success()
            : Error.Conflict("Auction.SettledWithActiveBid");
}

public partial class Bid : Entity<Auction, Bid>
{
    public Id<Bidder> BidderId { get; private set; }
    public decimal Amount { get; private set; }
    public BidStatus Status { get; private set; }

    public Result Withdraw()
        => Status.CanTransitionTo(BidStatus.Withdrawn) ? Raise(new Withdrawn()) : Error.Conflict("Bid.NotActive");

    internal Success Decide(bool won) => won ? Raise(new Won()) : Raise(new Lost());

    /// <summary>A behaviour that forgot its guard: Withdrawn -> Active is not an edge.</summary>
    public Success ReinstateWithoutGuard() => Raise(new Reinstated());

    public sealed record Placed(Id<Bidder> BidderId, decimal Amount) : Created;

    public sealed record PlacedAsWinner(Id<Bidder> BidderId, decimal Amount) : Created;

    [Transition<BidStatus>(BidStatus.Active, BidStatus.Withdrawn)]
    public sealed record Withdrawn : Event;

    [Transition<BidStatus>(BidStatus.Active, BidStatus.Won)]
    public sealed record Won : Event;

    [Transition<BidStatus>(BidStatus.Active, BidStatus.Lost)]
    public sealed record Lost : Event;

    public sealed record Reinstated : Event;

    private void On(Placed e)
    {
        BidderId = e.BidderId;
        Amount = e.Amount;
    }

    private void On(PlacedAsWinner e)
    {
        BidderId = e.BidderId;
        Amount = e.Amount;
        Status = BidStatus.Won;
    }

    private void On(Withdrawn e)
    {
        _ = e;
        Status = BidStatus.Withdrawn;
    }

    private void On(Won e)
    {
        _ = e;
        Status = BidStatus.Won;
    }

    private void On(Lost e)
    {
        _ = e;
        Status = BidStatus.Lost;
    }

    private void On(Reinstated e)
    {
        _ = e;
        Status = BidStatus.Active;
    }
}
