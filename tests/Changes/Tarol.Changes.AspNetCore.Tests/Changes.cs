using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Security;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore.Tests;

// ── user code: changes with their HTTP entries (C-5: attributes on the change) ─────────────────────────────────────────

[Endpoint("POST /auctions", Policy = "auctions:write")]
public sealed partial record CreateAuction(Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Title, Reserve, EndsAt, context.Now);
}

[Endpoint("POST /auctions/{auction}/bids", Policy = "auctions:bid")]
public sealed partial record PlaceBid(Id<Auction> Auction, Amount Amount) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.PlaceBid(context.Operation.Actor.Id, Amount, context.Now).ToResult();
}

[Endpoint("POST /auctions/{auction}/close", Policy = "auctions:write")]
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

/// <summary>No Policy: the default policy (an authenticated caller); the benchmark's endpoint.</summary>
[Endpoint("POST /auctions/{auction}/watch")]
public sealed partial record WatchAuction(Id<Auction> Auction, string Note) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Watch(Note);
}

/// <summary>A JSON name of its own, an optional member with a default, a PUT.</summary>
[Endpoint("PUT /auctions/{auction}/title")]
public sealed partial record RenameAuction(Id<Auction> Auction, [property: JsonPropertyName("new_title")] Title Title, string Reason = "none") : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Watch(Reason);
}

/// <summary>An endpoint without any resource handler: fail closed (403), never "allowed because nobody said no".</summary>
[Endpoint("POST /auctions/{auction}/touch")]
public sealed partial record TouchAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Watch("touch");
}

/// <summary>The payment webhook: two contract versions, each mapped by its own From factory (anti-corruption layer).</summary>
public sealed partial record RecordPayment(Id<Auction> Auction, Amount Amount, string PaymentId) : IChange<Auction>, IValidatable
{
    [Webhook("payments", "payment.succeeded", Version = "2")]
    public static RecordPayment From(PaymentSucceededV2 p) => new((Id<Auction>)p.Reference, new Amount(p.Amount), p.PaymentId);

    [Webhook("payments", "payment.succeeded", Version = "1")]
    public static RecordPayment From(PaymentSucceededV1 p) => new((Id<Auction>)p.Order, new Amount(p.Cents / 100m), p.Id);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Amount, PaymentId);
}

/// <summary>A reaction (step 9): the worker process runs it after the API committed Auction.Closed.</summary>
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Auction.Closed>]
    public static MarkPayoutDue From(Auction.Closed e, RecordedEvent recorded) => new(recorded.StreamId, 0);

    public Result<Payout> Execute(ChangeContext context) => Payout.MarkDue(Auction, Amount, context.Now);
}

// ── user code: authorization (AZ-D) = the app's role map (in Program.cs) + row rules on the loaded aggregate ──────────

/// <summary>Row rule: a seller closes only their own auction; an admin closes any auction of the tenant (another tenant's
/// is never loaded). Deny wins over every grant; the role map alone would let every seller close every auction.</summary>
public sealed class CloseAuctionRule : ChangeHandler<CloseAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CloseAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller && !context.User.IsInRole("admin"))
            Deny(context, "Auction.NotSeller");
        return Task.CompletedTask;
    }
}

/// <summary>Row rule on a creation (the new aggregate): a seller may hold at most a reserve of 100 000 without the "dealer" role.</summary>
public sealed class CreateAuctionRule : ChangeHandler<CreateAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CreateAuction change, Auction auction)
    {
        if (auction.Reserve > 100_000 && !context.User.IsInRole("dealer"))
            Deny(context, "Auction.ReserveLimit");
        return Task.CompletedTask;
    }
}

/// <summary>Row grant: the seller may read their own auction without the reader role (an ownership grant, not a role).</summary>
public sealed class ReadOwnAuction : ChangeHandler<Read<Auction>, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, Read<Auction> change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value == auction.Seller)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
