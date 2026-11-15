# Tarol.Messaging

Messages between systems for [Tarol](https://github.com/CLorat/Tarol-Net): another system's message becomes a **change**
that runs through the same pipeline as every other entry, deduplicated by the idempotency key (the inbox), retried and
dead-lettered like a reaction. The envelope is CloudEvents 1.0 in binary mode; each source system is trusted by the prefix
of its contracts and for the tenants you allow. A transport port with an in-memory transport, dead letters with re-drive,
and `MessageTest` for consumers in memory. Native-AOT ready.

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
```

## Diagnostics

TMSG001 (the factory's shape), TMSG002 (two factories for one contract version), TMSG003 (the contract is
`<system>.<name>`, the version 1 or more), TMSG006 (the contract record is not in the app's JSON context), TMSG007
(consumer names collide).

Design: docs/architecture/messaging.md. Licence: proprietary, see LICENSE.
