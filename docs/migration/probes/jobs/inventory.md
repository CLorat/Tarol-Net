# Inventory: scheduling in the old code and in Tarol-Net (step 14)

Read in `CLorat/Tarol@6a62f81` (cloned next to this repository) and in Tarol-Net `main` at `984e621`. **[verified by reading]**
unless marked otherwise. Searched for `Cron`, `Cronos`, `Schedul`, `Recurr`, `Quartz`, `Hangfire`, `PeriodicTimer`, `Task.Delay`,
`BackgroundService`, `IHostedService`, `nightly`, `settle`, `report`.

## 1. The old code (`CLorat/Tarol@6a62f81`)

| Where | What | Used by | Worth migrating |
|---|---|---|---|
| `Directory.Packages.props:13` `<PackageVersion Include="Cronos" Version="0.11.1" />` | a central version of Cronos | **no project** (0 `PackageReference`, 0 `using Cronos`, 0 `CronExpression` in any `.cs`) | no code; the choice of Cronos is the only hint. Current Cronos is 0.12.0 (§4 of the evaluation measures it) |
| `src/Sagas/Sagas/SagaStep.cs:93-110` `Schedule<TMessage>(message, delay)`, `ScheduleAt<TMessage>(message, dueAt)`; `:155` `PlannedSchedule` | one-shot timeouts delivered back to the saga | the old saga tests (`tests/Sagas/Sagas.Tests/Domain.cs:133,159`, `Engine/LifecycleTests.cs:58-68,222-232`) | **already replaced** by step 9's scheduled reactions `[OnEvent<T>(DueAt = …)]` (S-9, T-2); no recurrence existed |
| `src/Sagas/Sagas.Engine/SagaEngine.cs:211-219,258-259` | `Enqueue(SagaOutboxKind.Timeout, …, dueAt ?? now + delay)` | the saga engine | replaced (step 9) |
| `src/Sagas/Sagas/Abstractions/Abstractions.cs:68-69` `SagaOutboxKind.Timeout = 1`, `:75-84` `DueAt` ordering | the outbox kinds | the engine | replaced (step 9) |
| `src/Sagas/Sagas.Persistence.Marten/MartenSagaStore.cs:20-40` | claim `DueAt <= now && LockedUntil <= now` ordered by `DueAt`, then lock one by one | the Marten store | replaced by the Npgsql outbox (step 8C/9B) |
| `src/Sagas/Sagas.Engine/SagaOutboxProcessor.cs:143,151` | `Task.Delay(settings.PollInterval, stoppingToken)` **without** a `TimeProvider` | the outbox loop | replaced (`ReactionWorker` delays on the injected clock) |
| `src/TestUtility/Sagas.Testing/SagaTestClock.cs:11-20` | a `TimeProvider` that moves on command; `SagaTestHost.cs:219-244,290-296` advances to due timeouts and lists them | saga tests | replaced by `FakeTimeProvider` + `ReactionTest` (step 9) |
| `src/Clock/Clock/Clock.cs` | an `AsyncLocal<TimeProvider>` ambient clock | the old engine and tests | dropped in step 4 (time.md: an ambient clock cannot drive long-running loops) |
| `src/Persistence/Persistence/IUnitOfWorkScope.cs:5-8` | doc: "non-HTTP entry points (background workers, **scheduled tasks**, integration tests, console apps)" | — | the only mention of scheduled tasks; no scheduler exists |
| `src/Messaging/Messaging.Backplane/MessageBackplaneRunner.cs:8` | a `BackgroundService` relay loop | messaging | not a scheduler; replaced in step 11 |
| `examples/ArtFlow/…/Auction.Status.Enum.cs:7` `Settled = 2`; `…/Auction.Invariant.WhenSettlesBidMustBeEmpty.cs:8-11` | a settled state, no code that settles on a schedule | the sample | the use case "settle after the end" is a **scheduled reaction** per auction (step 9), not a recurring job |
| `templates/TarolSolution` | outdated template | — | not migrated (plan) |

**Nothing to migrate.** The old repository has no recurring schedule, no cron evaluation, no job abstraction, no job table,
no leader election. Its one-shot timeouts were already migrated as scheduled reactions in step 9.

## 2. What Tarol-Net already has that step 14 builds on

