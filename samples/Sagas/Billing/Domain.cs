using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Billing;

// Billing: an invoice per settled auction, paid at once by a stand-in payment provider (a reaction), published back.

public partial class Invoice : AggregateRoot<Invoice>
{
    public Guid Auction { get; private set; }
    public string Buyer { get; private set; } = "";
    public decimal Amount { get; private set; }
    public bool IsPaid { get; private set; }

    public static Invoice Issue(Guid auction, string buyer, decimal amount, DateTimeOffset now)
    {
        var invoice = new Invoice(Id<Invoice>.New(now));
        invoice.Raise(new Issued(auction, buyer, amount));
        return invoice;
    }

    /// <summary>Paying twice is not a failure (a redelivered row): it changes nothing.</summary>
    public Result Pay() => IsPaid ? Result.Success() : Raise(new Paid(Auction, Amount));

    public sealed record Issued(Guid Auction, string Buyer, decimal Amount) : Event;
    public sealed record Paid(Guid Auction, decimal Amount) : Event;

    private void On(Issued e) => (Auction, Buyer, Amount) = (e.Auction, e.Buyer, e.Amount);
    private void On(Paid e) => IsPaid = true;
}

/// <summary>Billing's own record of ArtFlow's contract: only the members it reads (the currency is ignored).</summary>
public sealed record AuctionSettledV1(Guid Auction, string Buyer, decimal Price);

/// <summary>What billing tells other systems about a paid invoice.</summary>
public sealed record InvoicePaidV1(Guid Invoice, Guid Auction, decimal Amount);

/// <summary>ArtFlow's settlement becomes an invoice (a consumer: the key (tenant, consumer:IssueInvoice, message id) issues it once).</summary>
public sealed partial record IssueInvoice(Guid Auction, string Buyer, decimal Amount) : ICreation<Invoice>
{
    [Consumes("artflow.auction-settled", 1)]
    public static IssueInvoice From(AuctionSettledV1 m) => new(m.Auction, m.Buyer, m.Price);

    public Result<Invoice> Execute(ChangeContext context) => Invoice.Issue(Auction, Buyer, Amount, context.Now);
}

/// <summary>The stand-in payment provider: every issued invoice is paid (a reaction, so its key pays it once).</summary>
public sealed partial record PayInvoice(Id<Invoice> Invoice) : IChange<Invoice>
{
    [OnEvent<Invoice.Issued>]
    public static PayInvoice From(Invoice.Issued e, RecordedEvent recorded) => new((Id<Invoice>)recorded.StreamId);

    public Result Execute(Invoice invoice, ChangeContext context) => invoice.Pay();
}

public static class Publications
{
    [Publishes<Invoice.Paid>("billing.invoice-paid", 1)]
    public static InvoicePaidV1 Paid(Invoice.Paid e, RecordedEvent r) => new(r.StreamId, e.Auction, e.Amount);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Invoice.Issued))]
[JsonSerializable(typeof(Invoice.Paid))]
[JsonSerializable(typeof(AuctionSettledV1))]
[JsonSerializable(typeof(InvoicePaidV1))]
internal sealed partial class BillingJson : JsonSerializerContext;
