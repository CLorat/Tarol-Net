# Messaging: evaluation (step 11 design)

Evaluation of [messaging.md](../architecture/messaging.md): inventory of the old Messaging code, candidates for the
transport, the envelope, consumers, publications, the inbox, dead letters and the outbox rows, measurements against
PostgreSQL 17, NATS JetStream 2.11 and RabbitMQ 4, the owner's answers, and what PRs 11A–11E must do. **Design only**: this
PR changes no package code. Probes and output: [`probes/messaging/`](probes/messaging/),
[`output.txt`](probes/messaging/output.txt), the inventory [`inventory.md`](probes/messaging/inventory.md). Labels as in
the [conventions](../architecture/conventions.md). Source of the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old Messaging code is a SignalR topic relay, at most once by construction** **[verified]**: 341 lines in 12
  projects (+ a test project under `src/`, an empty generator); no envelope, no ack, no security, connected to nothing.
  On SDK 11 its 12 integration tests still fail (F1) and pass once a source-generated hub JSON context is added, which
  refutes F2 **[verified]**. 21 of 23 review findings confirmed, F2 refuted, F4 changed (no NU1903 at 11.0.0); two new.
  Its layering is evaluated as transport candidate T-D (owner's request) and returns as the UI push (M-3, 11E).
* **Decided model**: a consumer is a change entry `[Consumes(contract, version)]` (like `[Webhook]`), run through the same
  pipeline with the key `(tenant, consumer:<name>, message id)` as the inbox (M-8, M-12); a publication is an outbox row
  `publish:<contract>@<version>` written in the commit and published by the reaction worker (M-7, M-10); effects (S-2) are
  `effect:<name>` rows of the same outbox; the envelope is CloudEvents in binary mode (M-5); the consumer runs as
  `System("Consumers.<name>")` on behalf of the envelope's actor (M-6); the contract prefix is the source, enforced by the
  transport, tenants allow-listed per source (M-13); dead letters in the consumer's table (M-9).
* **Transports** (M-1, M-4, M-14): a port with four implementations, all passing the same 21 scenario checks and the
  permission checks, JIT and Native AOT, 0 IL warnings **[verified]**: in-memory, the PostgreSQL bus
  (`Tarol.Messaging.Npgsql`), NATS JetStream (`Tarol.Messaging.Nats`), RabbitMQ (`Tarol.Messaging.RabbitMq`).
* **Measured** (4 cores, everything on one machine): end to end commit → published → committed in the other system
  p50/p99 7.7/11.9 ms (NATS), 8.9/14.0 ms (RabbitMQ), 9.8/22.3 ms (PostgreSQL bus) with NOTIFY on both sides, about
  50 ms p50 with the outbox's 100 ms poll; backlog 470–710 messages/s; native startup to the first message 39–142 ms; native size 5.8 MB (PostgreSQL bus) / 7.1 MB (+RabbitMQ.Client) / 10.9 MB (+NATS.Net) **[verified]**.
* **Found** (§5): a key scoped by contract makes the second consumer of a contract a silent replay; the broker's dedup id
  is not the message id (JetStream drops v2 of a dual publish); NATS.Net's `EnsureSuccess` treats a duplicate as an
  error; the Kernel's `OnBehalfOf` is System-only; `UPDATE … RETURNING` has no order (a native run handled a stream out of
  order on the bus); a NATS publish to a forbidden subject is a timeout; an ASP.NET Core request's unsampled activity
  makes OpenTelemetry drop the app's spans; an exception in a handler must never reach the transport's loop.
* **Aspire** (D-15): PostgreSQL (one database per system) + NATS + ArtFlow api + worker + billing: 5 of 5 round trips,
  every span linked, JIT projects and Native AOT binaries **[verified]**.
* **Plan** (M-11, M-14, M-15): five new packages, five PRs 11A–11E; rows 12–19 unchanged but for notes (§8).

## 1. Inventory of the old code **[verified]** (`CLorat/Tarol@6a62f81`)

Condensed; the full inventory (every project with its public types and file:line, every test project with its run, every
review finding with its status, ROADMAP Phase 5) is [`probes/messaging/inventory.md`](probes/messaging/inventory.md).

| Layer (old project) | Lines | What it is | Verdict |
|---|---:|---|---|
| Protocol (+ Json) | 52 | `IMessageProtocol`, `JsonAotProtocol<TContext>` (AOT-safe lookup, value-type cast bug F20) | idea kept: the app's JSON context reads the contract record |
| Transport (+ SignalR), Shared.SignalR | 136 | client side: `ITransport` over a `HubConnection` wrapper with no production adapter (F7–F11, F17–F19) | 11E, rebuilt |
| Gateway (+ SignalR) | 39 | `IMessageGateway.BroadcastAsync`; hub `Hub<ISignalRMessageClient>` without `[Authorize]` (F3); the gateway does not implement its port (F6) | 11E, rebuilt |
| Backplane (+ InMemory) | 104 | `IMessageBackplane { Publish(topic, bytes), Start(Func<topic, bytes, Task>) }`, a `BackgroundService` runner with back-off (F12, F13, F16), an unbounded channel (F14) | candidate T-D (§2.1) |
| Messaging, Security, Client, Generators | 16 | `[Message(topic)]` and `ITopicAuthorizer(topic)`, both unused; an empty class; an empty generator | not migrated |

Tests on SDK 11.0.100 **[verified]**: 38 tests; 12 of 12 integration tests fail with `Reflection-based serialization has
been disabled` from `JsonHubProtocol.WriteArguments` (F1); with `AddJsonProtocol` + a context for `string`/`byte[]` on both
sides **12 of 12 pass** (so a `CancellationToken` on the hub methods binds, F2 refuted); the runner test fails in 17 of 18 runs (three runs of 6);
`Messaging.UnitTests` has 0 tests. Restore at 11.0.0 reports no NU1903 (F4's vulnerability is gone, the unused reference
stays) and a new NU1510 for `Hosting.Abstractions` (.NET 11 base framework).

## 2. Candidates, as ArtFlow and billing code

### 2.1 Transport (question 1; M-1, M-4, M-14)

| # | Candidate | Message exchange | Result |
|---|---|---|---|
| T-1 | **PostgreSQL bus**: a `bus` schema both systems reach; `messages` (dedup by (id, type, version)) + one `deliveries` row per subscription; claim `FOR UPDATE SKIP LOCKED`; NOTIFY opt-in | one statement: `WITH m AS (INSERT … ON CONFLICT DO NOTHING RETURNING …) INSERT INTO deliveries SELECT … JOIN subscriptions`; the database stamps `session_user` | all checks **[verified]**; roles + row-level security bind a role to its prefix |
| T-2 | **NATS JetStream**: a stream per source (`artflow.>`), a durable pull consumer per subscription | `Nats-Msg-Id` = (event, contract, version); `Nak(delay)` for a retry; `AckWait` = lease | all checks **[verified]**; subject permissions per user |
| T-3 | **RabbitMQ**: a topic exchange per source, a quorum queue per subscription, publisher confirms | retry = ack + copy into `<queue>.retry` with a TTL that dead-letters back | all checks **[verified]**; exchange permissions, `user_id` validated |
| T-4 | read the publisher's feed (`IEventFeed.ReadAll` from the other system's database, own checkpoint) | none: the consumer reads the publisher's events | not probed: the consumer reads the publisher's domain events (no outward anti-corruption layer) and needs read access to its store; the projection mechanism of step 12 **[unverified]** |
| T-D | **the old layering** (owner's request): `IMessageBackplane` + runner as the port, Gateway for the UI | `(topic, bytes)`; `Start(Func<topic, bytes, Task>)` | as is: at most once **[verified]** (§3.4). Evolved by an envelope, a durable subscription per consumer and an answer per message it **is** T-A's port below; Gateway/SignalR → 11E |

The port all four implement (T-A):

```csharp
public interface IMessageTransport : IAsyncDisposable
{
    Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken ct);
    Task Publish(Message message, CancellationToken ct);                                        // durable when it returns
    Task Consume(Subscription subscription, MessageHandler handler, CancellationToken ct);
}
public delegate ValueTask<Handled> MessageHandler(Incoming incoming, CancellationToken ct);       // Ack | Retry(after)
public sealed record Incoming(Message? Message, int Attempt, string? AuthenticatedAs, ReadOnlyMemory<byte> Raw, string Topic);
```

Old shape → new shape: `Publish(topic, bytes)` → `Publish(Message)`; `Start(Func<topic, bytes, Task>)` →
`Consume(Subscription, handler → Handled)`; `IMessageProtocol` → `MessageScope.Payload<T>` through the app's context;
`IMessageGateway` → 11E.

### 2.2 Envelope (M-5)

| # | Candidate | On the wire |
|---|---|---|
| E-A | **CloudEvents 1.0, binary mode** (decided) | headers `ce-specversion`, `ce-id` (event id), `ce-source`, `ce-type` (contract), `ce-contractversion`, `ce-time`, `ce-subject` (stream id), `ce-tenant`, `ce-correlation`, `ce-causation`, `ce-actor`, `traceparent`, `content-type: application/json`; body = the contract JSON |
| E-B | own JSON envelope (the webhook shape) | body `{id, type, version, source, tenant, correlation, causation, actor, traceparent, data}` |

### 2.3 Consumers (question 2)

**K-A (decided)**, a factory on the change, user code:

```csharp
public sealed partial record RecordPayment(Id<Auction> Auction, string Invoice, decimal Amount) : IChange<Auction>
{
    [Consumes("billing.invoice-paid", 1)]
    public static RecordPayment From(Contracts.InvoicePaidV1 m) => new((Id<Auction>)m.Auction, m.Invoice.ToString("N"), m.Amount);

    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
}
```

Generated (as probed, `App/Generated.cs`):

```csharp
public static class GeneratedConsumers
{
    private static readonly string[] Topics_RecordPayment = ["billing.invoice-paid"];
    public static ConsumerSet Set { get; } = new([new ConsumerInfo("RecordPayment", Topics_RecordPayment)], Run);

    private static async ValueTask<ConsumerRun> Run(string consumer, ChangePipeline pipeline, Message message, MessageScope scope, CancellationToken ct)
    {
        switch (consumer, message.Type, message.Version)
        {
            case ("RecordPayment", "billing.invoice-paid", 1):
            {
                var payload = scope.Payload<Contracts.InvoicePaidV1>(message.Data);
                if (payload is null)
                    return ConsumerRun.Not(Mapped.Malformed);
                return new(Mapped.Ran, await pipeline.Run(RecordPayment.From(payload), scope.Operation, scope.Options, ct));
            }
            case ("RecordPayment", "billing.invoice-paid", _):
                return ConsumerRun.Not(Mapped.UnknownVersion);
            default:
                return ConsumerRun.Not(Mapped.UnknownContract);
        }
    }
}
```

**K-B** (not chosen, not probed): handler classes `IMessageHandler<InvoicePaidV1>.Handle(m, context, ct)` resolved from DI
(the MassTransit shape). A handler can do anything, so it is not effect-once by construction, needs no change and no
key, and C-5/C-13 already chose the factory shape for every entry.

### 2.4 Publications (question 4; M-7)

**PB-A (decided)**, a mapping method; generated (`App/Generated.cs`): names `publish:artflow.auction-settled@1`, a table
over static arrays (`Auction.Settled => OnSettled`), and the map (name, recorded) → `JsonSerializer.SerializeToUtf8Bytes(
Publications.SettledV1((Auction.Settled)r.Event.Event, r), AppJson.Default.AuctionSettledV1)`.

```csharp
[Publishes<Auction.Settled>("artflow.auction-settled", 1)]
public static Contracts.AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Buyer, e.Price, "EUR");
```

**PB-B**: `[Published("artflow.auction-settled", 1)]` on `Auction.Settled`; the event's JSON is the body. **PB-C**:
`builder.Publish<Auction.Settled, AuctionSettledV1>("artflow.auction-settled", 1, map)` at runtime. Neither probed: PB-B
makes the domain event the contract (a rename or a new member is a breaking change for every consumer); PB-C needs the
outbox table and the JSON metadata wired by hand and checked only at startup.

### 2.5 Inbox and dead letters (question 3; M-8, M-9)

| # | Candidate | Result |
|---|---|---|
| I-A | **the key table** (decided): `(tenant, consumer:<name>, message id)` stored with the commit | a redelivery after a lost ack replays without writing **[verified]**; a message that commits nothing stores no key and runs again on redelivery, deciding again on the current state (harmless by construction) |
| I-B | an `inbox` table (one row per handled message, no-ops and burials included) | not built: one more insert per message, a second retention; nothing in the scenarios needs receipts |
| DL-1 | **the consumer's `messages_dead`** (decided): headers, raw bytes, reason, attempts; `Redrive(id)` runs it again in process | **[verified]** on all four transports, incl. a re-drive that commits after the auction was settled |
| DL-2 | the broker's dead-letter queue (RabbitMQ DLX, NATS Term + advisory) | not built: three behaviours and three re-drive paths |

### 2.6 Outbox rows (question 4; M-10)

| # | Candidate | Result |
|---|---|---|
| O-A | **the same `outbox` table** (decided): `reaction = 'publish:artflow.auction-settled@1'`; the reaction worker runs the row | **[verified]**: the probe registers publications as `ReactionSet` runners, so claim, head per (stream, publication), backoff, lease, `outbox_dead` and `ActivityLink`s work unchanged; version order per stream with two publisher loops |
| O-B | a `publications` table with the mapped bytes written in the commit | not built: a serialization and the bytes in every commit, a second claim/head/dead-letter path, and a mapping bug would fail the user's commit |

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/messaging/output.txt) §1–§4: JIT and Native AOT, every transport)

