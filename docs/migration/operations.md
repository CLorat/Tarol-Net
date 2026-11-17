# Operations: evaluation (step 15 design)

This note evaluates [operations.md](../architecture/operations.md). It covers:
* the inventory of the old code and of what Tarol-Net already has;
* metrics: meters, names, the cost of instruments, and gauges over the database;
* health checks: the base framework, groups and thresholds, the AOT size;
* the admin entry for dead letters and dead jobs: paging, re-drive, discard, audit;
* schema upgrades beyond version 1, probed on a populated database with writers committing;
* retention of the dead tables and the drop of old read-model versions;
* OpenTelemetry: sources, meters and the Npgsql spans of an idle worker;
* several runners of one projection;
* the owner's answers, and what PRs 15-0 and 15A–15D must do.

**Design only**: this PR changes no package code. Probe and output: [`probes/operations/`](probes/operations/),
[`output.txt`](probes/operations/output.txt), the inventory [`inventory.md`](probes/operations/inventory.md). Labels as in the
[conventions](../architecture/conventions.md). Source of the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old code has nothing to migrate** **[verified by reading]**:
  * `src/Diagnostics/Diagnostics.HealthChecks` and `Diagnostics.OpenTelemetry` are empty project files (4 lines, no source);
  * one connection-flag `IHealthCheck` of the old backplane, already replaced in step 11;
  * the `Tarol.Sagas` activity source, already carried over in step 9;
  * no meter, no source-generated log, no schema version, no retention; the template's `Tarol.Migrations` does not exist.
* **Decided** (O-1 … O-8):
  * **Metrics** (O-1): one meter per package named like its `ActivitySource`, `tarol.<area>.<what>` with UCUM units, durations
    as histograms in seconds, tags without the tenant, `IMeterFactory`. Database numbers are observable gauges over a **snapshot
    polled every 15 s**, never a query per scrape.
  * **Health** (O-2): Microsoft's `IHealthCheck` in three groups. `live` is the process, `ready` its dependencies, `tarol` the
    backlogs; a backlog never fails readiness.
  * **Admin** (O-3): keyset paging, filters and `Discard` on the ports, `IDeadLetterAdmin`, an audit row in the same statement,
    `MapTarolAdmin(prefix, policy)` with a required policy.
  * **Schema** (O-4): versioned steps run by `EnsureSchema` under a polled session advisory lock, with `lock_timeout` and retries;
    `schema_version.compatible_from` lets old and new code share a schema; `UpgradeScript(from)` and `VerifyOnly` for migration
    tools.
  * **Retention** (O-5): dead letters are kept until a human acts, with an optional `tarol.purge-dead`; `DropVersion` stays
    explicit, with a `lock_timeout`.
  * **Tracing** (O-6): guidance only, `Tarol.*` wildcards. Tarol's own data sources skip root Npgsql commands by default, and a
    new `Tarol.Changes` source makes one span per pipeline run.
  * **Packages** (O-7): a new **`Tarol.Operations`** with contracts in `Tarol.Persistence`. Five PRs: 15-0 (the SignalR test
    fix), 15A–15D.
  * **Projections** (O-8): one runner per projection version behind a session advisory lock, on by default.
* **Measured** (4 cores, PostgreSQL 17 in Docker on the same host, SDK 11.0.100, JIT Release and Native AOT):
  * **Instruments:** `Counter.Add` 1.9 ns, 10.8 ns with a listener; `Histogram.Record` 3.2 / 7.5 ns; a disabled
    `[LoggerMessage]` 7.5 ns. About 10 ppm of a 1.8 ms commit.
  * **Gauges:** today's `Pending` (`count(*)`) takes 32 ms at 1,000,000 outbox rows, and run in a gauge callback it blocks the
    exporter 32 ms per scrape. One snapshot round (7 numbers) costs 1.2–1.7 ms at any size; a scrape of the cached gauges 1 µs.
  * **Health:** a report over the snapshot costs 9 µs natively. Native AOT, all with 0 IL warnings: 10.75 MB; +177 KB with the
    health-check package; +6.6 MB with ASP.NET Core, Kestrel and `MapHealthChecks`.
  * **Schema:**
    * 6 concurrent `EnsureSchema` on an empty database fail 99–100 of 120 times (`23505`);
    * a blocking advisory lock plus `CREATE INDEX CONCURRENTLY` deadlocks 100 of 120 times (`40P01`); a polled lock gives 0;
    * old code refuses a version 2 schema;
    * on 1,000,000 events with 4 writers: `ADD COLUMN` 5–12 ms (worst commit 8 ms), a volatile default 5.6 s (commits blocked 5.3 s),
      `CREATE INDEX` 370 ms (commits blocked 370 ms), `CONCURRENTLY` 0.44 s (worst commit 11 ms);
    * behind a 3 s reader, commits wait 2.9 s, or 206 ms with `lock_timeout` 200 ms and retries.
  * **Dead letters:** the three ports return their first page only; keyset paging reaches all 250; a discard plus its audit is one
    statement.
  * **Retention:** an hourly purge that finds nothing scans 21–37 ms without a `dead_at` index and 0.6–0.8 ms with one.
  * **DropVersion:** behind a 3 s reader the drop waits 2.8 s, and a reader arriving meanwhile waits 2.6 s, then `42P01`; with
    `lock_timeout` 200 ms the drop gives up after 203 ms and readers do not wait.
  * **Tracing:** an idle worker makes 51 root Npgsql spans/s (4.4 million a day); with the filter, 0.
  * **Runners:** 4 runners of one projection waste 591 of 791 batches (75 %) and take 2.55 s; gated, 0 wasted, 1.62 s.
* **Found** (§5):
  * the store's `EnsureSchema` has no lock and refuses newer schemas;
  * a blocking advisory lock deadlocks `CONCURRENTLY`;
  * an interrupted `CONCURRENTLY` build leaves an invalid index that `IF NOT EXISTS` keeps;
  * `Pending` is a full count;
  * dead-letter ports cannot page or discard;
  * the dead tables have no `dead_at` index;
  * `DropVersion` has no `lock_timeout`;
  * the sample leaves `Tarol.Sagas` out of its traces;
  * `Tarol.Security`'s counter has no unit and no `IMeterFactory`;
  * the pipeline has no span;
  * `UseNpgsqlReadModels` needs an `NpgsqlDataSource` in DI that `UseNpgsqlStore(Func<…>)` does not register;
  * the SignalR reconnect test waits a fixed 300 ms.

## 1. Inventory **[verified by reading]**

