using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Messaging.Contract;

// The consumers every transport runs in the contract suite: two of artflow.auction-settled (IssueInvoice, Sales), one of
// billing.invoice-paid (RecordPayment) and one of artflow.auction-noted (TrackNote, for the order per stream).

public readonly partial record struct Amount([Range(1, 1_000_000)] decimal Value);

public partial class Auction : AggregateRoot<Auction>
{
    public bool IsSettled { get; private set; }
    public decimal Paid { get; private set; }
    public List<string> Invoices { get; } = [];

    public static Auction Open(Guid id)
    {
        var auction = new Auction((Id<Auction>)id);
        auction.Raise(new Opened());
        return auction;
    }

    public Result Settle() => IsSettled ? Error.Conflict("Auction.Settled") : Raise(new Settled());

    public Result RecordPayment(string invoice, decimal amount)
        => !IsSettled ? Error.Conflict("Auction.NotSettled")
         : Invoices.Contains(invoice) ? Result.Success()
         : Raise(new PaymentRecorded(invoice, amount));

    public sealed record Opened : Event;
    public sealed record Settled : Event;
    public sealed record PaymentRecorded(string Invoice, decimal Amount) : Event;

    private void On(Opened e) => Paid = 0;
    private void On(Settled e) => IsSettled = true;
    private void On(PaymentRecorded e) { Invoices.Add(e.Invoice); Paid += e.Amount; }
}

public partial class Invoice : AggregateRoot<Invoice>
{
    public Guid Auction { get; private set; }

    public static Invoice Issue(Guid auction, decimal amount, DateTimeOffset now)
    {
        var invoice = new Invoice(Id<Invoice>.New(now));
        invoice.Raise(new Issued(auction, amount));
        return invoice;
    }

    public sealed record Issued(Guid Auction, decimal Amount) : Event;
    private void On(Issued e) => Auction = e.Auction;
}

public partial class Ledger : AggregateRoot<Ledger>
{
    public int Sales { get; private set; }

    public static Ledger For(Guid id) => new((Id<Ledger>)id);
    public Result Add(decimal price) => Raise(new SaleRecorded(price));

    public sealed record SaleRecorded(decimal Price) : Event;
    private void On(SaleRecorded e) => Sales++;
}

public partial class NoteLog : AggregateRoot<NoteLog>
{
    public List<int> Notes { get; } = [];

    public static NoteLog For(Guid id) => new((Id<NoteLog>)id);
    public Result Add(int n) => Raise(new Noted(n));

    public sealed record Noted(int N) : Event;
    private void On(Noted e) => Notes.Add(e.N);
}

public sealed record AuctionSettledV1(Guid Auction, decimal Price);
public sealed record AuctionNotedV1(Guid Auction, int N);
public sealed record InvoicePaidV1(Guid Invoice, Guid Auction, decimal Amount);

/// <summary>Validated: a price below 1 is Invalid (buried at attempt 1).</summary>
public sealed partial record IssueInvoice(Guid Auction, Amount Amount) : ICreation<Invoice>, IValidatable
{
    [Consumes("artflow.auction-settled", 1)]
    public static IssueInvoice From(AuctionSettledV1 m) => new(m.Auction, new Amount(m.Price));

    public Result<Invoice> Execute(ChangeContext context) => Invoice.Issue(Auction, Amount.Value, context.Now);
}

/// <summary>A second consumer of the same contract: its own subscription and key.</summary>
public sealed partial record RecordSale(Guid Ledger, decimal Price) : ILoads<Ledger>
{
    [Consumes("artflow.auction-settled", 1, Name = "Sales")]
    public static RecordSale From(AuctionSettledV1 m) => new(m.Auction, m.Price);

    public async ValueTask<Result<Ledger>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Ledger>)Ledger, context.Operation, cancellationToken)).TryGetValue(out Ledger? found) ? found : Contract.Ledger.For(Ledger);

    public Result Execute(Ledger ledger, ChangeContext context) => ledger.Add(Price);
}

/// <summary>A payment before the settlement is a Conflict (retried); for an auction that does not exist a NotFound (retried, then buried).</summary>
public sealed partial record RecordPayment(Id<Auction> Auction, string Invoice, decimal Amount) : IChange<Auction>
{
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Amount);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
}

/// <summary>Records the notes of one auction in the order they are consumed.</summary>
public sealed partial record TrackNote(Guid Log, int N) : ILoads<NoteLog>
{
    [Consumes("artflow.auction-noted", 1)]
    public static TrackNote From(AuctionNotedV1 m) => new(m.Auction, m.N);

    public async ValueTask<Result<NoteLog>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<NoteLog>)Log, context.Operation, cancellationToken)).TryGetValue(out NoteLog? found) ? found : NoteLog.For(Log);

    public Result Execute(NoteLog log, ChangeContext context) => log.Add(N);
}

/// <summary>The hosted round trips publish settlements through the outbox (the contract suite itself does not run a worker).</summary>
public static class Publications
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static AuctionSettledV1 Settled(Auction.Settled e, RecordedEvent r) => new(r.StreamId, 150m);
}

[JsonSerializable(typeof(AuctionSettledV1))]
[JsonSerializable(typeof(AuctionNotedV1))]
[JsonSerializable(typeof(InvoicePaidV1))]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.Settled))]
[JsonSerializable(typeof(Auction.PaymentRecorded))]
[JsonSerializable(typeof(Invoice.Issued))]
[JsonSerializable(typeof(Ledger.SaleRecorded))]
[JsonSerializable(typeof(NoteLog.Noted))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ContractJson : JsonSerializerContext;