### 3.1 Consumers through the pipeline (`selftest`, 21 checks per transport)

| Scenario | Result (identical on in-memory, PostgreSQL, PostgreSQL + NOTIFY, NATS, RabbitMQ) |
|---|---|
| valid message | both consumers of `artflow.auction-settled` commit, one subscription each |
| context | rule `Service(source)`: `Service:artflow`, envelope tenant and correlation, causation = event id of `Settled`; rule System-on-behalf (M-6): `System:Consumers.IssueInvoice (for alice)` → next hop `System:Reactions.RemindBuyer (for alice)` |
| redelivery after a lost ack | attempt 1 committed, attempt 2 replayed, one invoice |
| publisher crash after publish | published twice, one invoice: NATS reports 1 duplicate (`Nats-Msg-Id`), the bus drops it (`ON CONFLICT`), RabbitMQ delivers it and the key replays it |
| unknown contract | not delivered (subscriptions filter by contract); dropped when it arrives anyway; RabbitMQ returns the unroutable publish (`312 NO_ROUTE`) |
| unknown version (v3) | buried by both consumers, `Messaging.UnknownVersion` |
| invalid mapping (price 0) | `IssueInvoice` buried at attempt 1 (`Invalid Amount Range`), `RecordSale` commits |
| Conflict (payment before settlement) | `Auction.NotSettled` → retried → committed after the settlement |
| NotFound | retried 4 times (probe policy), then dead-lettered |
| poison (data not JSON) | buried at attempt 1, kept with its bytes |
| the loop back | billing pays → `billing.invoice-paid` → ArtFlow records the payment as billing, with billing's correlation |
| effect | provider down once, crash after sending once → 3 calls, 1 key, 1 mail |
| forged tenant / source / actor | `TenantNotAllowed` / `SourceMismatch` buried; a forged `ce-actor` is never the actor (trusting it would record `User:admin`) |
| key scope `consumer:<contract>` | the second consumer replays the first's outcome and never runs (M-12) |
| dual publish v1 + v2 | one invoice (same message id); the v1-only consumer buries v2 as `UnknownVersion` |
| re-drive | a dead-lettered Conflict, re-driven after the fix, commits |
| order per stream | publication order = version order; consumed in order with concurrency 1; with concurrency 4: 0 (in-memory, sequential by construction) to 11 inversions in 50 |

### 3.2 Who may publish under a prefix (`security`)

| Transport | Check | Result |
|---|---|---|
| NATS | user `billing` publishes on `artflow.auction-settled` | refused by subject permissions; the client sees **a timeout**, not an error |
| NATS | `Nats-Msg-Id` = event id for v1 and v2 | v2 dropped as a duplicate; (event, contract, version) keeps both |
| RabbitMQ | user `billing` publishes to exchange `artflow` | `403 ACCESS_REFUSED` (channel closed) |
| RabbitMQ | `billing` sets `user_id: artflow` | `406 PRECONDITION_FAILED` |
| PostgreSQL | role `billing` inserts `artflow.*` | `42501` row-level security |
| PostgreSQL | without the policy | the bus stamps `billing`, the consumer buries `SourceMismatch` |

### 3.3 Native AOT

One app with all transports: 0 IL warnings, 26.1 MB (with ASP.NET Core for the Aspire roles); every check of §3.1–§3.4
passes natively (§4 of the output). One transport per binary: 5.8 MB (PostgreSQL), 10.9 MB (NATS.Net 3.3.2; referencing
only `NATS.Client.JetStream` gives the same size), 7.1 MB (RabbitMQ.Client 7.2.2), all 0 IL warnings.

### 3.4 Candidate T-D, the old backplane shape (`legacy`, copied from `6a62f81`)

| Check | Result |
|---|---|
| a handler throws once | the message is lost; the next arrives after the back-off (1.6 s); `Start` called twice (F13) |
| two runners on one in-memory backplane | 50 / 50 of 100: competing, no fan-out (N1) |
| a stream that ends | 8.6 million restarts in 1 s (F12) |

## 4. Measurements **[verified]** ([`output.txt`](probes/messaging/output.txt) §5–§9; 4 cores, SDK 11.0.100, PostgreSQL 17.12, NATS 2.11, RabbitMQ 4.4, all in local Docker; both systems in one process, one clock)

### 4.1 Latency and throughput (§7: 50 settlements/s for 10 s after a 2 s warm-up; then a backlog of 2,000 drained by 4 publisher loops and consumer concurrency 8)

Commit of `Auction.Settled` (ArtFlow) → published → `IssueInvoice` committed (billing), Native AOT (JIT within ±1 ms
except where noted):

| Transport | Outbox wake-up | commit → published p50 / p99 | published → committed p50 / p99 | end to end p50 / p99 | backlog consumed/s |
|---|---|---:|---:|---:|---:|
| PostgreSQL bus, poll 100 ms | poll 100 ms | 47.2 / 101.5 ms | 14.9 / 58.6 ms | 66.7 / 138.1 ms | 571 |
| PostgreSQL bus, poll 100 ms | NOTIFY | 5.6 / 9.8 ms | 38.1 / 94.0 ms | 44.7 / 99.1 ms | 611 |
| PostgreSQL bus + NOTIFY | poll 100 ms | 47.8 / 100.8 ms | 4.3 / 9.0 ms | 51.9 / 106.8 ms | 471 |
| PostgreSQL bus + NOTIFY | NOTIFY | 5.8 / 17.6 ms | 3.9 / 7.4 ms | **9.8 / 22.3 ms** | 474 |
| NATS JetStream | poll 100 ms | 45.9 / 100.8 ms | 2.8 / 6.1 ms | 49.2 / 103.8 ms | 714 |
| NATS JetStream | NOTIFY | 5.0 / 8.6 ms | 2.7 / 4.8 ms | **7.7 / 11.9 ms** | 703 |
| RabbitMQ | poll 100 ms | 47.5 / 102.2 ms | 2.8 / 6.1 ms | 50.5 / 106.1 ms | 601 |
| RabbitMQ | NOTIFY | 6.1 / 9.7 ms | 2.8 / 5.0 ms | **8.9 / 14.0 ms** | 590 |