Condensed; every file:line is in [`inventory.md`](probes/operations/inventory.md).

| Where | What | Verdict |
|---|---|---|
| `CLorat/Tarol` `src/Diagnostics/Diagnostics.{HealthChecks,OpenTelemetry}/*.csproj` | empty project files | nothing to migrate (plan line 54) |
| `CLorat/Tarol` `Messaging.Backplane/MessagingBackplaneHealthCheck.cs:5-15` | `IHealthCheck` on a connection flag; the abstractions as a NuGet reference (`Directory.Packages.props:46`) | the shape survives as the `IsListening` input of `ready` |
| `CLorat/Tarol` `Sagas/Internals/SagaInternals.cs:44-53` | `ActivitySource "Tarol.Sagas"` | carried over in step 9 |
| `CLorat/Tarol` `templates/TarolSolution/…/Program.cs` | `AddNpgSql` (third-party), EF Core `AddDbContextCheck`, `Tarol.Migrations` (missing) | not migrated (D-14, plan) |
| Tarol-Net meters | `Tarol.Caching` (`IMeterFactory`, `{page}`, `By`), `Tarol.Security` (static `new Meter`, no unit) | the naming pattern; Security aligned in 15B |
| Tarol-Net sources | `Tarol.Sagas`, `Tarol.Jobs`, `Tarol.Queries`, `Tarol.Messaging` (+ transports) | no `Tarol.Changes` |
| Tarol-Net logs | 1001–1002, 9001–9011, 11001–11415, 12001–12103, 14001–14010 | none in the pipeline or the store |
| `NpgsqlSchema.cs:12` `Version = 1`; `NpgsqlAggregateStore.cs:103-110` | one script, re-run at every start, **no lock**, refuses a newer version | §3.1 |
| `NpgsqlBus.cs:92-125`, `NpgsqlMessageDeadLetters.cs:44-60` | bus version 1 under an xact lock; `messages_dead` without a version | §3.1 |
| `IOutbox.DeadLetters(max)`, `IJobRows.DeadJobs(max)`, `IMessageDeadLetters.List(max)` | the first page; `Redrive(id)` / `Take(id)`; no discard | §3.2 |
| `IOutbox.Pending` (`count(*)`), `IJobRows.Overdue`, `NpgsqlBus.Pending(subscription)`, `NpgsqlProjectionPositions.IsListening`, `ProjectionHost.Conflicts` | the numbers that exist | inputs of the snapshot |
| `NpgsqlReadModels.DropVersion` | in-process "in use" check, no `lock_timeout` | §4.6 |
| `MaintenanceTask`, `IMaintenanceHost` (14C) | a contract in `Tarol.Persistence` run by `Tarol.Jobs` | the pattern for the contracts of O-7 |
| `samples/Sagas` | `/health` returns "ok"; traces without `Npgsql` and without `Tarol.Sagas` | 15D |

**Base framework [verified]** (`~/.dotnet/shared/*/11.0.0`): `Microsoft.NETCore.App` has `Microsoft.Extensions.Diagnostics.Abstractions`
(`IMeterFactory`) and `System.Diagnostics.DiagnosticSource`. Only `Microsoft.AspNetCore.App` has `…Diagnostics.HealthChecks(.Abstractions)`
and `Microsoft.AspNetCore.Diagnostics.HealthChecks`.

## 2. Candidates

### 2.1 Metrics (question 1; O-1)

| | **per package + snapshot** (O-1) | one `Tarol` meter | per package, gauges query live |
|---|---|---|---|
| meters | `Tarol.Changes`, `.Sagas`, `.Jobs`, `.Queries`, `.Messaging`, `.Caching`, `.Security`, `.Operations` (= the source names) | `Tarol` | per package |
| configuration | `AddMeter("Tarol.*")` | `AddMeter("Tarol")` | `AddMeter("Tarol.*")` |
| existing meters | unchanged | `Tarol.Caching`, `Tarol.Security` renamed (a behaviour change) | unchanged |
| DB numbers per scrape | 1 µs (cached) | 1 µs | 32 ms at 1M rows (`count(*)`), 0.4 ms (`min(due_at)`), per process, synchronously |
| freshness | ≤ 15 s (configurable) | ≤ 15 s | the scrape |

Names follow the OpenTelemetry conventions: lowercase dotted names, UCUM units (`s`, `By`, `{row}`), histograms for durations in
seconds, no `_total`. Semantic-convention names (`messaging.process.duration`, `db.client.operation.duration`) belong to the
transports' and Npgsql's own instrumentation. Tarol's names are Tarol's.

### 2.2 Health checks (question 2; O-2)

| | **`IHealthCheck` in `Tarol.Operations` (shared framework)** (O-2) | `IHealthCheck` via NuGet in worker packages | own interface |
|---|---|---|---|
| dependency | `FrameworkReference Microsoft.AspNetCore.App` (allowed: not a NuGet package) | `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` 11.0.0 in Sagas, Jobs, Queries | none |
| Native AOT **[verified]** | 17.36 MB with Kestrel + `MapHealthChecks` (+6.6 MB), 0 IL warnings | 10.93 MB (+177 KB), 0 IL warnings | 10.75 MB |
| Kubernetes / Aspire | `MapHealthChecks` endpoints, `WithHttpHealthCheck` | the app maps them | the app adapts |

Readiness vs backlogs **[verified]** (`selftest health`). A stuck reaction (10 min), 3 dead letters, a job 2 min overdue and a
projection 45 s behind give `tarol`: Unhealthy / Degraded / Degraded / Degraded, while `live` stays Healthy. If those checks were
readiness, the API pods would leave the load balancer while only the worker can drain the backlog.

### 2.3 Admin entry (question 3; O-3)

| | **library API + endpoints** (O-3) | library API only | endpoints, audit as an event |
|---|---|---|---|
| paging | keyset (`id > after`), `Limit ≤ 200`, `Tenant`, `Name` | the same | the same |
| re-drive | by id; by filter ≤ 1,000 | the same | the same |
| discard | `Discard(id)` (new) | the same | the same |
| audit | `operations_audit` row in the same statement + log | the same | event on a system stream `tarol.operations`: needs a stream type and codec registration in every app |
| HTTP | `MapTarolAdmin(prefix, policy)`, policy required | each app writes its own | as recommended |

rest.md §8 named "an `[Endpoint]` with a role". Dead letters are not aggregates and a re-drive is not a change, so the generated
change entries do not fit. The admin endpoints are plain minimal-API endpoints with an authorization policy (fail closed: no
policy, no mapping).

### 2.4 Schema upgrades (question 4; O-4)

