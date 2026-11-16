using Tarol.Primitives;
using Tarol.Queries;

namespace ArtFlow;

// ── the auction list (step 12): a projection the worker hosts and the API lists (docs/queries/README.md) ──────────────

/// <summary>One row per auction, built from its events; the API's list contract.</summary>
[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Seller, decimal Reserve, [Sort] decimal HighestBid, int Bids, bool Settled, decimal Paid,
    [Sort] DateTimeOffset OpenedAt)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Seller, e.Reserve, 0m, 0, false, 0m, r.RecordedAt);

    public AuctionRow When(Bid.Placed e) => this with { HighestBid = Math.Max(HighestBid, e.Amount), Bids = Bids + 1 };

    public AuctionRow When(Auction.Settled e) => this with { Settled = true };

    public AuctionRow When(Auction.Reopened e) => this with { Settled = false };

    public AuctionRow When(Auction.PaymentRecorded e) => this with { Paid = Paid + e.Amount };
}