* The outbox's wake-up dominates: the 100 ms poll costs ~45 ms p50 and ~100 ms p99 on every transport; NOTIFY (S-10,
  opt-in, it costs writers commits/s) brings commit → published to 5–6 ms.
* On the brokers, delivery + the consumer's commit take 2.5–3 ms; the bus with its own NOTIFY 4 ms, without it its poll.
* Throughput is bound by the two systems' commits on one 4-core machine (both stores, the broker and the load generator
  share it): 470–720 messages/s; NATS highest, the bus with NOTIFY lowest (a NOTIFY per publish serializes commits).
* The interactive run before the round 2 question (JIT) gave the same picture (NATS 7.7/22.2 ms, RabbitMQ 8.9/12.8 ms,
  702 and 491 messages/s); the numbers in M-4's question are from it.

### 4.2 Startup: process start → connected, subscription declared, first message received (§6, median of 5)

| Transport | JIT (Release) | Native AOT |
|---|---:|---:|
| PostgreSQL bus | 388 ms | 142 ms |
| NATS JetStream | 411 ms | 39 ms |
| RabbitMQ | 254 ms | 59 ms |

### 4.3 Native AOT size (§4, §5)

One transport per binary, everything else equal (Tarol + Npgsql + the probe's messaging library): PostgreSQL bus
5,829,992 bytes; NATS 10,879,776 (+5.0 MB); RabbitMQ 7,147,224 (+1.3 MB). All 0 IL warnings.

### 4.4 Aspire (§8, §9)

AppHost 13.6.5: PostgreSQL (`postgres:18.3`, databases `artflow` and `billing`), NATS (`nats:2.14`, JetStream), ArtFlow's
api and worker and billing as three resources of the probe app. Five auctions through `POST /auctions`: 5 settled → 5
invoices, 5 sales, 5 reminders, 5 paid → 5 payments recorded in ArtFlow; every event with a `traceparent`; actors as
decided (M-6): invoice `System Consumers.IssueInvoice for alice`, the next hop `System Reactions.RemindBuyer for alice`,
payment `System Consumers.RecordPayment for psp`; both outboxes empty; the dashboard lists `api`, `worker` and
`billing` with the publish, consume and reaction spans, each linked. The same with the Native AOT binary as all three
resources.

## 5. Found while probing

1. **A key scoped by contract is shared by every consumer of that contract** **[verified]**: with
   `(tenant, consumer:artflow.auction-settled, id)` the second consumer (`RecordSale`) found the first one's key, returned
   its outcome as a replay and never ran. The plan's wording is replaced by `consumer:<name>` (M-12).
2. **The broker's dedup id is not the message id** **[verified]**: JetStream deduplicates `Nats-Msg-Id` per *stream*, not
   per subject; with the event id alone, v2 of a dual publish was dropped. Dedup id = (event, contract, version); the
   message id (`ce-id`) stays the event id so a consumer of both versions commits once. The PostgreSQL bus keys its
   messages the same way.
3. **NATS.Net's `EnsureSuccess()` throws `NatsJSDuplicateMessageException` for a duplicate** (its XML docs say so; run:
   the republish after a simulated crash timed out the scenario): a publisher must check `ack.Error` and count
   `ack.Duplicate` as success, or a redelivered publication retries until `outbox_dead`.
4. **`Actor` refuses `OnBehalfOf` for anything but a System actor** (`Actor.cs:36`, "Only a System actor acts on behalf of
   someone") **[verified]**; and `OperationContext.AsSystem` drops it for a Service actor **[verified by reading]**
   (`OperationContext.cs:55-63`). M-6 uses the System shape, so no Kernel change.
5. **An exception escaping a consumer's handler killed the transport's consume loop** on the first run **[verified]**
   (the old F13 again, in the new code): the consumer host must turn every exception but cancellation into a Retry.
6. **`UPDATE … FROM (… ORDER BY … LIMIT … SKIP LOCKED) … RETURNING` has no order.** The Native AOT run on the PostgreSQL
   bus handled one stream's notes out of order with concurrency 1 ([`output-before-claim-order-fix.txt`](probes/messaging/output-before-claim-order-fix.txt)
   §4); the plan is a hash join probing a **sequential scan of `deliveries`**, so `RETURNING` follows heap order
   **[verified]** (`EXPLAIN`, `Brokers/claim-order.sql`); a synthetic reproduction (reused free space) did not trigger it
   in 20 batches **[verified]**. Fixed by ordering after `RETURNING` (`WITH claimed AS (…) SELECT … ORDER BY id`); the
   outbox claim is not affected (one head per stream and reaction). SQL lesson for 11C.
7. **A NATS publish to a forbidden subject is a timeout**, not an error **[verified]**: the server drops it with a
   permission violation on the connection and JetStream never acks. Misconfigured publishers retry with timeouts until
   `outbox_dead`; 11D logs the connection's permission errors.
8. **RabbitMQ returns an unroutable message** (`mandatory`, `312 NO_ROUTE`) **[verified]**: a contract nobody subscribed
   throws at the publisher (retried, then `outbox_dead`). NATS and the bus store it with no delivery. 11D publishes
   unroutable messages as delivered (a publisher does not know its consumers) and logs them.
9. **Inside an ASP.NET Core request, an app span is dropped** unless `Microsoft.AspNetCore` is a traced source too: the
   request activity exists (logging) but is unsampled, so the parent-based sampler drops its children **[verified]** (the
   api resource exported nothing until the source was added; the stored `traceparent` came from the unsampled activity).
   Guidance for the sample and tracing.md.
10. **NATS.Net adds 5 MB to a native binary**, also with only `NATS.Client.JetStream` **[verified]**; RabbitMQ.Client 1.3 MB.
11. **Aspire resource names are global**: a database `billing` and a project `billing` collide at startup **[verified]**;
    `WithHttpHealthCheck` needs a declared endpoint **[verified]**.
12. **A caller that drops a version conflict loses the change** (C-7): the probe's stand-in payment provider ignored
    `PayInvoice`'s result and lost 1 of 5 payments when `RemindBuyer` committed on the same invoice at the same moment
    **[verified]**; retried like a webhook sender after a 503, 5 of 5. The sample's webhook path already answers 503.
13. **RabbitMQ 4 sometimes fails its first boot** in a fresh container (Erlang prelaunch) **[verified]** once in six
    starts; the probe restarts it; Testcontainers' wait strategy must tolerate it (11D).

## 6. Generator and diagnostics (11A, 11B; numbers final in the PRs)