| | **steps in `EnsureSchema`** (O-4) | migration tool only | today's script |
|---|---|---|---|
| concurrent first start | 0 failures (polled lock) **[verified]** | the tool's | 99–100 of 120 fail **[verified]** |
| rolling deploy | `compatible_from` **[verified]** | the tool's discipline | old code refuses **[verified]** |
| `CONCURRENTLY` | outside a transaction, invalid leftovers dropped **[verified]** | the tool's | impossible (one implicit transaction) |
| locks | `lock_timeout` 2 s + retries | the tool's | none: a DDL behind a reader blocks every commit |
| tool users | `UpgradeScript(from)`, `VerifyOnly` | yes | `Script()` |

### 2.5 Retention and drops (question 5; O-5)

| | **keep dead letters; drops explicit** (O-5) | retention 30 days by default | also schedule drops |
|---|---|---|---|
| dead letters | kept; optional `DeadRetention` → `tarol.purge-dead` | deleted after 30 days, logged | kept |
| `DropVersion` | explicit (QR-3), `lock_timeout` + retries, listed in the admin | explicit | a system job N days after the successor is current |
| risk | a table grows until a human acts (Degraded in `tarol`) | an unhandled failure disappears silently | a still-running old process loses its table (`42P01`, §4.6) |

### 2.6 OpenTelemetry (question 6; O-6)

| | **guidance + root filter** (O-6) | `Tarol.OpenTelemetry` | guidance only |
|---|---|---|---|
| dependency | none | OpenTelemetry in a Tarol package | none |
| idle Npgsql spans **[verified]** | 0 (filter on Tarol-built data sources) | 0 if it configures the filter | 51/s unless the app filters |
| configuration | `AddSource("Tarol.*", "Npgsql")`, `AddMeter("Tarol.*", "Npgsql")` | `AddTarolInstrumentation()` | as recommended |

### 2.7 Packages (question 7; O-7)

| | **`Tarol.Operations` + contracts** (O-7) | `Tarol.Diagnostics` | no new package |
|---|---|---|---|
| graph | `Tarol.Operations` → `Tarol.Persistence` (+ shared framework); packages register `OperationsSignal`, `IDeadLetterSource` | the same | `Tarol.Changes.AspNetCore` → Sagas, Jobs, Messaging contracts (upward) |
| workers without HTTP | snapshot and gauges still work; checks need the shared framework | the same | — |

### 2.8 Projection runners (question 8; O-8)

| | **gate on by default** (O-8) | guard only | gate opt-in |
|---|---|---|---|
| 4 runners, 200,000 events **[verified]** | 1.62 s, 0 wasted | 2.55 s, 591 of 791 batches wasted | as either |
| take-over after a crash | when the session ends (the lock dies with it) | immediate (no lock) | — |

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/operations/output.txt) §1, §3: JIT and Native AOT)

### 3.1 Schema (`selftest schema`, 11 of 11 on both)

| Check | Result |
|---|---|
| today's `EnsureSchema`, 6 at once on an empty schema, 20 rounds | 99 (JIT) / 100 (AOT) of 120 fail, `23505 pg_namespace_nspname_index` |
| the upgrader with a **blocking** `pg_advisory_lock` | 100 of 120 fail, `40P01`: the waiters are transactions `CREATE INDEX CONCURRENTLY` waits for |
| the upgrader **polling** `pg_try_advisory_lock` every 100 ms | 0 failures; the 3 steps ran once |
| old code (knows 1) on a version 2 schema | today: `InvalidOperationException` ("has version 2; … knows version 1") |
| `compatible_from` 1, schema 4, code knows 1 | starts, runs no step |
| `compatible_from` 3, code knows 2 | refuses, with both numbers |
| today's `EnsureSchema` re-run 95 times in 10 s while 4 writers commit | no failure; commits p50 3.0 ms, p99 6.7 ms, max 22 ms |
| populated store, two upgraders at once, a writer committing | steps ran once (3 + 0), both at version 4, indexes valid |
| `CREATE INDEX CONCURRENTLY` in a transaction | `25001` |
| an interrupted `CONCURRENTLY` build (statement timeout) | an **INVALID** index remains; the upgrader drops it and builds it again |

### 3.2 Dead letters (`selftest dead`, 9 of 9 on both)

| Check | Result |
|---|---|
| `IOutbox.DeadLetters(100)` of 250 | ids 1…100; no cursor: 101–250 unreachable |
| `IJobRows.DeadJobs(max)`, `IMessageDeadLetters.List(max)` (120 rows) | the same shape, 100 of 120 |
| keyset paging with tenant and reaction filters | 100 + 100 + 50 |
| `IOutbox.Redrive(id)` | the row is back, due now; an audit row is a second statement today |
| discard + audit in one statement (`DELETE … RETURNING` → `INSERT`) | 1 audit row with actor, action, target, reaction and tenant |
| `dead_at` indexes on `outbox_dead`, `jobs_dead`, `messages_dead` | none |

### 3.3 Health (`selftest health`, 3 of 3; 4 of 4 with `WithWeb`)

| Check | Result |
|---|---|
| snapshot after a catch-up | database up, lag 0 s, no dead letters |
| stuck reaction 10 min, 3 dead, job 2 min overdue, projection 45 s behind | oldest 601 s, dead 3, overdue 122 s, lag 46 s |
| `HealthCheckService` over the snapshot | backlog checks Unhealthy/Degraded, `live` Healthy; 30 µs (JIT) / 9 µs (AOT) per report |
| Kestrel `MapHealthChecks` | `/health/live` 200, `/health/ready` 503 (the probe still tagged the backlog checks `ready`; O-2 moves them to `tarol`) |

### 3.4 Spans (`selftest spans`, 2 of 2 on both)

| Npgsql tracing | idle worker, 10 s | 20 creations + 20 changes with reactions, 3 s |
|---|---|---|
| default | 514–515 root Npgsql spans (51/s) | 92–122 child, 232–251 root Npgsql; 20 `Tarol.Sagas` roots |
| `ConfigureCommandFilter`/`ConfigureBatchFilter(_ => Activity.Current is not null)` | **0** | 104–128 child Npgsql; the pipeline's own commands (no span: no `Tarol.Changes` source) are gone |

## 4. Measurements **[verified]** ([`output.txt`](probes/operations/output.txt); 4 cores, 15 GB, SDK 11.0.100, PostgreSQL 17 with default settings in Docker on the same host, Npgsql 10.0.3; JIT runs in Release)

### 4.1 Instruments on the hot path (§2 JIT, §9 Native AOT)

