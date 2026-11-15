# Sagas and reactions

Concept note for step 9 (see [migration/sagas.md](../migration/sagas.md)): **a reaction is a change with a different
context** (changes.md insight 5), and **a saga is an aggregate whose steps are reactions**. A committed event writes one
outbox row per reaction in its own transaction; a worker claims the row, maps the event to a change, runs it through the
same `ChangePipeline` as the system acting for the original user, and completes the row. Sagas need no runtime of their
own: their state is an aggregate on the same store, their timeouts are reactions with a due time, and their compensations
are events that other reactions turn into forward changes. It starts from the step 7 and 8 building blocks; the old
`Tarol.Sagas` (`CLorat/Tarol@6a62f81`, Marten-backed, `Saga<T>` with `When`/`SagaStep`) is reviewed in the migration note.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/sagas/`](../migration/probes/sagas/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: designed** (owner decisions S-1 … S-15, [migration/sagas.md](../migration/sagas.md) §11). Built in PRs 9A–9C.

## 1. What a user of Tarol needs

| ArtFlow use case | Shape | Built from |
|---|---|---|
| mark the payout due when a bid wins | reaction: `Bid.Won` → creation of `Payout` | `[OnEvent<Bid.Won>]` on `MarkPayoutDue.From` |
| settle the auction: wait for the buyer's payment for 3 days, re-open the auction if it does not come | saga with a timeout and a compensation | aggregate `Settlement` + four reactions |
| see a reaction that can never succeed | dead letter with the guard's code, re-drive by a human | worker, C-9 |
| follow "everything that happened because of this bid" | correlation id across hops, trace links | `OperationContext`, `ActivityLink` |
| test the whole chain without a database | in-memory outbox, fake clock, drain | `ReactionTest` |

## 2. Reactions (C-5, S-6, S-14)

```csharp
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);

    public Result<Payout> Execute(ChangeContext context) => Payout.MarkDue(Auction, Amount, context.Now);
}
```

* **Declaration**: a static factory on the change, `(TEvent)` or `(TEvent, RecordedEvent)` → the change; the factory is
  the mapping and nothing else (no I/O). TVAL006 is silenced inside it (C-13); the pipeline validates the result.
* **Name** (S-14): the change's type name, stored in every outbox row and idempotency key like an event alias. Rename the
  record only after pinning `[OnEvent<T>(Name = "MarkPayoutDue")]`. A row whose name no longer exists throws in `Run`, is
  retried and dead-lettered with "No reaction named …" **[verified]**. Several factories on one change (one per event
  type) share its name; the generated switch dispatches on (name, event type).
* **Context** (changes.md §5): `recorded.Context.AsSystem("Reactions." + name).CausedBy(event id)`: the system acting on
  behalf of the original user, same tenant and correlation **[verified]** (through three hops: Expired → Reopened still
  `onBehalfOf alice`).
* **Key**: `IdempotencyKey(tenant, "reaction:" + name, event id)`, stored with the reaction's commit (C-8), so a redelivery
  returns the first outcome without running **[verified]** (in process and after a real process crash). A reaction that
  commits no event stores no key; its redelivery decides again on the current state (harmless by construction: it
  decided "nothing to do").
* **Generated per assembly** (S-6: by the Changes generator, next to `GeneratedChanges`): one static array per event type
  (never a collection expression in the span-returning method, CS9203), the schedule of `DueAt` reactions, and the switch
  (name, event) → factory → `pipeline.Run(change, context, options, ct)`, wrapped in a public `GeneratedReactions.Set`
  (`ReactionSet`) **[verified]** as hand-written generated code.
* **Several assemblies** (S-3): `ReactionSet.Combine(a, b)`; a name in two sets throws at startup **[verified]**; one set
  returns its own span (no copy), several are concatenated per event.
* Reactions are changes only (S-2): every reaction is effect-once by its key. An outgoing e-mail or HTTP call is an
  outgoing message of step 11, not a reaction.

## 3. The worker (C-9, S-5, S-7, S-10, S-13)

```text
loop:  rows = outbox.Claim(batch 32, lease 1 min)                         ← head per (tenant, type, stream, reaction)
       for each row:  Activity "reaction <name>" (Consumer, linked to the row's traceparent)
                      result = set.Run(pipeline, row.Reaction, row.Event)     ← factory → pipeline.Run (key, AsSystem)
                      Done  → Complete(row)                                    (false: lease lost, another worker has it)
                      Retry → Retry(row, now + backoff(attempt), error)
                      Bury  → Bury(row, error)                                 → outbox_dead
       nothing claimed → wait PollInterval (100 ms) or a NOTIFY (opt-in)
```

| Pipeline answer (changes.md §6, amended by S-7) | Verdict |
|---|---|
| success, or a replayed key | Done |
| `Invalid` | Bury (mapping bug or contract change) |
| any `Error.Conflict` (guard, version, duplicate key) | Retry (C-9) |
| `NotFound`, `UnprocessableEntity` (a `[Read]` missing) | **Retry** (S-7: the target may be created by another reaction that has not run yet) |
| `Unavailable`, `Timeout`, `TooManyRequests`, `Internal`, an exception | Retry |
| `Forbidden`, `Unauthorized`, `Gone` | Bury |
| Retry at attempt `MaxAttempts` | Bury |

* **Backoff** (S-13): 1 s, 2 s, 4 s … capped at 5 min, ±20 % jitter derived from (row id, attempt) so tests can predict
  it; 10 attempts ≈ 12 minutes before the dead letter **[verified]** (curve 0.9, 2.0, 4.1, 8.1, 17.5, 28, 53, 106, 209, 293 s).
* **Dead letter** (S-5, DL-B): the row moves to `outbox_dead` in one statement (`DELETE … RETURNING` + `INSERT`), so it
  leaves the head order and the next row of its stream runs **[verified]**. Re-drive inserts a new outbox row (new id,
  behind the rows that waited meanwhile) **[verified]**; the port lists dead letters (`DeadLetters`) and re-drives one
  (`Redrive(id)`). A dead letter is a fact for a human: nothing re-drives automatically.
* **Lease** (1 min): a worker that dies after the reaction's commit leaves the row claimed; after the lease another worker
  claims it (attempt 2), the key replays, one commit **[verified]** (`Environment.FailFast` in a child process, redelivery
  after 3.1 s with a 3 s lease). `Complete`/`Retry`/`Bury` present the claim id and return `false` after a lost lease
  (8C) **[verified]**. The lease must exceed the slowest reaction plus clock skew between nodes (time.md).
* **Shutdown**: cancellation stops the loop between rows; a row in flight keeps its lease and comes back after it. No
  attempt is lost, at most delayed by one lease.
* **Wake-up** (S-10): poll with a 100 ms default (claim until empty, then wait); LISTEN/NOTIFY is opt-in because a NOTIFY
  in the commit costs writers about 30 % of their commits/s **[verified]** (evaluation §4.3).
* **Keys** (P-11): the host purges idempotency keys older than 7 days every hour, in batches.
* **Concurrency**: N loops per process (default 4), any number of processes; `SKIP LOCKED` keeps claims disjoint; per
  (stream, reaction) one row at a time, in version order.

## 4. Order and timeouts (P-10, S-8, S-9)

* **Head per (tenant, type, stream, reaction)** stays the guarantee (P-10). The claim changes (S-8) from the anti-join
  to a **maintained `head` flag** with a partial index `(due_at, id) WHERE head`: the commit sets `head` on a row that has
  no live predecessor of its key, `Complete`/`Bury` promote the next row in the same statement. The anti-join walks every
  waiting row when few streams hold deep backlogs (100,000 rows behind 16 heads: 600 ms per claim) and every future
  timeout (100,000: 20 ms); the flag claims in 0.04–0.2 ms in all five shapes measured **[verified]** (`Claim/`). The
  promotion must hold the stream row (`FOR SHARE` on `streams`) so a concurrent commit of the same stream sees it
  **[unverified]**: the race test belongs to PR 9B.
* **Scheduled reactions** (S-9, T-2): `[OnEvent<T>(DueAt = nameof(T.Member))]` writes the row with `due_at` = that member
  (never earlier than the commit), and such a reaction is **exempt from the head rule**: its rows are ordered by time
  only. Under the head rule a shortened deadline fired two days late, behind the older row **[verified]**; exempt, it fires
  on time and the older row later is a stale no-op **[verified]**.
* **Rule for every timeout change**: check your own deadline and the saga's state, and return `Result.Success()` when
  the timeout is stale or the saga ended. A `Conflict` there would be retried and dead-lettered (C-9).

## 5. Sagas (S-4, S-15): an aggregate whose steps are reactions

```csharp
public partial class Settlement : AggregateRoot<Settlement>
{
    public static Settlement For(Guid auction) => new((Id<Settlement>)auction);     // correlation: the auction's id

    public Result Begin(string buyer, decimal amount, DateTimeOffset now)            // start, restart, or a duplicate
    {
        if (Version > 0 && Status == SettlementStatus.AwaitingPayment)
            return Result.Success();
        Raise(new Started(buyer, amount));
        return Raise(new DeadlineSet(now.AddDays(3)));
    }
    public Result Pay(decimal amount) => Status != SettlementStatus.AwaitingPayment ? Result.Success()
        : amount < Amount ? Error.Conflict("Settlement.Underpaid") : Raise(new Completed(amount));
    public Result Expire(DateTimeOffset now)
        => Status != SettlementStatus.AwaitingPayment || now < Deadline ? Result.Success() : Raise(new Expired());
    …
}

public sealed partial record StartSettlement(Guid Auction, string Buyer, decimal Amount) : ILoads<Settlement>
{
    [OnEvent<Bid.Won>]
    public static StartSettlement From(Bid.Won e, RecordedEvent r) => new(r.StreamId, e.Bidder, e.Amount);
    public async ValueTask<Result<Settlement>> Load(IAggregateReader reader, ChangeContext context, CancellationToken ct)
        => (await reader.Load((Id<Settlement>)Auction, context.Operation, ct)).TryGetValue(out Settlement? s) ? s : Settlement.For(Auction);
    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Begin(Buyer, Amount, context.Now);
}

public sealed partial record ExpireSettlement(Id<Settlement> Settlement) : IChange<Settlement>
{
    [OnEvent<Settlement.DeadlineSet>(DueAt = nameof(ArtFlow.Settlement.DeadlineSet.DueAt))]
    public static ExpireSettlement From(Settlement.DeadlineSet e, RecordedEvent r) => new((Id<Settlement>)r.StreamId);
    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Expire(context.Now);
}

public sealed partial record ReopenAuction(Id<Auction> Auction) : IChange<Auction>
{
    [OnEvent<Settlement.Expired>]                                                     // compensation: a forward change
    public static ReopenAuction From(Settlement.Expired e, RecordedEvent r) => new((Id<Auction>)r.StreamId);
    public Result Execute(Auction auction, ChangeContext context) => auction.Reopen();
}
```

| Concern | How | Evidence |
|---|---|---|
| State | an aggregate: events, stored state per commit, lifecycles, invariants, replay test | step 6, **[verified]** |
| Correlation | by id: the saga's id is the id of the aggregate it is about (another stream type, same `Guid`); else a unique lookup through `ILoads` + `Find` | **[verified]** (`(Id<Settlement>)recorded.StreamId`) |
| Start | `ILoads<T>` load-or-create + one `Begin` behaviour (S-15) | **[verified]** |
| Restart after an end (old S7) | the same `Begin`: an ended saga starts round 2 in place, same id | **[verified]** |
| Second start while running (old N1) | `Begin` returns Success, nothing raised | **[verified]** |
| Steps | changes with `[OnEvent<T>]` on events of any aggregate | **[verified]** |
| What it asks of others | an event (`Expired`), turned into a change of the other aggregate by a reaction (`ReopenAuction`) | **[verified]** |
| Timeouts | an event with a due time + a scheduled reaction (§4) | **[verified]** with a fake clock |
| Compensation (old S2–S4) | a forward change per compensation, itself a reaction with retries and a dead letter: a failed compensation is visible as a dead letter with its error | **[verified]** path (any reaction); no special API |
| Completion | a lifecycle state; late or repeated messages return Success | **[verified]** (late payment, timeout after completion) |
| The correlation index that only grows (old S6) | none exists: correlation is the id | by construction |

What Tarol adds for sagas is only the scheduled reaction (§4) and the guide; there is no `Saga<T>` base class, no saga
generator, and no saga store. The alternative SA-B (a saga type whose handlers return steps, the issued commands stored
in the saga's event) was rejected: its commands are typed by a library interface the user cannot annotate, and without
generated polymorphism metadata the source-generated JSON wrote them as `{}`, silently **[verified]**.

## 6. Tracing (tracing.md insight 3)

* `ActivitySource "Tarol.Sagas"`; one `ActivityKind.Consumer` activity per row, named `reaction <name>`, **linked** to
  the row's stored `traceparent`, never parented (a saga's next step can be days later) **[verified]**.
* Tags: `tarol.reaction`, `tarol.stream.type`, `tarol.stream.id`, `messaging.message.id` (event id), `tarol.attempt`,
  `tarol.verdict`; error status with the code on Retry/Bury **[verified]**.
* The reaction's commit runs inside that activity, so its events store the worker's `traceparent` and the next hop links
  to it **[verified]**. The claim must return `traceparent` (8C's claim does not read it **[verified by reading]**).
* In the Aspire dashboard: 20 producer spans, 40 reaction spans, every reaction span with its link **[verified]**.

## 7. Hosting, DI, Aspire (D-15, S-11)

```csharp
builder.Services.AddTarolReactions(GeneratedReactions.Set, o =>     // Tarol.Sagas
{
    o.Workers = 4;                                                  // loops in this process
    o.PollInterval = TimeSpan.FromMilliseconds(100);
    o.UseNotify = false;                                            // S-10: opt-in
    o.Policy = new DeliveryPolicy { MaxAttempts = 10 };             // S-13 defaults
});
```

* `ReactionHost : BackgroundService` runs the loops and the key purge. **.NET 11's base shared framework ships
  `Microsoft.Extensions.Hosting.Abstractions`, `DependencyInjection.Abstractions`, `Logging.Abstractions` and `Options`**:
  referencing the package is NU1510 (pruned) **[verified]**, so `Tarol.Sagas` needs no package reference for them.
* The store registration (`Tarol.Persistence.Npgsql`, or the in-memory store) provides the outbox port; `Tarol.Sagas`
  depends on `Tarol.Changes` and `Tarol.Persistence` only.
* **Aspire** (D-15): an AppHost 13.6.5 builds and runs on SDK 11 with a PostgreSQL resource (`postgres:18.3`), the worker
  as a project or as its **Native AOT** binary (`AddExecutable` + `WithOtlpExporter`), connection string by reference,
  dashboard telemetry through OpenTelemetry 1.21 **[verified]**. No runtime package depends on Aspire; the AppHost is a
  sample/prototype project (step 13), not published with AOT.

## 8. Testing (question 5)

| Level | Tool | Covers |
|---|---|---|
| reaction mapping | plain unit test of `From` | the factory |
| chain | `ReactionTest`: `InMemoryAggregateStore` with an outbox, a fake `TimeProvider`, `Drain()` runs every due row and the rows they cause; `Advance(3.Days())` makes timeouts due | event → reaction → change → its reactions, timeouts, dead letters **[verified]** as `InMemoryOutbox` + `ReactionWorker.Drain` |
| store | integration tests on Testcontainers | claim, head promotion race, DL-B, scheduled rows, lease loss, crash between run and `Complete` **[verified]** in the probe |
| generator | snapshots | table, schedule, switch, diagnostics, incrementality |

Feed and claim timing: claims do **not** filter on `pg_snapshot_xmin` (only the feed does) **[verified by reading]**, but
rows of open transactions are invisible, so tests that wait for a row poll with a timeout.

## 9. AOT and packages (S-11)

| Package | Step 9 adds |
|---|---|
| `Tarol.Changes` (+ generator) | `[OnEvent<T>]` (`Name`, `DueAt`), `ReactionSet`, generated `GeneratedReactions.Set`, TCHG006–TCHG009 |
| `Tarol.Persistence` | the outbox port (`IOutbox`, `OutboxRow`, `DeadLetter`), the in-memory outbox in `InMemoryAggregateStore`, the schedule in the reaction table |
| `Tarol.Persistence.Npgsql` | `head` flag claim, `due_at` from the schedule, scheduled reactions exempt, `outbox_dead`, `traceparent` in claims, NOTIFY opt-in |
| `Tarol.Sagas` (new) | `ReactionWorker`, `DeliveryPolicy`, `ReactionHost`, `AddTarolReactions`, `ReactionTest`, `ActivitySource "Tarol.Sagas"`, skill `tarol-sagas` |

The probe worker (store, pipeline, generated-style set, hosting, OpenTelemetry exporter) publishes with **0 IL warnings**
and passes all its checks natively against PostgreSQL **[verified]**. No meta-package yet (D-8): `Tarol.Sagas` is not the
top of the graph until steps 10–11.

## 10. What Sagas owns, and what it does not

Owns: the worker loop, the delivery policy, dead letters and re-drive, hosting, the reaction test helper, the saga guide.
Does not own: the pipeline (Changes), the reaction table and its generator (Changes), the SQL (Persistence.Npgsql), the
saga's rules (the user's aggregate), outgoing messages (Messaging, step 11).

## New insights

1. **A saga needs no saga runtime.** Aggregates already give state, concurrency, replay and lifecycles; reactions give
   steps, keys and retries; one scheduled reaction gives timeouts. What is left is a guide.
2. **Never answer a late, repeated or stale message with a Conflict.** C-9 retries every Conflict and dead-letters it;
   a saga's "already done" is `Success`.
3. **Order by version and order by time are different orders.** Timeouts must not queue behind each other per stream.
4. **An anti-join head is O(backlog).** A head that is fine for shallow backlogs collapses behind one hot stream or many
   waiting timeouts; a maintained flag keeps claims O(batch).
5. **NOTIFY is not free**: it serializes commits; latency is cheaper bought with a short poll.
6. **.NET 11 moved the hosting abstractions into the base framework**: a worker library needs no package reference.
7. **Name collisions bite generated code**: a member named like its type shadows it inside an attribute's `nameof`
   (`Id<Settlement> Settlement`), and the generated namespace from the assembly name (`Probe.Sagas.App`) collides with a
   user type named like its first segment (`Probe`) **[verified]**.

## Decisions taken (owner, 2026-11-15; migration/sagas.md §11)

S-1 reactions and sagas in step 9, reactions first; S-2 reactions are changes only; S-3 compose sets across assemblies;
S-4 a saga is an aggregate (SA-A); S-5 dead letters in `outbox_dead` (DL-B); S-6 the reaction generator in Tarol.Changes;
S-7 NotFound and 422 retry, then dead-letter; S-8 maintained head flag; S-9 scheduled reactions by time (T-2); S-10 poll
100 ms, NOTIFY opt-in; S-11 one new package `Tarol.Sagas`; S-12 three PRs 9A–9C; S-13 10 attempts, 1 s … 5 min;
S-14 reaction name = type name, pin to rename; S-15 sagas start load-or-create.

## Open questions

* Logging: source-generated `LoggerMessage` for Retry/Bury/lease loss (old S10 found none); the event ids in PR 9C.
* Metrics (`System.Diagnostics.Metrics`: rows/s, attempts, dead letters, lag) and a health check on `Pending`/dead letters.
* Priority between reactions (a slow reaction's backlog delays others in the same claim order): not needed by a scenario.
* Partitioning work across processes by tenant: `SKIP LOCKED` suffices for the measured scale.