The messaging generator ships inside `Tarol.Messaging` (`TarolGenerator="true"`, D-10), next to the Changes generator,
like the endpoint generator of 10B. It emits per assembly `GeneratedConsumers.Set` (consumers, topics, switch) and
`GeneratedPublications.Set` (names, table over static arrays, map through the user's JSON context).

| Id | Severity | Rule |
|---|---|---|
| TMSG001 | Error | `[Consumes]` on a method that is not `static`, does not return its containing change, or whose parameters are not `(TPayload)` or `(TPayload, Message)` |
| TMSG002 | Error | two factories for one (consumer, contract, version) |
| TMSG003 | Error | a contract name that is not `<system>.<name>` (lowercase letters, digits, `-`, one dot after the system) or a version < 1 |
| TMSG004 | Error | `[Publishes<T>]` on a method that is not `static`, returns no class, or whose parameters are not `(T)` or `(T, RecordedEvent)` |
| TMSG005 | Error | two publications of one (contract, version) in an assembly; Warning when an assembly publishes under two prefixes |
| TMSG006 | Warning | a payload or contract record no `JsonSerializerContext` of the compilation lists in `[JsonSerializable]` (the attributes are visible; the generated metadata is not) |
| TMSG007 | Error | two consumers with one name in an assembly (two changes, or a pinned `Name`) |

The Validations analyzer extends the TVAL006 entry-factory exemption to `[Consumes]` (as 10B did for `[Webhook]`) and to
`[Publishes<T>]` methods that build value objects of a contract record.

## 7. Decided API (sketch for 11A–11E; names final in the PRs)

```csharp
// both systems
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .WithReactions(GeneratedReactions.Set)
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()                                       // also publishes: the outbox rows of GeneratedPublications.Set
    .AddMessaging(m => m
        .Publish(GeneratedPublications.Set)                    // 11B
        .Consume(GeneratedConsumers.Set, c => c.Concurrency = 1)
        .Trust("billing", t => t.Tenants(tenantA))             // M-13; or t.AnyTenant()
        .UseNats(builder.Configuration.GetConnectionString("nats")!));   // or .UseRabbitMq(uri), .UseNpgsqlBus(connectionString)

// tests
var test = MessageTest.Between(artflow, billing);              // in-memory stores + InMemoryTransport, a fake clock
await test.Drain();                                           // outbox rows, deliveries, retries, until nothing is due
```

| Type | Package | Role |
|---|---|---|
| `Message`, `IMessageTransport`, `Subscription`, `Incoming`, `Handled` | Tarol.Messaging | envelope (CloudEvents binary) and port |
| `ConsumesAttribute`, `ConsumerSet`, `MessageScope`, `ConsumerHost` | Tarol.Messaging | consumers (switch generated) |
| `PublishesAttribute<TEvent>`, `PublicationSet`, effects | Tarol.Messaging | publications and effects on the outbox |
| `IMessageDeadLetters` (+ in-memory), `Redrive` | Tarol.Messaging | DL-1 |
| `InMemoryTransport`, `MessageTest` | Tarol.Messaging | tests |
| `NpgsqlBus`, `NpgsqlMessageDeadLetters`, bus schema, RLS guide | Tarol.Messaging.Npgsql | 11C |
| `NatsTransport` / `RabbitMqTransport` | Tarol.Messaging.Nats / .RabbitMq | 11D |
| hub, gateway, `ITopicAuthorizer(principal, op, topic)`, client | Tarol.Messaging.SignalR | 11E |

## 8. Plan impact (question 9)

* **Row 11** becomes five PRs (M-11, M-14): 11A `Tarol.Messaging` (consumers), 11B publications and effects, 11C
  `Tarol.Messaging.Npgsql`, 11D `Tarol.Messaging.Nats` + `Tarol.Messaging.RabbitMq` + the sample (M-15), 11E
  `Tarol.Messaging.SignalR` (UI push, M-3). Five new packages; `Tarol.Messaging` depends on `Tarol.Sagas`.
* **Row 12** (queries): the UI push of 11E can carry read-model changes later; T-4 (reading another system's feed) is
  the projection mechanism, not a transport.
* **Row 15** (operations): metrics and health checks for subscriptions (lag, attempts, `messages_dead`), retention of
  `bus.messages`, an admin entry that also lists and re-drives `messages_dead`.
* **Row 17** (release, D-8): the meta-package question now has `Tarol.Messaging` at the top of the graph.
* **Row 18** (samples): ArtFlow + billing exist from 11D on; step 18 rebuilds on them.
* Nothing moves between steps 12–19.

## 9. Owner answers (2026-11-15)

Rounds: 1 (scope, before probing) M-1 … M-3; 2 (after the measurements) M-4, M-5, M-8, M-9; 3 M-6, M-7, M-10, M-11;
4 M-12 … M-15; implementation (11B) M-16. Not asked because already decided: the delivery table (C-9, S-7), unknown version → bury by default (R-10),
the backoff and attempts (S-13), dead letters for reactions (S-5), NOTIFY opt-in (S-10), no principal for internal
entries (R-6), the context as a parameter (C-11), the key stored with successes only (C-8), purge after 7 days (P-11).

| # | Question | Answer |
|---|---|---|
| M-1 | Scope: which transports does step 11 build? | **A transport port, an in-database transport over PostgreSQL, and one broker** chosen after probing (recommended). |
| M-2 | Which brokers to probe? | **NATS JetStream and RabbitMQ**, both; choose in a later round from the measurements (recommended). |
| M-3 | The old SignalR Gateway (the "user backend" push)? | **UI push as its own PR in step 11**, after the system-to-system PRs, rebuilt on top of them with an authenticated hub and tenant-scoped topics (recommended). Owner, with the session prompt: "Look at Tarol/Main there IS messaging for User Backend. We should utilize this as base as it is already a bit structured and flexible; this should be one option evaluated." The old layering (Protocol / Transport / Backplane / Gateway) is evaluated as a transport candidate (§2). |
| M-4 | Which broker gets its own package? (NATS: end to end p50/p99 7.7/22 ms, 702 consumed/s, +5.0 MB native, delayed retry and dedup native; RabbitMQ: 8.9/13 ms, 491/s, +1.3 MB, retry queue + TTL, `user_id` validated) | **Both, as two packages**: `Tarol.Messaging.Nats` and `Tarol.Messaging.RabbitMq` (against the recommendation, NATS only). |
| M-5 | The envelope on the wire? | **CloudEvents 1.0, binary content mode**: attributes as headers, the body is the contract JSON (recommended). |
| M-8 | Inbox: the key table only, or a table of received messages too? | **The idempotency-key table only** (C-8, P-11), key (tenant, `consumer:<name>`, message id) (recommended). |
| M-9 | Where do buried messages go? | **The consumer's own table** (`messages_dead`, DL-1), re-driven in process by id, the same on every transport (recommended). |
| M-6 | Which actor does a consumed message run as? (the Kernel allows `OnBehalfOf` only on a System actor) | **`Actor.System("Consumers.<name>", onBehalfOf: the envelope's actor id)`**, like a reaction; later hops keep it (against the recommendation `Service(source)`). Recorded, never used for authorization (R-6). |
| M-7 | How is a publication declared? | **`[Publishes<TEvent>(contract, version)]` on a static mapping method** (PB-A), the outward anti-corruption layer; generated table, names and serialization (recommended). |
| M-10 | Where are the publication rows? | **The same outbox**, rows named `publish:<contract>@<version>` (and `effect:<name>` for S-2 effects); the reaction worker's claim, head order, backoff, lease, dead letters and links are reused (O-A, recommended). |
| M-11 | Packages and PRs? | **The recommended split, five PRs**: 11A `Tarol.Messaging` (envelope, port, consumers, in-memory transport, dead letters, `MessageTest`, generator), 11B publications and effects, 11C the PostgreSQL bus, 11D NATS + RabbitMQ (M-4) with the Aspire sample, 11E the SignalR UI push (M-3). The option's label said "4 packages" but lists five; the place of the PostgreSQL bus is confirmed in M-14. |
| M-12 | Key scope of a consumer (the plan wrote `consumer:<contract>`; the probe shows the second consumer of a contract replaying the first one's outcome) | **`consumer:<consumer name>`** (the change's type name, pinned with `Name` before a rename, like S-14) (recommended). |
| M-13 | What may a consumer take from an envelope? | **The contract's prefix is the source**, enforced by the transport's permissions; the consumer buries a disagreeing `ce-source`, an unknown source, and a tenant the source is not allowed for (allow-list per source, default none, `AnyTenant` opt-in); correlation taken, causation = message id (recommended). |
| M-14 | Where does the PostgreSQL bus ship? | **`Tarol.Messaging.Npgsql`**, a package of its own; five new packages in step 11 (recommended). |
| M-15 | The runnable sample? | **Extend `samples/Sagas` in 11D** with a Billing project and NATS (RabbitMQ as an AppHost switch) (recommended). |
| M-16 | (11B) How does an application declare an effect (S-2)? The decisions fix the rows and the key, not the declaration; effects need services. | **A handler class** `IEffect<TEvent>` with `ValueTask<Result> Run(TEvent, EffectContext, CancellationToken)`, registered with `AddMessaging(…).Effect<TEvent, THandler>(name)` and resolved from DI per row; `EffectContext.IdempotencyKey` = the event id; the result classified like a change's, an exception a retry (recommended; not an attribute + generator, not a delegate). |

## 10. Changes against steps 7–10 (announced here, made in 11A–11D)

| Where | Change | Why |
|---|---|---|
| `Tarol.Sagas` `ReactionWorker` | runs rows of other handlers too (a row handler per name prefix: reactions, `publish:`, `effect:`), activity names `publish <contract>` / `effect <name>` | M-10; the probe used `ReactionSet`'s public constructor as a stand-in |
| `Tarol.Changes` `ReactionSet` / `IOutboxReactions` | composed with publications and effects into the store's table (`OutboxRows`) | one table, one worker |
| `Tarol.Validations` analyzer | TVAL006 exemption for `[Consumes]` factories and `[Publishes<T>]` methods | C-13 |
| `IdempotencyKey` docs | consumer scope `consumer:<name>`, not `<contract>` | M-12 |
| `docs/architecture/changes.md` §5, §7, §9; `identity-context.md` §2; `tracing.md` | consumer row (actor M-6), the messaging package names, the sampling note | §5 items 4 and 9 |

## 11. What the implementation must do (in the decided order)

### PR 11A `messaging: consumers through the change pipeline` (`Tarol.Messaging` + generator)
* `Message` (CloudEvents binary: headers in, headers out, a malformed envelope is `null` → poison), the port, `Subscription`,
  `Incoming`, `Handled`, `InMemoryTransport` (fan-out per subscription, redelivery with delay on the `TimeProvider`).
* `[Consumes(contract, version)]` with `Name`; the generator's consumer switch; TMSG001–TMSG003, TMSG006, TMSG007; snapshots.
* `ConsumerHost` (`BackgroundService`): one subscription per consumer, trust rules (M-13), context (M-6), key (M-12),
  `Delivery.Classify` + `DeliveryPolicy`, every exception a Retry (§5 item 5), `ActivitySource "Tarol.Messaging"`
  (links), source-generated logs.
* `IMessageDeadLetters` + in-memory, `Redrive(id)`; `MessageTest`; `AddMessaging` on the `TarolBuilder`.
* Tests: every row of §3.1 on the in-memory transport; the skill `tarol-messaging` (consumers part); an AOT smoke app.

### PR 11B `messaging: publications and effects from the outbox`
* `[Publishes<TEvent>(contract, version)]`, effects; the generated publication set; TMSG004, TMSG005.
* The worker's row handlers (§10); message id = event id, dedup id = (event, contract, version); duplicates are success.
* Tests: version order per stream, crash after publish → one commit at the consumer, dual publish, effect keys; skill part.

### PR 11C `messaging.npgsql: the in-database bus` (`Tarol.Messaging.Npgsql`)
* The bus schema (`EnsureSchema`), publish in one statement, claim ordered after `RETURNING` (§5 item 6), NOTIFY opt-in,
  `NpgsqlMessageDeadLetters`, the role map + row-level security guide (§3.2), retention hook.
* Integration tests on Testcontainers PostgreSQL (the whole §3.1 suite + §3.2), added to the CI `integration` job.

### PR 11D `messaging: NATS JetStream and RabbitMQ transports, the two-system sample` (`Tarol.Messaging.Nats`, `.RabbitMq`)
* NATS: stream per source, durable consumers, `Nak(delay)`, dedup id, duplicate = success (§5 item 3), permission errors
  logged (§5 item 7). RabbitMQ: exchange per source, quorum queues, confirms, retry queue + TTL, `user_id`, unroutable
  handling (§5 item 8).
* One shared contract suite per transport on Testcontainers (`nats:2.11 -js`, `rabbitmq:4`; boot retry, §5 item 12),
  both added to the CI `integration` job; AOT smoke apps with their sizes.
* `samples/Sagas`: a Billing project, NATS (RabbitMQ as an AppHost switch), the loop of §2, the sampling fix (§5 item 9).

### PR 11E `messaging.signalr: the UI push` (`Tarol.Messaging.SignalR`)
* A consumer subscription → SignalR groups `<tenant>/<topic>`; plain `Hub` with `[Authorize]`; `ITopicAuthorizer(principal,
  operation, topic)`; the source-generated hub JSON context (F1); a production connection with resubscribe on reconnect and
  an immutable subscription map (F7–F11, F17–F19); the reconnect scenario of `plan-messagingIntegrationTests.prompt.md`.

## 12. Skill evaluation checklist (`tarol-messaging`, 11A–11D)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| consume another system's event as a change | `[Consumes("billing.invoice-paid", 1)]` on `static From(Payload)` | a handler class with free code; I/O in the factory; one factory for two versions |
| add v2 of a contract | a second `From` overload with `[Consumes(…, 2)]` | changing v1's record in place |
| publish an own event | `[Publishes<Auction.Settled>("artflow.auction-settled", 1)]` mapping method | publishing the domain event itself; a contract without the system prefix |
| send an e-mail once per payment | an effect with the event id as idempotency key | sending inside `Execute` or a reaction |
| a second consumer of a contract | its own change (own name, key, subscription) | sharing one consumer and branching |
| accept a partner system | `.Trust("billing", t => t.Tenants(…))` + broker permissions on its prefix | `AnyTenant` for an external partner; reading the actor from `ce-actor` as the actor |
| a message keeps failing | read `messages_dead`, fix, `Redrive(id)` | a Conflict for an already-done message (C-9 retries it into the dead letters) |
| test two systems | `MessageTest` | a broker container in unit tests |

## 13. Verification of this PR (SDK 11.0.100, 2026-11-15) **[verified]**

Docs and probes only. `dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet test --solution Tarol.slnx` with Docker:
1049 of 1049 (at the start and again at the end); `eng/aot-smoke.sh` with `TAROL_PG`: 13 apps, 0 IL warnings, all checks
passed; `dotnet format --verify-no-changes` clean; `dotnet pack -c Release` + `eng/verify-packages.sh`: OK. CI of `main` after #29 (run 85): `build`, `aot-smoke`, `integration` green.

## 14. Open items

* Retention of `bus.messages` and `messages_dead`; subscription metrics: step 15.
* Order per key at higher consumer concurrency (partitioned subjects, single active consumer): not needed yet.
* Contract documents (AsyncAPI) and a schema check in CI: later, if asked.
* RabbitMQ topic permissions per routing key (finer than an exchange per source): 11D documents both.

## 15. Implementation: PR 11A (2026-11-15)

`messaging: consumers through the change pipeline`: the new package `Tarol.Messaging` with its generator.

### 15.1 Public API (new package `Tarol.Messaging`, depends on `Tarol.Sagas`)

| Type | Role |
|---|---|
| `Message`, `MessageHeaders` | the envelope, CloudEvents 1.0 binary mode (M-5): `Headers()`, `From(header, data)` (null = poison), `DedupId` (event, contract, version), `ActorSubject`, `SystemOf(contract)` |
| `IMessageTransport`, `Subscription`, `Incoming`, `Handled`, `Ack`, `MessageHandler` | the transport port (§2.1, T-A) |
| `InMemoryTransport` | fan-out per subscription, redelivery at a due time on the `TimeProvider`, `RunDue`, `Published`, `Pending` |
| `ConsumesAttribute` (`Name`) | the consumer factory (K-A) |
| `ConsumerSet` (+ `Combine`), `ConsumerInfo`, `ConsumerRunner`, `ConsumerRun`, `Mapped`, `MessageScope` | the generated set and its switch |
| `MessagingOptions` (`Application`, `Policy`, `Concurrency`, `Lease`, `Trust`), `SourceTrust` (`Tenants`, `AnyTenant`, `Allows`) | configuration and trust (M-13) |
| `MessageConsumers` (`Handle`, `Run`, `Redrive`, `Subscriptions`, `DeadLetters`, `ActivitySourceName`), `ConsumerAttempt` | the consumers (M-6, M-12) |
| `IMessageDeadLetters`, `InMemoryMessageDeadLetters`, `DeadMessage` | dead letters (M-9) |
| `MessagingHost`, `MessagingBuilder`, `MessagingTarolBuilderExtensions.AddMessaging` | hosting on the `TarolBuilder` |
| `MessageTest` | consumers in memory with a standing clock |
| generator | `GeneratedConsumers.Set`; TMSG001, TMSG002, TMSG003, TMSG006 (warning), TMSG007 |

### 15.2 Deliberate differences from §6, §7 and §11

* **TMSG004/TMSG005** (publications) come with 11B; the ids stay reserved.
* **`AddMessaging(application, configure)`** takes the application name as a parameter (it is required: every subscription
  is named after it) instead of `m.Options.Application`. A transport **and** a dead-letter store must be chosen
  explicitly; in-memory dead letters lose data at a restart, so there is no default until 11C.
* **The actor's "on behalf of"** is the envelope actor's *original* subject: `Kind:id:for` → `for`, else `id`
  (`Message.ActorSubject`), so a message published by a reaction that acted for alice is consumed "for alice", not "for
  Reactions.X".
* **`MessagingHost` declares only subscriptions** (`Prepare([], subscriptions)`); the topics a system publishes are
  declared by the publisher in 11B.
* **The worker's row handlers** (§10) are 11B; nothing in `Tarol.Sagas` or `Tarol.Changes` changed here except the
  Validations analyzer (below).
* **`InMemoryTransport.Consume` waits on a signal or the poll interval** of the `TimeProvider`; tests drive `RunDue`
  through `MessageTest.Drain` instead, so a standing clock never blocks.

### 15.3 Changes against steps 7–10

* `Tarol.Validations` analyzer: TVAL006 is exempt inside `[Consumes]` factories of `Tarol.Messaging` (the namespace was
  added to the entry-factory check; test `TVAL006_Exempt_In_A_Consumer_Factory_Of_Messaging`). No public API change.

### 15.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Messaging.Generators.Tests` | 18 | snapshot `consumers` (two consumers, two versions, a pinned name, the envelope parameter), the generated code compiles warning-free, nothing without consumers, incrementality (unrelated file, factory body), TMSG001 (5 shapes + not a change + name), TMSG002, TMSG003 (4), TMSG006 (missing, listed, no context), TMSG007 (two names, a shared name) |
| `Tarol.Messaging.Tests` | 61 | every row of §3.1 on the in-memory transport (valid, context, redelivery, two consumers of one contract, v1 + v2 of one event, unknown version, unknown contract, invalid mapping, Conflict retried, NotFound dead-lettered, poison, no envelope, forged tenant/source/authenticated publisher, unknown source, forged actor, re-drive, an exception in a factory, a failing dead-letter store); the envelope (headers, round trip, 9 malformed headers, optional headers, dedup id, actor subject, system of a contract); the transport (fan-out and order, due time, a throwing handler loses nothing, `Consume`); sets and options; `AddMessaging` with a real host; the consumer span and its link; the skill example verbatim |
| `Tarol.Validations.Generators.Tests` | +1 | the TVAL006 exemption |
| `Tarol.Messaging.AotSmoke` | 5 checks | envelope, `MessageTest` (commit, replay, invalid), context, the hosted consumers; Native AOT 0 IL warnings, 5.2 MB |
| `Tarol.Messaging.PackageTests` | 1 | the packed package: its generator and the Changes generator flow, a consumer runs through `MessageTest` |

### 15.5 Skill evaluation checklist (`tarol-messaging`, consumers part)

As §12, rows "consume another system's event", "add v2 of a contract", "a second consumer of a contract", "accept a
partner system", "a message keeps failing", "test two systems". The publication rows follow with 11B.

### 15.6 Suppressions and analyzer configuration

None.

### 15.7 Verification (SDK 11.0.100, 2026-11-15) **[verified]**

`dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution
Tarol.slnx` with Docker: **1129 of 1129** (1049 + 18 generator + 61 runtime + 1 analyzer); `eng/aot-smoke.sh` with
`TAROL_PG`: **14 apps** (13 + `Tarol.Messaging.AotSmoke`), 0 IL warnings, all checks passed; `dotnet pack -c Release` +
`eng/verify-packages.sh`: OK (`Tarol.Messaging` contains `lib/`, the generator under `analyzers/dotnet/cs/`, README,
LICENSE and `skills/tarol-messaging/SKILL.md`; its package consumer runs and lists the skill).

## 16. Implementation: PR 11B (2026-11-15)

`messaging: publications and effects from the outbox`: publications and effects in `Tarol.Messaging` (+ generator), the
worker's row handlers in `Tarol.Sagas`, the outbox rows in `Tarol.Changes` and `Tarol.Persistence`. Owner answer M-16
(effects as handler classes).

### 16.1 Public API

| Type | Package | Role |
|---|---|---|
| `PublishesAttribute<TEvent>` (`Contract`, `Version`) | Tarol.Messaging | the mapping method (M-7) |
| `PublicationInfo` (+ `NameOf`), `PublicationMapper`, `PublicationSet` (`Names`, `Topics`, `Find`, `Map`, `Serialize<T>`, `Combine`, `Empty`; an `IOutboxReactions`) | Tarol.Messaging | the generated set: rows `publish:<contract>@<version>`, the table over static arrays, the map through the app's JSON options |
| `MessagePublisher` (an `IOutboxRowHandler`) | Tarol.Messaging | runs publication rows: map → `Message.Of` → `IMessageTransport.Publish`; declares its topics before the first publish |
| `Message.Of(recorded, contract, version, data, traceparent)` | Tarol.Messaging | the envelope of a committed event |
| `IEffect<TEvent>`, `EffectContext` (`Name`, `Recorded`, `IdempotencyKey`), `EffectSet` (`Add<T>`, `WithServices`, `RowOf`; an `IOutboxRowHandler`) | Tarol.Messaging | effects (S-2, M-16): rows `effect:<name>`, a handler from a service scope per row |
| `MessagingBuilder.Publish(set)`, `.Effect<TEvent, THandler>(name)`, `.Effect<TEvent>(name, factory)` | Tarol.Messaging | registration: `WithOutboxRows` for the store, an `IOutboxRowHandler` service for the worker |
| `MessagingTest`, `MessagingSystem`, `MessagingSystemOptions` (`Reactions`, `Publications`, `Consumers`, `Effects`, `Policy`, `PublishThrough`, `Trust`), `MessagingStep` | Tarol.Messaging | several systems in memory: outbox → transport → consumers → … |
| generator | Tarol.Messaging | `GeneratedPublications.Set`; TMSG004, TMSG005 (error; warning for two prefixes), TMSG006 also for contract records |
| `IOutboxRowHandler` (`Names`, `ActivityName`, `Run`), `OutboxRows` (`Combine`, `Parts`), `ReactionSet.Empty`, `ReactionSet.ActivityName` | Tarol.Changes | an outbox row kind the worker runs; the handlers joined |
| `OutboxReactions.Combine`, `TarolBuilder.WithOutboxRows`, `TarolBuilder.OutboxTable` | Tarol.Persistence | the store's table: reactions and the rows other packages add, order-independent |

### 16.2 Deliberate differences from §6, §7, §10 and §11

* **The worker's row handlers** are an interface in `Tarol.Changes` (`IOutboxRowHandler`, which `ReactionSet` implements),
  not prefixes: the worker dispatches by row name through `OutboxRows`. Claim, head per (stream, row name), backoff, lease,
  `outbox_dead`, links and the log messages are unchanged; only the span name comes from the handler (`reaction <name>`,
  `publish <contract>`, `effect <name>`).
* **Registration is order-independent**: `AddMessaging` adds its rows to the store's table with
  `TarolBuilder.WithOutboxRows` (the stores read `OutboxTable` when they are resolved) and registers its handlers as
  `IOutboxRowHandler` services; the hosted worker joins them with the reactions. `AddReactionWorker()` without reactions
  now uses `ReactionSet.Empty` (an application that only publishes) instead of throwing; a hand-written
  `IOutboxReactions` that is not a `ReactionSet` still throws.
* **The map serializes through the application's `JsonSerializerOptions`** (`AddTarol(AppJson.Default, …)`,
  `PublicationSet.Serialize<T>` via `GetTypeInfo`), like the consumer side's `MessageScope.Payload<T>`, instead of naming a
  context property (`AppJson.Default.AuctionSettledV1`): the generator cannot know which context or property name an app
  uses. A record missing from the context is TMSG006 at build time and an `InvalidOperationException` (retried, then
  `outbox_dead`) at runtime.
* **`ce-causation`** is the causation of the event's operation (what caused the event at the publisher, as 11A's
  `Message.Causation` documents), not the event id as in the probe; the consumer's causation is the message id anyway.
  `ce-subject` is the stream id in `D` format.
