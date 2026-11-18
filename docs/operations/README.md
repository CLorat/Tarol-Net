# Operations

Step 15 ([architecture/operations.md](../architecture/operations.md), decisions O-1 … O-8 in
[migration/operations.md](../migration/operations.md) §9). A running Tarol system says how it is doing without being asked
twice, upgrades its own schema without stopping, and lets a human repair what it gave up on.

| You want | Use | Package |
|---|---|---|
| Kubernetes probes | `.AddOperations()`, `app.MapTarolHealth()`: liveness `/health/live`, readiness `/health/ready` | `Tarol.Operations` |
| dashboards and alerts | the gauges of the meter `Tarol.Operations`, `/health/tarol`, the packages' meters | `Tarol.Operations` + each package |
| traces | `AddSource("Tarol.*", "Npgsql")` in your OpenTelemetry setup | none (guidance) |
| operators re-drive or discard dead letters | `app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin")`, `IDeadLetterAdmin` | `Tarol.Operations` |
| deploy version N+1 while N runs | nothing: `EnsureSchema` upgrades additively under a lock | `Tarol.Persistence.Npgsql`, `Tarol.Messaging.Npgsql` |
| a migration tool owns the schema | `NpgsqlSchema.UpgradeScript(schema, from)` + `SchemaMode.VerifyOnly` | `Tarol.Persistence.Npgsql` |
| several workers on one projection | nothing: one runner per version (`SingleRunner`) | `Tarol.Queries.Npgsql` |

## Wire it

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })
    .AddOperations();                                                 // in the API and in the worker
builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Microsoft.AspNetCore", "Tarol.*", "Npgsql").AddOtlpExporter())
    .WithMetrics(m => m.AddMeter("Tarol.*", "Npgsql", "Microsoft.AspNetCore.Hosting").AddOtlpExporter());

