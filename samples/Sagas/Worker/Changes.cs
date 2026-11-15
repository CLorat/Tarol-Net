using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;

using Tarol.StronglyTypedId;

namespace ArtFlow;

// ── user code: reactions are changes with an [OnEvent<T>] factory (C-5) ───────────────────────────────────────────────

/// <summary>The payout reaction: a creation, deduplicated by its key (reaction, event id) because the factory makes the id (C-10).</summary>
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);

    public Result<Payout> Execute(ChangeContext context) => Payout.MarkDue(Auction, Amount, context.Now);
}

/// <summary>A reaction that can fail forever for one input ("poison"): C-9 retries, then dead-letters.</summary>
public sealed partial record AcknowledgeNote(Id<Auction> Auction, string Text) : IChange<Auction>
{
    [OnEvent<Auction.Noted>]
    public static AcknowledgeNote From(Auction.Noted e, RecordedEvent recorded) => new((Id<Auction>)recorded.StreamId, e.Text);

    public Result Execute(Auction auction, ChangeContext context) => auction.Acknowledge(Text);
}

// ── the settlement saga (SA-A): start, step, timeout, compensation; every step is a change ───────────────────────────────

/// <summary>Load-or-create (ILoads, C-16): the first Bid.Won starts the saga, a later one restarts an ended saga.</summary>
public sealed partial record StartSettlement(Guid Auction, string Buyer, decimal Amount) : ILoads<Settlement>
{
    [OnEvent<Bid.Won>]
    public static StartSettlement From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Bidder, e.Amount);

    public async ValueTask<Result<Settlement>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Settlement>)Auction, context.Operation, cancellationToken)).TryGetValue(out Settlement? existing)
            ? existing : ArtFlow.Settlement.For(Auction);

    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Begin(Buyer, Amount, context.Now);
}

public sealed partial record ConfirmSettlementPayment(Id<Settlement> Settlement, decimal Amount) : IChange<Settlement>
{
    [OnEvent<Auction.PaymentRecorded>]
    public static ConfirmSettlementPayment From(Auction.PaymentRecorded e, RecordedEvent recorded)
        => new((Id<Settlement>)recorded.StreamId, e.Amount);                // correlation: same id, another stream type

    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Pay(Amount);
}

// the timeout: a row due at DueAt; "ArtFlow." because the member Settlement shadows the type
public sealed partial record ExpireSettlement(Id<Settlement> Settlement) : IChange<Settlement>
{
    [OnEvent<Settlement.DeadlineSet>(DueAt = nameof(ArtFlow.Settlement.DeadlineSet.DueAt))]
    public static ExpireSettlement From(Settlement.DeadlineSet e, RecordedEvent recorded) => new((Id<Settlement>)recorded.StreamId);

    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Expire(context.Now);
}

// compensation = a forward change on another aggregate
public sealed partial record ReopenAuction(Id<Auction> Auction) : IChange<Auction>
{
    [OnEvent<Settlement.Expired>]
    public static ReopenAuction From(Settlement.Expired e, RecordedEvent recorded) => new((Id<Auction>)recorded.StreamId);

    public Result Execute(Auction auction, ChangeContext context) => auction.Reopen();
}

public sealed partial record RescheduleSettlement(Id<Settlement> Settlement, DateTimeOffset DueAt) : IChange<Settlement>
{
    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Reschedule(DueAt);
}

// ── plain changes the probe drives ────────────────────────────────────────────────────────────────────────────────────

public sealed partial record SettleAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Settle();
}

public sealed partial record PlaceBid(Id<Auction> Auction, decimal Amount) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.PlaceBid(context.Operation.Actor.Id, Amount, context.Now).ToResult();
}

public sealed partial record RecordPayment(Id<Auction> Auction, decimal Amount, string PaymentId) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Amount, PaymentId);
}

public sealed partial record NoteAuction(Id<Auction> Auction, string Text) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Note(Text);
}
