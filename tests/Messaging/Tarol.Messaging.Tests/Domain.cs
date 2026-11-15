using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Shop;

// One application with consumers of two other systems' contracts: billing.invoice-paid (v1, v2) and artflow.auction-settled.

public readonly partial record struct Amount([Range(1, 1_000_000)] decimal Value);

public partial class Auction : AggregateRoot<Auction>
{
    public bool IsSettled { get; private set; }
    public decimal Paid { get; private set; }
    public List<string> Invoices { get; } = [];

    public static Auction Open(DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened());
        return auction;
    }

    public Result Settle() => IsSettled ? Error.Conflict("Auction.Settled") : Raise(new Settled());

    /// <summary>A payment before its settlement is a Conflict (retried); the same invoice again is a success (a business duplicate).</summary>
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
    public decimal Amount { get; private set; }

    public static Invoice Issue(Guid auction, decimal amount, DateTimeOffset now)
    {
        var invoice = new Invoice(Id<Invoice>.New(now));
        invoice.Raise(new Issued(auction, amount));
        return invoice;
    }

    public sealed record Issued(Guid Auction, decimal Amount) : Event;
    private void On(Issued e) => (Auction, Amount) = (e.Auction, e.Amount);
}

public partial class Ledger : AggregateRoot<Ledger>
{
    public int Sales { get; private set; }

    public static Ledger For(Guid id) => new((Id<Ledger>)id);
    public Result Add(decimal price) => Raise(new SaleRecorded(price));

    public sealed record SaleRecorded(decimal Price) : Event;
    private void On(SaleRecorded e) => Sales++;
}

// ── the contract records, this application's own (no shared contract package) ─────────────────────────────────────────

public sealed record InvoicePaidV1(Guid Invoice, Guid Auction, decimal Amount);
public sealed record InvoicePaidV2(Guid Invoice, Guid Auction, MoneyV2 Paid);
public sealed record MoneyV2(decimal Amount, string Currency);
public sealed record AuctionSettledV1(Guid Auction, decimal Price);

// ── consumers: changes with [Consumes] factories ─────────────────────────────────────────────────────────────────────────

public sealed partial record RecordPayment(Id<Auction> Auction, string Invoice, decimal Amount) : IChange<Auction>
{
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Amount);

    [Consumes("billing.invoice-paid", 2)]
    public static RecordPayment From(InvoicePaidV2 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Paid.Amount);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
}

/// <summary>A creation validated by the pipeline: a price below 1 is Invalid (buried at attempt 1).</summary>
public sealed partial record IssueInvoice(Guid Auction, Amount Amount) : ICreation<Invoice>, IValidatable
{
    [Consumes("artflow.auction-settled", 1)]
    public static IssueInvoice From(AuctionSettledV1 m) => new(m.Auction, new Amount(m.Price));

    public Result<Invoice> Execute(ChangeContext context) => Invoice.Issue(Auction, Amount.Value, context.Now);
}

/// <summary>A second consumer of the same contract: its own subscription and key (M-12).</summary>
public sealed partial record RecordSale(Guid Ledger, decimal Price) : ILoads<Ledger>
{
    public static readonly Guid TheLedger = Guid.Parse("0193a000-0000-7000-8000-0000000000aa");

    [Consumes("artflow.auction-settled", 1, Name = "Sales")]
    public static RecordSale From(AuctionSettledV1 m) => new(TheLedger, m.Price);

    public async ValueTask<Result<Ledger>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Ledger>)Ledger, context.Operation, cancellationToken)).TryGetValue(out Ledger? found) ? found : Shop.Ledger.For(Ledger);

    public Result Execute(Ledger ledger, ChangeContext context) => ledger.Add(Price);
}

[JsonSerializable(typeof(InvoicePaidV1))]
[JsonSerializable(typeof(InvoicePaidV2))]
[JsonSerializable(typeof(AuctionSettledV1))]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.Settled))]
[JsonSerializable(typeof(Auction.PaymentRecorded))]
[JsonSerializable(typeof(Invoice.Issued))]
[JsonSerializable(typeof(Ledger.SaleRecorded))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ShopJson : JsonSerializerContext;
