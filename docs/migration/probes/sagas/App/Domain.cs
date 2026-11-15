using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace ArtFlow;

// ArtFlow, reduced to what reactions and the settlement saga need.

public partial class Auction : AggregateRoot<Auction>
{
    public EntitySet<Bid> Bids { get; } = [];
    public string Seller { get; private set; } = "";
    public decimal Reserve { get; private set; }
    public bool IsSettled { get; private set; }
    public decimal Paid { get; private set; }
    public int ReopenCount { get; private set; }
    public List<string> Acknowledged { get; } = [];

    public static Auction Create(string seller, decimal reserve, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(seller, reserve));
        return auction;
    }

    public Result<Bid> PlaceBid(string bidder, decimal amount, DateTimeOffset now)
        => IsSettled ? Error.Conflict("Auction.Settled")
         : amount <= Bids.Select(b => b.Amount).DefaultIfEmpty(Reserve - 1).Max() ? Error.Conflict("Bid.TooLow")
         : Raise(new Bid.Placed(bidder, amount, now));

    public Result Settle()
    {
        if (IsSettled)
            return Error.Conflict("Auction.Settled");
        var winner = Bids.OrderByDescending(b => b.Amount).FirstOrDefault();
        _ = winner?.Win();
        return Raise(new Settled());
    }

    public Result RecordPayment(decimal amount, string paymentId) => Raise(new PaymentRecorded(amount, paymentId));

    /// <summary>Compensation of an expired settlement: a forward change, not an undo.</summary>
    public Result Reopen() => IsSettled ? Raise(new Reopened()) : Result.Success();

    public Result Note(string text) => Raise(new Noted(text));

    /// <summary>A guard that fails for "poison" as long as the auction exists: a reaction that can never pass (C-9).</summary>
    public Result Acknowledge(string text) => text == "poison" ? Error.Conflict("Note.Poison") : Raise(new NoteAcknowledged(text));

    public sealed record Opened(string Seller, decimal Reserve) : Event;
    public sealed record Settled : Event;
    public sealed record PaymentRecorded(decimal Amount, string PaymentId) : Event;
    public sealed record Reopened : Event;
    public sealed record Noted(string Text) : Event;
    public sealed record NoteAcknowledged(string Text) : Event;

    private void On(Opened e) => (Seller, Reserve) = (e.Seller, e.Reserve);
    private void On(Settled e) => IsSettled = true;
    private void On(PaymentRecorded e) => Paid += e.Amount;
    private void On(Reopened e) => (IsSettled, ReopenCount) = (false, ReopenCount + 1);
    private void On(Noted e) { }
    private void On(NoteAcknowledged e) => Acknowledged.Add(e.Text);
}

public partial class Bid : Entity<Auction, Bid>
{
    public string Bidder { get; private set; } = "";
    public decimal Amount { get; private set; }

    public Success Win() => Raise(new Won(Bidder, Amount));

    public sealed record Placed(string Bidder, decimal Amount, DateTimeOffset At) : Created;
    public sealed record Won(string Bidder, decimal Amount) : Event;

    private void On(Placed e) => (Bidder, Amount) = (e.Bidder, e.Amount);
    private void On(Won e) { }
}

public partial class Payout : AggregateRoot<Payout>
{
    public Guid Auction { get; private set; }
    public decimal Amount { get; private set; }

    public static Payout MarkDue(Guid auction, decimal amount, DateTimeOffset now)
    {
        var payout = new Payout(Id<Payout>.New(now));
        payout.Raise(new Due(auction, amount));
        return payout;
    }

    public sealed record Due(Guid Auction, decimal Amount) : Event;
    private void On(Due e) => (Auction, Amount) = (e.Auction, e.Amount);
}

public enum SettlementStatus { AwaitingPayment, Completed, Expired }

/// <summary>
/// Candidate SA-A: the saga is an aggregate. Its id is the auction's id (correlation by id, another stream type), its
/// state is stored per commit like any aggregate, its steps are changes with [OnEvent] factories, its timeout is an event
/// with a due time, and what it asks of other aggregates are events that other reactions turn into changes.
/// </summary>
public partial class Settlement : AggregateRoot<Settlement>
{
    public decimal Amount { get; private set; }
    public string Buyer { get; private set; } = "";
    public DateTimeOffset Deadline { get; private set; }
    public SettlementStatus Status { get; private set; }

    public int Rounds { get; private set; }

    /// <summary>An empty instance for a load-or-create start (correlation: the auction's id, C-10: the factory chooses it).</summary>
    public static Settlement For(Guid auction) => new((Id<Settlement>)auction);

    /// <summary>
    /// Start, restart after an end (old S7), or ignore a second start while running (old N1): one behaviour, decided on the
    /// state, never a Conflict (C-9 would retry it and dead-letter).
    /// </summary>
    public Result Begin(string buyer, decimal amount, DateTimeOffset now)
    {
        if (Version > 0 && Status == SettlementStatus.AwaitingPayment)
            return Result.Success();
        Raise(new Started(buyer, amount));
        return Raise(new DeadlineSet(now.AddDays(3)));
    }

    /// <summary>A late or repeated payment after the end is not a failure: it changes nothing (C-9 would retry a Conflict).</summary>
    public Result Pay(decimal amount)
        => Status != SettlementStatus.AwaitingPayment ? Result.Success()
         : amount < Amount ? Error.Conflict("Settlement.Underpaid")
         : Raise(new Completed(amount));

    /// <summary>A stale timeout (the deadline moved later) is a no-op success: the row of the new deadline fires then.</summary>
    public Result Expire(DateTimeOffset now)
        => Status != SettlementStatus.AwaitingPayment || now < Deadline ? Result.Success() : Raise(new Expired());

    public Result Reschedule(DateTimeOffset dueAt) => Status != SettlementStatus.AwaitingPayment ? Result.Success() : Raise(new DeadlineSet(dueAt));

    public sealed record Started(string Buyer, decimal Amount) : Event;
    public sealed record DeadlineSet(DateTimeOffset DueAt) : Event;
    public sealed record Completed(decimal Paid) : Event;
    public sealed record Expired : Event;

    private void On(Started e) => (Buyer, Amount, Status, Rounds) = (e.Buyer, e.Amount, SettlementStatus.AwaitingPayment, Rounds + 1);
    private void On(DeadlineSet e) => Deadline = e.DueAt;
    private void On(Completed e) => Status = SettlementStatus.Completed;
    private void On(Expired e) => Status = SettlementStatus.Expired;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.Settled))]
[JsonSerializable(typeof(Auction.PaymentRecorded))]
[JsonSerializable(typeof(Auction.Reopened))]
[JsonSerializable(typeof(Auction.Noted))]
[JsonSerializable(typeof(Auction.NoteAcknowledged))]
[JsonSerializable(typeof(Bid.Placed))]
[JsonSerializable(typeof(Bid.Won))]
[JsonSerializable(typeof(Payout.Due))]
[JsonSerializable(typeof(Settlement.Started))]
[JsonSerializable(typeof(Settlement.DeadlineSet))]
[JsonSerializable(typeof(Settlement.Completed))]
[JsonSerializable(typeof(Settlement.Expired))]
internal sealed partial class AppJson : JsonSerializerContext;
