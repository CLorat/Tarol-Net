# Messaging

User guide for `Tarol.Messaging` (consumers, PR 11A; publications and effects, PR 11B) and `Tarol.Messaging.Npgsql` (the
in-database bus and dead letters, PR 11C), `Tarol.Messaging.Nats` and `Tarol.Messaging.RabbitMq` (the brokers, PR 11D),
`Tarol.Messaging.SignalR` (the UI push, PR 11E). The runnable two-system sample is [`samples/Sagas`](../../samples/Sagas/README.md). Concept: [architecture/messaging.md](../architecture/messaging.md); decisions: [migration/messaging.md](../migration/messaging.md) §9.

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
6. **Tests need no broker.** `MessageTest` runs the consumers on the in-memory store and transport with a standing clock;
   `MessagingTest` runs several systems together.
7. **Your events leave through the outbox.** A `[Publishes<TEvent>]` mapping method per contract version; the commit
   writes a row, the reaction worker publishes it at least once and in order per stream. An effect is an outgoing call
   run the same way, with the event id as the receiver's idempotency key.

## Packages

| Package | Contents | PR |
|---|---|---|
| `Tarol.Messaging` (+ generator) | `Message`, `IMessageTransport`, `InMemoryTransport`, `[Consumes]`, `GeneratedConsumers.Set`, `MessageConsumers`, `MessagingHost`, `AddMessaging`, `IMessageDeadLetters`, `MessageTest` | 11A |
| | publications `[Publishes<T>]` (`GeneratedPublications.Set`, `MessagePublisher`), effects `IEffect<T>` (`EffectSet`), `MessagingTest` | 11B |
| `Tarol.Messaging.Npgsql` | `NpgsqlBus` (`UseNpgsqlBus`), `NpgsqlBusOptions`, `NpgsqlBusSchema` (bus, row-level security, grants), `NpgsqlMessageDeadLetters` (`UseNpgsqlDeadLetters`) | 11C |
| `Tarol.Messaging.Nats` | `NatsTransport` (`UseNats`), `NatsTransportOptions` | 11D |
| `Tarol.Messaging.RabbitMq` | `RabbitMqTransport` (`UseRabbitMq`), `RabbitMqTransportOptions` | 11D |
| `Tarol.Messaging.SignalR` | `PushToUi`, `MapMessageHub`, `MessageHub`, `ITopicAuthorizer`/`TopicRules`, `PushedMessage`, `MessagePushClient` | 11E |

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

## Publishing

```csharp
public static class Publications
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Price);

    [Publishes<Auction.Settled>("artflow.auction-settled", 2)]                      // while consumers migrate: both
    public static AuctionSettledV2 SettledV2(Auction.Settled e, RecordedEvent r) => new(r.StreamId, new MoneyV2(e.Price, "EUR"));

    [Publishes<Auction.PaymentRecorded>("artflow.payment-recorded", 1)]
    public static PaymentRecordedV1? Paid(Auction.PaymentRecorded e, RecordedEvent r)
        => e.Internal ? null : new(r.StreamId, e.Invoice, e.Amount);              // null: not published
}

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddMessaging("artflow", m => m.Publish(GeneratedPublications.Set).UseInMemoryTransport());
```

* The method is `static` (public or internal), takes the event or `(event, RecordedEvent)` and returns the contract record
  (TMSG004); it is the outward anti-corruption layer, so the domain event is never the contract. List the record in the
  JSON context (TMSG006). One method per contract version in an assembly (TMSG005); an assembly publishes under its own
  system's prefix (a second prefix is a TMSG005 warning).
* The commit writes `publish:<contract>@<version>` rows to the outbox (M-10). Add `AddReactionWorker()` wherever they
  should leave; it needs no reactions. The worker's span is `publish <contract>`, linked to the commit; it is the
  message's `traceparent`.
* The envelope (`Message.Of`): id = the event id, `ce-source` = the contract's prefix, `ce-subject` = the stream id,
  `ce-time` = when it was recorded, tenant, correlation and causation of the event's operation, `ce-actor`
  `Kind:id[:for]`.
