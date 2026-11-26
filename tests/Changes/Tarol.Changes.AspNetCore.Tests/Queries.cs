using Tarol.Primitives;
using Tarol.Queries;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>The auction list (queries.md §3): one row per auction, bids from the entity's creation event.</summary>
[Projection("auction-list")]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt, AuctionStatus Status, [Sort] decimal HighestBid, int Bids,
    [Sort] DateOnly EndsOn, TimeOnly EndsAtTime)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0,
        DateOnly.FromDateTime(e.EndsAt.UtcDateTime), TimeOnly.FromDateTime(e.EndsAt.UtcDateTime));

    public AuctionRow When(Bid.Placed e) => this with { HighestBid = Math.Max(HighestBid, e.Amount), Bids = Bids + 1 };

    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

public enum AuctionSort
{
    EndsAt,
    [Descending] HighestBid,
    Id,
}

/// <summary>Open or closed auctions of a seller, ending soonest first.</summary>
[Endpoint("GET /auctions", Policy = "auctions:read")]
public sealed record AuctionSearch(AuctionStatus? Status, string? Seller, AuctionSort Sort = AuctionSort.EndsAt) : IQuery<AuctionRow>;

/// <summary>The caller's own auctions: the seller is the caller's subject, never a request value (QR-6).</summary>
[Endpoint("GET /my/auctions")]
public sealed record MyAuctions([FromClaim("sub")] string Seller, AuctionStatus? Status = null) : IQuery<AuctionRow>;

public enum DaySort
{
    EndsOn,
    Id,
}

/// <summary>The auctions ending on a calendar day, at a time of day (TN-7: DateOnly and TimeOnly filters and sorts).</summary>
[Endpoint("GET /auctions/by-day")]
public sealed record AuctionsByDay(DateOnly? EndsOn, TimeOnly? EndsAtTime, DaySort Sort = DaySort.EndsOn) : IQuery<AuctionRow>;

/// <summary>The auctions ending between two days, both inclusive and both optional (TN-44: range filters).</summary>
[Endpoint("GET /auctions/between")]
public sealed record AuctionsBetween([AtLeast(nameof(AuctionRow.EndsOn))] DateOnly? From, [AtMost(nameof(AuctionRow.EndsOn))] DateOnly? Until,
    [AtLeast(nameof(AuctionRow.HighestBid))] decimal? MinBid, DaySort Sort = DaySort.EndsOn) : IQuery<AuctionRow>;

/// <summary>A seller's auctions from the route.</summary>
[Endpoint("GET /sellers/{seller}/auctions")]
public sealed record SellerAuctions(string Seller, AuctionSort Sort = AuctionSort.HighestBid) : IQuery<AuctionRow>;

/// <summary>TN-54 (P-2): the rows of an SQL projection (auctions per seller), read with the Read&lt;Auction&gt; grant.</summary>
[Projection("seller-totals", Sql = true, Aggregate = typeof(Auction))]
public sealed partial record SellerTotalRow(Guid Id, string Seller, [Sort] int Auctions);

public enum SellerTotalSort
{
    [Descending] Auctions,
    Id,
}

/// <summary>The sellers with the most auctions first.</summary>
[Endpoint("GET /seller-totals", Policy = "auctions:read")]
public sealed record SellerTotals(string? Seller, SellerTotalSort Sort = SellerTotalSort.Auctions) : IQuery<SellerTotalRow>;

/// <summary>One seller's total.</summary>
[Endpoint("GET /seller-totals/{id}", Policy = "auctions:read")]
public sealed record SellerTotal(Guid Id) : IRowQuery<SellerTotalRow>;
