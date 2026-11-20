using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore.Tests;

// ArtFlow, reduced to what the HTTP entries need (from tests/Changes/Tarol.Changes.Tests/Domain.cs).
public readonly partial record struct Title([Length(3, 60)] string Value);
public readonly partial record struct Amount([Range(1, 1_000_000)] decimal Value);
public readonly partial record struct Contact([Email] string Value);
public readonly partial record struct Price([Range(Min = 0)] decimal Value, [Pattern("^[A-Z]{3}$", IgnoreCase = true)] string Currency);

[Lifecycle]
public enum AuctionStatus { [To(Closed)] Open, Closed }

[Tarol.Search.Searchable("Auction")]
public partial class Auction : AggregateRoot<Auction>
{
    public EntitySet<Bid> Bids { get; } = [];
    public string Seller { get; private set; } = "";
    [Tarol.Search.SearchTitle, Tarol.Search.Searchable(Tarol.Search.SearchWeight.A)] public string Title { get; private set; } = "";
    public decimal Reserve { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public AuctionStatus Status { get; private set; }
    public decimal Paid { get; private set; }
    public string? PaymentId { get; private set; }
    public int Watchers { get; private set; }

    public static Result<Auction> Create(string seller, Title title, Amount reserve, DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (endsAt < now.AddDays(1))
            return Invalid.Field("EndsAt", "TooSoon");
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(seller, title.Value, reserve.Value, endsAt));
        return auction;
    }

    public Result<Bid> PlaceBid(string bidder, Amount amount, DateTimeOffset now)
        => Status != AuctionStatus.Open || now >= EndsAt ? Error.Conflict("Auction.NotOpen")
         : bidder == Seller ? Error.Conflict("Bid.OwnAuction")
         : amount.Value <= HighestBid ? Error.Conflict("Bid.TooLow")
         : Raise(new Bid.Placed(bidder, amount.Value, now));

    public decimal HighestBid => Bids.Select(b => b.Amount).DefaultIfEmpty(Reserve - 1).Max();

    public Result Close(DateTimeOffset now) => Status.RequireTransitionTo(AuctionStatus.Closed).Bind(() => Raise(new Closed(now)));

    /// <summary>A payment webhook: a repeated delivery of the same payment is a success (C-9 would retry a Conflict forever).</summary>
    public Result RecordPayment(Amount amount, string paymentId)
        => PaymentId == paymentId ? Result.Success()
         : Status == AuctionStatus.Open ? Error.Conflict("Auction.NotClosed")
         : PaymentId is not null ? Error.Conflict("Auction.AlreadyPaid")
         : Raise(new PaymentRecorded(amount.Value, paymentId));

    /// <summary>The benchmark's change: no guard, constant state size.</summary>
    public Success Watch(string note) => Raise(new Watched(note));

    public sealed record Opened(string Seller, string Title, decimal Reserve, DateTimeOffset EndsAt) : Event;
    public sealed record Closed(DateTimeOffset At) : Event;
    public sealed record PaymentRecorded(decimal Amount, string PaymentId) : Event;
    public sealed record Watched(string Note) : Event;

    private void On(Opened e) => (Seller, Title, Reserve, EndsAt) = (e.Seller, e.Title, e.Reserve, e.EndsAt);
    private void On(Closed e) => Status = AuctionStatus.Closed;
    private void On(PaymentRecorded e) => (Paid, PaymentId) = (e.Amount, e.PaymentId);
    private void On(Watched e) => Watchers++;
}

public partial class Bid : Entity<Auction, Bid>
{
    public string Bidder { get; private set; } = "";
    public decimal Amount { get; private set; }

    public sealed record Placed(string Bidder, decimal Amount, DateTimeOffset At) : Created;
    private void On(Placed e) => (Bidder, Amount) = (e.Bidder, e.Amount);
}

[Tarol.Search.Searchable("Payout")]
public partial class Payout : AggregateRoot<Payout>
{
    [Tarol.Search.Searchable] public Guid Auction { get; private set; }
    public decimal Amount { get; private set; }

    public static Payout MarkDue(Guid auction, decimal amount, DateTimeOffset now)
    {
        var payout = new Payout(Id<Payout>.New(now));
        payout.Raise(new Due(auction, amount));
        return payout;
    }

    public sealed record Due(Guid Auction, decimal Amount) : Event;
    private void On(Due e) => (Auction, Amount) = (e.Auction, e.Amount);
}

/// <summary>GET by id (R-8): a view of one aggregate (hides Reserve and the bidders), built from the loaded aggregate.</summary>
[Endpoint("GET /auctions/{auction}", Policy = "auctions:read")]
public sealed record AuctionSummary(Guid Id, string Title, string Seller, AuctionStatus Status, decimal HighestBid, int Bids, DateTimeOffset EndsAt,
    Tarol.Options.Option<string> Payment)
    : IView<Auction, AuctionSummary>
{
    public static AuctionSummary From(Auction a) => new(a.Id.Value, a.Title, a.Seller, a.Status, a.Bids.Count == 0 ? 0 : a.HighestBid, a.Bids.Count, a.EndsAt,
        a.PaymentId is { } payment ? payment : Tarol.Options.Option.None);
}

/// <summary>The payment provider's own contract (anti-corruption: its names stay here).</summary>
public sealed record PaymentSucceededV2(string PaymentId, Guid Reference, decimal Amount, string Currency);
public sealed record PaymentSucceededV1(string Id, Guid Order, long Cents);


[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CreateAuction)), JsonSerializable(typeof(PlaceBid)), JsonSerializable(typeof(CloseAuction))]
[JsonSerializable(typeof(WatchAuction)), JsonSerializable(typeof(TouchAuction)), JsonSerializable(typeof(RenameAuction)), JsonSerializable(typeof(MakeOffer))]
[JsonSerializable(typeof(PaymentSucceededV2)), JsonSerializable(typeof(PaymentSucceededV1)), JsonSerializable(typeof(AuctionSummary))]
[JsonSerializable(typeof(decimal)), JsonSerializable(typeof(string)), JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Auction.Opened)), JsonSerializable(typeof(Auction.Closed)), JsonSerializable(typeof(Auction.PaymentRecorded))]
[JsonSerializable(typeof(Auction.Watched)), JsonSerializable(typeof(Bid.Placed)), JsonSerializable(typeof(Payout.Due))]
[JsonSerializable(typeof(AuctionRow)), JsonSerializable(typeof(Memo.Written))]
public sealed partial class AppJson : JsonSerializerContext;

/// <summary>A note visible to its owner only (step 18D: the audience of a search hit).</summary>
[Tarol.Search.Searchable("Memo")]
public partial class Memo : AggregateRoot<Memo>
{
    [Tarol.Search.SearchTitle, Tarol.Search.Searchable(Tarol.Search.SearchWeight.A)] public string Text { get; private set; } = "";
    [Tarol.Search.SearchAudience] public string? Owner { get; private set; }

    public static Memo Write(string text, string? owner)
    {
        var memo = new Memo(Id<Memo>.New());
        memo.Raise(new Written(text, owner));
        return memo;
    }

    public sealed record Written(string Text, string? Owner) : Event;
    private void On(Written e) => (Text, Owner) = (e.Text, e.Owner);
}