* At least once: a crash after publishing publishes again; the transport drops the duplicate by
  `Message.DedupId` (event, contract, version) or the consumer's key replays it. A transport that fails retries the row
  with the worker's backoff, then `outbox_dead`.

## Effects

```csharp
public sealed class SendReceipt(IMailer mailer) : IEffect<Auction.PaymentRecorded>
{
    public async ValueTask<Result> Run(Auction.PaymentRecorded e, EffectContext context, CancellationToken cancellationToken)
    {
        var sent = await mailer.Send(e.Buyer, "Receipt " + e.Invoice, idempotencyKey: context.IdempotencyKey, cancellationToken);
        return sent ? Result.Success() : Error.Unavailable("Mail.Down");
    }
}

m.Effect<Auction.PaymentRecorded, SendReceipt>("send-receipt");      // rows effect:send-receipt; the handler from DI per row
```

An effect runs at least once: the receiver must deduplicate by `context.IdempotencyKey` (the event id, the same on every
attempt). The result is classified like a change's: `Unavailable`, a timeout or an exception retry; `Invalid`,
`Forbidden` or `Gone` go to `outbox_dead`. Keep the name when renaming the handler (it is stored in the rows). An effect
that should change state is a reaction instead.

## The PostgreSQL bus (`Tarol.Messaging.Npgsql`)

Systems that share a PostgreSQL server need no broker: the bus is a schema (default `bus`) all of them reach.

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Invoice>())
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddMessaging("billing", m => m
        .Consume(GeneratedConsumers.Set)
        .Publish(GeneratedPublications.Set)
        .Trust("artflow", t => t.AnyTenant())
        .UseNpgsqlBus(busConnectionString, new NpgsqlBusOptions { Notify = true })
        .UseNpgsqlDeadLetters());
```

* **Tables**: `messages` (one row per event id, contract and version; `publisher` = the role, stamped by the database),
  `subscriptions` (contract → subscription), `deliveries` (one row per subscription and message until acknowledged).
  `Prepare` runs the script (`EnsureSchema`, under an advisory lock) and adds the subscriptions.
* **Publish** is one statement: the message `ON CONFLICT DO NOTHING` and its deliveries; with `Notify` a `NOTIFY` in the
  same batch. **Consume** claims `Batch` due deliveries per loop with `FOR UPDATE SKIP LOCKED`, ordered after
  `RETURNING`, with a lease of `Subscription.Lease`; acknowledge deletes, retry sets a later `due_at`; both present the claim.
* **Order**: one stream's messages arrive in publish order with concurrency 1 (`MessagingOptions.Concurrency`).
* **Latency**: with `Notify` a delivery arrives in a few milliseconds; without, at the next `PollInterval` (100 ms).
  Measured end to end 9.8 / 22.3 ms p50/p99 with NOTIFY on both outbox and bus (migration note §4).
* **Retention**: `NpgsqlBus.PurgeMessages(olderThan, batch, ct)`; health: `NpgsqlBus.Pending(subscription, ct)`.

### Who may publish under a prefix

One login role per system, named like its prefix. The schema's owner runs `NpgsqlBusSchema.Script()`,
`RowLevelSecurityScript()` (policy `publish_own_prefix`: `split_part(type, '.', 1) = session_user`) and
`GrantScript(role)` for each system. Each system connects as its role with `EnsureSchema = false` and maps roles to sources:
`Roles = { ["artflow"] = "artflow", ["billing"] = "billing" }`. A foreign prefix is refused by the database (SQLSTATE
42501); without the policy, the consumer sees the stamped role as `Incoming.AuthenticatedAs` and buries a message whose
role is not its prefix (`Messaging.SourceMismatch`). An unknown role arrives as `role:<name>` and is never a source.

### The brokers (`Tarol.Messaging.Nats`, `Tarol.Messaging.RabbitMq`)

```csharp
.AddMessaging("billing", m => m.….UseNats(builder.Configuration.GetConnectionString("nats")!))      // NATS JetStream
.AddMessaging("billing", m => m.….UseRabbitMq(builder.Configuration.GetConnectionString("rabbitmq")!)) // RabbitMQ
```

| | NATS JetStream | RabbitMQ |
|---|---|---|
| per source system | stream `ARTFLOW` (`artflow.>`), duplicate window | topic exchange `artflow` |
| per subscription | durable pull consumer `billing_IssueInvoice` (`AckWait` = lease, `MaxAckPending` = concurrency) | quorum queue `billing.IssueInvoice` + `.retry` |
| durable publish | the stream's acknowledgement | the publisher confirm |
| a republish | dropped by `Nats-Msg-Id` = (event, contract, version); counted as success | delivered; the consumer's key replays it |
| retry after a delay | `Nak(delay)` | ack + a copy into the retry queue with a TTL that dead-letters back |
| who may publish under a prefix | subject permissions per user | write permission on the exchange per user; `user_id` validated → `AuthenticatedAs` |
| forbidden publish | a timeout (the permission error is logged) | `ACCESS_REFUSED` |
| a contract nobody subscribes | stored | returned, logged, counted as delivered |
| native size (over `Tarol.Messaging` alone, measured 11D) | +5.7 MB | +2.0 MB |

Both take a `Prefix` (a namespace in front of every name) for environments that share a server. With RabbitMQ name each
system's user like its prefix, or set `StampUserId = false` when all systems share one user.

## The UI push (`Tarol.Messaging.SignalR`)

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddMessaging("artflow", m => m
        .Trust("billing", t => t.AnyTenant())
        .UseNats(natsUrl)
        .PushToUi(o =>
        {
            o.Forward("artflow.auction-settled", "billing.invoice-paid");
            o.Rules.Subscribe("artflow.*").Subscribe("billing.invoice-paid").Publish("artflow.chat", role: "member");
        }));
app.UseAuthentication();
app.UseAuthorization();
app.MapMessageHub();                                           // /tarol/messages
```

