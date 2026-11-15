using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Billing
{

// ── billing (system 2): invoices. Consumes artflow.auction-settled (v1 and v2), publishes billing.invoice-paid. ──────────

public readonly partial record struct Amount([Range(1, 1_000_000)] decimal Value);

public partial class Invoice : AggregateRoot<Invoice>
{
    public Guid Auction { get; private set; }
    public string Buyer { get; private set; } = "";
    public decimal Amount { get; private set; }
    public bool IsPaid { get; private set; }
    public int Reminders { get; private set; }

    public static Invoice Issue(Guid auction, string buyer, decimal amount, DateTimeOffset now)
    {
        var invoice = new Invoice(Id<Invoice>.New(now));
        invoice.Raise(new Issued(auction, buyer, amount));
        return invoice;
    }

    public Result Pay() => IsPaid ? Result.Success() : Raise(new Paid(Auction, Amount));
    public Result Remind() => Raise(new Reminded());

    public sealed record Issued(Guid Auction, string Buyer, decimal Amount) : Event;
    public sealed record Paid(Guid Auction, decimal Amount) : Event;
    public sealed record Reminded : Event;

    private void On(Issued e) => (Auction, Buyer, Amount) = (e.Auction, e.Buyer, e.Amount);
    private void On(Paid e) => IsPaid = true;
    private void On(Reminded e) => Reminders++;
}

/// <summary>One ledger per tenant: a second consumer of the same contract.</summary>
public partial class Ledger : AggregateRoot<Ledger>
{
    public int Sales { get; private set; }
    public decimal Total { get; private set; }

    public static Ledger For(Guid id) => new((Id<Ledger>)id);
    public Result Add(decimal price) => Raise(new SaleRecorded(price));

    public sealed record SaleRecorded(decimal Price) : Event;
    private void On(SaleRecorded e) => (Sales, Total) = (Sales + 1, Total + e.Price);
}

/// <summary>Consumer (K-A): one factory per contract version; the pipeline validates the result (Amount ≥ 1).</summary>
public sealed partial record IssueInvoice(Guid Auction, string Buyer, Amount Amount) : ICreation<Invoice>, IValidatable
{
    [Consumes("artflow.auction-settled", 1)]
    public static IssueInvoice From(Contracts.AuctionSettledV1 m) => new(m.Auction, m.Buyer, new Amount(m.Price));

    [Consumes("artflow.auction-settled", 2)]
    public static IssueInvoice From(Contracts.AuctionSettledV2 m) => new(m.Auction, m.Buyer.Name, new Amount(m.Price.Amount));

    public Result<Invoice> Execute(ChangeContext context) => Invoice.Issue(Auction, Buyer, Amount.Value, context.Now);
}

/// <summary>A second consumer of artflow.auction-settled (v1 only): load-or-create the tenant's ledger.</summary>
public sealed partial record RecordSale(Guid Ledger, decimal Price) : ILoads<Ledger>
{
    [Consumes("artflow.auction-settled", 1)]
    public static RecordSale From(Contracts.AuctionSettledV1 m, Message envelope)
        => new(envelope.Tenant.TryGetValue(out Tarol.StronglyTypedId.Id<Tarol.Kernel.ITenant> t) ? t.Value : Guid.Empty, m.Price);

    public async ValueTask<Result<Ledger>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Ledger>)Ledger, context.Operation, cancellationToken)).TryGetValue(out Ledger? existing) ? existing : Billing.Ledger.For(Ledger);

    public Result Execute(Ledger ledger, ChangeContext context) => ledger.Add(Price);
}

public sealed partial record PayInvoice(Id<Invoice> Invoice) : IChange<Invoice>
{
    public Result Execute(Invoice invoice, ChangeContext context) => invoice.Pay();
}

/// <summary>A reaction after a consumed message: shows which actor the next hop records.</summary>
public sealed partial record RemindBuyer(Id<Invoice> Invoice) : IChange<Invoice>
{
    [OnEvent<Invoice.Issued>]
    public static RemindBuyer From(Invoice.Issued e, RecordedEvent r) => new((Id<Invoice>)r.StreamId);

    public Result Execute(Invoice invoice, ChangeContext context) => invoice.Remind();
}

public static class Publications
{
    [Publishes<Invoice.Paid>("billing.invoice-paid", 1)]
    public static Contracts.InvoicePaidV1 Paid(Invoice.Paid e, RecordedEvent r) => new(r.StreamId, e.Auction, e.Amount);
}

namespace Contracts
{
    // billing's own records: fewer fields than ArtFlow publishes (Currency is ignored), its own names
    public sealed record AuctionSettledV1(Guid Auction, string Buyer, decimal Price);
    public sealed record AuctionSettledV2(Guid Auction, BuyerV2 Buyer, PriceV2 Price);
    public sealed record BuyerV2(string Name);
    public sealed record PriceV2(decimal Amount, string Currency);
    public sealed record InvoicePaidV1(Guid Invoice, Guid Auction, decimal Amount);
}

[JsonSerializable(typeof(Invoice.Issued))]
[JsonSerializable(typeof(Invoice.Paid))]
[JsonSerializable(typeof(Invoice.Reminded))]
[JsonSerializable(typeof(Ledger.SaleRecorded))]
[JsonSerializable(typeof(Contracts.AuctionSettledV1))]
[JsonSerializable(typeof(Contracts.AuctionSettledV2))]
[JsonSerializable(typeof(Contracts.InvoicePaidV1))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class BillingJson : JsonSerializerContext;
}
