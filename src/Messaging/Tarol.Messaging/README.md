# Tarol.Messaging

Messages between systems for [Tarol](https://github.com/CLorat/Tarol-Net): another system's message becomes a **change**
that runs through the same pipeline as every other entry, deduplicated by the idempotency key (the inbox), retried and
dead-lettered like a reaction. Your own events leave as **publications**: a mapping method per contract version, written
as an outbox row in the commit and published by the reaction worker; **effects** call the outside world once per event,
keyed by the event id. The envelope is CloudEvents 1.0 in binary mode; each source system is trusted by the prefix of its
contracts and for the tenants you allow. A transport port with an in-memory transport, dead letters with re-drive,
`MessageTest` and `MessagingTest` for one or several systems in memory. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.Messaging      # brings Tarol.Sagas, Tarol.Changes (and their generators) and Tarol.Persistence
```

## 30 seconds

```csharp
// a consumer: a change with one [Consumes] factory per contract version (the anti-corruption layer)
public sealed partial record RecordPayment(Id<Auction> Auction, string Invoice, decimal Amount) : IChange<Auction>
{
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Amount);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
}

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddMessaging("artflow", m => m
        .Consume(GeneratedConsumers.Set)                     // generated from the [Consumes] factories of this assembly
        .Trust("billing", t => t.Tenants(tenantA))           // billing.* contracts, for tenant A only
        .UseInMemoryTransport()                              // or a broker package: UseNats, UseRabbitMq, UseNpgsqlBus
        .UseInMemoryDeadLetters());                          // or UseNpgsqlDeadLetters (Tarol.Messaging.Npgsql)
```

## Publications and effects

```csharp
public static class Publications                           // the outward anti-corruption layer
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Price);
}

public sealed class SendReceipt(IMailer mailer) : IEffect<Auction.PaymentRecorded>
{
    public async ValueTask<Result> Run(Auction.PaymentRecorded e, EffectContext context, CancellationToken cancellationToken)
    {
        await mailer.Send(e.Buyer, "Receipt", context.IdempotencyKey, cancellationToken);   // the event id: the same on every attempt
        return Result.Success();
    }
}

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .WithReactions(GeneratedReactions.Set)
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()                                     // runs reactions, publications and effects
    .AddMessaging("artflow", m => m
        .Publish(GeneratedPublications.Set)                  // rows publish:artflow.auction-settled@1
        .Effect<Auction.PaymentRecorded, SendReceipt>("send-receipt")
        .UseInMemoryTransport());
```

The commit writes one outbox row per publication (`publish:<contract>@<version>`) and effect (`effect:<name>`); the
reaction worker runs them with its claim, head order per stream, backoff, lease, `outbox_dead` and trace links. Message id =
the event id (the same for every contract version); a duplicate after a crash is dropped by the transport or replayed by
the consumer's key. A mapping method returning `null` publishes nothing.

## What happens to a message

One durable subscription per consumer (`<application>.<consumer>`). For each delivery:

| Check / answer | Verdict |
|---|---|
| no CloudEvents headers, data that does not match the contract record | buried (`Messaging.Malformed`) |
| `ce-source` or the transport's publisher ≠ the contract's prefix; a source you do not trust; a tenant it may not name | buried (`Messaging.SourceMismatch`, `UnknownSource`, `TenantNotAllowed`) |
| a known contract in a version no factory reads | buried (`Messaging.UnknownVersion`) |
| a contract the consumer does not read | dropped |
| the pipeline commits, or the key replays the first outcome | done |
| any `Conflict`, `NotFound`, 422, an outage, an exception | retried after 1 s, 2 s, 4 s … 5 min; buried at attempt 10 |
| `Invalid`, `Forbidden`, `Unauthorized`, `Gone` | buried |

The change runs as `Actor.System("Consumers.<name>")` on behalf of the sender's actor, in the envelope's tenant and
correlation, caused by the message, with the key `(tenant, consumer:<name>, message id)`. Dead letters keep the headers
and the bytes: `MessageConsumers.Redrive(id)` runs one again.

## Testing

```csharp
var test = MessageTest.At(start, GeneratedConsumers.Set, AppJson.Default.Options, "artflow").Trust("billing", t => t.AnyTenant()).Given(auction);
await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(invoice, auction.Id.Value, 150m), AppJson.Default.InvoicePaidV1));
var attempts = await test.Drain();                    // what each consumer did
await test.Later(TimeSpan.FromSeconds(2)).Drain();     // retries, once their backoff passed

// two systems: outbox → transport → consumers → their outbox …
var two = MessagingTest.At(start)
    .System("artflow", AppJson.Default.Options, o => o.Publications = GeneratedPublications.Set)
    .System("billing", BillingJson.Default.Options, o => { o.Consumers = Billing.GeneratedConsumers.Set; o.Trust("artflow", t => t.AnyTenant()); });
two["artflow"].Given(auction);
await two["artflow"].When(new SettleAuction(auction.Id, 150m));
await two.Drain();
```

## Diagnostics

TMSG001 (the factory's shape), TMSG002 (two factories for one contract version), TMSG003 (the contract is
`<system>.<name>`, the version 1 or more), TMSG004 (the publication method's shape), TMSG005 (two publications of one
contract version; a warning for two prefixes in one assembly), TMSG006 (a payload or contract record is not in the app's
JSON context), TMSG007 (consumer names collide).

Design: docs/architecture/messaging.md. Licence: proprietary, see LICENSE.
