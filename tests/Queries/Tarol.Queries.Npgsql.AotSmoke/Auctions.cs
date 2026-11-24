using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Npgsql.AotSmoke;

public enum AuctionStatus
{
    Open,
    Closed,
}

public partial class Auction : AggregateRoot<Auction>
{
    public decimal HighestBid { get; private set; }

    public bool Closed { get; private set; }

    public static Auction Open(string title, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(title, endsAt));
        return auction;
    }

    public Result Bid(decimal amount) => Closed || amount <= HighestBid ? Error.Conflict("Auction.Bid") : Raise(new BidPlaced(amount));

    public Result Close() => Closed ? Error.Conflict("Auction.Closed") : Raise(new WasClosed());

    public sealed record Opened(string Title, DateTimeOffset EndsAt) : Event;
    public sealed record BidPlaced(decimal Amount) : Event;
    public sealed record WasClosed : Event;

#pragma warning disable CA1822 // the title is not decided on
    private void On(Opened e) { }
#pragma warning restore CA1822
    private void On(BidPlaced e) => HighestBid = e.Amount;
    private void On(WasClosed e) => Closed = true;
}

[Projection("auction-list")]
public sealed partial record AuctionRow(Guid Id, string Title, [Sort] DateTimeOffset EndsAt, AuctionStatus Status, [Sort] decimal HighestBid, int Bids,
    string[] Bidders)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.EndsAt, AuctionStatus.Open, 0m, 0, []);

    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1, Bidders = [.. Bidders, "b" + Bids] };

    public AuctionRow When(Auction.WasClosed e) => this with { Status = AuctionStatus.Closed };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.BidPlaced))]
[JsonSerializable(typeof(Auction.WasClosed))]
[JsonSerializable(typeof(AuctionRow))]
internal sealed partial class SmokeJson : JsonSerializerContext;
