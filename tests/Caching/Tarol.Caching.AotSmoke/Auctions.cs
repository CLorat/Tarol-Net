using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.StronglyTypedId;

namespace Tarol.Caching.AotSmoke;

public partial class Auction : AggregateRoot<Auction>
{
    public decimal HighestBid { get; private set; }

    public static Auction Open(string title, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(title, endsAt));
        return auction;
    }

    public void Bid(decimal amount) => Raise(new BidPlaced(amount));

    public sealed record Opened(string Title, DateTimeOffset EndsAt) : Event;

    public sealed record BidPlaced(decimal Amount) : Event;

#pragma warning disable CA1822 // the title is only projected
    private void On(Opened e) { }
#pragma warning restore CA1822

    private void On(BidPlaced e) => HighestBid = e.Amount;
}

[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, [Sort] DateTimeOffset EndsAt, decimal HighestBid)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.EndsAt, 0m);

    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.BidPlaced))]
[JsonSerializable(typeof(AuctionRow))]
public sealed partial class SmokeJson : JsonSerializerContext;