* **The publisher declares its topics** (`Prepare(topics, [])`) lazily before its first publish and again after a failed
  publish, so a publishing-only process needs no `MessagingHost`; `MessagingHost` still declares only subscriptions.
* **`AddMessaging` needs a dead-letter store only with consumers** (a publishing-only app has none).
* **Effects have no generator** (M-16): `Effect<TEvent, THandler>(name)` registers the handler as transient (unless it is
  registered) and resolves it from a scope per row; the name is validated at registration (letters, digits, `-`, `_`, `.`).
* **`MessagingTest`** is the two-system harness; `MessageTest` stays the consumer-only one. History given with `Given`
  is neither reacted to nor published (its rows complete without running), as in `ReactionTest`.
* **TMSG003's message** names the attribute (`[Consumes("…", 1)]` or `[Publishes<T>("…", 1)]`); the rule is unchanged.
* **TMSG005 as a warning** (two prefixes) is the error rule reported with a warning severity (one id in the release file).

### 16.3 Changes against steps 7–10

* `Tarol.Persistence`: `OutboxReactions.Combine`; `TarolBuilder.WithOutboxRows`/`OutboxTable`; `UseInMemoryStore` and
  `UseNpgsqlStore` read `OutboxTable` instead of `Reactions` (identical when no rows were added).
