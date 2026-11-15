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
* **Transports** (M-1, M-4, M-14): a port with four implementations, all passing the same 24 scenario checks and the
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
sides **12 of 12 pass** (so a `CancellationToken` on the hub methods binds, F2 refuted); the runner test fails in 5 of 6 runs;
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

### 3.1 Consumers through the pipeline (`selftest`, 24 checks per transport)

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
invoices, 5 sales, 5 reminders, 5 paid → 5 payments recorded in ArtFlow; every event with a `traceparent`; invoices
recorded by ArtFlow's consumer actor, payments by billing's; both outboxes empty; the dashboard lists `api`, `worker` and
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
12. **RabbitMQ 4 sometimes fails its first boot** in a fresh container (Erlang prelaunch) **[verified]** once in six
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
4 M-12 … M-15. Not asked because already decided: the delivery table (C-9, S-7), unknown version → bury by default (R-10),
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
1049 of 1049; `eng/aot-smoke.sh` with `TAROL_PG`: 13 apps, 0 IL warnings, all checks passed; `dotnet format
--verify-no-changes` clean. CI of `main` after #29 (run 85): `build`, `aot-smoke`, `integration` green.

## 14. Open items

* Retention of `bus.messages` and `messages_dead`; subscription metrics: step 15.
* Order per key at higher consumer concurrency (partitioned subjects, single active consumer): not needed yet.
* Contract documents (AsyncAPI) and a schema check in CI: later, if asked.
* RabbitMQ topic permissions per routing key (finer than an exchange per source): 11D documents both.
