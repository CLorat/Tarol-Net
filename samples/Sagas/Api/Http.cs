using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Results;
using Tarol.Security;

namespace ArtFlow;

// ── the HTTP entries of the worker's changes: attributes on partial declarations of the same records (C-5) ──────────────

[Endpoint("POST /auctions/{auction}/bids")]
public sealed partial record PlaceBid;

[Endpoint("POST /auctions/{auction}/settle")]
public sealed partial record SettleAuction;

public sealed partial record RecordPayment
{
    /// <summary>The payment provider's webhook: its contract mapped to the change (anti-corruption layer).</summary>
    [Webhook("payments", "payment.succeeded", Version = "1")]
    public static RecordPayment From(PaymentSucceeded p) => new((Tarol.StronglyTypedId.Id<Auction>)p.Reference, p.Amount, p.PaymentId);
}

/// <summary>The provider's own contract.</summary>
public sealed record PaymentSucceeded(string PaymentId, Guid Reference, decimal Amount);

[Endpoint("POST /auctions")]
public sealed partial record CreateAuction(decimal Reserve) : ICreation<Auction>
{
    public Result<Auction> Execute(ChangeContext context) => Auction.Create(context.Operation.Actor.Id, Reserve, context.Now);
}

/// <summary>GET by id: the view, not the stored state.</summary>
[Endpoint("GET /auctions/{auction}")]
public sealed record AuctionView(Guid Id, string Seller, decimal Reserve, int Bids, bool Settled, decimal Paid) : IView<Auction, AuctionView>
{
    public static AuctionView From(Auction auction)
        => new(auction.Id.Value, auction.Seller, auction.Reserve, auction.Bids.Count, auction.IsSettled, auction.Paid);
}

/// <summary>Only the seller settles their auction (a row rule on top of the seller role).</summary>
public sealed class SettleOwnAuction : ChangeHandler<SettleAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, SettleAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller)
            Deny(context, "Auction.NotSeller");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(CreateAuction)), JsonSerializable(typeof(PlaceBid)), JsonSerializable(typeof(SettleAuction))]
[JsonSerializable(typeof(PaymentSucceeded)), JsonSerializable(typeof(AuctionView)), JsonSerializable(typeof(decimal))]
internal sealed partial class AppJson;
