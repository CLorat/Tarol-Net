using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Caching;

public enum AuctionStatus { Open, Closed }

/// <summary>The write side with a stored state (streams.state): loads read the state.</summary>
public partial class Auction : AggregateRoot<Auction>
{
    public string Seller { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Lot { get; private set; } = "";
    public DateTimeOffset EndsAt { get; private set; }
    public AuctionStatus Status { get; private set; }
    public decimal HighestBid { get; private set; }
    public int Bids { get; private set; }

    public static Auction Open(string seller, string title, string lot, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var a = new Auction(Id<Auction>.New(now));
        a.Raise(new Opened(seller, title, lot, endsAt));
        return a;
    }

    public Result Bid(string bidder, decimal amount)
        => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed")
         : amount <= HighestBid ? Error.Conflict("Auction.BidTooLow")
         : Raise(new BidPlaced(bidder, amount));

    public Result Close() => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed") : Raise(new Closed());

    /// <summary>Gives the lot (a unique lookup) to another auction: the lookup is released by this commit.</summary>
    public Result Withdraw() => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed") : Raise(new Withdrawn());

    public sealed record Opened(string Seller, string Title, string Lot, DateTimeOffset EndsAt) : Event;
    public sealed record BidPlaced(string Bidder, decimal Amount) : Event;
    public sealed record Closed : Event;
    public sealed record Withdrawn : Event;

    private void On(Opened e) => (Seller, Title, Lot, EndsAt, Status) = (e.Seller, e.Title, e.Lot, e.EndsAt, AuctionStatus.Open);
    private void On(BidPlaced e) { HighestBid = e.Amount; Bids++; }
    private void On(Closed e) => Status = AuctionStatus.Closed;
    private void On(Withdrawn e) { Status = AuctionStatus.Closed; Lot = ""; }
}

/// <summary>The same events, loaded by replay (no stored state): the "plain load" of an event-sourced aggregate.</summary>
[LoadByReplay]
public partial class Ledger : AggregateRoot<Ledger>
{
    public decimal Balance { get; private set; }
    public int Entries { get; private set; }
    public static Ledger Open(DateTimeOffset now) { var l = new Ledger(Id<Ledger>.New(now)); l.Raise(new Started()); return l; }
    public Result Post(decimal amount) => Raise(new Posted(amount));
    public sealed record Started : Event;
    public sealed record Posted(decimal Amount) : Event;
#pragma warning disable CA1822
    private void On(Started e) { }
#pragma warning restore CA1822
    private void On(Posted e) { Balance += e.Amount; Entries++; }
}

/// <summary>A bid as a change (the pipeline loads Target, executes, appends with the expected version).</summary>
public sealed record PlaceBid(Id<Auction> Target, string Bidder, decimal Amount) : IChange<Auction>
{
    public Result Execute(Auction aggregate, ChangeContext context) => aggregate.Bid(Bidder, Amount);
}

[Projection("auction-list")]
public sealed partial record AuctionRow(Guid Id, string Seller, string Title, [Sort] DateTimeOffset EndsAt, AuctionStatus Status, [Sort] decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Seller, e.Title, e.EndsAt, AuctionStatus.Open, 0m, 0);
    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1 };
    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
    public AuctionRow When(Auction.Withdrawn e) => this with { Status = AuctionStatus.Closed };
}

/// <summary>The view of GET by id (step 10 shape: built from the loaded aggregate).</summary>
public sealed record AuctionView(Guid Id, string Seller, string Title, DateTimeOffset EndsAt, AuctionStatus Status, decimal HighestBid, int Bids, long Version)
{
    public static AuctionView From(Auction a) => new(a.Id.Value, a.Seller, a.Title, a.EndsAt, a.Status, a.HighestBid, a.Bids, a.Version);
}

public sealed record PageBody(IReadOnlyList<AuctionRow> Items, string? Next);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.BidPlaced))]
[JsonSerializable(typeof(Auction.Closed))]
[JsonSerializable(typeof(Auction.Withdrawn))]
[JsonSerializable(typeof(Ledger.Started))]
[JsonSerializable(typeof(Ledger.Posted))]
[JsonSerializable(typeof(AuctionRow))]
[JsonSerializable(typeof(AuctionView))]
[JsonSerializable(typeof(PageBody))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(AuctionStatus))]
internal sealed partial class ProbeJson : JsonSerializerContext;