| operation | JIT, no listener / listener | Native AOT, no listener / listener |
|---|---|---|
| `Counter<long>.Add(1, 2 tags)` | 1.90 / 10.8 ns | 7.67 / 13.7 ns |
| `Histogram<double>.Record(v, 2 tags)` | 3.15 / 7.47 ns | 5.15 / 6.39 ns |
| `[LoggerMessage]` Debug, disabled | 7.52 ns | 3.87 ns |
| one commit (1 event) | p50 1.77 ms, p99 5.29 ms | p50 1.78 ms, p99 3.22 ms |

A counter and a histogram per commit with a listener: 10–11 ppm of the p50 commit.

### 4.2 Gauges (§4): 1,000,000 events, 10,000 job rows

| outbox rows | `count(*)` (`Pending`) | `count(*) WHERE head` | oldest due head | `reltuples` | `count(*)` dead (rows/100) | snapshot (7 numbers) |
|---|---|---|---|---|---|---|
| 0 | 0.45 ms | 0.37 ms | 0.50 ms | 0.45 ms | 0.25 ms | 1.43 ms |
| 10,000 | 0.90 ms | 0.61 ms | 0.54 ms | 0.34 ms | 0.32 ms | 1.15 ms |
| 100,000 | 6.93 ms | 2.84 ms | 0.41 ms | 0.30 ms | 0.37 ms | 1.20 ms |
| 1,000,000 | 32.4 ms | 76.1 ms | 0.36 ms | 0.22 ms | 0.94 ms | 1.68 ms |

* Lag as an event count (500,000 behind): 60 ms; lag in seconds (two index lookups): inside the snapshot.
* `IJobRows.Overdue` at 10,000 rows: 0.46 ms.
* A scrape of 6 cached gauges: 987 ns. A gauge that runs `Pending` at 1,000,000 rows: 31.8 ms per scrape, on the exporter's thread.
* `reltuples` is −1 (shown 0) before the first `ANALYZE`: the estimate is a size, not an alert signal. The alert signal is the age
  of the oldest due head.

### 4.3 Schema changes on 1,000,000 events (363 MB) with 4 writers (§5)

| step | DDL time | commit p50 | p99 | max |
|---|---|---|---|---|
| baseline, no DDL | — | 3.57 ms | 7.48 ms | 9.45 ms |
| `ADD COLUMN x text NULL` | 11.5 ms | 3.00 | 6.00 | 7.90 |
| `ADD COLUMN y integer NOT NULL DEFAULT 0` | 4.91 ms | 3.12 | 6.29 | 6.90 |
| `ADD COLUMN z uuid … DEFAULT gen_random_uuid()` (rewrite) | 5,595 ms | 2.91 | 52.8 | **5,347** |
| `CREATE INDEX` | 370 ms | 3.18 | 370 | **372** |
| `CREATE INDEX CONCURRENTLY` | 439 ms | 3.26 | 8.58 | 10.8 |
| `CREATE INDEX CONCURRENTLY` on `outbox_dead`, 100,000 rows | 63.4 ms | 3.08 | 5.97 | 8.05 |
| `ADD COLUMN` behind a 3 s reader | 2,910 ms | 3.20 | 2,897 | **2,898** |
| the same, `lock_timeout = 200ms`, retried | 3,028 ms | 3.03 | 24.8 | 206 |

### 4.4 Retention of the dead tables, 200,000 rows each (§6)

| table | `dead_at` index | purge of 100,000 (batches of 1,000) | per batch | next run, nothing to purge |
|---|---|---|---|---|
| `outbox_dead` | no / yes | 708 / 781 ms | 7.0 / 7.7 ms | 33.3 / **0.78** ms |
| `jobs_dead` | no / yes | 730 / 819 ms | 7.2 / 8.1 ms | 37.1 / **0.68** ms |
| `messages_dead` | no / yes | 3,405 / 3,808 ms | 34 / 38 ms | 21.3 / **0.59** ms |

### 4.5 Several runners of one projection, 200,000 events, batch 1,000 (§8 JIT, §9 Native AOT)

| runners | gate | JIT | AOT | batches run | refused (wasted) |
|---|---|---|---|---|---|
| 1 | none | 2.17 s | 1.86 s | 200 | 0 |
| 2 | none | 1.87 s | 2.10 s | 396 / 399 | 196 / 199 |
| 4 | none | 2.55 s | 2.67 s | 791 / 780 | 591 / 580 |
| 2 | advisory lock | 1.61 s | 1.82 s | 200 | 0 |
| 4 | advisory lock | 1.62 s | 1.71 s | 200 | 0 |

O-8 was asked with an earlier JIT run of the same bench (1.98 / 1.71 / 2.26 s, 198 and 592 wasted, gated 1.51 s); `output.txt`
is the run of record. The gated runs are faster than one runner because they run after it, on a warm table and cache.

### 4.6 `DropVersion` while another session reads the old table for 3 s (§7)

| drop | result | a reader arriving meanwhile |
|---|---|---|
| today's `DropVersion` | 2,809 ms, dropped | waited 2,612 ms, then `42P01` |
| `DROP TABLE` with `lock_timeout = 200ms` | 203 ms, `55P03` | waited 1.9 ms, read |

### 4.7 Native AOT builds (§3)

| build | IL warnings | binary |
|---|---|---|
| no health checks | 0 | 10,751,592 bytes |
| + `Microsoft.Extensions.Diagnostics.HealthChecks` 11.0.0 | 0 | 10,928,344 bytes |
| + ASP.NET Core shared framework, Kestrel, `MapHealthChecks` | 0 | 17,359,320 bytes |

## 5. Found while probing

1. **Concurrent first starts fail.** The store's `EnsureSchema` takes no lock. Six processes on an empty database (an Aspire
   AppHost starting API and worker together) fail 99–100 of 120 starts with `23505` on `CREATE SCHEMA IF NOT EXISTS`. The bus and
   the read models already take a transaction-level advisory lock.
2. **A blocking advisory lock deadlocks `CREATE INDEX CONCURRENTLY`.** `CONCURRENTLY` waits for every older transaction, and a
   session blocked in `pg_advisory_lock` is one. Waiters must poll `pg_try_advisory_lock`.
3. **An interrupted `CONCURRENTLY` build leaves an INVALID index** that `CREATE INDEX … IF NOT EXISTS` then keeps for ever. A
   step must drop an invalid index of its name first.
