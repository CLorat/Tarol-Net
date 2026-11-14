using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace ArtFlow.B;

// ---- candidate (b): a closed hierarchy of states that carry their data ----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(Open), "open")]
[JsonDerivedType(typeof(Closed), "closed")]
[JsonDerivedType(typeof(Settled), "settled")]
public closed record AuctionState
{
    public sealed record Open : AuctionState;
    public sealed record Closed(DateTimeOffset At) : AuctionState;
    public sealed record Settled(DateTimeOffset ClosedAt, Id<Bidder>? Winner, decimal Price) : AuctionState;

    /// <summary>The graph, written by hand (what a later [To&lt;T&gt;] declaration would generate).</summary>
    public static bool IsEdge(AuctionState from, AuctionState to) => (from, to) switch
    {
        (Open, Closed) => true,
        (Closed, Settled) => true,
        _ => false,
    };
}

public partial class Auction : AggregateRoot<Auction>
{
    private static readonly AuctionState.Open OpenState = new();

    public AuctionState State { get; private set; } = OpenState;
    public DateTimeOffset EndTime { get; private set; }
    public decimal CurrentPrice { get; private set; }
    public EntitySet<Bid> Bids { get; } = [];

    public static Auction Open(Id<Auction> id, decimal minimalPrice, DateTimeOffset endTime)
    {
        var auction = new Auction(id);
        auction.Raise(new Opened(minimalPrice, endTime));
        return auction;
    }

    public Result<Bid> PlaceBid(Id<Bidder> bidder, decimal amount, DateTimeOffset now)
    {
        if (State is not AuctionState.Open || now > EndTime) return Error.Conflict("Auction.NotOpen");
        if (amount <= CurrentPrice) return Error.Conflict("Bid.TooLow");
        return Raise(new Bid.Placed(bidder, amount, now));
    }

    public Result Close(DateTimeOffset now)
    {
        if (State is not AuctionState.Open) return Error.Conflict("Auction.NotOpen");
        if (now < EndTime) return Error.Conflict("Auction.NotEnded");
        return Raise(new Closed(now));
    }

    public Result Settle()
    {
        if (State is not AuctionState.Closed closed) return Error.Conflict("Auction.NotClosed");
        Bid? winner = null;
        foreach (var bid in Bids)
            if (bid.Status == BidStatus.Active && (winner is null || bid.Amount > winner.Amount))
                winner = bid;
        foreach (var bid in Bids)
            if (bid.Status == BidStatus.Active)
                bid.Decide(won: bid == winner);
        return Raise(new Settled(closed.At, winner?.BidderId, winner?.Amount ?? CurrentPrice));
    }

    public Success ReopenWithoutGuard() => Raise(new Reopened());

    public string Describe() => State switch      // exhaustive without a default arm (CS8509 if a case is missing)
    {
        AuctionState.Open => "open",
        AuctionState.Closed c => $"closed at {c.At:O}",
        AuctionState.Settled s => $"settled for {s.Price}",
    };

    public sealed record Opened(decimal MinimalPrice, DateTimeOffset EndTime) : Event;
    public sealed record Closed(DateTimeOffset At) : Event;
    public sealed record Settled(DateTimeOffset ClosedAt, Id<Bidder>? Winner, decimal Price) : Event;
    public sealed record Reopened : Event;

    private void On(Opened e) { CurrentPrice = e.MinimalPrice; EndTime = e.EndTime; }
    private void On(Closed e) => State = new AuctionState.Closed(e.At);                 // built from the event, never from State
    private void On(Settled e) { State = new AuctionState.Settled(e.ClosedAt, e.Winner, e.Price); CurrentPrice = e.Price; }
    private void On(Reopened e) => State = OpenState;
    private void On(Bid.Placed e) => CurrentPrice = e.Amount;

    // "a closed auction has an end time" is gone: AuctionState.Closed cannot exist without At.
    [Invariant]
    private Result SettledHasNoActiveBid()
        => State is not AuctionState.Settled || Bids.All(static b => b.Status != BidStatus.Active)
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
