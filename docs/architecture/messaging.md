# Messaging

Concept note for step 11 (see [migration/messaging.md](../migration/messaging.md)): **messages between systems are
entries and outboxes Tarol already has.** Incoming, another system's message is the entry of a change: a
`[Consumes(contract, version)]` factory maps it (the anti-corruption layer), the same `ChangePipeline` runs it, the
idempotency key stores it with the commit (the inbox). Outgoing, our committed event writes one more outbox row in its
own transaction: a `[Publishes<TEvent>]` mapping turns it into the contract record, and the reaction worker publishes it
at least once with the event id as the message id. Between them sits a small transport port with an in-memory, a
PostgreSQL, a NATS JetStream and a RabbitMQ implementation. The old `Tarol.Messaging` (`CLorat/Tarol@6a62f81`, a SignalR
topic relay) is the design input of the UI push (§10).

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/messaging/`](../migration/probes/messaging/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: designed** (owner decisions M-1 … M-15, [migration/messaging.md](../migration/messaging.md) §9); PRs 11A–11E.

## 1. What a user of Tarol needs

| ArtFlow / billing use case | Shape | Built from |
|---|---|---|
| billing issues an invoice when ArtFlow settles an auction | consumer: `artflow.auction-settled` v1/v2 → creation of `Invoice` | `[Consumes]` on `IssueInvoice.From` |
| ArtFlow records the payment when billing reports it | consumer: `billing.invoice-paid` → change of `Auction` | `[Consumes]` on `RecordPayment.From` |
| tell other systems that an auction settled, without leaking the domain event | publication: `Auction.Settled` → `AuctionSettledV1` | `[Publishes<Auction.Settled>]` on a mapping method |
| send a receipt e-mail once per payment (S-2) | effect: a handler of an outgoing message, keyed by the event id | outbox row `effect:<name>` |
| a payment that arrives before its settlement | retry until the settlement commits (no order across systems) | C-9 Conflict → retry |
| a message that can never succeed | dead letter with its reason and bytes, re-drive by a human | `messages_dead` (M-9) |
| test both systems without a broker | in-memory stores and transport | `InMemoryTransport`, `MessageTest` |

## 2. One message, end to end **[verified]** (Aspire: 5 of 5 round trips, JIT and Native AOT; actors as decided in M-6)

```text
ArtFlow api      POST /auctions → SettleAuction → commit: events + outbox row "publish:artflow.auction-settled@1"
ArtFlow worker   claim (head per stream) → AuctionSettledV1 = Publications.SettledV1(e, r) → transport.Publish(message)
                 → Complete                                              span "reaction publish:…", linked to the commit
transport        NATS JetStream stream ARTFLOW / RabbitMQ exchange artflow / bus.messages + bus.deliveries
billing          durable subscription "billing.IssueInvoice" → checks (source, tenant) → context → key
                 → GeneratedConsumers: (IssueInvoice, artflow.auction-settled, 1) → IssueInvoice.From(payload)
                 → ChangePipeline.Run → Delivery.Classify → ack | retry(after) | bury     span "consume …", linked
billing          PayInvoice → Invoice.Paid → outbox "publish:billing.invoice-paid@1" → … → ArtFlow RecordPayment
```

## 3. Consumers (C-5, C-13, M-6, M-12, M-13)

```csharp
public sealed partial record IssueInvoice(Guid Auction, string Buyer, Amount Amount) : ICreation<Invoice>, IValidatable
{
    [Consumes("artflow.auction-settled", 1)]
    public static IssueInvoice From(Contracts.AuctionSettledV1 m) => new(m.Auction, m.Buyer, new Amount(m.Price));

    [Consumes("artflow.auction-settled", 2)]
    public static IssueInvoice From(Contracts.AuctionSettledV2 m) => new(m.Auction, m.Buyer.Name, new Amount(m.Price.Amount));

