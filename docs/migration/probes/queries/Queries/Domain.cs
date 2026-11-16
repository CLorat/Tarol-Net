using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Queries;

public enum AuctionStatus { Open, Closed }

/// <summary>The write side: an auction with a stored state (streams.state jsonb).</summary>
public partial class Auction : AggregateRoot<Auction>
{
    public string Seller { get; private set; } = "";
    public string Title { get; private set; } = "";
    public DateTimeOffset EndsAt { get; private set; }
    public AuctionStatus Status { get; private set; }
    public decimal HighestBid { get; private set; }
    public int Bids { get; private set; }

    public static Auction Open(string seller, string title, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var a = new Auction(Id<Auction>.New(now));
        a.Raise(new Opened(seller, title, endsAt));
        return a;
    }

    public Result Bid(string bidder, decimal amount)
        => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed")
         : amount <= HighestBid ? Error.Conflict("Auction.BidTooLow")
         : Raise(new BidPlaced(bidder, amount));

    public Result Close() => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed") : Raise(new Closed());

    public sealed record Opened(string Seller, string Title, DateTimeOffset EndsAt) : Event;
    public sealed record BidPlaced(string Bidder, decimal Amount) : Event;
    public sealed record Closed : Event;

    private void On(Opened e) => (Seller, Title, EndsAt, Status) = (e.Seller, e.Title, e.EndsAt, AuctionStatus.Open);
    private void On(BidPlaced e) { HighestBid = e.Amount; Bids++; }
    private void On(Closed e) => Status = AuctionStatus.Closed;
}

/// <summary>A second stream type the auction list ignores (the feed carries every type).</summary>
public partial class Lot : AggregateRoot<Lot>
{
    public string Name { get; private set; } = "";
    public static Lot Register(string name, DateTimeOffset now) { var l = new Lot(Id<Lot>.New(now)); l.Raise(new Registered(name)); return l; }
    public sealed record Registered(string Name) : Event;
    private void On(Registered e) => Name = e.Name;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(AuctionStatus))]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.BidPlaced))]
[JsonSerializable(typeof(Auction.Closed))]
[JsonSerializable(typeof(Lot.Registered))]
[JsonSerializable(typeof(AuctionRow))]
internal sealed partial class ProbeJson : JsonSerializerContext;