4. **Old code refuses a newer schema**: the rolling-deploy blocker that `compatible_from` removes.
5. **`Pending` is a full count**: 32 ms at 1,000,000 rows and no notion of age. The oldest due head is an index lookup (0.4 ms)
   and the signal an alert needs.
6. **Observable-gauge callbacks are synchronous**: a database query in one is sync over async on the exporter's thread.
7. **Dead-letter ports return only their first page and cannot discard.**
8. **The dead tables have no `dead_at` index**: retention scans them every run.
9. **`DropVersion` has no `lock_timeout`**: it waits behind readers and makes new readers wait behind it; it also checks "in use"
   only for its own process.
10. **The pipeline has no span**: with the root filter, a change outside HTTP, a reaction or a job has no trace at all.
11. **`Tarol.Security`'s counter** has no unit and uses a static `Meter`, unlike `Tarol.Caching`'s `IMeterFactory`.
12. **The sample's worker traces leave out `Tarol.Sagas`** (only the broker's, the producer's, `Tarol.Queries` and `Tarol.Jobs`).
13. **`UseNpgsqlReadModels` resolves `NpgsqlDataSource` from DI**, which `UseNpgsqlStore(Func<IServiceProvider, NpgsqlDataSource>)`
    does not register. Documented in 15D, or registered by 15B.
14. **`PushTests.After_a_reconnect_the_subscriptions_are_replayed`** waits a fixed 300 ms "for the replay on Reconnected". Under
    load the replay takes longer, the message goes out before the server knows the subscription, and `Until` times out. Fix:
    publish a marker every 200 ms until the first one arrives (bounded by the event, 15 s cap), then assert (15-0).

## 6. Decided design (sketch for 15-0 and 15A–15D; names final in the PRs)

### 6.1 Contracts in `Tarol.Persistence` (15B)

```csharp
// one part of the operations snapshot: a package registers it as a singleton (like MaintenanceTask)
public sealed record OperationsSignal(string Meter, string Name, string Unit, string Description,
    Func<IServiceProvider, CancellationToken, Task<IReadOnlyList<SignalValue>>> Read);
public readonly record struct SignalValue(double Value, string? TagName = null, string? TagValue = null);

// a kind of dead letters the admin entry pages, re-drives and discards
public interface IDeadLetterSource
{
    string Kind { get; }                                   // "reactions", "jobs", "messages"
    Task<DeadLetterPage> List(DeadLetterQuery query, CancellationToken cancellationToken);
    Task<bool> Redrive(long id, string actor, CancellationToken cancellationToken);
    Task<bool> Discard(long id, string actor, CancellationToken cancellationToken);
}
public sealed record DeadLetterQuery(long After = 0, int Limit = 50, Option<Id<ITenant>> Tenant = default, string? Name = null);
```

The ports get the same paging: `IOutbox.DeadLetters(DeadLetterQuery)` and `Discard(id)`, `IJobRows.DeadJobs(DeadLetterQuery)` and
`Discard(id)`, `IMessageDeadLetters.List(DeadLetterQuery)` and `Discard(id)`. The `max` overloads stay. Also on the ports:
`IOutbox.OldestDue()`, the age signal; `Pending` stays and is documented as a count.

### 6.2 Schema upgrades (15A)

* `NpgsqlSchema.Steps` (and `NpgsqlBusSchema.Steps`, the `messages_dead` steps): `UpgradeStep(Version, CompatibleFrom, Sql,
  Concurrently, Index)`.
* `NpgsqlSchema.Version = 2`. Version 2:
  * `compatible_from` on `schema_version`;
  * `outbox_dead (dead_at)` and `jobs_dead (dead_at)` built `CONCURRENTLY`;
  * `operations_audit (id, at, actor, action, target, detail jsonb)`.
* `messages_dead` gets its own `schema_version`, a `dead_at` index and `operations_audit` in its schema.
* `EnsureSchema` follows §4 of the concept note: polled session lock, the version 1 script only on an empty schema, steps with
  `lock_timeout` (option, 2 s) and up to 5 retries, the bump in the step's transaction, the `compatible_from` check.
* `NpgsqlStoreOptions.Schema` → `SchemaMode` `Upgrade` (default) | `VerifyOnly`. `NpgsqlSchema.UpgradeScript(schema, from)`
  returns the SQL of the steps after `from`.
* Logs 8001–8005: step applied, step retried (lock timeout), invalid index dropped, newer compatible schema, refused.
* A test fixture "version 1 database": the released version 1 script, populated, then upgraded by the new code, with the contract
  suite run afterwards.

### 6.3 Instruments, logs, traces, gate (15B)

* The instruments of the concept note's §2, each package on its own meter from `IMeterFactory` (an optional `IMeterFactory`
  parameter where today a type is constructed without DI, as `MemoryPageCache` does).
* `Tarol.Changes`: `ActivitySource "Tarol.Changes"` (`change <Name>`, Internal, tags `tarol.change`, `tarol.outcome`) and logs
  7001–7003.
* The root filter on the data sources Tarol builds (`UseNpgsqlStore(connectionString)`, the bus from a connection string), with
  the option `TraceRootCommands` (default false).
* `Tarol.Queries.Npgsql`: the projection gate (`NpgsqlReadModelOptions.SingleRunner`, default true; session
  `pg_try_advisory_lock(hashtext('tarol.projection:<schema>:<name>:<version>'))`, retry every 1 s), and `DropVersion` with
  `lock_timeout` and retries.
* `Tarol.Security`: the unit `{request}` and `IMeterFactory`.
* The signal and dead-letter registrations of each package (reactions, jobs, projections, messages, bus subscriptions).

### 6.4 `Tarol.Operations` (15C)

```csharp
builder.Services.AddTarol(…)
    .UseNpgsqlStore(cs)
    .AddOperations(o => { o.PollEvery = TimeSpan.FromSeconds(15); o.Thresholds.ReactionsDegraded = TimeSpan.FromSeconds(30); o.DeadRetention = null; });
app.MapTarolHealth();                                      // /health/live, /health/ready, /health/tarol
app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");  // required policy; no policy = no endpoints
```

* `OperationsHost` (a `BackgroundService`) polls the registered signals into a snapshot. The observable gauges (meter
  `Tarol.Operations`) and the checks read the snapshot. A failed poll keeps the last values and marks the snapshot stale; `ready`
  turns Unhealthy after 3 polls.
* The checks:
  * `live`: the process;
  * `ready`: the snapshot is fresh, the schema compatible, `IsListening` where `NpgsqlProjectionPositions` is registered;
  * `tarol`: the thresholds of the concept note's §3.