| Where | What | Relevance |
|---|---|---|
| `src/Changes/Tarol.Changes/Reactions.cs:32-47` `OnEventAttribute<TEvent>` with `Name`, `DueAt` | scheduled reactions (S-9) | the one-shot "at a time" mechanism; a recurrence could be built on it (variant D) |
| `src/Changes/Tarol.Changes/Reactions.cs:196-211` `ReactionSet.Context`, `ReactionSet.Options` | context `AsSystem("Reactions.<name>")`, key `(tenant, reaction:<name>, event id)` | the pattern a job's context and key follow (`Jobs.<name>`, `job:<name>`, occurrence) |
| `src/Changes/Tarol.Changes/ChangePipeline.cs:53-161` | `Run(IChange<T>)`, `Run(ICreation<T>)`, `ChangeOptions(Key, ExpectedVersion, Principal)`; no principal = internal entry, not authorized (R-6) | a job runs its changes here as an internal entry |
| `src/Kernel/Tarol.Kernel/OperationContext.cs:36-41` `OperationContext.Start(actor, tenant, clock)`; `Actor.cs:73` `Actor.System(component, onBehalfOf)` | a fresh conversation | the job's context (changes.md §5, row "job / timeout") |
| `src/Persistence/Tarol.Persistence/Outbox.cs` `IOutbox` (claim, complete, retry, bury, dead letters, re-drive, pending, wait) | the outbox port | variant D reuses it; a job table would copy its shape |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlOutbox.cs:241-256` claim: `head AND due_at <= $1 AND lease expired … ORDER BY due_at, id … FOR UPDATE SKIP LOCKED`, joined to `events` on `event_id` | the claim | **an outbox row needs an event** (`JOIN … events e ON e.event_id = u.event_id`): an occurrence can only be an outbox row if it is an event first |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlSchema.cs:9-13,90-103` version 1 script, `outbox` with `due_at`, `head`, `UNIQUE (reaction, event_id)` | the schema | a `jobs` table is a schema addition; "migrations beyond version 1" are step 15 (plan row 15) |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlAggregateStore.cs:433` `PurgeKeys(olderThan, batch)`; `NpgsqlSchema.cs:70-79` `idempotency_keys` | keys are deleted after the retention (P-11, 7 days) | **an occurrence key lives 7 days**: the cursor, not the key, must keep old occurrences from running again (probe check) |
| `src/Sagas/Tarol.Sagas/ReactionHost.cs:48-90` | a `BackgroundService` that runs the worker loops **and** the key purge every `PurgeEvery` (1 h) in **every** worker process | the only recurring task today; idempotent, not once per occurrence |
| `src/Sagas/Tarol.Sagas/ReactionWorker.cs:101-129,131-185` | the loop (poll, back off on errors), activity per row linked to `traceparent`, `LoggerMessage` 9001–9003 | hosting, logs and tracing a job worker can copy |
| `src/Sagas/Tarol.Sagas/DeliveryPolicy.cs` | `Classify` + backoff 1 s … 5 min, 10 attempts (C-9, S-13) | a job's failure policy |
| `src/Sagas/Tarol.Sagas/Testing/ReactionTest.cs:23-206` | `ReactionTest.At(now, set)`, `Later(Δ)`, `Drain()`; its own settable clock | the shape of a `JobTest` |
| `src/Messaging/Tarol.Messaging.Npgsql/NpgsqlBus.cs:212-222` `PurgeMessages(olderThan, batch)` | retention "step 15 schedules it" | a system job that has no schedule yet |
| `Directory.Packages.props:40-43` `Microsoft.Extensions.TimeProvider.Testing` 11.0.0 | `FakeTimeProvider` | the test clock |
| `docs/architecture/changes.md:23,29,158` | "settle after the end time (background job)", context `Actor.System("Jobs.AuctionSettlement")`, new correlation, key optional | the design target of row "job / timeout" |
| `docs/architecture/identity-context.md:34,70` | one DI scope per job; `Actor.System(component, onBehalfOf)` | the context rule |

## 3. Verdict

* Old code: **nothing to migrate**; the unused Cronos version is removed or replaced when the package that needs a cron parser
  adds its own central version (§9 decides which).
* Tarol-Net: the pieces are there (pipeline, keys, system context, outbox, worker hosting, logs, fake clock). What is missing is a
  **schedule** (a cron or interval with a time zone), a **durable cursor** per (tenant, job) that survives downtime, an exactly-once
  rule across processes, and a test helper.
