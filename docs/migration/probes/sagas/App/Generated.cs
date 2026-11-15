using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;

namespace ArtFlow;

// ── written by hand in the probe: what the reaction generator emits for this assembly ───────────────────────────────────
// One static array per event type (CS9203: never a collection expression in the span-returning method), names = the
// change's type name unless pinned with Name = "…", a schedule for [OnEvent<T>(DueAt = …)], one switch that maps
// (reaction, event) → factory → pipeline.Run with the reaction's context and key.

internal static class GeneratedReactions
{
    private static readonly string[] OnBidWon = ["MarkPayoutDue", "StartSettlement"];
    private static readonly string[] OnNoted = ["AcknowledgeNote"];
    private static readonly string[] OnPaymentRecorded = ["ConfirmSettlementPayment"];
    private static readonly string[] OnDeadlineSet = ["ExpireSettlement"];
    private static readonly string[] OnExpired = ["ReopenAuction"];

    public static readonly string[] Names =
        ["AcknowledgeNote", "ConfirmSettlementPayment", "ExpireSettlement", "MarkPayoutDue", "ReopenAuction", "StartSettlement"];

    public static ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => domainEvent switch
    {
        Bid.Won => OnBidWon,
        Auction.Noted => OnNoted,
        Auction.PaymentRecorded => OnPaymentRecorded,
        Settlement.DeadlineSet => OnDeadlineSet,
        Settlement.Expired => OnExpired,
        _ => ReadOnlySpan<string>.Empty,
    };

    public static DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => (domainEvent, reaction) switch
    {
        (Settlement.DeadlineSet e, "ExpireSettlement") => e.DueAt,
        _ => null,
    };

    public static ValueTask<Result<ChangeOutcome>> React(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken ct)
    {
        var context = ReactionSet.Context(reaction, recorded);
        var options = ReactionSet.Options(reaction, recorded);
        return (reaction, recorded.Event.Event) switch
        {
            ("MarkPayoutDue", Bid.Won e) => pipeline.Run(MarkPayoutDue.From(e, recorded), context, options, ct),
            ("StartSettlement", Bid.Won e) => pipeline.Run(StartSettlement.From(e, recorded), context, options, ct),
            ("AcknowledgeNote", Auction.Noted e) => pipeline.Run(AcknowledgeNote.From(e, recorded), context, options, ct),
            ("ConfirmSettlementPayment", Auction.PaymentRecorded e) => pipeline.Run(ConfirmSettlementPayment.From(e, recorded), context, options, ct),
            ("ExpireSettlement", Settlement.DeadlineSet e) => pipeline.Run(ExpireSettlement.From(e, recorded), context, options, ct),
            ("ReopenAuction", Settlement.Expired e) => pipeline.Run(ReopenAuction.From(e, recorded), context, options, ct),
            _ => throw new InvalidOperationException($"The reaction '{reaction}' does not handle {recorded.Event.Event.GetType().Name}."),
        };
    }

    public static ReactionSet Set { get; } = new(Names, ReactionsTo, DueAt, React);
}

/// <summary>A second assembly's set (S-3): composing it with a duplicate name must fail at startup.</summary>
internal static class OtherAssemblyReactions
{
    private static readonly string[] OnWon = ["NotifyBuyer"];
    private static readonly string[] Duplicate = ["MarkPayoutDue"];

    public static ReactionSet Distinct { get; } = new(OnWon, e => e is Bid.Won ? OnWon : ReadOnlySpan<string>.Empty, (_, _) => null,
        (_, _, _, _) => ValueTask.FromResult<Result<ChangeOutcome>>(new ChangeOutcome(Guid.Empty, 0, 0)));

    public static ReactionSet Clashing { get; } = new(Duplicate, e => e is Bid.Won ? Duplicate : ReadOnlySpan<string>.Empty, (_, _) => null,
        (_, _, _, _) => ValueTask.FromResult<Result<ChangeOutcome>>(new ChangeOutcome(Guid.Empty, 0, 0)));
}
