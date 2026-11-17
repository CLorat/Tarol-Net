# Operations

Concept note for step 15 (see [migration/operations.md](../migration/operations.md)): **a running Tarol system says how it is doing
without being asked twice, upgrades its own schema without stopping, and lets a human repair what it gave up on.**
* Every package counts what it does on its own meter. Database numbers (backlogs, dead letters, lag) come from one cached snapshot,
  never from a query per scrape.
* Health checks come in three groups: the process (`live`), its dependencies (`ready`), and Tarol's backlogs (`tarol`). A backlog
  never takes an API out of the load balancer.
* Schemas carry versioned upgrade steps. One process applies them under an advisory lock; old and new code run on one schema
  during a rolling deploy.
* Dead reactions, jobs and messages are listed page by page, re-driven or discarded, and every such act is audited.
* Tracing needs no Tarol package: the guide names the sources, and Tarol's own data sources stop tracing idle polls.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/operations/`](../migration/probes/operations/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: done** (owner decisions O-1 … O-8, [migration/operations.md](../migration/operations.md) §9); built in PRs 15-0 and
15A–15D (§15–§18 there). The guide: [docs/operations/README.md](../operations/README.md).

## 1. What an operator of a Tarol system needs

| Question at 03:00 | Answered by | Today (step 14) |
|---|---|---|
| Is the process alive? | `/health/live` | the sample's `MapGet("/health", () => "ok")` |
| Can it serve? (database, schema, checkpoint LISTEN) | `/health/ready` | nothing |
| Are reactions or jobs stuck, is a projection behind? | the gauges `tarol.reactions.oldest_due`, `tarol.jobs.overdue`, `tarol.projections.lag`; `/health/tarol` | `IOutbox.Pending` (a `count(*)`), `IJobRows.Overdue`, no lag |
| What failed, and how often? | the counters per package (`tarol.changes.runs{outcome}`, …), the logs | logs in Sagas, Messaging, Queries, Jobs; **none in the pipeline or the store** |
| What gave up, and can I fix it? | the admin entry: list, re-drive, discard; the audit | the first page of each dead table, re-drive by id, no discard |
| Can I deploy version N+1 while N runs? | schema steps with `compatible_from` | old code **refuses** a newer schema **[verified]** |
| Where did this request go? | traces of `Tarol.*` and `Npgsql` | 51 root spans/s from an idle worker if Npgsql is traced **[verified]** |

## 2. Metrics (O-1)

* **A meter per package**, named like its `ActivitySource`: `Tarol.Changes`, `Tarol.Sagas`, `Tarol.Jobs`, `Tarol.Queries`,
  `Tarol.Messaging`; `Tarol.Caching` and `Tarol.Security` exist. The database gauges are on `Tarol.Operations`. All meters are
  created from `IMeterFactory` (`Microsoft.Extensions.Diagnostics.Abstractions` is in the base framework).
* **Names** `tarol.<area>.<what>`, UCUM units (`s`, `By`, `{row}`, `{change}`). Durations are histograms in seconds, counters
  have no `_total`. Tags have low cardinality: a change, reaction, job, projection or consumer name, and an outcome. **Never the
  tenant** (unbounded, and data protection: step 16).
* **Cost [verified]**: `Counter.Add` with 2 tags costs 1.9 ns, or 10.8 ns with a listener; `Histogram.Record` 3.2 / 7.5 ns
  (Native AOT 7.7 / 13.7 and 5.2 / 6.4). That is about 10 ppm of a 1.8 ms commit: instruments go wherever the work is.
* **Database numbers are gauges over a snapshot.** One query round every 15 s (default) reads the oldest due outbox head, the
  outbox size estimate, the dead counts, the most overdue job, the feed head and each projection's lag in seconds. The round costs
  1.2–2.1 ms at any table size **[verified]**. Gauges read the last snapshot: a scrape costs about 1 µs. A gauge that queried
  instead would run `count(*)` for 32 ms at 1,000,000 outbox rows (76 ms with `WHERE head`), synchronously on the exporter's
  thread, in every process.