* `Tarol.Changes`: `IOutboxRowHandler`, `OutboxRows`; `ReactionSet` implements the interface (`ActivityName`, `Empty`).
* `Tarol.Sagas`: `ReactionWorker` and `ReactionHost` take an `IOutboxRowHandler` (source compatible); the host joins the
  `IOutboxRowHandler` services; `AddReactionWorker()` without reactions (above).
* `Tarol.Validations` analyzer: TVAL006 is exempt inside `[Publishes<T>]` methods of `Tarol.Messaging` (the event's values
  were validated when it was raised); test `TVAL006_Exempt_In_A_Publication_Method_Of_Messaging`.

### 16.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Messaging.Tests` | +19 (80) | **round trip** ArtFlow → billing → ArtFlow (settled → invoice + sale → invoice-paid → payment → payment-recorded, unsubscribed); the **envelope** of a committed event and the consumer's context (actor `for alice` over two hops, correlation, causation); the **`publish` span** is the `traceparent`, links to the commit, the consumer's span links to it; a **crash after publish** → published twice, one invoice (replayed); a failing transport → retries → `outbox_dead`; **version order per stream with two worker loops** (4 streams × 12 notes); **dual publish** v1 + v2 → one invoice, v2 buried by the v1-only consumers, one payment from v1 + v2 of invoice-paid; an unsubscribed contract and a `null` mapping; an **effect** down once and crashing after sending → 3 calls, 1 key, 1 mail; an effect's key and context, `Invalid` → `outbox_dead`; given history is not published; the sets (names, topics, table, map, `Combine` and its refusals, malformed names, `Serialize` without metadata), `Message.Of`, `EffectSet` (names, derived events, refusals), `OutboxRows` (join, dispatch, activity names, a name twice); `AddMessaging` with `Publish` + `Effect` on a real host with the reaction worker and no reactions; the skill's publication example verbatim |
| `Tarol.Messaging.Generators.Tests` | +20 (38) | snapshot `publications` (two versions, `internal` and nullable, a base event's row on a derived event, with and without `RecordedEvent`), compiles warning-free, consumers and publications as two files, incrementality (unrelated file, mapping body), TMSG004 (9 shapes + a hidden type), TMSG003 on publications (2), TMSG005 (duplicate; two prefixes as a warning that still generates), TMSG006 for contract records |
| `Tarol.Sagas.Tests` | +2 (29) | a worker without reactions runs a DI-registered row handler; the store's table joins reactions and added rows; the "hand-written table" refusal |
| `Tarol.Validations.Generators.Tests` | +1 | the TVAL006 exemption in `[Publishes<T>]` methods |
| `Tarol.Messaging.AotSmoke` | 8 checks (+3) | publication and effect through `MessagingTest`, the hosted worker publishing and running a DI effect; Native AOT 0 IL warnings, 5.4 MB |
| `Tarol.Messaging.PackageTests` | 1 | the packed generator also emits `GeneratedPublications`; a publication and an effect drained through `MessagingTest` |

### 16.5 Skill evaluation checklist (`tarol-messaging`, publications part)

As §12, rows "publish an own event" (expected `[Publishes<Auction.Settled>("artflow.auction-settled", 1)]` on a static
mapping method, `AddMessaging(…).Publish(GeneratedPublications.Set)` and `AddReactionWorker()`; mistakes: publishing the
domain event, no prefix, no worker) and "send an e-mail once per payment" (expected `IEffect<Auction.PaymentRecorded>`
with `context.IdempotencyKey`, `.Effect<TEvent, THandler>(name)`; mistakes: sending in `Execute` or a reaction, a key that
changes per attempt). Plus: "test two systems together" → `MessagingTest.System(…)`, `Drain()`, `Later(…)`; mistake: a
broker container in unit tests. The skill grew to 9.2 KB (above the ≈ 8 KB guide) with both parts; it stays one skill
because consumers and publications share registration and testing.

### 16.6 Suppressions and analyzer configuration

None.

### 16.7 Verification (SDK 11.0.100, 2026-11-15) **[verified]**

`dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution
Tarol.slnx` with Docker: **1171 of 1171** (1129 + 19 runtime + 20 generator + 2 Sagas + 1 analyzer; the new runtime tests
also 6 times in a row); `eng/aot-smoke.sh` with `TAROL_PG`: **14 apps**, 0 IL warnings each, all checks passed
(`Tarol.Messaging.AotSmoke` 8 checks, 5.4 MB); `dotnet pack -c Release` + `eng/verify-packages.sh`: OK (`Tarol.Messaging`
contains `lib/`, the generator under `analyzers/dotnet/cs/`, README, LICENSE and `skills/tarol-messaging/SKILL.md`; its
package consumer generates and runs a consumer, a publication and an effect). CI of `main` after #31 (run 89): `build`,
`aot-smoke`, `integration` green.


## 17. Implementation: PR 11C (2026-11-15)

`messaging.npgsql: the in-database bus`: the new package `Tarol.Messaging.Npgsql`, and the transport contract suite all
transports share (`tests/Messaging/Shared`).

### 17.1 Public API (new package `Tarol.Messaging.Npgsql`, depends on `Tarol.Messaging` and Npgsql)

| Type | Role |
|---|---|
| `NpgsqlBus` (`IMessageTransport`, `IAsyncDisposable`) | `EnsureSchema`, `Prepare`, `Publish`, `Consume`, `PurgeMessages`, `Pending`, `Schema` |
| `NpgsqlBusOptions` | `Schema` (`bus`), `EnsureSchema`, `Notify`, `PollInterval` (100 ms), `Batch` (16), `Roles` (role → source) |
| `NpgsqlBusSchema` | `Script`, `DeadLettersScript`, `RowLevelSecurityScript` (policy `publish_own_prefix`), `GrantScript`, `Version`, `DefaultSchema` |
| `NpgsqlMessageDeadLetters` (`IMessageDeadLetters`) | `<schema>.messages_dead` (default `tarol`), `EnsureSchema`, `Add`, `List`, `Take` |
| `NpgsqlMessagingBuilderExtensions` | `UseNpgsqlBus(connectionString, options)`, `UseNpgsqlBus(dataSource, options)`, `UseNpgsqlDeadLetters(schema, dataSource, ensureSchema)` |

### 17.2 Deliberate differences from §6, §7 and §11

* **The schema script runs under an advisory lock** (`pg_advisory_xact_lock`), in one transaction: a publisher and the
  consumers of one process prepare at the same moment, and parallel `CREATE … IF NOT EXISTS` race on the catalog. The same
  for `messages_dead`. The bus schema has its own `schema_version` (1); a newer version is refused.
* **`EnsureSchema = false`** for systems that connect as restricted roles: the owner runs the scripts once.
* **`NpgsqlBusSchema.RowLevelSecurityScript` and `GrantScript`** are the shipped guide of §3.2 (the probe's SQL), as
  scripts instead of prose only.
* **A late acknowledgement after a lost lease** changes nothing (it presents the old claim) and is logged (11101).
* **The `LISTEN` connection reconnects** after a failure while the loops keep polling (11102); a failed claim ends
  `Consume` with the exception and `MessagingHost` consumes again after its delay (the deliveries wait).
* **Retention** is a method (`PurgeMessages(olderThan, batch, ct)`, the shape of the store's key purge), not hosted:
  step 15 schedules it with the other purges.
* **Default batch 16** per claim and loop (the probe used 32): the lease covers the whole batch.
* **`UseNpgsqlDeadLetters`** defaults to the `NpgsqlDataSource` that `UseNpgsqlStore(connectionString)` registers and to
  schema `tarol`; the bus from a connection string owns a data source of its own (disposed with it), so the two never
  collide in DI.

### 17.3 Changes against steps 7–10

None in the packages. The CI `integration` job runs `tests/Messaging/Tarol.Messaging.Npgsql.IntegrationTests`.

### 17.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `tests/Messaging/Shared` (`TransportContract`, `ContractDomain`) | 14 per transport | every row of §3.1 on a real transport in real time: valid (two consumers of one contract), context (actor, tenant, correlation, causation, every header and the bytes survive, `AuthenticatedAs`), a lost acknowledgement (redelivery replayed), a republish (one commit; dropped by the bus), a contract without a subscription, unknown version and invalid mapping buried at once, a Conflict retried until its precondition commits, NotFound retried, buried and re-driven, poison data with its bytes, forged tenant / source / actor, dual publish v1 + v2 (both kept), order per stream (30 messages), messages waiting for a consumer that starts later, a retry waiting for its delay |
| `Tarol.Messaging.Npgsql.IntegrationTests` | 37 | the contract polling (14) and with NOTIFY (14, the poll at 5 s so the NOTIFY delivers); §3.2: row-level security refuses `billing` → `artflow.*` (42501), its own prefix passes and is authenticated as `billing`, without the policy the stamped role is buried `SourceMismatch`, an unknown role is `role:billing`; a lease that runs out (claimed again, the late answer changes nothing); retries counting attempts; the claim order after reused free space (§5 item 6, 300 messages split across the heap); the schema created by 8 at once, a newer version refused, a bad schema name; retention keeps undelivered messages; dead letters (headers, bytes, poison without headers, taken once); a hosted system on the Npgsql store publishing through its outbox and consuming on the bus with NOTIFY; the skill example verbatim |
| `Tarol.Messaging.Npgsql.AotSmoke` | 6 checks | the scripts; with `TAROL_PG` a hosted system: consumed from the bus, published through the outbox and consumed back, a dead letter in `messages_dead`, the bus drained; Native AOT 0 IL warnings, 14.4 MB (with the Npgsql store and the host) |
| `Tarol.Messaging.Npgsql.PackageTests` | 1 | the packed package: the bus and the dead letters register and resolve, the scripts, the Messaging generator flows |

### 17.5 Skill evaluation checklist (`tarol-messaging-npgsql`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| two systems on one PostgreSQL exchange messages | `UseNpgsqlBus(connectionString, new NpgsqlBusOptions { Notify = true })`, same `Schema` everywhere | a hand-written queue table and polling loop |
| keep dead letters in the database | `UseNpgsqlDeadLetters()` (after `UseNpgsqlStore(connectionString)`) | in-memory dead letters in production |
| only billing may publish `billing.*` | one role per system, `NpgsqlBusSchema.RowLevelSecurityScript` + `GrantScript`, `EnsureSchema = false`, `Roles` map | trusting `ce-source`; one shared role for all systems |
| keep the bus small | `NpgsqlBus.PurgeMessages(olderThan, batch, ct)` on a schedule | deleting deliveries that are still due |

### 17.6 Suppressions and analyzer configuration

None.

### 17.7 Verification (SDK 11.0.100, 2026-11-15) **[verified]**

`dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution
Tarol.slnx` with Docker: **1208 of 1208** (1171 + 37 in `Tarol.Messaging.Npgsql.IntegrationTests`); the integration project
alone **10 times in a row in Release, 36 of 36 each** (before the skill test was added); `eng/aot-smoke.sh` with `TAROL_PG`:
**15 apps** (14 + `Tarol.Messaging.Npgsql.AotSmoke`), 0 IL warnings each, all checks passed; `dotnet pack -c Release` +
`eng/verify-packages.sh`: OK (`Tarol.Messaging.Npgsql` contains `lib/`, README, LICENSE and
`skills/tarol-messaging-npgsql/SKILL.md`; its package consumer runs and lists the skill).

## 18. Implementation: PR 11D (2026-11-15)

`messaging: NATS JetStream and RabbitMQ transports, the two-system sample`: the new packages `Tarol.Messaging.Nats`
(NATS.Net 3.3.2) and `Tarol.Messaging.RabbitMq` (RabbitMQ.Client 7.2.2), and billing as a second system in
`samples/Sagas` (M-15).

### 18.1 Public API

| Type | Package | Role |
|---|---|---|
| `NatsTransport` (`IMessageTransport`, `IAsyncDisposable`) | Tarol.Messaging.Nats | `Prepare`, `Publish`, `Consume`, `StreamOf`, `Duplicates` |
| `NatsTransportOptions` | Tarol.Messaging.Nats | `Url`, `Username`, `Password`, `Prefix`, `DuplicateWindow` (2 min), `PublishTimeout` (5 s), `ClientName` |
| `NatsMessagingBuilderExtensions` | Tarol.Messaging.Nats | `UseNats(url)`, `UseNats(options)` |
| `RabbitMqTransport` (`IMessageTransport`, `IAsyncDisposable`) | Tarol.Messaging.RabbitMq | `Prepare`, `Publish`, `Consume`, `ExchangeOf`, `QueueOf`, `Unroutable` |
| `RabbitMqTransportOptions` | Tarol.Messaging.RabbitMq | `Uri`, `Prefix`, `StampUserId` (true), `ClientName` |
| `RabbitMqMessagingBuilderExtensions` | Tarol.Messaging.RabbitMq | `UseRabbitMq(uri)`, `UseRabbitMq(options)` |

### 18.2 Deliberate differences from §6, §7 and §11

* **`Prefix`** on both transports: a namespace in front of subjects, streams and durable consumers (NATS) or exchanges and
  queues (RabbitMQ). The contract suite runs every test in a namespace of its own on one broker; environments that share a
  broker use it the same way.
* **NATS: a subscription reads one stream.** A consumer whose contracts span two sources is refused at `Prepare`
  (`ArgumentException`); the probe took the first topic's stream silently.
* **NATS: a publish without an acknowledgement** (no stream, or a forbidden subject) is a `TimeoutException` with a hint;
  the connection's `-ERR Permissions Violation` is logged (11201). `PublishTimeout` defaults to 5 s.
* **RabbitMQ: `StampUserId`** (default true) sets `user_id` to the connection's user, which the broker validates, so
  `Incoming.AuthenticatedAs` is the publishing user: name each system's user like its prefix. With one shared user (the
  sample's Aspire broker) it is turned off.
* **RabbitMQ: another system's exchange** is declared actively when the user may configure it, otherwise passively (it must
  exist: its publisher declares it; until then `Prepare` fails with 404 and the host retries). The probe declared every
  exchange actively, which per-user permissions refuse.
* **RabbitMQ: a broken consumer channel** ends `Consume` with an exception (the host consumes again after its delay); a
  failed acknowledgement is logged (11302) and the broker delivers again.
* **The sample's billing pays in a reaction** (`PayInvoice`), not with a stand-in provider that runs a change and ignores its
  result: the probe's loop lost one payment that way (§5 item 12, C-7). The README states the rule.
* **`Auction.Settled` carries the buyer and the price** in the sample (the publication maps them); an auction without a bid
  publishes nothing (the mapping returns `null`).

### 18.3 Changes against steps 7–10

None in the packages. The CI `integration` job runs both new integration projects; the `aot-smoke` job gets RabbitMQ as a
service and NATS with JetStream as a container (`TAROL_NATS`, `TAROL_RABBITMQ`). `samples/Sagas`: `Billing/`,
`Worker/Messaging.cs`, `Shared/Broker.cs`; the AppHost adds NATS (or RabbitMQ), `billingdb` and billing; `apphost.sh` checks
the loop.

### 18.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Messaging.Nats.IntegrationTests` | 18 | the transport contract (14) on `nats:2.11 -js`, a prefix per test; subject permissions (`tests/…/Nats.cs`, the probe's `nats.conf`): billing refused on `artflow.*` with a `TimeoutException` and a logged permission error, accepted on its own prefix; v1 + v2 of one event are two messages and a republish a counted duplicate; one stream per subscription; the skill example verbatim |
| `Tarol.Messaging.RabbitMq.IntegrationTests` | 18 | the transport contract (14) on `rabbitmq:4-management` (a failed first boot is retried with a fresh container), a prefix per test; the probe's users and permissions: billing refused on exchange `artflow` (`ACCESS_REFUSED`), its publish channel recovers, a forged `user_id` refused (`PRECONDITION_FAILED`), a consumer of artflow's exchange declares it passively and sees `AuthenticatedAs = artflow`; an unroutable message is counted as delivered; the skill example verbatim |
| `Tarol.Messaging.Nats.AotSmoke` | 5 checks | names, options; with `TAROL_NATS` a duplicate, a retry after a delay, the envelope; Native AOT 0 IL warnings, 11.1 MB (+5.7 MB over `Tarol.Messaging.AotSmoke`) |
| `Tarol.Messaging.RabbitMq.AotSmoke` | 5 checks | names, options; with `TAROL_RABBITMQ` an unroutable publish, a retry after a delay, the envelope; Native AOT 0 IL warnings, 7.4 MB (+2.0 MB) |
| `Tarol.Messaging.Nats.PackageTests`, `Tarol.Messaging.RabbitMq.PackageTests` | 1 each | the packed packages register and resolve |
| `samples/Sagas` | `apphost.sh` | ArtFlow → billing → ArtFlow, 5 of 5, JIT with NATS, JIT with RabbitMQ, Native AOT with NATS (README) |

### 18.5 Skill evaluation checklist (`tarol-messaging-nats`, `tarol-messaging-rabbitmq`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| connect two systems over NATS | `UseNats(new NatsTransportOptions { Url, Username, Password })`, one user per system with publish on `<prefix>.>` | `EnsureSuccess()` on an ack; `Nats-Msg-Id` = event id; one consumer over two sources |
| connect two systems over RabbitMQ | `UseRabbitMq(uri)` with a user named like the prefix; write only on its exchange | one shared user with `StampUserId = true`; a delayed-message plugin |
| a publish to NATS times out | check the user's subject permissions (log 11201) | raising the timeout |
| the sample with RabbitMQ | `ARTFLOW_BROKER=rabbitmq` | a database and a project of the same name in the AppHost |

### 18.6 Suppressions and analyzer configuration

None.

### 18.7 Verification (SDK 11.0.100, 2026-11-15) **[verified]**

`dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution
Tarol.slnx` with Docker: **1244 of 1244** (1208 + 18 NATS + 18 RabbitMQ); `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`
and `TAROL_RABBITMQ`: **17 apps** (15 + the two brokers'), 0 IL warnings each, all checks passed; `dotnet pack -c Release` +
`eng/verify-packages.sh`: OK (both packages contain `lib/`, README, LICENSE and their skill; their package consumers run).
`samples/Sagas/apphost.sh … 5`: 5 of 5 round trips with NATS (JIT), with RabbitMQ (JIT) and with the three Native AOT
binaries (NATS), the README's result. The broker integration projects passed on their first full runs (17 + 17, before the
skill tests were added), in the solution run, and **10 times each in a row in Release (18 of 18 every time)**.

## 19. Implementation: PR 11E (2026-11-15)

`messaging.signalr: the UI push`: the new package `Tarol.Messaging.SignalR` (M-3), rebuilt from the old Gateway/Transport
layering on top of the system-to-system messaging. ASP.NET Core is the framework reference; the only package is the SignalR
.NET client (`Microsoft.AspNetCore.SignalR.Client` 11.0.0).

### 19.1 Public API

| Type | Role |
|---|---|
| `UiPushExtensions.PushToUi(messaging, configure)`, `MapMessageHub(endpoints)` | registration: SignalR with `SignalRJson`, the forwarder, the authorizer; the hub at `HubPath` |
| `UiPushOptions` | `Forward(topics)`, `Topics`, `Rules`, `Name` (`UiPush`), `HubPath` (`/tarol/messages`), `TenantClaim`, `Tenant`, `SubjectClaim` |
| `UiPushForwarder` (`BackgroundService`) | the consumer subscription `<app>.<Name>`: trust (M-13), JSON check, `SendAsync` to `<tenant>/<topic>`; `Forward(incoming)`, `Subscription` |
| `MessageHub` (`Hub`, `[Authorize]`) | `Subscribe(topic)`, `Unsubscribe(topic)`, `Publish(topic, data)`, `Group(tenant, topic)`, `MessageMethod` |
| `ITopicAuthorizer`, `TopicOperation`, `TopicRules` (`Allow`, `Subscribe`, `Publish`) | authorization per principal, operation and topic (F3) |
| `HubConnections` | the hub's open connections, aborted when the application stops |
| `PushedMessage`, `SignalRJson` | what clients receive; the source-generated hub JSON (F1) |
| `MessagePushClient` | `Connect`, `Start`, `Subscribe` (→ `IAsyncDisposable`), `Publish`, `Stop`, `State`, `Topics` |

### 19.2 The old findings (inventory §3) and where they went

| Finding | Now |
|---|---|
| F1 no source-generated hub JSON | `SignalRJson`, added by `PushToUi` and `Connect` |
| F2 `CancellationToken` on hub methods | refuted in the probe; the hub methods take none (the connection's token is `Context.ConnectionAborted`) |
| F3 no authentication or topic authorization | `[Authorize]`, `ITopicAuthorizer` for every subscribe and publish, the tenant from the principal |
| F4, F5 unused or ancient packages | none: the framework reference and the SignalR client only |
| F6 the gateway does not implement its port | gone: the forwarder is a consumer of `IMessageTransport` |
| F7 no production connection, the reconnect lost | `MessagePushClient` replays on `Reconnected` (test: server stopped and started again on the same port) |
| F8 subscribe before start | replayed at `Start` (test) |
| F9 a failed subscribe leaves the topic dead | the handler is removed and the exception thrown (test) |
| F10 the map read without the lock | an immutable map swapped with `Interlocked.CompareExchange` |
| F11 unsubscribe removes every handler | a handle per handler; the last one unsubscribes (test) |
| F12–F14, F16 the backplane runner | gone: the transports of 11A–11D |
| F15 no envelope | `Message` (CloudEvents); `PushedMessage` carries id, version, subject, time, sender |
| F17 the `On` handle discarded | kept and disposed with the client |
| F18 handlers serial, one throw skips the rest | each handler isolated and logged (test) |
| F19 `payload.ToArray()` copies | the data is parsed once into a `JsonElement` per message |
| F20 the value-type cast | gone (no `JsonAotProtocol`) |
| F21–F23 names, a test project under `src/`, an empty generator | gone by construction |

### 19.3 Deliberate differences from §7, §10 and §11

* **Client publishes** (`MessageHub.Publish`) exist besides the forwarding: the old plan's scenarios (client → backend →
  same and other clients) need them. They reach the caller's tenant only and carry the server-stamped `Sender`; they are not
  messages between systems (no outbox, no transport).
* **`HubConnections` aborts the connections when the application stops**, and any client that reconnects into the
  shutdown. Without it a graceful stop with live UI clients waited the host's whole shutdown timeout (30 s, measured), because
  the clients' automatic reconnect (first delay 0) opened new WebSocket connections during the shutdown.
* **The client never gives up**: `Connect` reconnects forever (0, 1, 2, 5 s, then every 10 s with jitter) instead of
  SignalR's default policy, which stops after about 42 s (a full-solution run under load once ended a client
  `Disconnected` for good); a connection that still closes for good without `Stop` is started again by the client.
* **A client handle's disposal never throws**: the unsubscribe after a lost connection is best effort (the native smoke run
  found a `WebSocketException` there).
