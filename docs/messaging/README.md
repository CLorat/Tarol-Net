# Messaging

User guide for `Tarol.Messaging` (consumers, PR 11A; publications, the PostgreSQL bus, the brokers and the UI push follow in
11B–11E). Concept: [architecture/messaging.md](../architecture/messaging.md); decisions: [migration/messaging.md](../migration/messaging.md) §9.

## Design in six points

1. **Another system's message is the entry of a change.** A `[Consumes(contract, version)]` factory maps the contract
   record to the change's input; the change runs through the same pipeline as every other entry.
2. **The idempotency key is the inbox.** `(tenant, consumer:<name>, message id)` is stored with the change's commit: a
   redelivery returns the first outcome and writes nothing.
3. **Each source is trusted by its prefix.** A contract is `<system>.<name>`; the transport's permissions decide who may
   publish under a prefix, and you decide which tenants a source may name (default: none).
4. **Failures are the reaction worker's.** Conflicts, missing targets and outages retry with backoff; invalid or
   malformed messages, unknown versions and refused senders become dead letters at once; dead letters keep the bytes and
   can be re-driven.
5. **The context tells who acted.** `System("Consumers.<name>")` on behalf of the sender's actor, in the sender's tenant
   and conversation, caused by the message.
6. **Tests need no broker.** `MessageTest` runs the consumers on the in-memory store and transport with a standing clock.

## Packages

| Package | Contents | PR |
|---|---|---|
| `Tarol.Messaging` (+ generator) | `Message`, `IMessageTransport`, `InMemoryTransport`, `[Consumes]`, `GeneratedConsumers.Set`, `MessageConsumers`, `MessagingHost`, `AddMessaging`, `IMessageDeadLetters`, `MessageTest` | 11A |
| | publications `[Publishes<T>]` and effects from the outbox | 11B |
| `Tarol.Messaging.Npgsql` | the in-database bus and `messages_dead` | 11C |
| `Tarol.Messaging.Nats`, `Tarol.Messaging.RabbitMq` | the brokers | 11D |
| `Tarol.Messaging.SignalR` | the UI push | 11E |

## Declaring a consumer

```csharp
public sealed partial record RecordPayment(Id<Auction> Auction, string Invoice, decimal Amount) : IChange<Auction>
{
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Amount);

    [Consumes("billing.invoice-paid", 2)]
    public static RecordPayment From(InvoicePaidV2 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Paid.Amount);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
}
```

* The payload records are **yours**: billing's `InvoicePaidV1` may have more members; unknown members are ignored. List
  them in your `JsonSerializerContext` (TMSG006 warns otherwise).
* A factory may take the envelope too: `From(InvoicePaidV1 m, Message envelope)` (its tenant, subject, time).
* One change is one consumer. A second consumer of the same contract is another change (its own subscription, key and
  dead letters). Pin `Name = "…"` before renaming a change: the name is stored in keys and subscriptions.
* Messages come in no order across systems: a message whose precondition is missing is a `Conflict` (retried); a message
  whose effect already exists is a success.

## Registering

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddMessaging("artflow", m => m
        .Consume(GeneratedConsumers.Set)
        .Trust("billing", t => t.Tenants(tenantA))
        .UseInMemoryTransport()
        .UseInMemoryDeadLetters());
```

`m.Options`: `Policy` (attempts and backoff, the reaction worker's `DeliveryPolicy`), `Concurrency` (per subscription,
default 1: the transport's order holds), `Lease`. Several assemblies: call `Consume` once per set. Tracing: add
`MessageConsumers.ActivitySourceName` (`Tarol.Messaging`); each delivery is a `consume <contract>` span linked to the
publisher's `traceparent`.

## Dead letters

`MessageConsumers.DeadLetters.List(max, ct)` lists them (subscription, topic, message id, attempts, reason, headers,
bytes); after fixing the cause, `MessageConsumers.Redrive(id, ct)` runs one again as attempt 1. The key still holds: a
message whose change did commit is not repeated.

## Testing

```csharp
var test = MessageTest.At(start, GeneratedConsumers.Set, AppJson.Default.Options, "artflow")
    .Trust("billing", t => t.Tenants(tenantA))
    .InTenant(tenantA)
    .Given(auction);
await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(invoice, auction.Id.Value, 150m), AppJson.Default.InvoicePaidV1));
var attempts = await test.Drain();                 // ConsumerAttempt: consumer, verdict, reason, outcome
await test.Later(TimeSpan.FromSeconds(2)).Drain(); // retries
```
