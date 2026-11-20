using Tarol.Messaging;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace ArtFlow;

// ── ArtFlow ↔ billing (step 11, M-15): the contracts are ArtFlow's own records; billing has its own ──────────────────────

/// <summary>What ArtFlow tells other systems about a settled auction (the outward anti-corruption layer).</summary>
public sealed record AuctionSettledV1(Guid Auction, string Buyer, decimal Price, string Currency);

/// <summary>A bid, for the browsers watching the auction (BC-9: the API pushes it to the hub's topic); no bidder identity.</summary>
public sealed record BidPlacedV1(Guid Auction, decimal Amount, DateTimeOffset At);

/// <summary>ArtFlow's record of billing's invoice-paid contract (only the members it reads).</summary>
public sealed record InvoicePaidV1(Guid Invoice, Guid Auction, decimal Amount);

public static class Publications
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static AuctionSettledV1? SettledV1(Auction.Settled e, RecordedEvent r)
        => e.Buyer.Length == 0 ? null : new(r.StreamId, e.Buyer, e.Price, "EUR");          // no winner, nothing to bill

    [Publishes<Bid.Placed>("artflow.bid-placed", 1)]
    public static BidPlacedV1 BidPlacedV1(Bid.Placed e, RecordedEvent r) => new(r.StreamId, e.Amount, e.At);
}

public sealed partial record RecordPayment
{
    /// <summary>Billing's message mapped to the same change as the payment provider's webhook.</summary>
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Amount, "invoice:" + m.Invoice.ToString("N"));
}
