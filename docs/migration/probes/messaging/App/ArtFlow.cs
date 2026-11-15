using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace ArtFlow
{

// ── ArtFlow (system 1): auctions. Publishes artflow.auction-settled, consumes billing.invoice-paid. ─────────────────────

public partial class Auction : AggregateRoot<Auction>
{
    public string Seller { get; private set; } = "";
    public string? Buyer { get; private set; }
    public decimal Price { get; private set; }
    public bool IsSettled { get; private set; }
    public List<string> Invoices { get; } = [];
    public decimal Paid { get; private set; }

    public static Auction Open(string seller, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(seller));
        return auction;
    }

    public Result Settle(string buyer, decimal price) => IsSettled ? Error.Conflict("Auction.Settled") : Raise(new Settled(buyer, price));

    /// <summary>A payment can arrive before the settlement it pays (no order across systems): a Conflict, so it is retried (C-9).
    /// The same invoice again is a business duplicate: a success (rest.md §8).</summary>
    public Result RecordPayment(string invoice, decimal amount)
        => !IsSettled ? Error.Conflict("Auction.NotSettled")
         : Invoices.Contains(invoice) ? Result.Success()
         : Raise(new PaymentRecorded(invoice, amount));

    public Result Note(int sequence) => Raise(new Noted(sequence));

    public sealed record Opened(string Seller) : Event;
    public sealed record Settled(string Buyer, decimal Price) : Event;
    public sealed record PaymentRecorded(string Invoice, decimal Amount) : Event;
    public sealed record Noted(int Sequence) : Event;

    private void On(Opened e) => Seller = e.Seller;
    private void On(Settled e) => (Buyer, Price, IsSettled) = (e.Buyer, e.Price, true);
    private void On(PaymentRecorded e) { Invoices.Add(e.Invoice); Paid += e.Amount; }
    private void On(Noted e) { }
}

public sealed partial record OpenAuction(string Seller) : ICreation<Auction>
{
    public Result<Auction> Execute(ChangeContext context) => Auction.Open(Seller, context.Now);
}

public sealed partial record SettleAuction(Id<Auction> Auction, string Buyer, decimal Price) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Settle(Buyer, Price);
}

public sealed partial record NoteAuction(Id<Auction> Auction, int Sequence) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Note(Sequence);
}

/// <summary>Consumer (K-A): billing's invoice-paid becomes a change of the auction. The factory is the anti-corruption layer.</summary>
public sealed partial record RecordPayment(Id<Auction> Auction, string Invoice, decimal Amount) : IChange<Auction>
{
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(Contracts.InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Amount);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
}

/// <summary>Publications (PB-A): the outward anti-corruption layer, one mapping per (event, contract, version).</summary>
public static class Publications
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static Contracts.AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Buyer, e.Price, "EUR");

    [Publishes<Auction.Settled>("artflow.auction-settled", 2)]                            // published only while migrating (probe switch)
    public static Contracts.AuctionSettledV2 SettledV2(Auction.Settled e, RecordedEvent r) => new(r.StreamId, new(e.Buyer), new(e.Price, "EUR"));

    [Publishes<Auction.Noted>("artflow.auction-noted", 1)]
    public static Contracts.AuctionNotedV1 Noted(Auction.Noted e, RecordedEvent r) => new(r.StreamId, e.Sequence);
}

namespace Contracts
{
    // ArtFlow's own records of the contracts it publishes and consumes (no shared contract package)
    public sealed record AuctionSettledV1(Guid Auction, string Buyer, decimal Price, string Currency);
    public sealed record AuctionSettledV2(Guid Auction, PartyV2 Buyer, MoneyV2 Price);
    public sealed record PartyV2(string Name);
    public sealed record MoneyV2(decimal Amount, string Currency);
    public sealed record AuctionNotedV1(Guid Auction, int Sequence);
    public sealed record InvoicePaidV1(Guid Invoice, Guid Auction, decimal Amount);
}

[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.Settled))]
[JsonSerializable(typeof(Auction.PaymentRecorded))]
[JsonSerializable(typeof(Auction.Noted))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Contracts.AuctionSettledV1))]
[JsonSerializable(typeof(Contracts.AuctionSettledV2))]
[JsonSerializable(typeof(Contracts.AuctionNotedV1))]
[JsonSerializable(typeof(Contracts.InvoicePaidV1))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ArtFlowJson : JsonSerializerContext;
}
