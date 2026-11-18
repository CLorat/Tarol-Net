---
name: tarol-operations
description: >-
  Use when adding health checks, Kubernetes probes, metrics, dashboards or alerts to a Tarol system, when operators must list,
  re-drive or discard dead reactions, jobs or messages, drop old projection versions, or keep dead letters for a limited time.
  Covers AddOperations, OperationsOptions (PollEvery, Thresholds, DeadRetention), OperationsHost and OperationsSnapshot, the
  gauges of the meter Tarol.Operations, MapTarolHealth (live, ready, tarol), MapTarolAdmin, IDeadLetterAdmin, DeadLetterQuery,
  ReadinessCheck and tarol.purge-dead. Do not write your own health checks or gauges that query the database, do not delete
  dead rows by hand, do not decompile or web-search.
---
# Tarol.Operations

## Rules

1. **Register** `.AddOperations()` on the Tarol builder after the store and the other packages, in every process (API and
   worker). Map `app.MapTarolHealth()` and, where operators work, `app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin")`
   with a policy you define (`AddAuthorizationBuilder().AddPolicy(...)`). An empty policy throws: the entry fails closed.
2. **Probes**: liveness `/health/live`, readiness `/health/ready`. **Never** `/health/tarol` as readiness: a backlog would take
   every API pod out of the load balancer while only the worker can drain it.
3. **Alerts** read `tarol.reactions.oldest_due` (seconds; also `/health/tarol`), `tarol.jobs.overdue`, `tarol.projections.lag`
   and the `*.dead` gauges, not the backlog size (`tarol.reactions.backlog` is an estimate).
4. **Database numbers come from the snapshot** (`OperationsHost`, one poll every `PollEvery`, 15 s). A new number is an
   `OperationsSignal` singleton (cheap index lookups; it runs on the poll), never a gauge callback that queries.
5. **Export** with `AddMeter("Tarol.*", "Npgsql")` and `AddSource("Tarol.*", "Npgsql")`; there is no Tarol OpenTelemetry package.
6. **Dead letters**: `IDeadLetterAdmin.List(kind, new DeadLetterQuery { After = next, Limit = 200 })` page by page,
   `Redrive(kind, id, actor)`, `Redrive(kind, filter, max ≤ 1000, actor)`, `Discard(kind, id, actor)`. Kinds
   (`DeadLetterKinds`): `reactions`, `jobs`, `messages`. Every act is audited (`operations_audit`) and logged.
7. **Retention** is off: dead letters wait for a human. `o.DeadRetention = TimeSpan.FromDays(30)` adds `tarol.purge-dead`
   (needs `AddJobs`). Old projection versions are dropped only explicitly (`DropVersion`), after no pod of the old version runs.
8. **Tests**: `await provider.GetRequiredService<OperationsHost>().PollOnce()` instead of waiting for the poll; read
   `HealthCheckService.CheckHealthAsync(r => r.Tags.Contains(TarolHealthTags.Tarol))`.

## Example

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseInMemoryStore()
    .AddOperations(o =>
    {
        o.PollEvery = TimeSpan.FromSeconds(15);
        o.Thresholds.ReactionsDegraded = TimeSpan.FromSeconds(30);
    });
builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));
var app = builder.Build();
app.MapTarolHealth();
app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");

// an operator re-drives the dead reactions of one page
var admin = app.Services.GetRequiredService<IDeadLetterAdmin>();
var page = await admin.List(DeadLetterKinds.Reactions, new DeadLetterQuery { Limit = 50 }, ct);
foreach (var dead in page.Items)
    _ = await admin.Redrive(DeadLetterKinds.Reactions, dead.Id, "ops-alice", ct);
```

## Data subjects and the audit (step 16)

* With `AddDataProtection` (`IDataSubjects` registered), `MapTarolAdmin` also maps `GET {prefix}/subjects/{id}/export?tenant=…`
  (a JSON attachment) and `POST {prefix}/subjects/{id}/erase?tenant=…` (`tenant` required, else 400; the empty GUID for tenant-less data), under the same policy; both are audited with the
  principal's name.
* `OperationsOptions.AuditRetention` (at least 1 day) adds the system job `tarol.purge-audit`, which deletes older
  `operations_audit` rows. Without it they are kept for ever.

## Common mistakes

| Mistake | Result |
|---|---|
| `MapTarolAdmin("/tarol/admin", policy: "")` | `InvalidOperationException` at mapping: the entry would be open |
| the `tarol` group (or one `MapHealthChecks("/health")` of every check) as readiness | API pods leave the load balancer on a backlog |
| a gauge whose callback runs `SELECT count(*)` | 32 ms per scrape at 1,000,000 rows, sync over async on the exporter's thread |
| alerting on `IOutbox.Pending` | a full count, no age; use `tarol.reactions.oldest_due` |
| `DELETE FROM tarol.outbox_dead …` by hand | no audit; use `Discard` |
| `Limit = 1000` in a `DeadLetterQuery` | `ArgumentOutOfRangeException` (≤ 200); follow `Next` |
| `DeadRetention` without `AddJobs` | nothing purges (the job host runs maintenance) |
| dropping a projection version while old pods run | `TimeoutException` / 409, or later `42P01` in those pods |
| the tenant as a metric tag in your own meter | unbounded series and personal data; never |

## AOT

0 IL warnings with Kestrel: the report is written with `Utf8JsonWriter`, the admin JSON through `OperationsJson`, the
endpoints are `RequestDelegate`s. Logs: 15001 re-drive, 15002 re-drive by filter, 15003 discard, 15004 drop, 15005 a failed poll.
