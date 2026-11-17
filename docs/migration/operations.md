# Operations: evaluation (step 15 design)

Draft; the owner's answers are recorded as they arrive (§9).

## 9. Owner answers (2026-11-17)

| # | Question | Answer |
|---|---|---|
| O-1 | Metrics | **Per package + snapshot** (recommended): one meter per package named like its ActivitySource; `tarol.<area>.<what>`, UCUM units, durations as histograms in `s`, low-cardinality tags (never the tenant), `IMeterFactory`; database numbers as observable gauges over a snapshot polled every 15 s, never a query per scrape. |
| O-4 | Schema migrations beyond version 1 | **Versioned steps in `EnsureSchema`** (recommended): version 1 script only on an empty schema, missing steps under a polled session advisory lock, `lock_timeout` 2 s + retries, `CONCURRENTLY` outside a transaction with invalid-index cleanup, `schema_version.compatible_from`, `UpgradeScript(from)`, `VerifyOnly`; additive steps within a major. |
| O-5 | Retention and drops | **Keep dead letters; drops explicit** (recommended): no default retention, optional `DeadRetention` as the system job `tarol.purge-dead` with a `dead_at` index in the version 2 steps; `DropVersion` explicit (QR-3) with `lock_timeout` and retries, listed by the admin entry, never scheduled. |
| O-6 | OpenTelemetry | **Guidance + root filter** (recommended): no package, no OpenTelemetry dependency; `Tarol.*` wildcards; the data sources Tarol builds get the Npgsql root-command filter by default (opt-out); a `Tarol.Changes` ActivitySource, one span per pipeline run. |
| O-2 | Health checks | **`IHealthCheck`, three groups** (recommended): in `Tarol.Operations` on the ASP.NET Core shared framework; `live` (process), `ready` (database, schema compatible, checkpoint LISTEN), `tarol` (backlogs: reactions 30 s / 5 min, dead letters > 0 Degraded, jobs 1 / 10 min, projection lag 30 s / 5 min; configurable), not part of readiness; `MapTarolHealth()`. |
| O-3 | Admin entry | **Library API + endpoints** (recommended): keyset paging with filters and `Discard(id)` on the ports, `IDeadLetterAdmin` over reactions, jobs and messages, projection versions with `DropVersion`, `operations_audit` in the same statement + a log line, `MapTarolAdmin(prefix, policy)` with a required policy, re-drive by filter capped at 1,000. |
| O-7 | Packages and PRs | **`Tarol.Operations`, 15-0 + 15A–15D** (recommended): contracts in `Tarol.Persistence`; 15-0 the SignalR test fix; 15A schema upgrades; 15B instruments, logs, paging, ActivitySource, root filter, gate; 15C `Tarol.Operations`; 15D guide and sample. |
| O-8 | Projection runners | **Advisory-lock gate, on by default** (recommended): a session `pg_try_advisory_lock` per projection version, others retry every second; the checkpoint guard stays. |