var app = builder.Build();
app.MapTarolHealth();
app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");             // the policy is required: no policy, no endpoints
```

A worker without HTTP still gets the snapshot and its gauges; for probes it needs a listener (`WebApplication.CreateSlimBuilder`
and `MapTarolHealth()`, as the sample's worker does). With .NET Aspire: `.WithHttpHealthCheck("/health/ready")` on the resource.

## Metrics

Every package counts what it does on its own meter, created from the host's `IMeterFactory`. Names are `tarol.<area>.<what>`,
durations are histograms in seconds, tags have low cardinality and **never the tenant**.

| Meter | Instruments | Outcome tag |
|---|---|---|
| `Tarol.Changes` | `tarol.changes.runs` `{change}`, `tarol.changes.duration` `s` (tag `tarol.change`) | committed, conflict, invalid, denied, not_found, error |
| `Tarol.Sagas` | `tarol.reactions.runs` `{row}`, `.duration`, `.lateness` (start − due) `s` (tag `tarol.reaction`) | completed, retried, buried |
| `Tarol.Jobs` | `tarol.jobs.occurrences` `{occurrence}`, `.duration`, `.lateness` `s` (tag `tarol.job`) | ran, retried, dead, skipped |
| `Tarol.Queries` | `tarol.projections.events` `{event}`, `.batches` `{batch}`, `.batch.duration` `s` (tag `tarol.projection`) | written, refused |
| `Tarol.Messaging` | `tarol.messages.consumed` `{message}`, `.duration` `s` (tag `tarol.consumer`); `tarol.messages.published` (tag `tarol.contract`) | completed, dropped, retried, buried; published, skipped, failed |
| `Tarol.Caching` | `tarol.caching.hits`, `.misses`, `.evictions` `{page}` | |
| `Tarol.Security` | `tarol.authorization.denied` `{request}` (tags `change`, `reason`, `actor.kind`) | |
| `Tarol.Operations` | the snapshot gauges below | |

**Database numbers come from one snapshot.** `OperationsHost` reads every registered `OperationsSignal` once per `PollEvery`
(15 s) and the gauges publish the last values: a scrape costs about a microsecond and never queries. A gauge that ran
`SELECT count(*)` instead would cost 32 ms per scrape at 1,000,000 outbox rows, synchronously on the exporter's thread.

| Gauge | Unit | Alert? |
|---|---|---|
| `tarol.reactions.oldest_due` | `s` | **yes**: the age of the oldest due reaction row (30 s Degraded, 5 min Unhealthy) |
| `tarol.reactions.backlog` | `{row}` | no: an estimate (`reltuples`, 0 before the first `ANALYZE`) |
| `tarol.reactions.dead`, `tarol.jobs.dead`, `tarol.messages.dead` | `{row}` … | yes: a human has work (> 0 Degraded) |
| `tarol.jobs.overdue` | `s` | yes: 1 min / 10 min |
| `tarol.projections.lag` (tag `tarol.projection`) | `s` | yes: 30 s / 5 min |
| `tarol.messages.backlog` (tag `tarol.subscription`) | `{message}` | the PostgreSQL bus only |

A number of your own is an `OperationsSignal` singleton (`services.AddSingleton(new OperationsSignal(meter, name, unit,
description, read))`): it runs on the poll, so keep it to index lookups.

## Health checks

| Group | Checks | Fails when |
|---|---|---|
| `live` | `tarol-live` | never on the database: a restart does not fix PostgreSQL |
| `ready` | `tarol-ready` | no snapshot yet, 3 polls failed (`StaleAfter`), the schema not ensured, the checkpoint `LISTEN` down |
| `tarol` | `tarol-reactions`, `tarol-dead-letters`, `tarol-jobs`, `tarol-projections` | the thresholds (`o.Thresholds`) |

**Never use the `tarol` group as readiness.** The API and the worker share one database, but only the worker drains it: a
backlog that took the API pods out of the load balancer would turn a slow worker into an outage. `/health/tarol` answers JSON
(`status`, and per check `status` and `description`): 200 while Healthy or Degraded, 503 when Unhealthy.

```yaml
livenessProbe:  { httpGet: { path: /health/live,  port: 8080 }, periodSeconds: 10 }
readinessProbe: { httpGet: { path: /health/ready, port: 8080 }, periodSeconds: 5, failureThreshold: 3 }
```

## The admin entry

`MapTarolAdmin(prefix, policy)` maps, behind the policy:

| Method | Path | Answer |
|---|---|---|
| GET | `dead/{kind}?after=&limit=&tenant=&name=` | a page (`items`, `next`): `kind` is `reactions`, `jobs` or `messages`; `limit` ≤ 200 |
| POST | `dead/{kind}/{id}/redrive` | 204, 404 |
| POST | `dead/{kind}/redrive?tenant=&name=&max=` | `{ "redriven": n }`, at most 1,000 per call |
| DELETE | `dead/{kind}/{id}` | 204, 404 |
| GET | `projections` | name, version, state, rows, bytes, superseded |
| DELETE | `projections/{name}/{version}` | 204; 409 for the newest version, one in use, or one still read |

The actor is the principal's name (else its `sub`). Every act writes an `operations_audit` row (when, actor, action, target,
detail) in the same statement on PostgreSQL, and a log line (15001–15004). Code uses `IDeadLetterAdmin` the same way.

**Retention**: dead letters are kept until a human acts. `AddOperations(o => o.DeadRetention = TimeSpan.FromDays(30))` adds the
system job `tarol.purge-dead` (hourly, once per cluster, needs `AddJobs`), which purges older ones on the `dead_at` indexes and
audits each purge. Projection versions are never dropped automatically: drop one after no pod of its version runs.

## Schema upgrades and rolling deploys

`EnsureSchema` (run by `UseNpgsqlStore`, the bus and `UseNpgsqlDeadLetters` at start) takes a session advisory lock per schema
by polling, runs the version 1 script only on an empty schema, then the missing steps with `lock_timeout` 2 s and up to 5
attempts; `CREATE INDEX CONCURRENTLY` steps run outside a transaction and drop an invalid leftover first. The version is
stored in the step's transaction.

* **Rolling deploys**: `schema_version.compatible_from` is the oldest code version that still runs on the schema. Tarol's steps
  are additive within a major version, so old pods keep running on the new schema; a contract step (a drop or rename) comes
  only with a major version and raises `compatible_from`, and old code then refuses with both versions in the message (8005).
* **Version 2** (step 15): `operations_audit`, the `dead_at` indexes. Code of step 14 refuses a version 2 schema: deploy 15
  everywhere (or let old pods restart onto it).
* **A migration tool**: run `NpgsqlSchema.UpgradeScript(schema, from)` (0 = an empty database; `NpgsqlBusSchema.UpgradeScript`,
  `DeadLettersUpgradeScript` likewise), with the `CONCURRENTLY` statements outside a transaction, and set
  `SchemaMode = SchemaMode.VerifyOnly`, which checks and fails fast.

## Tracing

There is no Tarol OpenTelemetry package. Add `Tarol.*` and `Npgsql` as sources: `Tarol.Changes` (one span per pipeline run, a
child of the request, reaction or job), `Tarol.Sagas` (a Consumer span per reaction row, linked to the committing request),
`Tarol.Jobs`, `Tarol.Queries` (a span per projection batch), `Tarol.Messaging` and the transports.

The data sources Tarol builds (`UseNpgsqlStore(connectionString)`, `UseNpgsqlBus(connectionString)`) trace Npgsql commands only
inside an activity: an idle worker's claims made 51 root spans per second before. For a data source you build, call
`new NpgsqlSlimDataSourceBuilder(cs).EnableArrays().TraceOnlyInActivities()`; to trace everything set `TraceRootCommands = true`.
Sample by parent (`ParentBasedSampler`) so a reaction's commands follow its decision.

## Logs

| Ids | Package | What |
|---|---|---|
| 7001–7003 | `Tarol.Changes` | an error result (Warning), a conflict (Debug), an exception (Error) |
| 8001–8005 | `Tarol.Persistence.Npgsql` | schema step applied, retried after a lock timeout, invalid index dropped, newer compatible schema, refused |
| 8006–8008 | `Tarol.Persistence.Npgsql` (`NpgsqlKeyStore`) | data keys created, a key erased, cached keys evicted (Debug; the tenant id, never a subject or a value) |
| 12010, 12011 | `Tarol.Queries` | this host is a projection's runner; it lost the gate |
| 15001–15005 | `Tarol.Operations` | re-drive, re-drive by filter, discard, drop (with the actor); a failed poll |
| 16001–16008 | `Tarol.DataProtection` | subject erased, tenant erased, subject exported, retention deleted streams (with the actor or job), a failed erasure participant (Error), in-memory eviction (Debug), an erasure run again, streams skipped for pending reactions; ids and counts, never personal values |

Earlier ids: 1001 (authorization denial), 9001–9011 (reactions), 11001–11415 (messaging), 12001–12103 (queries), 14001–14010 (jobs).