* **Forwarding**: a consumer subscription `<app>.UiPush` sends each forwarded message to the SignalR group
  `<tenant>/<topic>` of its envelope's tenant; only this application's messages and those of a trusted source for an
  allowed tenant are forwarded. A disconnected client misses it: the push notifies, the store is the truth.
* **The hub** (`MessageHub`, `[Authorize]`): `Subscribe`, `Unsubscribe`, `Publish`. The tenant comes from the caller's
  claim, never from the client; `ITopicAuthorizer` decides every subscribe and publish (nothing is allowed by default); a
  client's publish carries the caller's `sub` as `Sender`.
* **The .NET client** `MessagePushClient`: subscribe any time (before `Start` too), handles that remove one handler,
  subscriptions replayed after every reconnect, failing handlers isolated.
* **Native AOT**: `SignalRJson` (source-generated hub JSON) and, in the app, the feature switch
  `Microsoft.AspNetCore.SignalR.Hub.IsCustomAwaitableSupported=false`.
* **Scale-out**: one forwarder name per web node (`o.Name = "UiPush-" + Environment.MachineName`).

## Dead letters in the database

`UseNpgsqlDeadLetters(schema = "tarol", dataSource = null)` keeps them in `<schema>.messages_dead` of the consumer's
database (by default the `NpgsqlDataSource` of `UseNpgsqlStore(connectionString)`), created at start; it works with every
transport. `NpgsqlBusSchema.DeadLettersScript(schema)` for a migration tool.

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

Several systems together, with publications and effects:

```csharp
var test = MessagingTest.At(start)
    .System("artflow", AppJson.Default.Options, o => { o.Publications = GeneratedPublications.Set; o.Consumers = GeneratedConsumers.Set; o.Trust("billing"); })
    .System("billing", BillingJson.Default.Options, o => { o.Publications = Billing.GeneratedPublications.Set; o.Consumers = Billing.GeneratedConsumers.Set; o.Trust("artflow"); });
test["artflow"].Given(auction);
await test["artflow"].When(new SettleAuction(auction.Id, 150m), Actor.User("alice"));
var steps = await test.Drain();            // MessagingStep: application, outbox row or delivery
await test.Later(TimeSpan.FromSeconds(2)).Drain();
```

`o.Effects = new EffectSet().Add<T>("name", _ => fake)` plugs in test doubles; `o.PublishThrough` wraps the transport
(e.g. one that fails after publishing, to see the duplicate replayed); `test[app].OutboxDead()` lists given-up rows.