| Meter | Instrument | Kind, unit | Tags |
|---|---|---|---|
| `Tarol.Changes` | `tarol.changes.runs`, `tarol.changes.duration` | counter `{change}`, histogram `s` | `tarol.change`, `tarol.outcome` (committed, conflict, invalid, denied, not_found, error) |
| `Tarol.Sagas` | `tarol.reactions.runs`, `.duration`, `.lateness` (start − due) | counter `{row}`, histograms `s` | `tarol.reaction`, `tarol.outcome` (completed, retried, buried) |
| `Tarol.Jobs` | `tarol.jobs.occurrences`, `.duration`, `.lateness` | counter `{occurrence}`, histograms `s` | `tarol.job`, `tarol.outcome` (ran, retried, dead, skipped) |
| `Tarol.Queries` | `tarol.projections.events`, `.batches`, `.batch.duration` | counters `{event}` `{batch}`, histogram `s` | `tarol.projection`, `tarol.outcome` (written, refused) |
| `Tarol.Messaging` | `tarol.messages.consumed`, `.published`, `.duration` | counters `{message}`, histogram `s` | `tarol.consumer` or `tarol.contract`, `tarol.outcome` |
| `Tarol.Operations` (snapshot) | `tarol.reactions.backlog`, `.oldest_due`, `.dead`; `tarol.jobs.overdue`, `.dead`; `tarol.projections.lag`; `tarol.messages.dead`, `.backlog` (PostgreSQL bus) | gauges `{row}`, `s` | `tarol.projection`, `tarol.subscription` |
| `Tarol.Security` | `tarol.authorization.denied` | counter, unit `{request}` added | unchanged |

## 3. Health checks (O-2)

`Microsoft.Extensions.Diagnostics.HealthChecks` (`IHealthCheck`) is in the ASP.NET Core shared framework, **not** in the base
framework **[verified]**. The checks live in `Tarol.Operations`, which takes the shared framework (no NuGet dependency). A worker
that wants Kubernetes probes needs an HTTP listener anyway. In Native AOT that costs +6.6 MB (10.75 → 17.36 MB); the checks
alone would cost +177 KB, and every build has 0 IL warnings **[verified]**.

| Group (tag) | Checks | Fails when |
|---|---|---|
| `live` | the process answers | never on the database: a restart does not fix PostgreSQL |
| `ready` | the snapshot is fresh (database reachable), schema compatible (§4), the checkpoint `LISTEN` is up where caching uses it (`NpgsqlProjectionPositions.IsListening`) | the process cannot serve correctly |
| `tarol` | oldest due reaction > 30 s Degraded, > 5 min Unhealthy; dead letters > 0 Degraded; a job overdue > 1 min / 10 min; projection lag > 30 s / 5 min (configurable) | dashboards and alerts read it; **not** readiness: a backlog must not take a healthy API pod out of the load balancer |

The checks read the snapshot (9 µs per report natively, no round trip) **[verified]**. `MapTarolHealth()` maps `/health/live`,
`/health/ready` and `/health/tarol`.

## 4. Schema upgrades (O-4)

Each Npgsql script (store, bus, `messages_dead`) gets **versioned steps**. `EnsureSchema` does this:
1. It takes a **session** advisory lock per schema and **polls** `pg_try_advisory_lock` every 100 ms. A session blocked in
   `pg_advisory_lock` is a transaction that `CREATE INDEX CONCURRENTLY` waits for: deadlock in 100 of 120 starts **[verified]**.
2. It runs the version 1 script only on an empty schema. Re-running it at every start takes table locks for nothing.
3. It runs the missing steps in order, each with `lock_timeout` 2 s and retries. A DDL behind a 3 s reader blocks every commit for
   2.9 s; with `lock_timeout` 200 ms and retries the worst commit waits 206 ms **[verified]**.
4. `CONCURRENTLY` steps run outside a transaction and first drop an invalid index left by an interrupted build.
5. It bumps `schema_version` in the step's transaction.

`schema_version` gains **`compatible_from`**: the oldest code version that still runs on the schema. Expand steps keep it, contract
steps raise it. Code that knows version K starts on version V > K while `compatible_from` ≤ K, and refuses with both numbers
otherwise. Today it refuses any newer schema, so a rolling deploy's old pods crash-loop **[verified]**.

Tarol's own steps are **additive within a major version**: nullable columns or constant defaults (5–12 ms, no commit slower than
8 ms on 1,000,000 events), `CREATE INDEX CONCURRENTLY` (no commit slower than 11 ms). Forbidden: volatile defaults (a 5.6 s
rewrite that blocks commits 5.3 s), a plain `CREATE INDEX` (commits blocked 370 ms), drops and renames, which go into a contract
step of a major version.

`NpgsqlSchema.UpgradeScript(from)` hands the same SQL to a migration tool. `EnsureSchema = VerifyOnly` checks only and fails fast.
Today, 6 processes starting at once on an empty database fail 99–100 of 120 times (`23505` on `pg_namespace`) **[verified]**: the
lock fixes that too.

## 5. Dead letters and the admin entry (O-3, O-5)

* The ports list **pages**: `After` (keyset on id), `Limit` (≤ 200), `Tenant`, `Name`. Today each returns only its first page
  **[verified]**. The ports gain `Discard(id)`.
* `IDeadLetterAdmin` covers the three kinds (reactions, jobs, messages), and lists the projection versions with their state, lag
  and size (the drop of §6).
