using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.Tests;

// ── the default: the pipeline loads the target named by the only Id<T> member ──────────────────────────────────────

public sealed partial record CreateAuction(string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

public sealed partial record PlaceBid(Id<Auction> Auction, Amount Amount) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.PlaceBid(context.Operation.Actor.Id, Amount, context.Now).ToResult();
}

public sealed partial record SettleAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Settle(context.Now);
}

public sealed partial record TouchAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Touch();
}

public sealed partial record SabotageAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Sabotage();
}

public sealed partial record ExplodeAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Explode();
}

/// <summary>Two Id&lt;Auction&gt; members: the target is explicit (no TCHG001).</summary>
public sealed partial record CloseOneOfTwo(Id<Auction> First, Id<Auction> Second) : IChange<Auction>
{
    Id<Auction> IChange<Auction>.Target => Second;
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

// ── special changes (C-16) ─────────────────────────────────────────────────────────────────────────────────────────

public sealed partial record CreateAuctionOfArtwork([Read] Id<Artwork> Artwork, string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt)
    : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.CreateFor(context.Read<Artwork>(), context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

public sealed partial record CloseByLot(string Lot) : ILoads<Auction>
{
    public async ValueTask<Result<Auction>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Find<Auction>("lot", Lot, context.Operation, cancellationToken)).TryGetValue(out Id<Auction> id)
           && (await reader.Load(id, context.Operation, cancellationToken)).TryGetValue(out Auction? auction)
            ? auction
            : Error.NotFound("Auction.NotFound", $"lot {Lot}");

    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

public sealed partial record ApprovePayout(Id<Payout> Payout, decimal Amount) : ILoads<Payout>
{
    public async ValueTask<Result<Payout>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load(Payout, context.Operation, cancellationToken)).TryGetValue(out Payout? existing)
            ? existing
            : Tests.Payout.Due(Payout, Amount);

    public Result Execute(Payout payout, ChangeContext context) => payout.Approve();
}

/// <summary>A [Read] on a change of an existing aggregate, for the dispatcher and reads together.</summary>
public sealed partial record CloseIfArtworkVerified(Id<Auction> Auction, [Read] Id<Artwork> Artwork) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context)
        => context.Read<Artwork>().IsVerified ? auction.Close(context.Now) : Error.Conflict("Artwork.NotVerified");
}
