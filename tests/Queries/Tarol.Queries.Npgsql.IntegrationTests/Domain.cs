using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Npgsql.IntegrationTests;

public enum AuctionStatus
{
    Open,
    Closed,
}

/// <summary>The write side: an auction with bids (ArtFlow, reduced).</summary>
public partial class Auction : AggregateRoot<Auction>
{
    public string Seller { get; private set; } = "";
    public AuctionStatus Status { get; private set; }
    public decimal HighestBid { get; private set; }

    public static Auction Open(string seller, string title, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(seller, title, endsAt));
        return auction;
    }

    public Result Bid(string bidder, decimal amount)
        => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed")
         : amount <= HighestBid ? Error.Conflict("Auction.BidTooLow")
         : Raise(new BidPlaced(bidder, amount));

    public Result Close() => Status != AuctionStatus.Open ? Error.Conflict("Auction.Closed") : Raise(new Closed());

    public Result Rename(string title) => Raise(new Renamed(title));

    public sealed record Opened(string Seller, string Title, DateTimeOffset EndsAt) : Event;
    public sealed record BidPlaced(string Bidder, decimal Amount) : Event;
    public sealed record Closed : Event;
    public sealed record Renamed(string Title) : Event;

    private void On(Opened e) => (Seller, Status) = (e.Seller, AuctionStatus.Open);
    private void On(BidPlaced e) => HighestBid = e.Amount;
    private void On(Closed e) => Status = AuctionStatus.Closed;
#pragma warning disable CA1822 // an event that changes nothing the aggregate decides on still needs its On handler
    private void On(Renamed e) { }
#pragma warning restore CA1822
}

/// <summary>A second stream type the auction list ignores (the feed carries every type).</summary>
public partial class Lot : AggregateRoot<Lot>
{
    public string Name { get; private set; } = "";

    public static Lot Register(string name, DateTimeOffset now)
    {
        var lot = new Lot(Id<Lot>.New(now));
        lot.Raise(new Registered(name));
        return lot;
    }

    public sealed record Registered(string Name) : Event;

    private void On(Registered e) => Name = e.Name;
}

/// <summary>The read model of the auction list (queries.md §3). Renamed is not handled: the row stays as it is.</summary>
[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt, AuctionStatus Status,
    [Sort] decimal HighestBid, int Bids, string? Winner)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r)
        => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0, null);

    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1, Winner = e.Bidder };

    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

/// <summary>The same list, version 2 (a new column): built from the start next to v1 (QR-3).</summary>
[Projection("auction-list", Version = 2)]
public sealed partial record AuctionRowV2(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt, AuctionStatus Status,
    [Sort] decimal HighestBid, int Bids, string? Winner, bool HasBids)
{
    public static AuctionRowV2 When(Auction.Opened e, RecordedEvent r)
        => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0, null, false);

    public AuctionRowV2 When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1, Winner = e.Bidder, HasBids = true };

    public AuctionRowV2 When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

/// <summary>A day-care log entry (Tatalu, TN-6): about one child, or a group entry that names several children and labels.</summary>
public partial class LogEntry : AggregateRoot<LogEntry>
{
    public Guid Child { get; private set; }

    public static LogEntry Write(Guid child, Guid[] tagged, string[] labels, string text, DateTimeOffset now)
    {
        var entry = new LogEntry(Id<LogEntry>.New(now));
        entry.Raise(new Written(child, tagged, labels, text));
        return entry;
    }

    public sealed record Written(Guid Child, Guid[] Tagged, string[] Labels, string Text) : Event;

    private void On(Written e) => Child = e.Child;
}

/// <summary>The log list: a uuid column, a uuid[] and a text[] column (set filters, TN-6).</summary>
[Projection("log-list")]
public sealed partial record LogRow(Guid Id, Guid Child, Guid[] Tagged, IReadOnlyList<string> Labels, string Text, [Sort] DateTimeOffset At)
{
    public static LogRow When(LogEntry.Written e, RecordedEvent r) => new(r.StreamId, e.Child, e.Tagged, e.Labels, e.Text, r.RecordedAt);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.Opened))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.BidPlaced))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.Closed))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.Renamed))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Lot.Registered))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AuctionRow))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AuctionRowV2))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LogEntry.Written))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LogRow))]
public sealed partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;
