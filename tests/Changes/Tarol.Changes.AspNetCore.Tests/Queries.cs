using Tarol.Primitives;
using Tarol.Queries;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>The auction list (queries.md §3): one row per auction, bids from the entity's creation event.</summary>
[Projection("auction-list")]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt, AuctionStatus Status, [Sort] decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0);

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

/// <summary>A seller's auctions from the route.</summary>
[Endpoint("GET /sellers/{seller}/auctions")]
public sealed record SellerAuctions(string Seller, AuctionSort Sort = AuctionSort.HighestBid) : IQuery<AuctionRow>;