* `IDeadLetterAdmin`: `List(kind, query)`, `Redrive(kind, id)`, `Redrive(kind, filter, max ≤ 1,000)`, `Discard(kind, id)`,
  `Projections()` (name, version, state, lag, rows, bytes, superseded), `DropVersion(name, version)`. Each writes
  `operations_audit` and a log line (15001–15005) with the actor `ClaimsPrincipal.Identity.Name`.
* The endpoints:

| Method | Path | Does |
|---|---|---|
| GET | `{prefix}/dead/{kind}?after=&limit=&tenant=&name=` | one page + `next` |
| POST | `{prefix}/dead/{kind}/{id}/redrive` | 204, 404 |
| POST | `{prefix}/dead/{kind}/redrive?tenant=&name=&max=` | `{ redriven }` |
| DELETE | `{prefix}/dead/{kind}/{id}` | 204, 404 |
| GET | `{prefix}/projections` | the versions |
| DELETE | `{prefix}/projections/{name}/{version}` | 204, 409 (current, or the lock timed out) |

* `tarol.purge-dead` as a `MaintenanceTask` when `DeadRetention` is set.
* The skill `tarol-operations`, README, `Tarol.Operations.AotSmoke` (0 IL warnings; with `TAROL_PG` a snapshot, a page and a
  re-drive natively), `Tarol.Operations.PackageTests`.

### 6.5 Guide and sample (15D)

* `docs/operations/README.md`:
  * the meters and instruments, the snapshot;
  * health groups and Kubernetes probes;
  * the admin entry;
  * upgrades: rolling deploys, `VerifyOnly`, `UpgradeScript` for a migration tool;
  * OpenTelemetry: `AddSource("Tarol.*", "Npgsql")`, `AddMeter("Tarol.*", "Npgsql")`, parent-based sampling, the root filter on a
    data source the app builds.
* `samples/Sagas`:
  * API and worker with `AddOperations`, `MapTarolHealth`, the AppHost's `WithHttpHealthCheck("/health/ready")`;
  * the admin entry behind a Zitadel role;
  * metrics to OTLP;
  * traces with `Tarol.*` and `Npgsql`.

## 7. Earlier decisions that constrain operations (not re-asked)

| Decision | Text (as recorded) | Consequence here |
|---|---|---|
| P-4 (persistence.md) | shipped versioned idempotent SQL + `EnsureSchema()` | versions become steps; the script stays the version 1 baseline |
| P-5 | tenant first in every key | the audit and dead-letter queries filter by tenant first; no tenant tag on metrics |
| S-5, S-13 (sagas.md) | dead letters in `outbox_dead`, re-driven by a human after 10 attempts | the admin entry is that human's tool; retention is off by default (O-5) |
| M-9 (messaging.md) | `messages_dead` in the consumer's database, re-driven by id | a source per consumer database; its audit lives next to it |
| J-7, J-9 (jobs.md) | `jobs_dead` and re-drive; Tarol's maintenance as system jobs (`MaintenanceTask`) | `tarol.purge-dead` is a `MaintenanceTask`; `IJobRows.Overdue` feeds the snapshot |
| QR-3 (queries.md) | old versions dropped explicitly | `DropVersion` stays explicit (O-5) |
| CA-2, caching.md §17 | `IsListening` public for step 15 | an input of `ready` |
| R-5, R-6 (rest.md) | authorization of changes by roles; checked only with a principal | the admin endpoints are not changes: an ASP.NET Core policy guards them (§2.3) |
| rest.md §8 | row 15 takes the denial counter and the admin entry | the counter gets its unit (15B); the entry is §6.4 |
| D-1, D-14 | no Marten, no EF Core | no EF migrations; schema steps are Tarol's SQL |
| D-15 | Docker for integration tests; Aspire for samples | upgrade tests on Testcontainers; the AppHost probes `/health/ready` |
| conventions | no reflection on AOT paths, no package the shared framework provides, no upward references | `FrameworkReference` for health checks; contracts in `Tarol.Persistence` |
| time.md, TKRN001 | no ambient clock | the poll, thresholds and audit times use the injected `TimeProvider` |

## 8. Plan impact

* **Row 15:** "designed", the split 15-0 and 15A–15D (O-7).
* **Row 16 (data protection):**
  * the audit table holds actors (personal data);
  * dead letters hold payloads (`messages_dead.data`, `last_error` texts);
  * retention and erasure of both belong there;
  * no tenant or subject in metric tags (O-1).
* **Row 17 (release):** the public API baseline must include the new port overloads; the release notes name the schema
  version 2 and its rolling-deploy rule.
* **Row 18 (samples):** ArtFlow runs with `AddOperations`; its AppHost waits on `/health/ready`.

## 9. Owner answers (2026-11-17)

Rounds:
* **Round 1**, after the inventory and the schema, gauge, span and retention probes: O-1, O-4, O-5, O-6.
* **Round 2**, after the AOT sizes, the health, dead-letter and runner probes: O-2, O-3, O-7, O-8.

