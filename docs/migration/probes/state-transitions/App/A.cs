using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace ArtFlow.A;

// ---- candidate (a): enum + declared lifecycle; every step is checked on the live path until commit ----

[Lifecycle]
public enum AuctionStatus
{
    [To(Closed)] Open,
    [To(Settled)] Closed,
    Settled,
}

public partial class Auction : AggregateRoot<Auction>
{
    public AuctionStatus Status { get; private set; }
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
        if (Status != AuctionStatus.Open || now > EndTime) return Error.Conflict("Auction.NotOpen");   // a behaviour, not a step
        if (amount <= CurrentPrice) return Error.Conflict("Bid.TooLow");
        return Raise(new Bid.Placed(bidder, amount, now));
    }

    public Result Close(DateTimeOffset now)
    {
        var allowed = Status.RequireTransitionTo(AuctionStatus.Closed);           // AuctionStatus.NotAllowed, "Closed -> Closed"
        if (!allowed.IsSuccess) return allowed;
        if (now < EndTime) return Error.Conflict("Auction.NotEnded");             // a condition of this step: a guard
        return Raise(new Closed(now));
    }

    public Result Settle()
    {
        if (!Status.CanTransitionTo(AuctionStatus.Settled)) return Error.Conflict("Auction.NotClosed");
        Bid? winner = null;
        foreach (var bid in Bids)
            if (bid.Status == BidStatus.Active && (winner is null || bid.Amount > winner.Amount))
                winner = bid;
        foreach (var bid in Bids)
            if (bid.Status == BidStatus.Active)
                bid.Decide(won: bid == winner);
        return Raise(new Settled(winner?.BidderId, winner?.Amount ?? CurrentPrice));
    }

    /// <summary>Two steps in one behaviour (and one commit): Open -> Closed -> Settled.</summary>
    public Result CloseAndSettle(DateTimeOffset now)
    {
        var closed = Close(now);
        return closed.IsSuccess ? Settle() : closed;
    }

    /// <summary>A behaviour that forgot its guard: the step check reports it at commit.</summary>
    public Success ReopenWithoutGuard() => Raise(new Reopened());

    public sealed record Opened(decimal MinimalPrice, DateTimeOffset EndTime) : Event;
    public sealed record Closed(DateTimeOffset At) : Event;
    public sealed record Settled(Id<Bidder>? Winner, decimal Price) : Event;
    /// <summary>Allowed in 2025, forbidden by today's lifecycle; old streams still contain it.</summary>
    public sealed record Reopened : Event;

    private void On(Opened e) { CurrentPrice = e.MinimalPrice; EndTime = e.EndTime; }
    private void On(Closed e) { Status = AuctionStatus.Closed; ClosedAt = e.At; }
    private void On(Settled e) { Status = AuctionStatus.Settled; Winner = e.Winner; CurrentPrice = e.Price; }
    private void On(Reopened e) { Status = AuctionStatus.Open; ClosedAt = null; }
    private void On(Bid.Placed e) => CurrentPrice = e.Amount;

    [Invariant]
    private Result ClosedHasClosedAt()
        => Status == AuctionStatus.Open || ClosedAt is not null ? Result.Success() : Error.Conflict("Auction.ClosedWithoutTime");

    [Invariant]
    private Result SettledHasNoActiveBid()
        => Status != AuctionStatus.Settled || Bids.All(static b => b.Status != BidStatus.Active)
            ? Result.Success() : Error.Conflict("Auction.SettledWithActiveBid");
}

public partial class Bid : Entity<Auction, Bid>
{
    public Id<Bidder> BidderId { get; private set; }
    public decimal Amount { get; private set; }
    public BidStatus Status { get; private set; }

    public Result Withdraw()
        => Status.CanTransitionTo(BidStatus.Withdrawn) ? Raise(new Withdrawn()) : Error.Conflict("Bid.NotActive");

    internal Success Decide(bool won) => won ? Raise(new Won()) : Raise(new Lost());

    public sealed record Placed(Id<Bidder> BidderId, decimal Amount, DateTimeOffset At) : Created;
    public sealed record Withdrawn : Event;
    public sealed record Won : Event;
    public sealed record Lost : Event;

    private void On(Placed e) { BidderId = e.BidderId; Amount = e.Amount; }
    private void On(Withdrawn e) => Status = BidStatus.Withdrawn;
    private void On(Won e) => Status = BidStatus.Won;
    private void On(Lost e) => Status = BidStatus.Lost;
}
