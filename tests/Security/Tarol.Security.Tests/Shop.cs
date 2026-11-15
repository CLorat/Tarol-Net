using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Security.Tests;

// ArtFlow, reduced to what authorization needs.
public partial class Auction : AggregateRoot<Auction>
{
    public string Seller { get; private set; } = "";
    public decimal Reserve { get; private set; }
    public bool Closed { get; private set; }
    public int Watchers { get; private set; }

    public static Auction Create(string seller, decimal reserve, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(seller, reserve));
        return auction;
    }

    public Result Close() => Closed ? Error.Conflict("Auction.Closed") : Raise(new WasClosed());
    public Success Watch() => Raise(new Watched());

    public sealed record Opened(string Seller, decimal Reserve) : Event;
    public sealed record WasClosed : Event;
    public sealed record Watched : Event;
    private void On(Opened e) => (Seller, Reserve) = (e.Seller, e.Reserve);
    private void On(WasClosed e) => Closed = true;
    private void On(Watched e) => Watchers++;
}

public partial class Artwork : AggregateRoot<Artwork>
{
    public string Owner { get; private set; } = "";

    public static Artwork Register(string owner)
    {
        var artwork = new Artwork(Id<Artwork>.New());
        artwork.Raise(new Registered(owner));
        return artwork;
    }

    public sealed record Registered(string Owner) : Event;
    private void On(Registered e) => Owner = e.Owner;
}

public sealed partial record CreateAuction(decimal Reserve) : ICreation<Auction>
{
    public Result<Auction> Execute(ChangeContext context) => Auction.Create(context.Operation.Actor.Id, Reserve, context.Now);
}

public sealed partial record AuctionArtwork([Read] Id<Artwork> Artwork, decimal Reserve) : ICreation<Auction>
{
    public Result<Auction> Execute(ChangeContext context) => Auction.Create(context.Operation.Actor.Id, Reserve, context.Now);
}

public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close();
}

public sealed partial record WatchAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Watch();
}

public sealed partial record TouchAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Watch();
}

// ── rules ────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class CloseAuctionRule : ChangeHandler<CloseAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CloseAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller && !context.User.IsInRole("admin"))
            Deny(context, "Auction.NotSeller");
        return Task.CompletedTask;
    }
}

/// <summary>A rule on a creation: it sees the new aggregate.</summary>
public sealed class ReserveLimitRule : ChangeHandler<CreateAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CreateAuction change, Auction auction)
    {
        if (auction.Reserve > 100_000 && !context.User.IsInRole("dealer"))
            Deny(context, "Auction.ReserveLimit");
        return Task.CompletedTask;
    }
}

/// <summary>An ownership grant: the seller reads their own auction without a reader role.</summary>
public sealed class ReadOwnAuction : ChangeHandler<Read<Auction>, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, Read<Auction> change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value == auction.Seller)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>A rule that reads a [Read] aggregate from the requirement's context: only the artwork's owner auctions it.</summary>
public sealed class OwnArtworkRule : ChangeHandler<AuctionArtwork, Auction>
{
    public DateTimeOffset? SawNow { get; private set; }

    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, AuctionArtwork change, Auction auction)
    {
        SawNow = requirement.Context.Now;
        if (requirement.Context.Read<Artwork>().Owner != context.User.FindFirst("sub")?.Value)
            Deny(context, "Artwork.NotOwner");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(Auction.Opened)), JsonSerializable(typeof(Auction.WasClosed)), JsonSerializable(typeof(Auction.Watched))]
[JsonSerializable(typeof(Artwork.Registered))]
public partial class ShopJson : JsonSerializerContext;