* **Audit**: every re-drive, discard and drop writes a row in `operations_audit` (when, actor, action, target, detail) in the same
  statement, plus a log line. `DELETE … RETURNING` feeds the audit `INSERT` **[verified]**. The actor is the principal's name.
* `MapTarolAdmin(prefix, policy)` maps the endpoints; the policy is required (fail closed). Re-drive by filter is capped at 1,000 rows
  per call.
* **Retention**: dead letters are a human's work items and are **kept** by default. They are counted in the gauges and Degraded
  in `/health/tarol`. An optional `DeadRetention` per table runs as the system job `tarol.purge-dead` (`MaintenanceTask`, J-9).
  The `dead_at` indexes come with the version 2 steps: an hourly run that finds nothing scans 21–37 ms without one, 0.6–0.8 ms with
  one **[verified]**.

## 6. Read models (O-5, O-8)

* `DropVersion` stays explicit (QR-3) and is never scheduled. It gets `lock_timeout` and retries. Today it waits 2.8 s behind a 3 s
  reader, and a reader arriving meanwhile waits 2.6 s and then fails with `42P01`. With 200 ms it gives up after 203 ms and readers
  are not delayed **[verified]**.
* **One runner per projection version**: the runner holds a session `pg_try_advisory_lock`; the others retry every second and
  take over when that session ends. Without the gate, 4 runners waste 591 of 791 batches and take 2.55 s instead of 1.62 s
  **[verified]**. The checkpoint guard stays as the safety net.

## 7. Logs and tracing (O-1, O-6)

* **Logs** (`[LoggerMessage]`, 3.9–7.5 ns disabled **[verified]**), new event ids by family:
  * `Tarol.Changes` 7001–: a change that failed with an error, a conflict at Debug;
  * `Tarol.Persistence.Npgsql` 8001–: schema upgraded, step retried after a lock timeout, purge counts;
  * `Tarol.Operations` 15001–: re-drive, discard, drop with the actor, a stale snapshot.
* **Traces**: a new `ActivitySource` `Tarol.Changes` makes one span per pipeline run, a child of HTTP, a reaction or a job. No
  Tarol OpenTelemetry package and no OpenTelemetry dependency. The guide says `AddSource("Tarol.*", "Npgsql")`,
  `AddMeter("Tarol.*", "Npgsql")`.
* **Npgsql spans**: an idle worker makes 51 root spans/s, 4.4 million a day **[verified]**. The data sources Tarol builds itself
  (`UseNpgsqlStore(connectionString)`, the bus) get `ConfigureCommandFilter`/`ConfigureBatchFilter(_ => Activity.Current is not null)`
  by default, with an opt-out. Idle: 0 root spans; the commands inside reactions and changes stay as children **[verified]**.

## 8. Packages (O-7)

* The contracts go in `Tarol.Persistence`, as 14C's `MaintenanceTask` did: a snapshot part (`OperationsSignal`) and a dead-letter
  source (`IDeadLetterSource`) that each package registers. So nothing references upward.
* The new package **`Tarol.Operations`** (ASP.NET Core shared framework, depends on `Tarol.Persistence`) runs the snapshot poll,
  the gauges, the checks, the admin API and endpoints, and `tarol.purge-dead`.
* Instruments, logs, port paging, the root filter and the gate are additions in their own packages.
* PRs: **15-0** the SignalR test fix, **15A** schema upgrades, **15B** instruments and per-package additions, **15C**
  `Tarol.Operations`, **15D** guide and sample.

## New insights

* `EnsureSchema` at every process start is itself an operation: without a lock it fails concurrent first starts, and with a
  blocking lock it deadlocks the first `CONCURRENTLY` step.
* "Pending" as `count(*)` is the wrong number: it costs 32 ms at 1,000,000 rows and says nothing about staleness. The age of the
  oldest due head (an index lookup, 0.4 ms) is the alert signal.
* Readiness is not where backlogs belong: the API and the worker share one database, but only the worker can drain it.

## Decisions taken (owner, 2026-11-17; migration/operations.md §9)

O-1 meters per package + snapshot gauges; O-2 `IHealthCheck` in three groups; O-3 library API + endpoints with audit; O-4 versioned
steps in `EnsureSchema` with `compatible_from`; O-5 dead letters kept, drops explicit; O-6 guidance + Npgsql root filter + a
`Tarol.Changes` source; O-7 `Tarol.Operations`, 15-0 + 15A–15D; O-8 the projection gate, on by default.

## Open questions

* An `IHealthCheck` for the message transports (NATS, RabbitMQ connection state) and their broker-side backlog (JetStream
  `num_pending`, queue depth): in 15C if the transports expose it cheaply, else later.
* Exemplars (trace ids on histogram points) and per-tenant numbers: not without step 16's rules.