The prefix O- was free (taken: C-, CA-, D-, J-, M-, P-, Q-, QR-, R-, S-, T-). Not asked because already decided: §7.

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| O-1 | How are metrics organized? (`Counter.Add` 1.9 / 10.8 ns, `Histogram.Record` 3.2 / 7.5 ns, ~10 ppm of a 1.8 ms commit; `Pending` 30 ms at 1M rows, a querying gauge blocks the exporter 33 ms per scrape; a snapshot round 1.2–2.1 ms at any size, a cached scrape 1 µs) | **Per package + snapshot** (recommended): one meter per package named like its `ActivitySource`; `tarol.<area>.<what>`, UCUM units, durations as histograms in `s`, low-cardinality tags (never the tenant), `IMeterFactory`; database numbers as observable gauges over a snapshot polled every 15 s, never a query per scrape. |
| O-2 | How do health checks work? (`IHealthCheck` only in the ASP.NET Core shared framework; AOT 10.75 MB, +177 KB with the package, +6.6 MB with Kestrel, 0 IL warnings; 9 µs per report natively; a backlog must not fail the API's readiness) | **`IHealthCheck`, three groups** (recommended): in `Tarol.Operations` on the ASP.NET Core shared framework; `live` (process), `ready` (database, schema compatible, checkpoint LISTEN), `tarol` (backlogs: reactions 30 s / 5 min, dead letters > 0 Degraded, jobs 1 / 10 min, projection lag 30 s / 5 min; configurable), not part of readiness; `MapTarolHealth()`. |
| O-3 | What is the admin entry? (the three ports return their first page only; keyset paging reaches all 250; no discard; discard + audit in one statement) | **Library API + endpoints** (recommended): keyset paging with filters and `Discard(id)` on the ports, `IDeadLetterAdmin` over reactions, jobs and messages, projection versions with `DropVersion`, `operations_audit` in the same statement + a log line, `MapTarolAdmin(prefix, policy)` with a required policy, re-drive by filter capped at 1,000. |
| O-4 | Schema migrations beyond version 1? (no lock: 99 of 120 concurrent first starts fail; old code refuses version 2; blocking lock + `CONCURRENTLY` deadlocks 100 of 120, polled 0; on 1M events: `ADD COLUMN` 8 ms max commit, volatile default 5.1 s, `CREATE INDEX` 299 ms, `CONCURRENTLY` 11 ms; behind a 3 s reader 2.9 s, with `lock_timeout` 207 ms) | **Versioned steps in `EnsureSchema`** (recommended): version 1 script only on an empty schema, missing steps under a polled session advisory lock, `lock_timeout` 2 s + retries, `CONCURRENTLY` outside a transaction with invalid-index cleanup, `schema_version.compatible_from`, `UpgradeScript(from)`, `VerifyOnly`; additive steps within a major. |
| O-5 | Retention and drops? (purge of 200,000 in 0.6–3 s; a run that finds nothing 17–39 ms without a `dead_at` index, 0.4–0.6 ms with; `DropVersion` waits 2.8 s behind a reader, a new reader waits 2.6 s then `42P01`; with `lock_timeout` 202 ms, readers unaffected) | **Keep dead letters; drops explicit** (recommended): no default retention, optional `DeadRetention` as the system job `tarol.purge-dead` with a `dead_at` index in the version 2 steps; `DropVersion` explicit (QR-3) with `lock_timeout` and retries, listed by the admin entry, never scheduled. |
| O-6 | What does Tarol ship for OpenTelemetry? (an idle worker: 51 root Npgsql spans/s, 4.4 M a day; with the command filter 0, the reactions' commands stay children; wildcards supported) | **Guidance + root filter** (recommended): no package, no OpenTelemetry dependency; `Tarol.*` wildcards; the data sources Tarol builds get the Npgsql root-command filter by default (opt-out); a `Tarol.Changes` ActivitySource, one span per pipeline run. |
| O-7 | Packages and PRs? (contracts in the lowest package as 14C's `MaintenanceTask`) | **`Tarol.Operations`, 15-0 + 15A–15D** (recommended): contracts in `Tarol.Persistence`; 15-0 the SignalR test fix; 15A schema upgrades; 15B instruments, logs, paging, ActivitySource, root filter, gate; 15C `Tarol.Operations`; 15D guide and sample. |
| O-8 | Several workers on one projection? (200,000 events: 1 runner 1.98 s; 2 runners 1.71 s, 198 of 398 batches wasted; 4 runners 2.26 s, 592 of 792 wasted; gated 1.51 s, 0 wasted) | **Advisory-lock gate, on by default** (recommended): a session `pg_try_advisory_lock` per projection version, others retry every second; the checkpoint guard stays. |

## 10. Changes against steps 7–14 (announced here, made in 15-0 and 15A–15D)

| Where | Change | Why |
|---|---|---|
| `Tarol.Persistence` | `OperationsSignal`, `SignalValue`, `IDeadLetterSource`, `DeadLetterQuery`, `DeadLetterPage`; `IOutbox.DeadLetters(DeadLetterQuery)`, `Discard`, `OldestDue`; `IJobRows.DeadJobs(DeadLetterQuery)`, `Discard`; in-memory parity | O-1, O-3 (15B) |
| `Tarol.Persistence.Npgsql` | `NpgsqlSchema.Version = 2` with steps; `EnsureSchema`: lock, no re-run, upgrade, **accepts a newer compatible schema** (behaviour change); `SchemaMode.VerifyOnly`; `UpgradeScript`; logs 8001–8005 | O-4 (15A) |
| `Tarol.Persistence.Npgsql` | `UseNpgsqlStore(connectionString)` builds its data source with the root-command filter (`TraceRootCommands = false`): **fewer Npgsql spans** (behaviour change) | O-6 (15B) |
| `Tarol.Messaging` | `IMessageDeadLetters.List(DeadLetterQuery)`, `Discard`; meter `Tarol.Messaging` | O-1, O-3 (15B) |
| `Tarol.Messaging.Npgsql` | bus and `messages_dead` steps (`messages_dead` versioned, `dead_at` index, audit), the same lock and `compatible_from`; the bus's own data source gets the root filter | O-4, O-6 (15A, 15B) |
| `Tarol.Changes` | meter and `ActivitySource "Tarol.Changes"`; logs 7001–7003 | O-1, O-6 (15B) |
| `Tarol.Sagas`, `Tarol.Jobs`, `Tarol.Queries` | meters with the instruments of the concept note's §2 | O-1 (15B) |
| `Tarol.Queries.Npgsql` | the single-runner gate, **on by default** (behaviour change: one runner per projection version works); `DropVersion` with `lock_timeout` + retries | O-8, O-5 (15B) |
| `Tarol.Security` | `tarol.authorization.denied` gets the unit `{request}` and `IMeterFactory` | O-1 (15B) |
| `Tarol.Operations` (new) | snapshot, gauges, checks, admin API and endpoints, `tarol.purge-dead` | O-2, O-3, O-5 (15C) |
| `tests/Messaging/Tarol.Messaging.SignalR.Tests/PushTests.cs` | the reconnect tests wait for the first delivered marker, not 300 ms | §5 item 14 (15-0) |
| `samples/Sagas` | operations wired; traces with `Tarol.*` and `Npgsql` | 15D |
| `docs/architecture/{jobs,sagas,messaging,queries,caching,persistence}.md` | step 15 pointers ("designed in operations.md") | this PR |
| `Directory.Packages.props` | nothing new: health checks come from the shared framework, no OpenTelemetry in packages | O-2, O-6 |

No change to `IAggregateStore`, `ChangePipeline`'s results, the outbox claim, the job claim or the event codec.

## 11. What the implementation must do (in the decided order)

The PRs stack 15A → 15B → 15C → 15D (15-0 stands alone): **retarget each PR to `main` before its base is merged**, and put a fix
found later into the lowest branch it belongs to.

### PR 15-0 `tests(signalr): wait for the replayed subscription, not 300 ms`
* Both reconnect tests in `PushTests.cs`: after `Connected`, publish a marker every 200 ms until the inbox has one (15 s cap),
  then publish the message under test and assert.
* Run `Tarol.Messaging.SignalR.Tests` 25 times in Release, alone, then the full suite once.

### PR 15A `persistence.npgsql, messaging.npgsql: versioned schema upgrades`
* `UpgradeStep`, `NpgsqlSchema.Steps`, `Version = 2` (§6.2), `UpgradeScript`, `SchemaMode`.
* `EnsureSchema` per §4 of the concept note, for the store, the bus and `messages_dead`.
* Logs 8001–8005.
* Integration tests (Testcontainers):
  * 6 concurrent first starts → 0 failures;
  * a populated version 1 database (the shipped version 1 script) upgraded while writers commit, then the contract suite;
  * old code (version 1 constant in a test double) on version 2 with `compatible_from` 1 starts, with 3 refuses;
  * an interrupted `CONCURRENTLY` step is rebuilt;
  * a step behind a held reader times out, retries and completes;
  * `VerifyOnly` on an old schema fails fast with both versions.
* Run the concurrency tests 10–25 times in Release, alone.

### PR 15B `instruments, logs and traces per package; dead-letter paging; the projection gate`
* The contracts of §6.1 with in-memory parity and the shared contract suites (paging, filters, discard).
* Meters and instruments (§2 of the concept note); `IMeterFactory`; the `Tarol.Security` unit.
* The `Tarol.Changes` source and logs; the root filter on Tarol-built data sources.
* `SingleRunner` and `DropVersion`'s `lock_timeout`.
* Tests: instruments via `MeterListener` (names, units, tags, no tenant tag); the activity tree of a change run by a reaction; the
  gate (two hosts, one works, the other takes over after `pg_terminate_backend`), bounded by events, not by wall-clock counts.
* Skills `tarol-changes`, `tarol-sagas`, `tarol-queries-npgsql`, `tarol-persistence-npgsql` updated (meters, logs, gate, upgrades).

### PR 15C `operations: Tarol.Operations — snapshot, health checks, admin entry`
* `src/Operations/Tarol.Operations/` per the conventions §8 (§6.4):
  * `FrameworkReference Microsoft.AspNetCore.App`;
  * `AddOperations`, `OperationsHost`, the gauges;
  * `MapTarolHealth`, `MapTarolAdmin`, `IDeadLetterAdmin`, `tarol.purge-dead`;
  * logs 15001–15005;
  * README, XML docs, the skill `skills/tarol-operations/SKILL.md`.
* Tests:
  * unit tests on `FakeTimeProvider`: thresholds, stale snapshot, groups;
  * HTTP tests (`TestServer`): the endpoints, the required policy (no policy → `InvalidOperationException` at mapping), 401/403,
    paging, re-drive by filter capped, audit rows;
  * integration tests: the snapshot SQL on a populated store.
* `Tarol.Operations.AotSmoke` (0 IL warnings) and `Tarol.Operations.PackageTests`.

### PR 15D `docs: the operations guide; samples/Sagas with health, metrics and the admin entry`
* `docs/operations/README.md` (§6.5); CHANGELOG, root README package table; `docs/architecture/operations.md` status; plan row 15
  "done".
* `samples/Sagas` wired; the AppHost run (`apphost.sh`) passes with `/health/ready` as the wait condition.

## 12. Skill evaluation checklist (`tarol-operations`, 15C; updates in 15B)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| add Kubernetes probes to the API and the worker | `AddOperations()`, `MapTarolHealth()`; liveness `/health/live`, readiness `/health/ready` | the `tarol` group as readiness (a backlog takes the API out); a database check in liveness |
| alert when reactions are stuck | the gauge `tarol.reactions.oldest_due` (or `/health/tarol`) | alerting on `Pending`/`backlog` size; a gauge callback that queries the database |
| export Tarol's metrics and traces | `AddMeter("Tarol.*", "Npgsql")`, `AddSource("Tarol.*", "Npgsql")` | listing sources one by one and missing `Tarol.Sagas`; tracing Npgsql roots on a data source the app builds |
| let operators re-drive dead letters | `MapTarolAdmin("/tarol/admin", policy: "tarol-admin")` + the policy | mapping without a policy; deleting rows by hand (no audit); re-driving without paging |
| deploy a new version while the old one runs | nothing: `EnsureSchema` upgrades additively; `compatible_from` | running the version 1 script by hand; a contract step in a minor version |
| use a migration tool | `SchemaMode.VerifyOnly` + `NpgsqlSchema.UpgradeScript(schema, from)` | `EnsureSchema` and the tool both upgrading |
| keep dead letters for 30 days only | `o.DeadRetention = TimeSpan.FromDays(30)` (needs `AddJobs`) | a cron job with `DELETE` without the index or the audit |
| drop an old projection version | the admin entry / `DropVersion` after no process reads it | dropping while old pods still run (`42P01`) |
| add a counter to the app's own change | the app's own meter; `tarol.*` names are Tarol's | the tenant as a tag |

## 13. Verification of this PR (SDK 11.0.100) **[verified]**

Docs and probes only; the package code is `main` at `c20d2ca` (step 14 done).
* **At the start on `main`** (2026-11-17, worktree at `c20d2ca`):
  * `dotnet build Tarol.slnx`: 0 warnings;
  * `dotnet format --verify-no-changes`: clean;
  * `dotnet test --solution Tarol.slnx` with Docker: **1591 of 1591** (as 14C recorded in jobs.md §17.7);
  * `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **22 apps**, 0 IL warnings, all checks passed;
  * `dotnet pack -c Release` + `eng/verify-packages.sh`: OK;
  * CI run 172 of `main` (`build`, `aot-smoke`, `integration`): green.
* **On this branch**: §13.1, filled in before the PR is opened.
* **The probe**: `all.sh` sections 0–9 ([`output.txt`](probes/operations/output.txt)). The run was interrupted by a worker restart
  after section 5 and resumed (sections 6–9 ran after the restart). The schema probe was fixed twice before the recorded run: the
  upgrader first deadlocked with `CONCURRENTLY` (§5 item 2), and re-ran the version 1 script on a live store. The retention bench
  gained the steady-state column, and the gauge seed's times now follow feed order.

## 14. Open items

* The message transports' health and broker-side backlog (NATS `num_pending`, RabbitMQ queue depth): in 15C if the transports
  expose them cheaply, else later.
* Exemplars and per-tenant numbers: not without step 16's rules.
* A contract step (a drop or rename) has no case yet; the first one sets `compatible_from` and needs a major version (step 17's
  release notes).
* `UseNpgsqlReadModels` and a data source the app provides (§5 item 13): register or document it in 15B.
