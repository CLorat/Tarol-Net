using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace ArtFlow;

// ── user code, candidate A: an input record that names its target and calls one behaviour ─────────────────────

// create an auction (user, REST): the caller chooses the id so a retry with the same id is idempotent
[Endpoint("POST /auctions", Policy = "auctions:write")]
public sealed partial record CreateAuction(Id<Auction> Auction, Title Title, Amount Reserve, DateTimeOffset EndsAt)
    : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => ArtFlow.Auction.Create(Auction, context.Operation.Actor.Id, Title, Reserve, EndsAt, context.Now);
}

// close an auction (user, REST): 409 when already closed, 400 when invalid, 404 when missing
[Endpoint("POST /auctions/{auction}/close", Policy = "auctions:write")]
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

[Endpoint("POST /auctions/{auction}/bids")]
public sealed partial record PlaceBid(Id<Auction> Auction, Amount Amount) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context)
        => auction.PlaceBid(context.Operation.Actor.Id, Amount, context.Now).ToResult();
}

// settle after the end time (background job, system actor): no entry attribute, a typed in-process call
public sealed partial record SettleAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Settle(context.Now);
}

// mark the payout due (reaction to Bid.Won, after commit): causation = the event, key = the event id
public sealed partial record MarkPayoutDue(Id<Payout> Payout, Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded)
        => new((Id<Payout>)recorded.StreamId, recorded.StreamId, e.Amount);   // one payout per auction: same id

    public Result<Payout> Execute(ChangeContext context) => ArtFlow.Payout.MarkDue(Payout, Auction, Amount, context.Now);
}

// record a payment (webhook from the payment provider): signature, delivery id = idempotency key
public sealed record PaymentSucceededV2(string PaymentId, Guid Reference, decimal Amount, string Currency);

public sealed partial record RecordPayment(Id<Auction> Auction, Amount Amount, string PaymentId) : IChange<Auction>, IValidatable
{
    [Webhook("payments", "payment.succeeded")]
    public static RecordPayment From(PaymentSucceededV2 payload)
#pragma warning disable TVAL006 // finding: the mapping builds an input that the pipeline validates; Create here would validate twice
        => new((Id<Auction>)payload.Reference, new Amount(payload.Amount), payload.PaymentId);
#pragma warning restore TVAL006   // anti-corruption: their names → ours

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Amount, PaymentId);
}
