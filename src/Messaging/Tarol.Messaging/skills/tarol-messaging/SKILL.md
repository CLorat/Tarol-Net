---
name: tarol-messaging
description: >-
  Use when a Tarol app must react to another system's messages, publish its own events to other systems, call the
  outside world once per event (e-mail, payment API), or test that: [Consumes(contract, version)] factories,
  [Publishes<TEvent>(contract, version)] mapping methods, IEffect<TEvent>, GeneratedConsumers.Set /
  GeneratedPublications.Set, AddMessaging (Consume, Publish, Effect, Trust, Use…Transport, Use…DeadLetters), the Message
  envelope (CloudEvents binary mode), MessageConsumers, IMessageTransport, InMemoryTransport, MessageTest and
  MessagingTest. Do not write a message handler class, a broker loop, an inbox or publications table, or send from a
  change or reaction; do not decompile or web-search.
---
# Tarol.Messaging (consumers, publications, effects)

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

## Publications and effects

9. **Publish with a mapping method**: `[Publishes<TEvent>("<own system>.<name>", v)]` on a `static` method `(TEvent)` or
   `(TEvent, RecordedEvent)` returning the contract record (a class in the JSON context; `null` = not published). Never
   publish the domain event itself. One method per contract version (TMSG005); v2 is a second method.
10. **Rows, not sends**: the commit writes `publish:<contract>@<version>` rows; the reaction worker publishes them at
    least once, in version order per stream, so `AddReactionWorker()` must run (it needs no reactions). Message id =
    event id: a duplicate is dropped by the transport or replayed by the consumer's key.
11. **An effect** is `IEffect<TEvent>` (`effect:<name>` rows), registered with `.Effect<TEvent, THandler>("name")`,
    resolved from DI per row, run at least once: pass `context.IdempotencyKey` (the event id) to the receiver.
    `Unavailable` or an exception retries; `Invalid` goes to `outbox_dead`.
12. **Test systems together with `MessagingTest`**: `.System(app, json, o => …)` (`Publications`, `Consumers`, `Effects`,
    `Trust`, `PublishThrough` for fault injection), `test[app].Given/When`, `Drain()`, `Later(span)`, `OutboxDead()`.

```csharp
public static class Publications
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Price);
}

public sealed class SendReceipt(Mailbox mailbox) : IEffect<Auction.PaymentRecorded>
{
    public ValueTask<Result> Run(Auction.PaymentRecorded domainEvent, EffectContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(mailbox.Send("receipt " + domainEvent.Invoice, context.IdempotencyKey));
}
```

```csharp
services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>())
    .UseInMemoryStore()
    .AddReactionWorker()
    .AddMessaging("artflow", m => m
        .Publish(GeneratedPublications.Set)
        .Effect<Auction.PaymentRecorded, SendReceipt>("send-receipt")
        .UseInMemoryTransport());

var test = MessagingTest.At(start)
    .System("artflow", ShopJson.Default.Options, o => o.Publications = GeneratedPublications.Set)
    .System("billing", ShopJson.Default.Options, o => { o.Consumers = GeneratedConsumers.Set; o.Trust("artflow", t => t.AnyTenant()); });
test["artflow"].Given(open);
(await test["artflow"].When(new SettleAuction(open.Id, 150m))).IsSuccess.ShouldBeTrue();
await test.Drain();

test.Transport.Published.Single().Type.ShouldBe("artflow.auction-settled");
test["billing"].Store.Recorded.Count(r => r.Event.Event is Invoice.Issued).ShouldBe(1);
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
| `[Publishes<T>]` on an instance or private method, a parameter that is not the event or `RecordedEvent`, returning a struct or string | TMSG004 |
| two methods for one contract version; contracts of two prefixes in one assembly | TMSG005 (error; warning) |
| `Publish(…)` without `AddReactionWorker()` in any process | rows pile up in the outbox, nothing leaves |
| sending an e-mail or calling an API in `Execute` or a reaction | not effect-once: use an `IEffect<T>` with its key |

## Operations (step 15)

Meter `"Tarol.Messaging"` (`MessageConsumers.MeterName`): `tarol.messages.consumed` (`tarol.consumer`, outcome completed |
dropped | retried | buried), `tarol.messages.duration`, `tarol.messages.published` (`tarol.contract`, outcome published |
skipped | failed). Dead letters page with `IMessageDeadLetters.List(new DeadLetterQuery { After, Limit, Tenant, Name })`
(`Name` = the subscription, `Tenant` = the envelope's `ce-tenant`) and `Discard(id)`; `MessageConsumers.Redrive(id, actor)`
audits the take in `messages_dead`.

## AOT

Everything is source-generated: the switch, the publication table and map, the payload and contract records through
`JsonTypeInfo` of the app's context, the envelope's headers. `Effect<TEvent, THandler>` keeps the handler's constructor. No reflection; Native AOT publishes with 0 IL warnings.