    public Result<Invoice> Execute(ChangeContext context) => Invoice.Issue(Auction, Buyer, Amount.Value, context.Now);
}
```

* **Declaration**: a static factory `(TPayload)` or `(TPayload, Message)` on a change, one per contract version, like
  `[Webhook]` (R-10). The factory maps and does nothing else; TVAL006 is exempt there (C-13), the pipeline validates the
  result (an `Invalid` is buried at attempt 1 **[verified]**).
* **Generated per assembly** (like the webhook switch): `GeneratedConsumers.Set` = the consumers with their contracts
  and the switch (consumer, contract, version) → payload through the app's JSON context → factory → `pipeline.Run`.
  A payload that does not deserialize is `Malformed`, a known contract in a version no factory reads is
  `UnknownVersion`, anything else `UnknownContract` **[verified]** as hand-written generated code.
* **Name** (M-12): the change's type name, pinnable with `Name`; it names the subscription (`<app>.<name>`) and the key
  scope. **Key**: `(tenant, "consumer:<name>", message id)`, stored with the commit (C-8). With `consumer:<contract>` the
  second consumer of a contract replays the first one's outcome and never runs **[verified]**; with the name both commit.
* **One durable subscription per consumer**: each retries and dead-letters on its own (as one outbox row per reaction).
* **Context** (M-6): `Actor.System("Consumers.<name>", onBehalfOf: <the envelope actor's id>)`, the envelope's tenant
  (after the trust check) and correlation, causation = the message id. The next hop keeps `for alice` **[verified]**
  (`System:Consumers.IssueInvoice (for alice)` → `System:Reactions.RemindBuyer (for alice)`). The Kernel allows
  `OnBehalfOf` only on a System actor **[verified]** (`Actor.cs:36` refused `Service(artflow) for alice`). The actor is
  recorded, never authorized: consumers pass no principal (R-6).
* **Trust** (M-13): the contract's prefix is the source (`billing.invoice-paid` → `billing`). The consumer buries
  `Messaging.SourceMismatch` (a `ce-source` or an authenticated publisher that disagrees), `Messaging.UnknownSource`
  and `Messaging.TenantNotAllowed` (a tenant outside the source's allow-list; default none, `AnyTenant` per source)
  **[verified]**. A forged `ce-actor` is only ever recorded as the "for" of the consumer **[verified]**; trusting it as
  the actor (what not to do) recorded `User:admin` **[verified]**.
* **Delivery** (C-9, S-7, R-10): `Delivery.Classify` unchanged: done or replayed → ack; any Conflict, NotFound, 422, an
  outage or an exception → retry after `DeliveryPolicy.Backoff`, buried at `MaxAttempts`; `Invalid`, Forbidden, Gone,
  malformed data, an unknown version → buried at once; an unknown contract → acknowledged and dropped (subscriptions
  filter by contract, so it arrives only by misconfiguration) **[verified]** on all four transports.
* **Order** (changes.md §7): not across systems. A payment before its settlement is a Conflict, retried until the
  settlement commits **[verified]**. Per stream, the publisher sends in version order (§4) and a subscription with
  concurrency 1 keeps it **[verified]**; with concurrency 4 the order per stream breaks (1–11 inversions in 50 messages)
  **[verified]**.
* **Where they run**: a consumer host (`BackgroundService`, `Tarol.Messaging`) next to the reaction worker; any number of
  processes (the transport spreads deliveries).

## 4. Publications and effects (M-7, M-10, S-2)

```csharp
public static class Publications
{
    [Publishes<Auction.Settled>("artflow.auction-settled", 1)]
    public static Contracts.AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Buyer, e.Price, "EUR");
}
```

* **Declaration** (M-7): a static mapping method per (event, contract, version): the outward anti-corruption layer; the
  domain event is never the contract. Contracts are prefixed by the owning system.
* **Rows** (M-10): the commit writes one outbox row `publish:<contract>@<version>` per publication, in the same table as
  reactions; the **reaction worker runs them**: claim, head per (stream, publication) so one stream's messages leave in
  version order, backoff, lease, `outbox_dead`, links **[verified]** (publications registered as `ReactionSet` runners;
  the implementation gives the worker a row handler instead of the trick).
* **Message id** = the event id, the same for every contract version of one event: a consumer that reads v1 and v2
  commits once **[verified]**. A broker's dedup id must be (event, contract, version): JetStream's `Nats-Msg-Id` is unique
  per stream, so the event id alone drops v2 of a dual publish **[verified]**.
* **At least once, effect once at the receiver**: a crash after publish and before `Complete` publishes again; JetStream
  (`Nats-Msg-Id`) and the PostgreSQL bus (`ON CONFLICT (id, type, version)`) drop the duplicate, RabbitMQ delivers it
  and the consumer's key replays it; one invoice in every case **[verified]**. NATS.Net's `EnsureSuccess()` throws on a
  duplicate ack: the publisher must treat it as success or it retries until the dead letter **[verified]**.
* **Effects** (S-2): `effect:<name>` rows on the same outbox; the handler calls the outside world with the event id as
  idempotency key. Provider down once and a crash after sending → 3 calls, 1 key, 1 mail **[verified]**.

## 5. The envelope (M-5): CloudEvents 1.0, binary content mode **[verified]** on all four transports

| Header | Value | Trusted? |
|---|---|---|
| `ce-specversion`, `ce-id`, `ce-source`, `ce-type`, `ce-time` | `1.0`, the event id, the system, the contract, `RecordedAt` | id, type, time: yes; source: checked against the prefix |
| `ce-contractversion` | the contract version (extension) | yes |
| `ce-subject` | the stream id (the ordering key) | — |
| `ce-tenant` | `Id<ITenant>` | only within the source's allow-list |
| `ce-correlation`, `ce-causation` | the operation's ids | correlation: yes (not security data); causation becomes the message id |
| `ce-actor` | `Kind:id[:for]` | recorded as "for" only (M-6) |
| `traceparent` | the publishing span | a link, never a parent (tracing.md insight 3) |
| body | the contract JSON, `content-type: application/json` | deserialized into the consumer's own record |

Each side owns its contract records (no shared contract package): billing's `AuctionSettledV1` has fewer members than
ArtFlow's and deserializes the same bytes **[verified]**.

## 6. Transports (M-1, M-4, M-14)

```csharp
public interface IMessageTransport : IAsyncDisposable
{
    Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken ct);
    Task Publish(Message message, CancellationToken ct);                         // returns when durable
    Task Consume(Subscription subscription, MessageHandler handler, CancellationToken ct);   // handler → Ack | Retry(after)
}
```

| | in-memory | PostgreSQL bus | NATS JetStream | RabbitMQ |
|---|---|---|---|---|
| package | `Tarol.Messaging` | `Tarol.Messaging.Npgsql` | `Tarol.Messaging.Nats` | `Tarol.Messaging.RabbitMq` |
| topology | channel per subscription | `bus.messages` + one `deliveries` row per subscription, `SKIP LOCKED` | stream per source (`artflow.>`), durable pull consumer per subscription | topic exchange per source, quorum queue per subscription |
| durable publish | — | the insert's commit | stream ack | publisher confirm |
| retry after a delay | timer | `due_at` | `Nak(delay)` | ack + copy into `<queue>.retry` with a TTL that dead-letters back |
| broker dedup | — | `(id, type, version)` | `Nats-Msg-Id` window | none (consumer key) |
| who may publish under a prefix | — | role + row-level security; `session_user` stamped | subject permissions per user | exchange write permission; `user_id` validated |
| forbidden publish | — | `42501` at once | **a timeout** (no ack) | `ACCESS_REFUSED` at once |
| unroutable publish | dropped | stored, no delivery | stored | returned (`mandatory`) → retried by the publisher |
| native size (one transport) | — | 5.8 MB | 10.9 MB | 7.1 MB |
| end to end p50/p99 (NOTIFY, native) | — | 9.8 / 22.3 ms (bus NOTIFY) | 7.7 / 11.9 ms | 8.9 / 14.0 ms |

All four pass the same 21 scenario checks, JIT and Native AOT, 0 IL warnings **[verified]**. NATS.Net 3.3.2 and
RabbitMQ.Client 7.2.2 are AOT-clean as used here **[verified]** (publish, not docs).

## 7. Measurements **[verified]** (4 cores, both systems + PostgreSQL 17 + the broker on one machine; [`output.txt`](../migration/probes/messaging/output.txt) §7)

The outbox wake-up dominates: with the 100 ms poll, commit → published is ~45 ms p50 / ~100 ms p99 on every transport;
with the outbox's NOTIFY (S-10, opt-in) 5–6 ms. Published → committed in billing: 2.5–3 ms on the brokers, 4 ms on the
PostgreSQL bus with its NOTIFY, 15–40 ms with its 100 ms poll. A backlog of 2,000 drained at 470–710 messages/s end to end;
native startup to the first message 39 ms (NATS), 59 ms (RabbitMQ), 142 ms (bus). Numbers per row in the migration note §4.

## 8. Testing

| Level | Tool | Covers |
|---|---|---|
| mapping | unit test of `From` / the publication method | the anti-corruption layers |
| two systems | `MessageTest`: in-memory stores, `InMemoryTransport`, drain | publish → consume → commit → publish back, retries, dead letters, re-drive **[verified]** (`selftest memory`) |
| transports | one contract suite per transport on Testcontainers (PostgreSQL, `nats:2.11 -js`, `rabbitmq:4`) | the 21 scenario checks + the permission checks |
| generator | snapshots | consumer switch, publication table, diagnostics |
| native | an AOT smoke app per package | round trip on its transport |

## 9. Tracing (tracing.md)

`ActivitySource "Tarol.Messaging"`: one `Consumer` span per delivery, `consume <contract>`, linked to the message's
`traceparent` (the publishing span, itself linked to the commit). Tags `messaging.destination.name`,
`messaging.message.id`, `messaging.consumer.group.name`, `tarol.attempt`, `tarol.verdict` **[verified]** in the Aspire
dashboard. Found: inside an ASP.NET Core request a span of an app source is dropped by OpenTelemetry's parent-based
sampler unless `Microsoft.AspNetCore` is listened to as well (the request activity exists for logging but is unsampled)
**[verified]**.

## 10. The UI push (M-3, 11E)

The old Backplane → Gateway → SignalR relay comes back last, on top of publications: a consumer subscription that
forwards messages to SignalR groups `<tenant>/<topic>`; an authenticated plain `Hub`; `ITopicAuthorizer(principal,
operation, topic)`; a source-generated hub JSON context (the old F1: with it the 12 old integration tests pass
**[verified]**); the transport fixes F7–F11 and the reconnect scenario. The old backplane shape itself (`Publish(topic,
bytes)`, `Start(Func<topic, bytes, Task>)`) is at most once by construction: no answer per message, so no ack, retry or
dead letter **[verified]** (a failing handler loses its message and delays the next by the back-off; two nodes compete
instead of fanning out; an ending stream spins 8.6 million times a second). The port of §6 is that shape plus an
envelope, a durable subscription and an answer.

## 11. What Messaging owns, and what it does not

Owns: the envelope, the transport port and its four implementations, consumers (attribute, generated switch, host,
trust rules, dead letters, re-drive), publications and effects (attribute, generated table and mapping), `MessageTest`,
the UI push. Does not own: the pipeline and `Delivery.Classify` (Changes), the outbox and its worker (Persistence,
Sagas), the keys and their purge (Persistence), authorization of changes (Security).

## New insights

1. **A consumer is a webhook without HTTP, and a publication is a reaction without a change.** Both reuse what exists:
   the pipeline, the key, the outbox, the worker, the classification.
2. **A key scoped by contract is shared by every consumer of it.** The second consumer silently replays the first.
3. **The broker's dedup id and the message id are different things.** One event published as v1 and v2 is one message
   for the consumer's key but two for the broker's dedup.
4. **A duplicate is a success.** NATS.Net's `EnsureSuccess` treats it as an error; taken literally, a crash-redelivered
   publication retries into the dead letters.
5. **Who may publish is the transport's job, what to believe is the consumer's.** The prefix is enforced by permissions;
   tenant and actor in an envelope are claims.
6. **"On behalf of" is a System-only idea in the Kernel**: a consumer is the system acting for another system's user.
7. **An at-most-once port cannot become at-least-once by its implementations**: the answer per message must be in the
   port's signature.

## Decisions taken (owner, 2026-11-15; migration/messaging.md §9)

M-1 port + PostgreSQL + broker; M-2 probe NATS and RabbitMQ; M-3 UI push as the last PR; M-4 both brokers as packages;
M-5 CloudEvents binary mode; M-6 System on behalf of the envelope's actor; M-7 `[Publishes<TEvent>]` mapping methods;
M-8 the key table is the inbox; M-9 dead letters in the consumer's table; M-10 publications in the same outbox, the
reaction worker publishes; M-11 five PRs 11A–11E; M-12 key `consumer:<name>`; M-13 prefix = source, tenant allow-list;
M-14 `Tarol.Messaging.Npgsql`; M-15 the sample in `samples/Sagas`.

## Open questions

* Retention of `bus.messages` (rows without deliveries) and of `messages_dead`: step 15 (operations), with metrics on
  subscription lag and dead letters. Designed in step 15: [operations.md](operations.md) (O-5: `messages_dead` kept, optional `tarol.purge-dead`; O-1: `tarol.messages.*`; O-4: versioned bus steps).
* Partitioned order per key at higher concurrency (NATS subject partitions, RabbitMQ consistent-hash or single active
  consumer): not needed by a scenario; Conflict retries cover order across streams.
* Schema registries or published contract documents (AsyncAPI): later, if asked.
