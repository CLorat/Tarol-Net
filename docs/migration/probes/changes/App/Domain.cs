using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace ArtFlow;

// value objects: rules once (Tarol.Validations)
public readonly partial record struct Title([Length(3, 60)] string Value);
public readonly partial record struct Amount([Range(1, 1_000_000)] decimal Value);

[Lifecycle]
public enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, [To(Paid)] Settled, Paid }

[Lifecycle]
public enum BidStatus { [To(Won, Lost)] Active, Won, Lost }

public partial class Auction : AggregateRoot<Auction>
{
    public EntitySet<Bid> Bids { get; } = [];
    public string Title { get; private set; } = "";
    public string Seller { get; private set; } = "";
    public decimal Reserve { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public AuctionStatus Status { get; private set; }
    public decimal Paid { get; private set; }

    // creation: a static factory; the caller picks the id so a retried creation is idempotent
    public static Result<Auction> Create(Id<Auction> id, string seller, Title title, Amount reserve, DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (endsAt < now.AddDays(1))
            return Invalid.Field("EndsAt", "TooSoon");
        var auction = new Auction(id);
        auction.Raise(new Opened(seller, title.Value, reserve.Value, endsAt));
        return auction;
    }

    // needs another aggregate read-only: only a verified artwork can be auctioned (old ArtFlow prerequisite ArtworkVerified)
    public static Result<Auction> CreateFor(Artwork artwork, string seller, Title title, Amount reserve, DateTimeOffset endsAt, DateTimeOffset now)
        => artwork.IsVerified ? Create(Id<Auction>.New(now), seller, title, reserve, endsAt, now) : Error.Conflict("Artwork.NotVerified");

    public Result<Bid> PlaceBid(string bidder, Amount amount, DateTimeOffset now)
        => Status != AuctionStatus.Open || now >= EndsAt ? Error.Conflict("Auction.NotOpen")
         : amount.Value <= Bids.Select(b => b.Amount).DefaultIfEmpty(Reserve - 1).Max() ? Error.Conflict("Bid.TooLow")
         : Raise(new Bid.Placed(bidder, amount.Value, now));

    // a user closes early (seller) or the job closes at the end time
    public Result Close(DateTimeOffset now)
        => Status.RequireTransitionTo(AuctionStatus.Closed).Bind(() => Raise(new Closed(now)));

    // settle after the end time: the highest bid wins
    public Result Settle(DateTimeOffset now)
    {
        if (now < EndsAt)
            return Error.Conflict("Auction.NotEnded");
        var close = Status == AuctionStatus.Open ? Close(now) : Result.Success();
        if (!close.IsSuccess)
            return close;
        var winner = Bids.OrderByDescending(b => b.Amount).FirstOrDefault();
        foreach (var bid in Bids)
            if (bid != winner) bid.Lose();
        winner?.Win();
        return Status.RequireTransitionTo(AuctionStatus.Settled).Bind(() => Raise(new Settled(now)));
    }

    public Result RecordPayment(Amount amount, string reference)
        => Status != AuctionStatus.Settled ? Error.Conflict("Auction.NotSettled")
         : Raise(new PaymentRecorded(amount.Value, reference));

    public sealed record Opened(string Seller, string Title, decimal Reserve, DateTimeOffset EndsAt) : Event;
    public sealed record Closed(DateTimeOffset At) : Event;
    public sealed record Settled(DateTimeOffset At) : Event;
    public sealed record PaymentRecorded(decimal Amount, string Reference) : Event;

    private void On(Opened e) => (Seller, Title, Reserve, EndsAt) = (e.Seller, e.Title, e.Reserve, e.EndsAt);
    private void On(Closed e) => Status = AuctionStatus.Closed;
    private void On(Settled e) => Status = AuctionStatus.Settled;
    private void On(PaymentRecorded e)
    {
        Paid += e.Amount;
        if (Paid >= Bids.Where(b => b.Status == BidStatus.Won).Sum(b => b.Amount))
            Status = AuctionStatus.Paid;
    }
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

public enum PayoutStatus { Due, Paid }

public partial class Payout : AggregateRoot<Payout>
{
    public Guid Auction { get; private set; }
    public decimal Amount { get; private set; }
    public PayoutStatus Status { get; private set; }

    public bool Approved { get; private set; }

    public Result Approve(DateTimeOffset now) => Approved ? Error.Conflict("Payout.AlreadyApproved") : Raise(new WasApproved(now));
    public sealed record WasApproved(DateTimeOffset At) : Event;
    private void On(WasApproved e) => Approved = true;

    public static Result<Payout> MarkDue(Id<Payout> id, Guid auction, decimal amount, DateTimeOffset now)
    {
        var payout = new Payout(id);
        payout.Raise(new BecameDue(auction, amount, now));
        return payout;
    }

    public sealed record BecameDue(Guid Auction, decimal Amount, DateTimeOffset At) : Event;
    private void On(BecameDue e) => (Auction, Amount) = (e.Auction, e.Amount);
}

public partial class Artwork : AggregateRoot<Artwork>
{
    public string Title { get; private set; } = "";
    public bool IsVerified { get; private set; }

    public static Artwork Register(string title, DateTimeOffset now)
    {
        var artwork = new Artwork(Id<Artwork>.New(now));
        artwork.Raise(new Registered(title));
        return artwork;
    }

    public Result Verify() => IsVerified ? Error.Conflict("Artwork.AlreadyVerified") : Raise(new Verified());

    public sealed record Registered(string Title) : Event;
    public sealed record Verified : Event;
    private void On(Registered e) => Title = e.Title;
    private void On(Verified e) => IsVerified = true;
}
