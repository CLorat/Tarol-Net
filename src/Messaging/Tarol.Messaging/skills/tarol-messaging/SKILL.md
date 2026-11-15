---
name: tarol-messaging
description: >-
  Use when a Tarol app must react to another system's messages (integration events, a partner's contract) or test
  that: [Consumes(contract, version)] factories on changes, the generated GeneratedConsumers.Set, AddMessaging (Consume,
  Trust, UseInMemoryTransport, UseInMemoryDeadLetters), MessagingOptions/SourceTrust, the Message envelope (CloudEvents
  binary mode), MessageConsumers (Handle, Redrive), IMessageTransport, InMemoryTransport and MessageTest. Do not write a
  message handler class, a broker loop of your own or an inbox table; do not decompile or web-search.
---
# Tarol.Messaging (consumers)

## Rules

1. **A consumer is a change with a `[Consumes]` factory** per contract version: `public static TChange From(TPayload m)`
   or `From(TPayload m, Message envelope)`, returning its change. It maps and does nothing else; the pipeline validates
   the result (an `Invalid` is buried at attempt 1). The payload record is **this** app's own record of the contract
   (no shared contract package); list it in the app's `JsonSerializerContext` (TMSG006).
2. **Contracts are `<system>.<name>`** (`billing.invoice-paid`), the prefix is the system that owns it (TMSG003). The
   prefix is the source you trust; the transport's permissions decide who may publish under it.
3. **Register**: `services.AddTarol(AppJson.Default, …).Use…Store(…).AddMessaging("<app>", m => m.Consume(GeneratedConsumers.Set)
   .Trust("<source>", t => t.Tenants(…) or t.AnyTenant()).Use…Transport().Use…DeadLetters())`. A source not trusted, or
   a tenant it may not name, is buried. Default: no tenant (only messages without one).
4. **One change is one consumer**: its name (the type name, or `Name = "…"` pinned before a rename) is its subscription
   `<app>.<name>` and its key scope `consumer:<name>` (TMSG007). Two consumers of one contract are two changes.
5. **The key `(tenant, consumer:<name>, message id)` is the inbox**: a redelivery returns the first outcome and writes
   nothing. The message id is the publisher's event id, the same for every contract version, so a consumer reading v1 and
   v2 of one event commits once.
6. **Failures** follow the reaction worker: any `Conflict`, `NotFound`, 422, outage or exception → retry with backoff,
   dead letter at attempt 10; `Invalid`, `Forbidden`, `Gone`, an unknown version, malformed data → dead letter at once.
   Messages arrive in no order across systems: a message before its precondition is a `Conflict` (retried); an already
   done message is `Result.Success()` (a business duplicate), never a `Conflict`.
7. **The context** is `System("Consumers.<name>")` on behalf of the sender's actor, the envelope's tenant and
   correlation, caused by the message. The sender's actor is recorded, never authorized.
8. **Test with `MessageTest`**: `Trust`, `Given`, `Message(contract, version, payload, typeInfo)`, `Send`, `Drain()`,
   `Later(span)` (retries become due), `DeadMessages()`, `Redrive(id)`, `Load(id)`.

## Example

```csharp
// registration
var services = new ServiceCollection();
services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>())
    .UseInMemoryStore()
    .AddMessaging("shop", m => m
        .Consume(GeneratedConsumers.Set)
        .Trust("billing", t => t.Tenants(tenantA))
        .UseInMemoryTransport()
        .UseInMemoryDeadLetters());

// test: billing's invoice-paid becomes RecordPayment (see the record in rule 1's shape)
var test = MessageTest.At(start, GeneratedConsumers.Set, ShopJson.Default.Options, "shop")
    .Trust("billing", t => t.Tenants(tenantA))
    .InTenant(tenantA)
    .Given(auction);
await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(Guid.NewGuid(), auction.Id.Value, 150m), ShopJson.Default.InvoicePaidV1));
var attempts = await test.Drain();

attempts.Single().Verdict.ShouldBe(Verdict.Done);
test.Load(auction.Id).Paid.ShouldBe(150m);
```

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

## Common mistakes

| Mistake | What you get |
|---|---|
| `[Consumes]` on an instance method, a second parameter that is not `Message`, a factory returning another type | TMSG001 |
| two factories for one contract version | TMSG002 |
| `[Consumes("invoice-paid", 1)]` (no system prefix) or version 0 | TMSG003 |
| the payload record missing from the JSON context | TMSG006 warning; at runtime `InvalidOperationException` → retried, then a dead letter |
| two changes pinned to one `Name`, or one change's factories with different names | TMSG007 |
| forgetting `.Trust("billing", …)` | every message buried as `Messaging.UnknownSource` |
| `t.AnyTenant()` for an outside partner | the partner can write into every tenant: list its tenants |
| a `Conflict` for a message that was already handled | retried until it is a dead letter: return `Result.Success()` |
| I/O, a clock read or a lookup in `From` | not deterministic on redelivery: load in the change (`ILoads<T>`, `[Read]`) |

## AOT

Everything is source-generated: the switch, the payload through `JsonTypeInfo` of the app's context, the envelope's
headers. No reflection; Native AOT publishes with 0 IL warnings.
