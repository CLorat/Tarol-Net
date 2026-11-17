# Inventory: operations in the old code and in Tarol-Net (step 15)

Read in `CLorat/Tarol@6a62f81` (cloned next to this repository) and in Tarol-Net `main` at `c20d2ca` (step 14 done).
**[verified by reading]** unless marked otherwise. Searched for `Meter`, `System.Diagnostics.Metrics`, `ActivitySource`, `HealthCheck`,
`IHealthCheck`, `LoggerMessage`, `ILogger`, `OpenTelemetry`, `EventId`, `Migrat`, `schema_version`, `DeadLetter`, `Redrive`,
`Retention`, `Purge`.

## 1. The old code (`CLorat/Tarol@6a62f81`)

| Where | What | Used by | Worth migrating |
|---|---|---|---|
| `src/Diagnostics/Diagnostics.HealthChecks/Diagnostics.HealthChecks.csproj` (4 lines), `src/Diagnostics/Diagnostics.OpenTelemetry/Diagnostics.OpenTelemetry.csproj` (4 lines) | an empty `<Project Sdk="Microsoft.NET.Sdk">`: no source file, no reference | nothing | **nothing** (plan line 54) |
| `src/Messaging/Messaging.Backplane/MessagingBackplaneHealthCheck.cs:5-15` | `IHealthCheck`: Healthy while `MessagingBackplaneHealthService.IsConnected`, else Unhealthy with the last error | the backplane runner (`MessageBackplaneRunner.cs:28,45-46` sets the flag) | the **shape** only: a connection flag read by a check (Tarol-Net's `NpgsqlProjectionPositions.IsListening` is the same idea); the backplane itself was replaced in step 11 |
| `Directory.Packages.props:46` `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` 11.0.0; `src/Messaging/Messaging.Backplane/Messaging.Backplane.csproj:11` | the abstractions package as a NuGet reference of a non-web library | the backplane check | the precedent for a worker-side check: the abstractions are not in the base framework (§3) |
| `src/Sagas/Sagas/Internals/SagaInternals.cs:44-53` `SagaDiagnostics.SourceName = "Tarol.Sagas"` | an `ActivitySource`, "register with `tracing.AddSource("Tarol.Sagas")`" | the old engine | already carried over by name (`ReactionWorker.ActivitySourceName`, step 9) |
| `src/Sagas/Sagas.Engine/SagaOutboxProcessor.cs:45-53` | after `MaxAttempts`: `DeadLetterAsync(entry.Id, ex.ToString())`, no log, no metric | the outbox loop | replaced (step 9: `Bury`, log 9002) |
| `src/Sagas/Sagas.Engine/SagaOutboxProcessor.cs:99-104` | a commented-out `logger.LogCritical("… Manual intervention required.")` | — | the only "operator" message of the old code, never enabled |
| `src/Sagas/Sagas.Persistence.Marten/MartenSagaStore.cs:78-86`, `Sagas.Persistence.InMemory/InMemorySagaStore.cs:70-80,171-175` | dead letter = a flag `DeadLettered = true`; `GetDeadLetters()` only in the in-memory store | tests | replaced (`outbox_dead`, `IOutbox.DeadLetters`/`Redrive`, step 9) |
| `templates/TarolSolution/src/$product$.Api/Program.cs:23-29,68-80` | `AddHealthChecks().AddNpgSql(…).AddDbContextCheck<…>()`, `MapHealthChecks("/health")` with a JSON writer | the outdated template | not migrated (plan): `AspNetCore.HealthChecks.NpgSql` 9.0.0 (`templates/TarolSolution/Directory.Packages.props:10`) is a third-party package; EF Core is gone (D-14) |
| `templates/TarolSolution/src/$product$.Migration/Program.cs:3-57` | `using Tarol.Migrations`, `AddDatabaseMigration<…>`, `MigrationStrategy.EnsureCreated/MigrateAsync` | the template | **no `Tarol.Migrations` exists** in the repository (0 `namespace Tarol.Migrations`); EF Core migrations, gone with EF Core |
| `templates/TarolSolution/tests/$product$.IntegrationTests/HealthChecks/HealthCheckTests.cs:13-40` | GET `/health` → `"Healthy"`, entries `postgresql` and `dbcontext` | the template | the test shape only |
| (none) | `Meter`, `System.Diagnostics.Metrics`, `LoggerMessage`, schema versions, retention | — | **nothing**: the old code has no metrics, no source-generated logs, no schema versioning, no retention |

**Nothing to migrate.** The old repository has two empty diagnostics projects, one connection-flag health check, one activity
source (already carried over) and an EF Core migration template whose package does not exist.

## 2. What Tarol-Net already has that step 15 builds on

| Where | What | Relevance |
|---|---|---|
| `src/Security/Tarol.Security/ChangeAuthorization.cs:26,34-35` | meter `Tarol.Security`, counter `tarol.authorization.denied` (no unit), a static `new Meter(…)` | the only counter outside caching; the unit and `IMeterFactory` differ from caching (§5 of the evaluation) |
| `src/Caching/Tarol.Caching/MemoryPageCache.cs:19,45-50` | meter `Tarol.Caching` from `IMeterFactory` (or `new Meter`), counters `tarol.caching.{hits,misses,evictions}` `{page}`, gauges `tarol.caching.{entries,bytes}` | the naming pattern: `tarol.<area>.<what>`, UCUM units, an in-process observable gauge |
| `src/Sagas/Tarol.Sagas/ReactionWorker.cs:33`, `src/Jobs/Tarol.Jobs/JobHost.cs:21`, `src/Queries/Tarol.Queries/Hosting/ProjectionHost.cs:24`, `src/Messaging/Tarol.Messaging/MessageConsumers.cs:36` | `ActivitySource`s `Tarol.Sagas`, `Tarol.Jobs`, `Tarol.Queries`, `Tarol.Messaging` (+ the transports' and the sample's) | the tracing guide lists them; there is no `Tarol.Changes` source (a change outside HTTP or a worker has no span) |
| logs: Security 1001–1002, Sagas 9001–9011, Messaging 11001–11415, Queries 12001–12103, Jobs 14001–14010 | `[LoggerMessage]` with event ids per family | **no logs in `Tarol.Changes` (the pipeline) or `Tarol.Persistence(.Npgsql)` (the store)**: plan row 15 |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlSchema.cs:12,21-149` | `Version = 1`, one idempotent script (`CREATE … IF NOT EXISTS`, one `ALTER TABLE … ADD COLUMN IF NOT EXISTS` for step 10's `fingerprint`), `schema_version (version)` | the base of upgrade steps |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlAggregateStore.cs:103-110` | `EnsureSchema`: runs the whole script, then throws if the stored version is newer; **no advisory lock** | probe: concurrent starts fail (§3 of the evaluation); old code refuses a newer schema |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlTarolBuilderExtensions.cs:48-55` | `EnsureSchemaService` runs it when the host starts (`ensureSchema = true` by default) | every API and worker process runs the script at every start |
| `src/Messaging/Tarol.Messaging.Npgsql/NpgsqlBus.cs:92-125`, `NpgsqlBusSchema.cs:14,63-64` | the bus script, version 1, `schema_version`, **under** `pg_advisory_xact_lock` | the same upgrade question for the bus |
| `src/Messaging/Tarol.Messaging.Npgsql/NpgsqlMessageDeadLetters.cs:44-60`, `NpgsqlBusSchema.cs:72-88` | `messages_dead` script, advisory lock, no version | a third script without a version |
| `src/Queries/Tarol.Queries.Npgsql/NpgsqlReadModels.cs:73-85`, `NpgsqlReadModelSchema.cs:20-33` | `DropVersion` (refuses a version in use **in this process**, no `lock_timeout`), the `projections` checkpoint table, the DDL under an advisory lock | QR-3's explicit drop; probe: a drop waits behind readers and blocks new ones |
| `src/Queries/Tarol.Queries/Hosting/ProjectionHost.cs:84` | `Conflicts`: batches the checkpoint guard refused | the wasted-batch count of the runner bench |
| `src/Queries/Tarol.Queries.Npgsql/NpgsqlProjectionPositions.cs:70` | `IsListening` (public for step 15, caching.md §17) | a check input |
| `src/Persistence/Tarol.Persistence/Outbox.cs:175,184,189` | `IOutbox.DeadLetters(max)`, `Redrive(id)`, `Pending()` (`count(*)`, `NpgsqlOutbox.cs:315`) | no cursor, no filter, no discard; `Pending` is a full count |
| `src/Persistence/Tarol.Persistence/JobRows.cs:163,172,182` | `IJobRows.DeadJobs(max)`, `Redrive(id)`, `Overdue()` (index `jobs_due`) | the job inputs of the checks; same paging gap |
| `src/Messaging/Tarol.Messaging/DeadLetters.cs:31,37`, `MessageConsumers.cs:97` | `IMessageDeadLetters.List(max)`, `Take(id)`; `MessageConsumers.Redrive(id)` | the consumer dead letters; same paging gap |
| `src/Messaging/Tarol.Messaging.Npgsql/NpgsqlBus.cs:220,239` | `PurgeMessages(olderThan, batch)` (system job `tarol.purge-messages`, `NpgsqlMessagingBuilderExtensions.cs:48-60`), `Pending(subscription)` | bus retention exists; subscription backlog exists for the PostgreSQL bus only |
| `src/Persistence/Tarol.Persistence/Maintenance.cs:11,17` | `MaintenanceTask(Name, Every, Run)`, `IMaintenanceHost` (14C) | the contract retention jobs use; the pattern for a contract in a lower package that a higher one runs |
| `samples/Sagas/Worker/Program.cs:41-44` | tracing without `Npgsql` ("every idle claim would be a span"), `Tarol.Sagas` missing from the list | probe: 51 idle Npgsql spans/s; the guide fixes the list |
| `samples/Sagas/Api/Program.cs:52`, `AppHost/Program.cs:21` | `MapGet("/health", () => "ok")`, `WithHttpHealthCheck("/debug/ready")` | no real check today |

## 3. The base framework (.NET 11.0.0, `~/.dotnet/shared`) **[verified]**

| Assembly | `Microsoft.NETCore.App` | `Microsoft.AspNetCore.App` |
|---|---|---|
| `Microsoft.Extensions.Diagnostics.Abstractions` (`IMeterFactory`) | yes | yes |
| `System.Diagnostics.DiagnosticSource` (`Meter`, `ActivitySource`) | yes | yes |
| `Microsoft.Extensions.Hosting.Abstractions`, `…DependencyInjection.Abstractions` | yes | yes |
| `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` (`IHealthCheck`) | **no** | yes |
| `Microsoft.Extensions.Diagnostics.HealthChecks` (`HealthCheckService`) | **no** | yes |
| `Microsoft.AspNetCore.Diagnostics.HealthChecks` (`MapHealthChecks`) | no | yes |

A worker package (`Tarol.Sagas`, `Tarol.Jobs`, `Tarol.Queries`, `Tarol.Persistence.Npgsql`) has no ASP.NET Core reference: an
`IHealthCheck` there needs a NuGet reference or the shared framework.

## 4. Verdict

* Old code: **nothing to migrate**.
* Tarol-Net: traces exist for every worker; meters exist for caching and security only; the store and the pipeline log nothing;
  the dead-letter ports list only a first page and cannot discard; `Pending` counts the whole outbox; the store's `EnsureSchema`
  has no lock, no upgrade steps, and refuses a newer schema; three scripts (store, bus, `messages_dead`) and the read tables each
  ensure themselves differently.
