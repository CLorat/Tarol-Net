using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.Tests;

// ArtFlow, reduced to what the changes need (docs/migration/probes/changes/App/Domain.cs).
public readonly partial record struct Title([Length(3, 60)] string Value);
public readonly partial record struct Amount([Range(1, 1_000_000)] decimal Value);

[Lifecycle]
public enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, Settled }

[Lifecycle]
public enum BidStatus { [To(Won, Lost)] Active, Won, Lost }

public partial class Auction : AggregateRoot<Auction>
{
    public EntitySet<Bid> Bids { get; } = [];
    public string Lot { get; private set; } = "";
    public string Seller { get; private set; } = "";
    public decimal Reserve { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public AuctionStatus Status { get; private set; }

    public static Result<Auction> Create(string seller, string lot, Title title, Amount reserve, DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (endsAt < now.AddDays(1))
            return Invalid.Field("EndsAt", "TooSoon");
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(seller, lot, title.Value, reserve.Value, endsAt));
        return auction;
    }

    public static Result<Auction> CreateFor(Artwork artwork, string seller, string lot, Title title, Amount reserve, DateTimeOffset endsAt, DateTimeOffset now)
        => artwork.IsVerified ? Create(seller, lot, title, reserve, endsAt, now) : Error.Conflict("Artwork.NotVerified");

    public Result<Bid> PlaceBid(string bidder, Amount amount, DateTimeOffset now)
        => Status != AuctionStatus.Open || now >= EndsAt ? Error.Conflict("Auction.NotOpen")
         : amount.Value <= Bids.Select(b => b.Amount).DefaultIfEmpty(Reserve - 1).Max() ? Error.Conflict("Bid.TooLow")
         : Raise(new Bid.Placed(bidder, amount.Value, now));

    public Result Close(DateTimeOffset now) => Status.RequireTransitionTo(AuctionStatus.Closed).Bind(() => Raise(new Closed(now)));

    public Result Settle(DateTimeOffset now)
    {
        if (now < EndsAt)
            return Error.Conflict("Auction.NotEnded");
        var close = Status == AuctionStatus.Open ? Close(now) : Result.Success();
        if (!close.IsSuccess)
            return close;
        var winner = Bids.OrderByDescending(b => b.Amount).FirstOrDefault();
        foreach (var bid in Bids)
            if (bid != winner)
                _ = bid.Lose();
        _ = winner?.Win();
        return Status.RequireTransitionTo(AuctionStatus.Settled).Bind(() => Raise(new Settled(now)));
    }

    /// <summary>Raises nothing: a change that commits no event.</summary>
    public Result Touch() => Version > 0 ? Result.Success() : Error.Conflict("Auction.New");

    /// <summary>Raises an event that breaks the invariant.</summary>
    public Success Sabotage() => Raise(new Sabotaged());

    /// <summary>Throws while deciding.</summary>
    public Result Explode() => throw new InvalidOperationException($"boom {Id}");

    [Invariant]
    private Result ReserveIsPositive() => Reserve > 0 ? Result.Success() : Error.Conflict("Auction.Reserve");

    public sealed record Opened(string Seller, string Lot, string Title, decimal Reserve, DateTimeOffset EndsAt) : Event;
    public sealed record Closed(DateTimeOffset At) : Event;
    public sealed record Settled(DateTimeOffset At) : Event;
    public sealed record Sabotaged : Event;

    private void On(Opened e) => (Seller, Lot, Reserve, EndsAt) = (e.Seller, e.Lot, e.Reserve, e.EndsAt);
    private void On(Closed e) => Status = AuctionStatus.Closed;
    private void On(Settled e) => Status = AuctionStatus.Settled;
    private void On(Sabotaged e) => Reserve = 0;
}

public partial class Bid : Entity<Auction, Bid>
{
    public string Bidder { get; private set; } = "";
    public decimal Amount { get; private set; }
    public BidStatus Status { get; private set; }

    public Success Win() => Raise(new Won(Amount));
    public Success Lose() => Raise(new Lost());

    public sealed record Placed(string Bidder, decimal Amount, DateTimeOffset At) : Created;
    public sealed record Won(decimal Amount) : Event;
    public sealed record Lost : Event;

    private void On(Placed e) => (Bidder, Amount) = (e.Bidder, e.Amount);
    private void On(Won e) => Status = BidStatus.Won;
    private void On(Lost e) => Status = BidStatus.Lost;
}

public partial class Artwork : AggregateRoot<Artwork>
{
    public bool IsVerified { get; private set; }
    public bool IsRegistered { get; private set; }

    public static Artwork Register(bool verified)
    {
        var artwork = new Artwork(Id<Artwork>.New());
        artwork.Raise(new Registered());
        if (verified)
            artwork.Raise(new Verified());
        return artwork;
    }

    public sealed record Registered : Event;
    public sealed record Verified : Event;
    private void On(Registered e) => IsRegistered = true;
    private void On(Verified e) => IsVerified = true;
}

public partial class Payout : AggregateRoot<Payout>
{
    public decimal Amount { get; private set; }
    public bool Approved { get; private set; }

    public static Result<Payout> Due(Id<Payout> id, decimal amount)
    {
        var payout = new Payout(id);
        payout.Raise(new BecameDue(amount));
        return payout;
    }

    public Result Approve() => Approved ? Error.Conflict("Payout.AlreadyApproved") : Raise(new WasApproved());

    public sealed record BecameDue(decimal Amount) : Event;
    public sealed record WasApproved : Event;
    private void On(BecameDue e) => Amount = e.Amount;
    private void On(WasApproved e) => Approved = true;
}

[JsonSerializable(typeof(decimal))]
public partial class TestJson : JsonSerializerContext;
