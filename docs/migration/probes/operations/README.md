# Probes: Operations (step 15 design)

Evidence for [docs/migration/operations.md](../../operations.md) and [docs/architecture/operations.md](../../../architecture/operations.md).
The probe is copied and adapted, never referenced, and stays out of `Tarol.slnx`. `*.csproj.txt` becomes a project file in a
work directory (`run.sh`); `TarolRoot` points at this repository, so the probe builds against **today's** packages
(`Tarol.Jobs`, `Tarol.Persistence.Npgsql`, `Tarol.Queries.Npgsql`, `Tarol.Messaging.Npgsql`, the Changes, Primitives and Queries
generators). Nothing in it is package code.

| File | What it is |
|---|---|
| `Operations/Schema.cs` | the would-be upgrader: `Step` (version, `CompatibleFrom`, SQL, transactional or not), `Upgrader.Ensure` (the version 1 script only on an empty database, then the missing steps under an advisory lock that is **polled**, each step with a `lock_timeout` and retries; invalid leftovers of `CREATE INDEX CONCURRENTLY` dropped first), `schema_version.compatible_from` for rolling deploys, a plausible version 2 (`dead_at` indexes, a `redriven_by` column) |
| `Operations/Signals.cs` | the would-be operations snapshot: one query round (outbox estimate, oldest due head, dead counts, most overdue job, feed head, projection lag in seconds), observable gauges that read the last snapshot, and readiness checks over it (`IHealthCheck`, thresholds of the evaluation's §6) |
| `Operations/Domain.cs` | `Item` (created, touched), the change `TouchItem`, the reaction `AckTouch` (one outbox row per touch), the projection `ItemRow` (`item-list`), the job `Nightly` |
| `Operations/SelfTest.cs` | `selftest schema` (concurrent `EnsureSchema`, the upgrader blocking and polling, rolling deploys, a populated upgrade, `CONCURRENTLY`), `dead` (paging, filters, re-drive, discard, audit), `health` (snapshot, `HealthCheckService`; with `WEB` the Kestrel endpoints), `spans` (Npgsql spans of an idle worker host, with and without a command filter) |
| `Operations/Bench.cs` | `bench metrics` (instrument and log cost against a commit), `gauges` (a query per scrape vs the snapshot, outbox 0 … 1,000,000 rows), `upgrade <events>` (DDL on a populated store with 4 writers), `retention <rows>` (the dead tables with and without a `dead_at` index), `drop` (`DropVersion` while a reader holds the table), `runners <items>` (1, 2, 4 runners of one projection, with and without an advisory-lock gate) |
| `inventory.md` | the old code (`CLorat/Tarol@6a62f81`) and what Tarol-Net already has, file:line |
| `output.txt` | the last `all.sh` run (sections 0–9) |

```bash
dockerd > /tmp/dockerd.log 2>&1 &                 # postgres:17 (tarol-probe-pg, 55432); all.sh starts it
docs/migration/probes/operations/all.sh /tmp/o      # resumable: sec-<n>.txt + .ok per section; ALL_REDO=<n> redoes one
docs/migration/probes/operations/run.sh /tmp/w Operations run -c Release -- selftest schema   # one run; PROBE_PG overrides the database
docs/migration/probes/operations/run.sh /tmp/w Operations run -c Release -p:WithWeb=true -- selftest health
```

Notes:
* Builds: default = `Microsoft.Extensions.Diagnostics.HealthChecks` 11.0.0 (NuGet) in a worker without ASP.NET Core;
  `-p:WithHealth=false` = no health-check assembly (the size baseline); `-p:WithWeb=true` = the ASP.NET Core shared framework,
  Kestrel and `MapHealthChecks`.
* The gauge, upgrade and retention benches seed rows with SQL (`generate_series`), not through the store: they measure queries
  and DDL on tables of that size. The runner, drop and health checks seed through `NpgsqlAggregateStore.Append`.
* Never run `all.sh` next to the full test suite: sections 2 and 4–9 measure latency.
