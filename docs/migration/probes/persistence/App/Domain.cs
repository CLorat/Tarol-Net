using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace ArtFlow;

// a value object inside events and state: needs ValueObjectJson.Resolver first (V-9)
public readonly partial record struct Title([Length(3, 60)] string Value);

public partial class Auction : AggregateRoot<Auction>
{
    public EntitySet<Bid> Bids { get; } = [];
    public Title Title { get; private set; }
    public string Lot { get; private set; } = "";
    public string Seller { get; private set; } = "";
    public decimal Reserve { get; private set; }
    public string Currency { get; private set; } = "";
    public bool IsClosed { get; private set; }

    public static Auction Open(Id<Auction> id, string seller, Title title, string lot, decimal reserve)
    {
        var auction = new Auction(id);
        auction.Raise(new Opened(seller, title, lot, reserve, "EUR"));
        return auction;
    }

    public Result<Bid> PlaceBid(string bidder, decimal amount)
        => IsClosed ? Error.Conflict("Auction.Closed")
         : amount <= Bids.Select(b => b.Amount).DefaultIfEmpty(Reserve - 1).Max() ? Error.Conflict("Bid.TooLow")
         : Raise(new Bid.Placed(bidder, amount));

    public Result Relot(string lot) => Raise(new Relotted(lot));

    public Result Close() => IsClosed ? Error.Conflict("Auction.Closed") : Raise(new Closed());

    // v2 added Currency; v1 payloads are upcast (R-2)
    [EventType("auction.opened", 2)]
    public sealed record Opened(string Seller, Title Title, string Lot, decimal Reserve, string Currency) : Event;
    public sealed record Relotted(string Lot) : Event;
    public sealed record Closed : Event;

    private void On(Opened e) => (Seller, Title, Lot, Reserve, Currency) = (e.Seller, e.Title, e.Lot, e.Reserve, e.Currency);
    private void On(Relotted e) => Lot = e.Lot;
    private void On(Closed e) => IsClosed = true;
}

public partial class Bid : Entity<Auction, Bid>
{
    public string Bidder { get; private set; } = "";
    public decimal Amount { get; private set; }

    public sealed record Placed(string Bidder, decimal Amount) : Created;

    private void On(Placed e) => (Bidder, Amount) = (e.Bidder, e.Amount);
}

[LoadByReplay]
public partial class Ledger : AggregateRoot<Ledger>
{
    public decimal Total { get; private set; }

    public static Ledger Start(Id<Ledger> id) { var l = new Ledger(id); l.Raise(new Started()); return l; }
    public Success Book(decimal amount) => Raise(new Booked(amount));

    public sealed record Started : Event;
    public sealed record Booked(decimal Amount) : Event;
    private void On(Started e) => Total = 0;
    private void On(Booked e) => Total += e.Amount;
}

// what the Primitives generator would emit as T.Events (candidate K-A); written by hand in the probe
public static class Catalogs
{
    public static readonly EventCatalog Auction = new("auction",
    [
        new(new("auction.opened", 2), typeof(Auction.Opened)),
        new(new("auction.relotted", 1), typeof(Auction.Relotted)),
        new(new("auction.closed", 1), typeof(Auction.Closed)),
        new(new("auction.bid.placed", 1), typeof(Bid.Placed)),
    ]);

    public static readonly EventCatalog Ledger = new("ledger",
    [
        new(new("ledger.started", 1), typeof(Ledger.Started)),
        new(new("ledger.booked", 1), typeof(Ledger.Booked)),
    ]);
}

/// <summary>auction.opened v1 → v2: Currency did not exist; every v1 auction was in EUR.</summary>
public sealed class OpenedV1ToV2 : IEventUpcaster
{
    public EventType From => new("auction.opened", 1);
    public EventType To => new("auction.opened", 2);
    public void Upcast(JsonObject payload) => payload["currency"] = "EUR";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.Relotted))]
[JsonSerializable(typeof(Auction.Closed))]
[JsonSerializable(typeof(Bid.Placed))]
[JsonSerializable(typeof(Ledger.Started))]
[JsonSerializable(typeof(Ledger.Booked))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
public partial class AppJson : JsonSerializerContext;