* **The UI push is a notification**: a forwarded message reaches the clients connected at that moment; a send failure is
  retried twice, then dropped and logged. Scale-out needs one forwarder name per web node (README), not a SignalR backplane.
* **Native AOT needs one feature switch in the app** (`Microsoft.AspNetCore.SignalR.Hub.IsCustomAwaitableSupported=false`);
  without it SignalR's server reports IL2026/IL3050 at publish. The package does not set it for the app (no `build/` targets,
  conventions §6); the README and the skill say so.
* **The tests are a plain test project** (`Tarol.Messaging.SignalR.Tests`, Kestrel on a loopback port, no Docker), so they run
  in the `build` job.

### 19.4 Changes against steps 7–10

None.

### 19.5 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Messaging.SignalR.Tests` | 18 | the scenarios of the old `plan-messagingIntegrationTests.prompt.md` on Kestrel with real clients: same-client round trip (sender stamped), other clients of the tenant (another tenant gets nothing), topic isolation, unsubscribe (one handler; the last unsubscribes), **reconnect** (server stopped and started on the same port, subscriptions replayed), a connection without automatic reconnect started again by the client itself, subscribe before start, payload integrity, 50 concurrent publishes, cancellation and shutdown with live clients (under 5 s); forwarding from the transport to the tenant's subscribers (id, sender, subject, data) and not to another tenant; untrusted tenant / source mismatch / non-JSON not forwarded; refused subscribe and publish (`Forbidden`, no handler kept), an invalid topic, an unauthenticated connection (401), a tenant claim that is not a tenant; a failing handler isolated; a handle disposed after the server went away; groups and rules; the skill example verbatim |
| `Tarol.Messaging.SignalR.AotSmoke` | 5 checks | hub, forwarder and client in one native process: subscribe, a publish through the hub, a forwarded message, a refused subscribe, a fast stop with a live client; Native AOT 0 IL warnings, 17.5 MB |
| `Tarol.Messaging.SignalR.PackageTests` | 1 | the packed package: registered, the hub mapped, a client built |

### 19.6 Skill evaluation checklist (`tarol-messaging-signalr`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| show settled auctions live in the browser | `[Publishes<T>]` + `PushToUi(o => { o.Forward("artflow.auction-settled"); o.Rules.Subscribe("artflow.*"); })`, `MapMessageHub()` | a typed hub; sending from a reaction with `IHubContext` |
| a chat between users of one tenant | `o.Rules.Publish("artflow.chat", role: …)`, `MessagePushClient.Publish` | taking the tenant or the sender from the client |
| a .NET app listens and survives restarts | `MessagePushClient.Connect`, `Subscribe` handles, `Start` | its own reconnect loop; one handler unsubscribing all |
| publish the web app with Native AOT | the `IsCustomAwaitableSupported=false` switch | suppressing IL2026/IL3050 |

### 19.7 Suppressions and analyzer configuration

None.

### 19.8 Verification (SDK 11.0.100, 2026-11-15) **[verified]**

VERIFICATION_11E
