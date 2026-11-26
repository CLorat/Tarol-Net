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

/// <summary>A sensitive view (TN-59): every granted read, a 304 included, reaches the access observers.</summary>
[Endpoint("GET /auctions/{auction}/dossier", Sensitive = true)]
public sealed record AuctionDossier(Guid Id, string Seller, decimal Reserve) : IView<Auction, AuctionDossier>
{
    public static AuctionDossier From(Auction a) => new(a.Id.Value, a.Seller, a.Reserve);
}

/// <summary>The auction house's settings: one per tenant (TN-2), its id is the tenant's.</summary>
[PerTenant]
public partial class House : AggregateRoot<House>
{
    public string Name { get; private set; } = "";

    public static House Open(Tarol.Kernel.OperationContext operation, string name)
    {
        var house = new House(TenantAggregates.IdOf<House>(operation));
        house.Raise(new Founded(name));
        return house;
    }

    public Result Rename(string name) => Raise(new Retitled(name));

    public sealed record Founded(string Name) : Event;
    public sealed record Retitled(string Name) : Event;

    private void On(Founded e) => Name = e.Name;
    private void On(Retitled e) => Name = e.Name;
}

/// <summary>GET /house: the tenant's one house, no id in the route (TN-2, TS-8).</summary>
[Endpoint("GET /house")]
public sealed record HouseView(Guid Id, string Name) : IView<House, HouseView>
{
    public static HouseView From(House h) => new(h.Id.Value, h.Name);
}

/// <summary>A view with members From and Until (Tatalu FB-F06.4-1): its factory is implemented explicitly.</summary>
[Endpoint("GET /house/season")]
public sealed record HouseSeasonView(string Name, DateOnly From, DateOnly Until) : IView<House, HouseSeasonView>
{
    static HouseSeasonView IView<House, HouseSeasonView>.From(House h) => new(h.Name, new DateOnly(2026, 4, 1), new DateOnly(2026, 10, 31));
}

/// <summary>The tenant's house as one projection row (TN-54, Tatalu FB-F01.2-4: a history row per tenant).</summary>
[Tarol.Queries.Projection("houses")]
public sealed partial record HouseRow(Guid Id, string Name, int Renames)
{
    public static HouseRow When(House.Founded e, RecordedEvent r) => new(r.StreamId, e.Name, 0);

    public HouseRow When(House.Retitled e) => this with { Name = e.Name, Renames = Renames + 1 };
}

/// <summary>GET /house/row: the caller's tenant's row (House is [PerTenant]).</summary>
[Endpoint("GET /house/row")]
public sealed record TenantHouseRow : Tarol.Queries.IRowQuery<HouseRow>;

/// <summary>GET /house-rows/{house}: the row of a stream id.</summary>
[Endpoint("GET /house-rows/{house}")]
public sealed record HouseRowById(Guid House) : Tarol.Queries.IRowQuery<HouseRow>;

/// <summary>A view for its reader (TN-43, Tatalu FB-E07-3): the content depends on who reads.</summary>
[Endpoint("GET /house/greeting")]
public sealed record HouseGreeting(string Name, string Reader) : IView<House, HouseGreeting>
{
    public static HouseGreeting From(House house, ViewContext context) => new(house.Name, context.Operation.Actor.Id);
}

/// <summary>A restricted view (TN-43, VA-2, Tatalu FB-F01.1-6: tax and bank only for the caregiver): its own grant.</summary>
[Endpoint("GET /house/ledger", Restricted = true)]
public sealed record HouseLedger(Guid Id, string Name) : IView<House, HouseLedger>
{
    public static HouseLedger From(House h) => new(h.Id.Value, h.Name);
}

/// <summary>A shelf that starts with its first book (TN-42, Tatalu FB-F02.5-2: HealthLoad.OrStart in every change).</summary>
public partial class Shelf : AggregateRoot<Shelf>, IStartable<Shelf>
{
    public int Books { get; private set; }

    public static Shelf Start(Id<Shelf> id, ChangeContext context)
    {
        var shelf = new Shelf(id);
        shelf.Raise(new Started());
        return shelf;
    }

    public Result Shelve(string title) => string.IsNullOrWhiteSpace(title) ? Error.Conflict("Shelf.Title") : Raise(new Shelved(title));

    public sealed record Started : Event;
    public sealed record Shelved(string Title) : Event;

    private void On(Started e) => Books = 0;
    private void On(Shelved e) => Books++;
}

[Endpoint("PUT /shelves/{shelf}/books")]
public sealed partial record ShelveBook(Id<Shelf> Shelf, string Title) : IChange<Shelf>, IOrStart<Shelf>
{
    public Result Execute(Shelf shelf, ChangeContext context) => shelf.Shelve(Title);
}

[Endpoint("GET /shelves/{shelf}")]
public sealed record ShelfView(Guid Id, int Books) : IView<Shelf, ShelfView>
{
    public static ShelfView From(Shelf s) => new(s.Id.Value, s.Books);
}

/// <summary>The payment provider's own contract (anti-corruption: its names stay here).</summary>
public sealed record PaymentSucceededV2(string PaymentId, Guid Reference, decimal Amount, string Currency);
public sealed record PaymentSucceededV1(string Id, Guid Order, long Cents);

/// <summary>A bid for the browsers (BC-25: its schema is the topic's in x-tarol-topics).</summary>
public sealed record BidPlacedV1(Guid Auction, decimal Amount, DateTimeOffset At);


[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CreateAuction)), JsonSerializable(typeof(PlaceBid)), JsonSerializable(typeof(CloseAuction))]
[JsonSerializable(typeof(WatchAuction)), JsonSerializable(typeof(TouchAuction)), JsonSerializable(typeof(RenameAuction)), JsonSerializable(typeof(MakeOffer))]
[JsonSerializable(typeof(PaymentSucceededV2)), JsonSerializable(typeof(PaymentSucceededV1)), JsonSerializable(typeof(AuctionSummary)), JsonSerializable(typeof(AuctionDossier))]
[JsonSerializable(typeof(decimal)), JsonSerializable(typeof(string)), JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Auction.Opened)), JsonSerializable(typeof(Auction.Closed)), JsonSerializable(typeof(Auction.PaymentRecorded))]
[JsonSerializable(typeof(Auction.Watched)), JsonSerializable(typeof(Bid.Placed)), JsonSerializable(typeof(Payout.Due))]
[JsonSerializable(typeof(AuctionRow)), JsonSerializable(typeof(SellerTotalRow)), JsonSerializable(typeof(Memo.Written)), JsonSerializable(typeof(BidPlacedV1))]
[JsonSerializable(typeof(House.Founded)), JsonSerializable(typeof(House.Retitled)), JsonSerializable(typeof(OpenHouse)), JsonSerializable(typeof(RenameHouse))]
[JsonSerializable(typeof(HouseView))]
[JsonSerializable(typeof(HouseSeasonView))]
[JsonSerializable(typeof(HouseRow)), JsonSerializable(typeof(CloseAuctionByLink))]
[JsonSerializable(typeof(HouseGreeting))]
[JsonSerializable(typeof(HouseLedger))]
[JsonSerializable(typeof(Shelf.Started))]
[JsonSerializable(typeof(Shelf.Shelved))]
[JsonSerializable(typeof(ShelveBook))]
[JsonSerializable(typeof(ShelfView))]
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

