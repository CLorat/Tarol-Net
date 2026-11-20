# Tarol.Operations

Operations for Tarol systems: a running system says how it is doing without being asked twice, and a human can repair what it
gave up on (docs/architecture/operations.md).

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddOperations(o => o.Thresholds.ReactionsDegraded = TimeSpan.FromSeconds(30));
builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));

app.MapTarolHealth();                                       // /health/live, /health/ready, /health/tarol
app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");   // the policy is required: no policy, no endpoints
```

## The snapshot and its gauges

The packages register `OperationsSignal`s (the store: oldest due reaction, outbox size, dead reactions and jobs, the most
overdue job; the read models: lag per projection; the bus: backlog per subscription; `messages_dead`: dead messages).
`OperationsHost` reads them once every `PollEvery` (15 s) into an `OperationsSnapshot` and publishes each as an observable
gauge on the meter `Tarol.Operations` (`tarol.reactions.oldest_due` in `s`, `tarol.reactions.dead` in `{row}`, …). A
scrape reads the snapshot: about a microsecond, never a query on the exporter's thread. A failed poll keeps the last values
and logs 15005. Export with `AddMeter("Tarol.*")`; the packages' own meters (`Tarol.Changes`, `Tarol.Sagas`, …) come along.

## Health checks

| Group | Check | Fails when |
|---|---|---|
| `live` | `tarol-live` | never on the database: a restart does not fix PostgreSQL |
| `ready` | `tarol-ready` | no snapshot yet, `StaleAfter` (3) polls failed, or a package's `ReadinessCheck` reports a problem (the schema not ensured, the checkpoint `LISTEN` down) |
| `tarol` | `tarol-reactions`, `tarol-dead-letters`, `tarol-jobs`, `tarol-projections` | oldest due reaction > 30 s Degraded / 5 min Unhealthy; any dead letter Degraded; a job overdue > 1 / 10 min; a lag > 30 s / 5 min (`Thresholds`) |

Point the liveness probe at `/health/live` and readiness at `/health/ready`. `/health/tarol` (JSON) is for dashboards and
alerts, never readiness: a backlog must not take a healthy API pod out of the load balancer.

## The admin entry

`IDeadLetterAdmin` pages (`DeadLetterQuery`: `After`, `Limit` ≤ 200, `Tenant`, `Name`), re-drives (by id, or by filter, at most
1,000 per call) and discards the dead letters of every registered kind (`reactions`, `jobs`, `messages`), lists projection
versions and drops superseded ones. Every act names its actor (the principal's name), the PostgreSQL sources write an
`operations_audit` row in the same statement, and every act is logged (15001–15004).

| Method | Path | Answer |
|---|---|---|
| GET | `{prefix}/dead` | the dead-letter kinds |
| GET | `{prefix}/dead/{kind}?after=&limit=&tenant=&name=` | one page and `next` |
| POST | `{prefix}/dead/{kind}/{id}/redrive` | 204, 404 |
| POST | `{prefix}/dead/{kind}/redrive?tenant=&name=&max=` | `{ "redriven": n }` |
| DELETE | `{prefix}/dead/{kind}/{id}` | 204, 404 |
| GET | `{prefix}/projections` | the versions (state, rows, bytes, superseded) |
| DELETE | `{prefix}/projections/{name}/{version}` | 204; 409 for the newest, one in use, or one still read |

Every error is `application/problem+json` with a `code` (`AdminProblems`): `Admin.Query` (400), `Admin.Tenant` (400),
`Admin.Kind` (404), `Admin.NotFound` (404), `Projection.Drop` (409), `DataProtection.ErasureIncomplete` (409, with `failed`:
the participants to re-run; the key is erased, run the erasure again).

## Retention

Dead letters are kept until a human acts (O-5). `DeadRetention = TimeSpan.FromDays(30)` adds the system job `tarol.purge-dead`
(hourly, once per cluster with `AddJobs` of `Tarol.Jobs`): it deletes older ones on the `dead_at` indexes and audits each purge.
Projection versions are never dropped automatically.

* **Data subjects (step 16)**: `GET {prefix}/subjects/{id}/export?tenant=` and `POST {prefix}/subjects/{id}/erase?tenant=` (`tenant` required; the empty GUID for tenant-less data) when `IDataSubjects` is registered; `OperationsOptions.AuditRetention` → `tarol.purge-audit`.

## AOT

0 IL warnings: the health report is written with `Utf8JsonWriter`, the admin answers through a source-generated
`OperationsJson`, the endpoints are plain `RequestDelegate`s. The package takes the ASP.NET Core shared framework
(`FrameworkReference Microsoft.AspNetCore.App`), no NuGet dependency and no OpenTelemetry package.

Design: docs/architecture/operations.md. Licence: AGPL-3.0-only, see LICENSE; a commercial licence is available from the owner (NOTICE).
