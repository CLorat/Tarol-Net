# Scheduled jobs: evaluation (step 14 design)

This note evaluates [jobs.md](../architecture/jobs.md). It covers:
* the inventory of the old code;
* what a job is and how it is declared;
* the cron parser, time zones and DST;
* three ways to run an occurrence exactly once across processes, probed under kill -9, freezes and crashes;
* missed runs after downtime, failures and overlap;
* tenancy, testing and hosting;
* the owner's answers, and what PRs 14A–14C must do.

**Design only**: this PR changes no package code. Probe and output: [`probes/jobs/`](probes/jobs/),
[`output.txt`](probes/jobs/output.txt), the inventory [`inventory.md`](probes/jobs/inventory.md). Labels as in the
[conventions](../architecture/conventions.md). Source of the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old code has nothing to migrate** **[verified by reading]**:
  * no recurring schedule, no cron evaluation, no job abstraction, no leader election;
  * an unused central version of Cronos 0.11.1 (`Directory.Packages.props:13`, referenced by no project);
  * one-shot saga timeouts (`SagaStep.Schedule`/`ScheduleAt`), already replaced by step 9's scheduled reactions.
* **Decided** (J-1 … J-9):
  * **What a job is** (J-1): both a change entry `[Scheduled(cron)]` on a static factory of a change (generated, like `[OnEvent<T>]`) and an `IJob` class with DI for fan-outs and reports.
  * **Declaration** (J-2): in code, with a configuration override per job name (`Tarol:Jobs:<name>:Cron`, `TimeZone`, `Enabled`).
  * **Cron parser** (J-3): **our own** 5-field parser. Unix (Vixie) day-of-month OR day-of-week; once in the repeated fall-back hour; a skipped time runs at the end of the gap.
  * **Exactly once** (J-4): **one row per (tenant, job)** in a new `jobs` table:
    * the row holds the cursor (the oldest occurrence not yet run);
    * claimed with `FOR UPDATE SKIP LOCKED` and a lease that a heartbeat extends;
    * every occurrence runs with the idempotency key (tenant, `job:<name>`, occurrence) in its change's commit (C-8);
    * the cursor moves after every occurrence.
  * **Missed runs** (J-5): a policy per job, default **Latest**. All is opt-in, capped at 100 per claim; Skip has a grace of 1 min.
  * **Tenancy** (J-6): a job is tenant-less by default, or per tenant through an `ITenantDirectory` the application provides.
  * **Failures** (J-7): as for reactions:
    * C-9 classification, 10 attempts, backoff 1 s … 5 min;
    * then a dead letter in `jobs_dead`, re-driven by a human;
    * never two runs of one row at a time.
  * **Packages** (J-8): a new package **`Tarol.Jobs`**, with the attribute and generator in `Tarol.Changes` and the port in `Tarol.Persistence` (+ Npgsql). Three PRs, 14A–14C.
  * **System jobs** (J-9): Tarol's key purge and bus retention become system jobs in 14C.
* **Measured** (4 cores, PostgreSQL 17.12 in Docker on the same host, SDK 11.0.100, JIT Release and Native AOT):
  * **Chaos** (3 worker processes × 2 loops, 10 jobs firing every second, 61 s = 610 occurrences). A random process was killed with -9 ≈ 11 times, frozen for 2–6 s 12 times, and crashed right after its commit 21–25 times. Results:
    * **0 gaps** for every variant;
    * **0 duplicate commits** with the occurrence key, **24 occurrences committed twice** without it;
    * Native AOT gives the same result.
  * **Fire latency without faults:** p50 51 ms / p99 107 ms with a 100 ms poll; 476 / 988 ms with a 1 s poll.
  * **Claims:** a claim of a job row costs ~1 ms at 10, 1,000 and 100,000 rows.
  * **10,000 tenants due at once:** job rows take 38 / 12 / 9.2 s with 1 / 4 / 8 loops; the outbox variant takes 102 / 35 / 26 s.
  * **Behind 20,000 waiting reactions:** a job starts 78 ms late on job rows and **11.5 s** late on the outbox.
  * **A 5 s run on a 2 s lease:** with a heartbeat, 1 run; without one, 2 runs and 1 commit; on the outbox, one occurrence ran 4 times (2 commits for 3 occurrences).
  * **Cron:** our parser takes 0.11–2.9 µs per `Next` natively, Cronos 0.04–0.12 µs. Both are AOT-clean (0 IL warnings); Cronos adds 53 KB.
* **Found** (§5):
  * An occurrence stored with more precision than `timestamptz` keeps (µs) gets a key text that no longer matches the cursor read back.
  * A cursor that moves only after a whole catch-up loses it all on a crash.
  * One claim holding several jobs runs them one after another.
  * The key lives 7 days; the cursor must protect older occurrences.
  * On the outbox, a job competes with reactions, its batch-mates outlive their leases, and a changed schedule still runs the old row.
  * Cronos and Unix cron mean different things for `0 2 13 * 5` and in the repeated hour.

## 1. Inventory **[verified by reading]**

Condensed; every file:line is in [`inventory.md`](probes/jobs/inventory.md).

| Where | What | Verdict |
|---|---|---|
| `CLorat/Tarol` `Directory.Packages.props:13` Cronos 0.11.1 | a central version, 0 references | nothing to migrate; J-3 chose our own parser |
| `Sagas/SagaStep.cs:93-110`, `SagaEngine.cs:211-259`, `MartenSagaStore.cs:20-40` | one-shot timeouts, claim by `DueAt` + `LockedUntil` | replaced by scheduled reactions (step 9, S-9) |
| `Sagas.Engine/SagaOutboxProcessor.cs:143,151` | `Task.Delay` without a clock | replaced (the worker delays on the injected `TimeProvider`) |
| `Clock/Clock.cs`, `Sagas.Testing/SagaTestClock.cs` | ambient clock, own fake clock | replaced by `TimeProvider` / `FakeTimeProvider` (step 4) |
| `Persistence/IUnitOfWorkScope.cs:5-8` | "scheduled tasks" named as an entry point | the only mention; no scheduler |
| Tarol-Net `ReactionHost.cs:48-90` | the key purge every hour in **every** worker process | the one recurring task today; a system job (J-9) |
| Tarol-Net `NpgsqlBus.cs:212-222` `PurgeMessages` | retention "step 15 schedules it" | a system job (J-9) |
| Tarol-Net `NpgsqlOutbox.cs:241-256` | the claim joins `events` on `event_id` | an outbox row needs an event: variant D makes each occurrence an event |

## 2. Candidates

### 2.1 What a job is (question 1; J-1)

| | **K-A change entry** | **K-B `IJob` class** | K-C both |
|---|---|---|---|
| shape | `[Scheduled("0 2 * * *")] static CloseDay From(JobOccurrence o) => new(o.At)` on an `IChange<T>`/`ICreation<T>` | `sealed class SettleEnded(IReadModels reads) : IJob { ValueTask<Result> Run(JobRun run, CancellationToken ct) }` | K-A for one change, K-B for many |
| fits | one aggregate per occurrence (close a day, open a period) | fan-out over a list (settle every ended auction), reports, maintenance | both |
| entry rule | C-5: attributes on a static factory, the factory is pure (S-2) | DI, may read projections; a decision still reads the aggregate (QR-6) | — |
| key | (tenant, `job:<name>`, occurrence) | per change: (tenant, `job:<name>`, occurrence/part); part = the change's target, or given | — |
| generated | yes (`GeneratedJobs.Set`, the Changes generator, S-6) | registered in code | — |

The probe's job body is K-B shaped (a delegate that runs `RecordOccurrence` through the pipeline with the key); K-A is the same
call made by a generated runner, as `GeneratedReactions.Set` makes it for reactions **[verified]** (step 9).

### 2.2 Declaration, time zones, DST (question 2; J-2)

* **In code**: the attribute's literal (K-A), or `AddJobs(j => j.Add<TJob>("0 2 * * *", o => o.TimeZone = "Europe/Vienna"))` (K-B).
  Default zone UTC; IANA ids.
* **Configuration override** (J-2): `Tarol:Jobs:<name>:Cron`, `:TimeZone`, `:Enabled`. It is read and validated at startup. The
  attribute's literal is validated at compile time too (a TCHG diagnostic).
* **A changed schedule** (from a deploy or from configuration) must not run the old occurrence. On variant D it did **[verified]**:
  the outbox row of the old `Due` fired at the old time after `Reschedule`. A job row stores the schedule text. At startup a
  different text sets the cursor to the first occurrence after now (no catch-up across a schedule change).
* **DST** **[verified]**, Europe/Vienna, both parsers:

| Schedule | Night | Runs at |
|---|---|---|
| `30 2 * * *` | spring forward 2027-03-28 (02:00 → 03:00) | 03-27 02:30+01, **03-28 03:00+02** (the end of the gap), 03-29 02:30+02 |
| `30 2 * * *` | fall back 2026-10-25 (03:00 → 02:00) | 10-24 02:30+02, **10-25 02:30+02 once** (the first one), 10-26 02:30+01 |
| `0 * * * *` | fall back, 01:00–04:00 | Cronos: 01:00+02, 02:00+02, **02:00+01**, 03:00+01; ours: 01:00+02, 02:00+02, 03:00+01 (once per clock time) |
| `*/30 * * * *` | spring forward 01:00–03:30 | both: 01:00, 01:30, 03:00, 03:30 (02:00 and 02:30 do not exist) |

* An unknown zone throws `TimeZoneNotFoundException`. It must throw at registration, never at 02:00 **[verified]**. Zones resolve
  under `InvariantGlobalization` and Native AOT from `/usr/share/zoneinfo` **[verified]**. A container image without tzdata fails
  at startup **[unverified]**: the docs say so.

### 2.3 Cron parser (question 3; J-3)

| | Cronos 0.12.0 | **our 5-field parser** (J-3) | fixed intervals only |
|---|---|---|---|
| Native AOT **[verified]** | 0 IL warnings, +53 KB (8,262,912 vs 8,209,440 bytes) | 0 IL warnings | 0 |
| `Next` natively **[verified]** | 0.04 µs (UTC), 0.10–0.12 µs (Vienna) | 0.11 / 1.0 µs (UTC), 0.24 / 2.9 µs (Vienna) for `0 2 * * *` / `*/15 9-17 * * 1-5` | 0.02 µs |
| day-of-month and day-of-week both set | **AND** (`0 2 13 * 5` = Friday the 13th) | **OR**, as Unix (Vixie) cron (every Friday and every 13th) | — |
| repeated fall-back hour | interval fields (`*/15`, `0 *`, `1-3`) run in **both** hours | once per clock time | — |
| differential **[verified]** | 273,703 occurrences (14 expressions × UTC, Vienna, New York × 2 years): 401 differ, **38** in the repeated hour, **363** dom+dow, nothing else | | |
| seconds, `L W # ?`, names | yes | no seconds, no `L W # ?`; names (`MON`, `JAN`) to add in 14C | — |
| a daily job at 02:00 Vienna | yes | yes | no: drifts by an hour twice a year |

The costs are microseconds a day per job. The differences are semantics, and our parser keeps Unix's.

### 2.4 Exactly once across processes (question 4; J-4)

| | **A job rows** | B one leader | D the outbox |
|---|---|---|---|
| mechanism | `jobs` row per (tenant, job): cursor `next_at`, claim id, lease; `SKIP LOCKED`; heartbeat lease/3; key per occurrence | `pg_try_advisory_lock` on a session; the leader reads due rows (no lease) and advances by the cursor it read | a `Schedule` aggregate; each `Due(at)` event has two scheduled reactions: run the job, raise the next `Due` |
| new storage | 2 tables | the same table | none; **2 events + 2 outbox rows per occurrence** |
| parallel across processes | yes | no | yes |
| a frozen process | its rows wait until its lease (3 s in the probe) | **every job waits** while the leader is frozen | its rows wait until their lease |
| kill -9 take-over | after the lease | 19–62 ms (the session closes) | after the lease |
| a run longer than the lease | heartbeat: 1 run | no lease | the row and its batch-mates run again (§4.4) |
| competes with reactions | no | no | yes (§4.3) |
| a changed schedule | text in the row → cursor recomputed at startup | the same | the old row still runs (§2.2) |

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/jobs/output.txt) §1, §3: JIT and Native AOT)

| Check | Result |
|---|---|
| two workers, one due row | one claims (`SKIP LOCKED`), one commit |
| crash between commit and cursor move | after the lease the occurrence runs again and the key replays: 2 runs, **1 commit** |
| the same without the key | **2 commits** |
| key purged (retention), the same occurrence again | 2 commits: the key alone does not protect an occurrence older than the retention |
| B: two leaders | exactly one; its session terminated → the other leads after 19–62 ms |
| D: schedule aggregate + scheduled reactions | one commit per second |
| D: rescheduled | the old occurrence still ran |
| fake clock: before 02:00 / at 02:00 / again | nothing / one run, the cursor moves to tomorrow / nothing |
| context and key | `System("Jobs.nightly")`, the row's tenant, a new correlation, no causation, key `job:nightly` / `2026-11-16T02:00:00.0000000Z` |
| 3 days down: All / Latest / Skip | 3 / 1 / 0 runs; the cursor at the next future occurrence in all three |
| 240 missed hourly runs, All, cap 100 | 100 + 100 + 40 in three claims |
| a run longer than the lease, no heartbeat | another worker claims it; the first one's advance is refused |
| the same with a heartbeat (lease/3) | no second claim; Latest skips the occurrence that passed meanwhile |
| three Vienna nights on the fake clock | 02:30+01, 03:00+02, 02:30+02 |
| a failing job | retried after 1 s, 2 s, given up after 3 attempts; the cursor moves on |

All checks: cron 8 of 8, test 14 of 14, postgres 7 of 7, on JIT and Native AOT.

## 4. Measurements **[verified]** ([`output.txt`](probes/jobs/output.txt); 4 cores, 15 GB, SDK 11.0.100, PostgreSQL 17.12 with default settings in Docker on the same host, Npgsql 10.0.3; JIT runs in Release)

### 4.1 Chaos (§5, §6, §10; 3 processes × 2 loops, 10 jobs every second, 61 s = 610 occurrences)

Faults: every 1.5–3 s a random process is killed (-9, restarted after 0.3 s) or frozen (SIGSTOP 2–6 s); "crash" = `FailFast`
right after the commit of every 25th run of a process, before the cursor moves. Lease 3 s.

| Variant | Faults | kills / freezes / crashes | gaps | occurrences committed twice | runs started twice | lateness p50 / p99 / max |
|---|---|---|---|---|---|---|
| A | none | — | 0 | 0 | 0 | 51 / 107 / 151 ms |
| A, poll 1 s | none | — | 0 | 0 | 0 | 476 / 988 / 1,002 ms |
| B | none | — | 0 | 0 | 0 | 72 / 144 / 175 ms |
| D | none | — | 0 | 0 | 0 | 55 / 215 / 249 ms |
| **A** | all three | 11 / 12 / 22 | **0** | **0** | 32 | 88 ms / 4.2 s / 5.3 s |
| A without the key | all three | 10 / 12 / 23 | 0 | **24** (29 extra commits) | 25 | 79 ms / 6.1 s / 9.2 s |
| B | all three | 10 / 12 / 25 | 0 | 0 | 24 | 177 ms / 5.4 s / 6.2 s |
| D | all three | 10 / 12 / 21 | 0 | 0 | 30 | 262 ms / 8.2 s / 9.2 s |
| A, Native AOT | all three | 11 / 12 / 22 | 0 | 0 | 33 | 67 ms / 4.1 s / 5.8 s |
| D, Native AOT | all three | 11 / 12 / 21 | 0 | 0 | 27 | 97 ms / 4.4 s / 6.3 s |

"Runs started twice" are the crashes after the commit and the frozen workers whose lease passed: the key turns each into a replay.

### 4.2 Claims and tenants (§4, §7)

| | 1 loop | 4 loops | 8 loops |
|---|---|---|---|
| A: claim of 16 rows + roll back, at 10 / 1,000 / 100,000 rows | p50 0.80 / 0.99 / 0.99 ms, p99 1.7 / 3.2 / 2.1 ms | | |
| A: 10,000 tenants, one nightly job each, all due at once | 38.1 s (262/s) | 11.9 s (838/s) | 9.2 s (1,093/s) |
| D: the same as 10,000 schedules | 101.6 s (98/s) | 34.6 s (289/s) | 25.5 s (392/s) |

Each occurrence is one commit (A) or two commits plus two rows (D).

### 4.3 A reaction backlog (§8; 20,000 `AckPing` rows on 200 streams due before the job, 4 reaction loops)

| Variant | job due → started | backlog drained |
|---|---|---|
| D (the job is an outbox row) | **11,495 ms** | 18.9 s |
| A (+ 1 job loop) | 78 ms | 20.0 s |

### 4.4 A run longer than the lease (§9; 5 s run, 2 s lease, 3 workers, 9 s)

| Variant | occurrences started | runs | most runs of one occurrence | commits |
|---|---|---|---|---|
| A + heartbeat | 1 | 1 | 1 | 1 |
| A, no heartbeat | 1 | 2 | 2 | 1 |
| D (its schedule fires every second) | 3 | 6 | 4 | 2 |

On D the worker runs a claimed batch one row after another, so the rows behind the slow one outlive their lease and are claimed again.

### 4.5 Cron (§2, §3)

| Schedule | JIT UTC / Vienna | Native AOT UTC / Vienna |
|---|---|---|
| interval 15 min | 0.05 / 0.02 µs | 0.02 / 0.02 µs |
| Cronos `0 2 * * *` | 0.12 / 0.25 µs | 0.04 / 0.12 µs |
| Cronos `*/15 9-17 * * 1-5` | 0.12 / 0.20 µs | 0.04 / 0.10 µs |
| ours `0 2 * * *` | 0.27 / 0.41 µs | 0.11 / 0.24 µs |
| ours `*/15 9-17 * * 1-5` | 1.67 / 3.61 µs | 1.01 / 2.91 µs |

Ours walks days, hours and minutes; a bitmask walk (14C) makes the second row cheaper, but at one call per occurrence it does not matter.

## 5. Found while probing

1. **An occurrence must round-trip through the store unchanged** **[verified]**. An anchor with 100 ns ticks gave an occurrence of
   `…22.1793033`, and the cursor read back from `timestamptz` was `…22.179303`. The schedule then produced a second occurrence
   between them, with a different key text, and committed twice. Occurrences are whole seconds (cron is minute-grained anyway),
   and the key is their UTC text.
2. **Move the cursor after every occurrence, not after the claim** **[verified]**. When the cursor moved only after the whole catch-up,
   a crash every 7 runs lost all progress: 74 of 105 occurrences were still missing after 60 s. Moving it after each occurrence
   gave 0 gaps.
3. **One claim, one job** **[verified]**. A loop that claimed 16 rows ran them one after another; one job's catch-up delayed the others
   it held. Lateness under chaos p50 12.2 s with 16 rows per claim, 0.35 s with 1. 14B claims one row per loop and runs loops in
   parallel.
4. **The key lives 7 days** (P-11) **[verified]**. After a purge the same occurrence commits again. The cursor, which only moves forward,
   keeps old occurrences from running again. The key covers the window between the commit and the cursor's move.
5. **The key protects commits, not side effects** **[verified]**. A frozen worker past its lease, or a crash after the commit, starts the
   body again: 24–33 such runs per chaos window, all of them replays. An `IJob` that sends mail or calls HTTP must do it through an
   outgoing message (step 11) or an effect (`IEffect<T>`), not in its body.
6. **On the outbox, jobs inherit reaction semantics they do not want** **[verified]**:
   * the claim is shared, so 20,000 waiting reactions delay a job by 11.5 s;
   * a slow job outlives its 1 min lease and its batch-mates run again;
   * a scheduled row is never due before its commit;
   * a changed schedule runs the old row;
   * each occurrence costs 2 events (10,000 tenants × nightly = 20,000 events a night; a per-minute job for 1,000 tenants
     = 2.9 M events a day).
7. **A leader is simple and fragile** **[verified]**. It takes over in 19–62 ms after a kill, but a frozen leader (GC, SIGSTOP, a stalled
   VM) holds its session lock and stops every job. There is also no parallelism.
8. **Cron dialects differ** **[verified]**. In Cronos `0 2 13 * 5` is Friday the 13th; in Unix cron it is every Friday and every 13th.
   Cronos also fires `0 * * * *` twice in the repeated fall-back hour. Ours follows Unix and fires once per clock time.
9. **Fake-clock heartbeats need steps** **[verified]**. A heartbeat on `Task.Delay(d, fake)` beats only when the test advances in steps
   smaller than lease/3 and yields. `JobTest.Advance` must step through due times (as `SagaTestHost` did, `SagaTestHost.cs:219-244`).
10. **A job that finds nothing to do must return `Success`** (sagas insight 2). A `Conflict` is retried and dead-lettered (C-9).

## 6. Decided design (sketch for 14A–14C; names final in the PRs)

* **Two shapes** (J-1):
  * the change entry
    ```csharp
    public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
    {
        [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]          // name = the type name (pin with Name =, as S-14)
        public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
        public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
    }
    ```
    generated into `GeneratedJobs.Set` (a `JobSet`) by the Changes generator: the definitions and the runner
    (name, occurrence) → factory → `pipeline.Run(change, context, new ChangeOptions(key))`;
  * the job class
    ```csharp
    public sealed class SettleEndedAuctions(IReadModels reads) : IJob
    {
        public async ValueTask<Result> Run(JobRun run, CancellationToken ct)
        {
            foreach (var a in await reads.Store<AuctionRow>().Ended(run.Occurrence.At, ct))
                if ((await run.Change(new SettleAuction(a.Id), ct)).TryGetValue(out Violation? v)) return v;  // key: occurrence/target
            return Result.Success();
        }
    }
    builder.Services.AddTarol(…).AddJobs(j => j.Add<SettleEndedAuctions>("15 2 * * *", o => o.Missed = Missed.Latest), GeneratedJobs.Set);
    ```
* **Occurrence**: `JobOccurrence(string Job, DateTimeOffset At, TimeZoneInfo Zone, Option<Id<ITenant>> Tenant)`; `At` whole seconds, UTC.
* **Context**: `OperationContext.Start(Actor.System("Jobs." + name), tenant, clock)`. It has a new correlation per run and no
  causation. It is an internal entry: no principal, no authorization (R-6).
* **Key**: `IdempotencyKey(tenant, "job:" + name, at.UtcDateTime.ToString("O"))`; for `run.Change` the target id is appended
  (`…/<target>`); a creation passes its part explicitly.
* **Schedule**: our parser (J-3: 5 fields, names, Vixie OR, once in the repeated hour, end of the gap); `Every(TimeSpan)` for
  intervals; validated at registration and, for literals, by a TCHG diagnostic.
* **Configuration** (J-2): `Tarol:Jobs:<name>:Cron`, `:TimeZone`, `:Enabled`, read at startup.
* **Rows** (J-4): the `jobs` table holds one row per (tenant, job) with these columns:
  * the schedule: `schedule` (cron + zone text), `enabled`;
  * the cursor: `next_at`;
  * the claim: `claim_id`, `lease_until`;
  * failures: `attempts`, `last_error`.

  The other parts:
  * `jobs_dead` holds (id, tenant, name, occurrence, attempts, error, dead_at);
  * the port `IJobRows` (`Sync`, `Claim`, `Move`, `Advance`, `Extend`, `Retry`, `Bury`, `DeadJobs`, `Redrive`, `Overdue`) on both stores;
  * at startup, `Sync` registers new rows (cursor = the first occurrence after now), recomputes the cursor of a changed schedule,
    and disables the rows of removed jobs.
* **Runner**: one row per claim; occurrences by the policy; the key per occurrence; `Move` after each; heartbeat lease/3;
  `Advance` releases.
* **Missed runs** (J-5): `Missed.Latest` (default), `All` (`MaxCatchUp` 100 per claim), `Skip` (grace 1 min).
* **Tenancy** (J-6): tenant-less by default; `PerTenant = true` needs an `ITenantDirectory` (the app's). Its tenants are synced into
  rows at startup and every hour, so a new tenant starts at its next occurrence and a removed one's row is disabled.
* **Failures** (J-7):
  * `Delivery.Classify` + `DeliveryPolicy` (C-9, S-7, S-13): a retry is the same occurrence after the backoff;
  * after `MaxAttempts` the occurrence moves to `jobs_dead` and the cursor moves on;
  * `Redrive(id)` runs it once more with its key;
  * never two runs of a row at a time.
* **Host** (J-8): `JobHost : BackgroundService` in the worker process (`AddJobs`, beside `AddReactionWorker`):
  * `Workers` loops (default 2), poll 1 s, lease 1 min;
  * logs `LoggerMessage` 14001–14010 (retry, dead letter, lease lost, schedule changed, missed occurrences skipped);
  * `ActivitySource "Tarol.Jobs"`: one `Internal` activity per occurrence, `job <name>`, tags `tarol.job`, `tarol.occurrence`,
    `tarol.tenant`, `tarol.attempt`, `tarol.verdict`;
  * no hosting package reference on .NET 11 (sagas.md §7).
* **System jobs** (J-9, 14C):
  * `tarol.purge-keys` (hourly, tenant-less, Latest) replaces `ReactionHost`'s per-process purge when a job host is registered;
  * `tarol.purge-messages` when `Tarol.Messaging.Npgsql` is registered.
* **Testing**: `JobTest.At(now, jobs)` over `InMemoryAggregateStore` (its in-memory rows) and a fake clock, with
  `Advance(Δ)` stepping through due times, `Runs`, `DeadJobs()`, and `Drain()` of the reactions the jobs cause (with `ReactionTest`).

## 7. Earlier decisions that constrain jobs (not re-asked)

| Decision | Text (as recorded) | Consequence here |
|---|---|---|
| C-5 (changes.md §9) | entries are attributes on the change; `[OnEvent<T>]`, `[Webhook]`, `[Consumes]` on a static `From` factory | `[Scheduled]` on a static `From(JobOccurrence)` factory (K-A) |
| C-7 | version conflicts: never retry in the pipeline | the job runner retries a `Conflict` as a delivery (C-9), never inside the pipeline |
| C-8 | idempotency keys: successes only, stored with the append | the occurrence key; a run that commits nothing stores no key and decides again (harmless by construction) |
| C-9, S-7, S-13 | Conflict = retry; NotFound/422 retry; 10 attempts, 1 s … 5 min | the job failure policy (J-7) |
| C-10 | the factory generates a new aggregate's id | a creation job is deduplicated by its key only, as a reaction |
| C-11, R-6 | context is a parameter; checked only when a principal is passed | a job is an internal entry: no permission check |
| changes.md §5 | job: `Actor.System("Jobs.<name>")`, new correlation, no causation | as recorded; the key is no longer "optional" (§10) |
| S-2 | reactions are changes only; the factory is pure | K-A factories are pure; side effects go to messages/effects (§5 item 5) |
| S-6 | the reaction generator lives in `Tarol.Changes` | so does the job entry generator |
| S-9 (T-2 of sagas.md) | scheduled reactions by time, exempt from the head rule | the one-shot "at a time" mechanism stays for sagas; recurrence is not built on it (J-4) |
| S-11, D-8 | one new package per step where needed; no meta-package yet | `Tarol.Jobs` (J-8) |
| P-4 | shipped idempotent SQL + `EnsureSchema` | `jobs`, `jobs_dead` added with `CREATE TABLE IF NOT EXISTS` (version 1 script, like `outbox_dead` in 9B) |
| P-5 | tenant first in every key | `PRIMARY KEY (tenant, name)` |
| P-11 | keys purged after 7 days | the cursor protects older occurrences (§5 item 4); the purge itself becomes a system job (J-9) |
| QR-6 | a decision never reads a projection | an `IJob` may read a projection to *find* targets; each change decides on its own aggregate |
| time.md, TKRN001 | no ambient clock; scheduled time from the engine's `TimeProvider` | the runner, heartbeat and delays use the injected clock; `FakeTimeProvider` drives them |
| D-15 | Docker for integration tests; Aspire for samples only | integration tests on Testcontainers; the sample runs a job in the AppHost worker |

## 8. Plan impact

* **Row 14:** the decided split, three PRs (J-8).
* **Row 15 (operations):**
  * the meter `Tarol.Jobs`: occurrences, lateness, attempts, dead jobs;
  * a health check on the most overdue cursor (`now − min(next_at)` of enabled rows) and on `jobs_dead`;
  * an admin entry to list and re-drive dead jobs (with the dead letters);
  * the schema upgrade path if `jobs` changes after version 1.
* **Row 16 (data protection):** jobs are where retention runs (deletion sweeps, key rotation for crypto-shredding). A per-tenant
  job row is the tenant's: it is removed with the tenant (the directory sync).
* **Row 18 (samples):** ArtFlow's nightly settlement report as an `IJob` in the worker.

## 9. Owner answers (2026-11-16 / 2026-11-17)

Rounds:
* **Round 1**, after the inventory, the cron, DST and AOT numbers and the first probes: J-1 … J-3.
* **Round 2**, after the chaos, tenant, backlog and lease numbers: J-4 … J-9.

The prefix J- was free (taken: C-, CA-, D-, M-, P-, Q-, QR-, R-, S-, T-). Not asked because already decided: §7.

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| J-1 | What is a job? (system context `Jobs.<name>`, new correlation, key (tenant, `job:<name>`, occurrence); a crash between commit and cursor move: 1 commit with the key, 2 without; a nightly settlement is a fan-out, one aggregate per commit) | **Both shapes** (recommended): a change entry `[Scheduled]` on a static factory, generated like `[OnEvent<T>]`; and `IJob` classes with DI for fan-outs and reports, each change keyed by (occurrence, target). |
| J-2 | How is a schedule declared? (a literal can be checked at compile time; an unknown zone throws `TimeZoneNotFoundException`; zones resolve under InvariantGlobalization and Native AOT) | **Code + configuration override**: the attribute or `AddJobs(…Add<T>(cron))` is the default; `Tarol:Jobs:<name>:Cron`/`:Enabled`/`:TimeZone` override it, validated at startup (the recommendation was code only). |
| J-3 | Which cron parser? (Cronos 0 IL warnings, +53 KB, `Next` 0.04–0.12 µs natively; ours 0.11–2.9 µs; both handle the DST gap and the repeated 02:30; 401 of 273,703 occurrences differ: 38 interval fields in both repeated hours, 363 dom+dow AND vs OR) | **Our own 5-field parser**: Unix (Vixie) OR semantics, once in the repeated hour, no `L W # ?`, no seconds; no dependency (the recommendation was Cronos). |
| J-4 | Exactly once across processes? (610 occurrences, kill/freeze/crash: 0 gaps everywhere, 0 duplicate commits with the key, 24 without; backlog: A 78 ms vs D 11.5 s; 10,000 tenants: A 9.2 s vs D 25.5 s; long run: A + heartbeat 1 run, D 4 runs of one occurrence; D runs the old occurrence after a cron change; B takes over in 19–62 ms) | **A: job rows + key** (recommended): `jobs` table, one row per (tenant, job), `SKIP LOCKED` claims with a lease and a heartbeat, the occurrence key in the commit, the cursor moved after every occurrence. |
| J-5 | Missed runs after downtime? (3 days down: All 3, Latest 1, Skip 0, cursor at the next future occurrence; All capped 100 per claim; moving the cursor per occurrence: p50 0.35 s instead of 12 s) | **Per job, default Latest** (recommended): `All` opt-in with `MaxCatchUp` (100 per claim), `Skip` with a grace of 1 min. |
| J-6 | Where does the tenant come from? (no tenant registry; 10,000 per-tenant rows due at once: 38 / 12 / 9.2 s with 1 / 4 / 8 loops; claim ~1 ms at any table size) | **Tenant-less, or per tenant** (recommended): tenant-less by default; `PerTenant` asks the app's `ITenantDirectory`, synced at startup and hourly; one row and key per tenant. |
| J-7 | Failures and overlap? (retry after 1 s, 2 s, given up after 3 attempts; without heartbeat a second run, 1 commit; with heartbeat one run) | **Like reactions + `jobs_dead`** (recommended): C-9/S-7/S-13 classification and backoff on the same occurrence, then a dead job and the cursor moves on, re-driven by a human; never overlapping runs (one claim per row, heartbeat). |
| J-8 | Packages and PRs? | **A new `Tarol.Jobs`, three PRs** (recommended): attribute and generator in `Tarol.Changes`, port and in-memory rows in `Tarol.Persistence`, tables and claims in `Tarol.Persistence.Npgsql`, the rest in `Tarol.Jobs`; 14A, 14B, 14C. |
| J-9 | Tarol's own maintenance as system jobs? (the key purge runs hourly in every worker process; `PurgeMessages` is scheduled by nobody) | **Yes, in 14C** (recommended): `tarol.purge-keys` and `tarol.purge-messages`, once per hour across the cluster; `ReactionHost` keeps its loop only without a job host. |

## 10. Changes against steps 7–13 (announced here, made in 14A–14C)

| Where | Change | Why |
|---|---|---|
| `Tarol.Changes` | `ScheduledAttribute` (`Cron`, `TimeZone`, `Name`, `Missed`, `PerTenant`), `JobOccurrence`, `JobSet`; the generator emits `GeneratedJobs.Set` next to `GeneratedReactions.Set`; new diagnostics from TCHG009 (bad factory shape, invalid cron literal, duplicate job name) | J-1, J-2 (14A) |
| `Tarol.Persistence` | the port `IJobRows` with `JobRow`, `DeadJob`; in-memory rows in `InMemoryAggregateStore` (`store.Jobs`); `TarolBuilder` registers it with the store | J-4 (14A) |
| `Tarol.Persistence.Npgsql` | `jobs` and `jobs_dead` in the version 1 script (`CREATE TABLE IF NOT EXISTS`, additive); `NpgsqlJobRows` | J-4, J-7 (14B) |
| `Tarol.Sagas` `ReactionHost` | the hourly key purge is skipped when a job host is registered (`tarol.purge-keys` runs it once per cluster) | J-9 (14C), a behaviour change |
| `Tarol.Messaging.Npgsql` | `PurgeMessages` scheduled as `tarol.purge-messages` with a retention option | J-9 (14C) |
| `docs/architecture/changes.md` §5 | row "job / timeout": key (tenant, `job:<name>`, occurrence), no longer "optional" | J-1, J-4 |
| `docs/architecture/sagas.md` | the open question "Partitioning work across processes" and §10: jobs are not reactions; `ReactionHost`'s purge moves | J-4, J-9 |
| `Directory.Packages.props` | nothing new: no cron dependency (J-3); the old repository's Cronos version is not carried over | J-3 |

No change to `IAggregateStore`, `ChangePipeline`, `IOutbox`, the outbox SQL or the reaction worker's claim.

## 11. What the implementation must do (in the decided order)

The three PRs are stacked (14B on 14A, 14C on 14B): **retarget each PR to `main` before its base is merged**. In step 13, #46 merged
into its stacked base two seconds before that base went to `main`.

### PR 14A `changes: scheduled entries; persistence: job rows port`
* `Tarol.Changes`:
  * `[Scheduled]` on a static `From(JobOccurrence)` factory of an `IChange<T>` or `ICreation<T>`;
  * the generated `GeneratedJobs.Set` (definitions + runner through `ChangePipeline` with the occurrence key), composed across
    assemblies (`JobSet.Combine`, a name in two sets throws);
  * TCHG diagnostics with snapshot, incrementality and diagnostic tests; TVAL006 exempt in the factory (C-13).
* `Tarol.Persistence`:
  * `IJobRows`, `JobRow`, `DeadJob`;
  * the in-memory rows on the store's `TimeProvider`;
  * a shared contract suite for both stores: claim, move, advance with and without the claim, extend, retry, bury, re-drive, sync.

### PR 14B `persistence.npgsql: job rows`
* `jobs` and `jobs_dead` in the script.
* `NpgsqlJobRows`:
  * one-row claims with `SKIP LOCKED`;
  * `Move`/`Advance`/`Extend`/`Retry` guarded by the claim id;
  * `Bury` (`DELETE … RETURNING` into `jobs_dead` + advance) and `Redrive` in one statement each;
  * `Sync` in one transaction.
* Integration tests on Testcontainers:
  * the contract suite;
  * two claimers, one row;
  * lease expiry and a lost advance;
  * a crash between commit and move (the key replays);
  * the µs round trip of the cursor (§5 item 1).
* A chaos test: two processes, kill -9 between commit and move, count commits per occurrence. Bound it by occurrences, never by a
  wall-clock count. Run it 10–25 times in Release, not next to the suite.

### PR 14C `jobs: the job worker, cron, JobTest; samples and docs`
* `src/Jobs/Tarol.Jobs/` per the conventions §8:
  * `Cron` (our parser, names, Vixie OR, DST rules of §2.2, `Every`);
  * `IJob`, `JobRun` (`Change`, `Create` with the key), `JobOptions`, `Missed`, `ITenantDirectory`;
  * `JobHost`, `AddJobs`, the configuration override (`Tarol:Jobs:<name>`);
  * system jobs (J-9), `JobTest`;
  * logs, `ActivitySource "Tarol.Jobs"`;
  * README, XML docs, the skill `skills/tarol-jobs/SKILL.md`.
* Tests (unit, `FakeTimeProvider`):
  * parser: the DST table of §2.2, a differential against a recorded list, dom+dow OR, invalid expressions;
  * the policies (All / Latest / Skip, the cap);
  * heartbeat and overlap;
  * retry → dead job → re-drive;
  * a schedule change at startup, the per-tenant sync;
  * the configuration override.
* `Tarol.Jobs.AotSmoke` (0 IL warnings, with `TAROL_PG`: a job runs once natively) and `Tarol.Jobs.PackageTests`.
* `samples/Sagas`: a nightly settlement report job in the worker.
* `docs/jobs/` guide; `docs/architecture/jobs.md` status; CHANGELOG, README; plan row 14 "done".

## 12. Skill evaluation checklist (`tarol-jobs`, 14C; `tarol-changes` update, 14A)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| close the books every night at 02:00 Vienna time | `[Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]` on the change's `From(JobOccurrence)` | a `BackgroundService` with `Task.Delay(24h)`; `DateTime.Now` (TKRN001); a `PeriodicTimer` per process |
| settle every auction that ended | an `IJob` reading the list and `run.Change(new SettleAuction(id))` per auction | one change that loads many aggregates; reading a projection inside `Execute` (QR-6) |
| send a report by e-mail | an `IJob` that publishes a message / effect | sending mail in the job body (runs again after a crash, §5 item 5) |
| a job per tenant | `PerTenant = true` + an `ITenantDirectory` | looping over tenants in one tenant-less job with one key |
| run only once after downtime / every missed day | `Missed = Missed.Latest` (default) / `Missed.All` | assuming every missed occurrence runs |
| pause a job in production | `Tarol:Jobs:<name>:Enabled = false` | deleting the row by hand |
| test it | `JobTest.At(now, …).Advance(1.Days())`, `FakeTimeProvider` | sleeping in tests; counting runs inside a wall-clock window |
| a job that finds nothing to do | return `Result.Success()` | returning a `Conflict` (retried and dead-lettered, C-9) |

## 13. Verification of this PR (SDK 11.0.100) **[verified]**

Docs and probes only; the package code is `main` at `984e621` (step 13 done).
* **At the start on `main`** (2026-11-16):
  * `dotnet build Tarol.slnx`: 0 warnings;
  * `dotnet format --verify-no-changes`: clean;
  * `dotnet test --solution Tarol.slnx` with Docker: **1442 of 1442**;
  * `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: 21 apps, 0 IL warnings, all checks passed;
  * `dotnet pack -c Release` + `eng/verify-packages.sh`: OK;
  * CI run 162 of `main` (`build`, `aot-smoke`, `integration`): green.
* **On this branch** (2026-11-17, at `5195f38`; only docs and probes changed):
  * `dotnet build Tarol.slnx`: 0 warnings, 0 errors;
  * `dotnet format --verify-no-changes`: clean;
  * `dotnet test --solution Tarol.slnx` with Docker: **1442 of 1442**;
  * `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: 21 apps, 0 IL warnings, all checks passed;
  * `dotnet pack -c Release` + `eng/verify-packages.sh`: OK.
* **The probe**: `all.sh` sections 0–10 ([`output.txt`](probes/jobs/output.txt)); sections 7 and 9 were run again after two bench
  fixes (a seed slower than its lead, and a count of one occurrence on a schedule that fires every second).

## 14. Open items

* Seconds in schedules (sub-minute jobs): not needed by a scenario; `Every(TimeSpan)` covers short intervals.
* Priority between jobs, or a job that must run on a specific process (a GPU, a licence): not needed.
* A job's own progress inside an occurrence (resume a half-done fan-out): today each change is keyed, so a re-run skips what
  committed. A checkpoint per occurrence would help long fan-outs.
* `PushTests.After_a_reconnect_the_subscriptions_are_replayed` (SignalR) can still time out under full-suite load (caching.md §14);
  it passed in both full runs of this session (`main` and this branch).

## 15. Implementation 14A: scheduled entries and the job rows port

`changes: scheduled entries; persistence: job rows port`. Labels as in the conventions.

### 15.1 Public API

`Tarol.Changes` (`Jobs.cs`):
* `ScheduledAttribute(string cron)` with `TimeZone`, `Name`, `Missed`, `PerTenant`, on a static `From(JobOccurrence)` factory of an
  `IChange<T>` or `ICreation<T>`;
* `enum Missed { Latest, All, Skip }` (Latest is the default, value 0);
* `JobOccurrence(job, at, zone, tenant)` with `Job`, `At` (UTC), `Zone`, `Tenant`, `Local` and `KeyText`; `at` must be a whole
  second (else `ArgumentException`);
* `JobDefinition(Name, Cron, TimeZone, Missed, PerTenant)`, the delegate `JobRunner(pipeline, occurrence, clock, ct)`;
* `JobSet`: `Definitions`, `Contains`, `Run(pipeline, occurrence, clock, ct)`, `Combine(params JobSet[])` (a name in two sets
  throws), `Empty`, and the static `Context(occurrence, clock)`, `Key(occurrence, part?)`, `Options(occurrence, part?)`;
* generated per assembly: `public static class GeneratedJobs { JobSet Set; Run(…) }` (namespace = assembly name);
* diagnostics TCHG009 (factory shape, empty `Name`/`TimeZone`, an undefined `Missed`), TCHG010 (invalid cron literal), TCHG011
  (duplicate job name).

`Tarol.Persistence` (`JobRows.cs`, `InMemory/InMemoryJobRows.cs`):
* `IJobRows`: `Sync`, `Claim`, `Move`, `Advance`, `Extend`, `Retry`, `Bury`, `DeadJobs`, `Redrive`, `Rows`, `Overdue`;
* records `JobSchedule`, `JobSyncChange` + `enum JobSyncKind`, `JobRow`, `JobState`, `DeadJob`;
* `InMemoryAggregateStore.Jobs` (in-memory rows on the store's clock);
* `TarolBuilder.UseStore(factory, outbox, jobs)` (a new overload; it registers `IJobRows`); `UseInMemoryStore` uses it.

### 15.2 Deliberate differences from §6 and §11

| Sketch | Built | Why |
|---|---|---|
| `Missed` in `Tarol.Jobs` (§11 14C) | in `Tarol.Changes` | the attribute takes it (`Missed = Missed.All`), and `Tarol.Changes` does not reference `Tarol.Jobs` |
| `Claim` of rows | `Claim(lease)` returns `Option<JobRow>`: one row | §5 item 3 (one row per claim) made part of the port's shape |
| `Move(row, next)` … | the parameter is `cursor` | CA1716: `next` is a reserved word in VB (the lessons named `Next`) |
| `Redrive(id)` "runs it once more" | a pending flag on the dead job; the row's next claim runs it **first**, under the row's own claim (`JobRow.Redrive`, `JobRow.Occurrence`), so it never runs beside the row (J-7). `Advance` then deletes the dead job; `Bury` keeps it dead with the new attempts | a new row per re-drive (as the outbox does) would break `PRIMARY KEY (tenant, name)` and could overlap the row's own run; a re-drive is requested by an admin process without a job host |
| `Overdue` | `Option<DateTimeOffset>`: the oldest due cursor of an enabled row | step 15's health check computes now − it |
| (not sketched) | `Rows(ct)` → `JobState` | tests, `JobTest` and admin pages read the cursors |
| `Sync` registers, recomputes, disables | also **re-enables** at `First` (no catch-up for a pause) and **drops the claim** of a rescheduled row (the old holder's `Advance` returns `false`) | a paused job that resumes must not run its backlog; a holder of the old schedule must not write an old-schedule cursor |
| a TCHG diagnostic for literals | TCHG010 runs the field rules of `CronSyntax.cs` (`Tarol.Cron.CronFields`), a file 14C links into `Tarol.Jobs`' parser | one set of rules for compile time and startup; the zone is checked at startup (14C), not at compile time (the compiler's zone database is not the server's) |
| `JobRunner(pipeline, occurrence)` | takes the `TimeProvider` too | `OperationContext.Start` reads the correlation id's timestamp from the clock (a fake one in tests) |

### 15.3 Changes against steps 7–13

| Where | Change | Kind |
|---|---|---|
| `TarolBuilder` | `UseStore(factory, outbox, jobs)` overload; `UseInMemoryStore` registers `IJobRows` | additive API |
| `InMemoryAggregateStore` | `Jobs` property | additive API |
| `Tarol.Validations` analyzer | TVAL006 exempt inside a static `[Scheduled]` factory of an `IValidatable` change (C-13) | behaviour (fewer warnings) |
| `Tarol.Changes` generator | `GeneratedJobs.g.cs` for assemblies with `[Scheduled]`; nothing else changes (snapshots of changes and reactions unchanged) | additive |
| `docs/architecture/changes.md` §5 | the "job" row: `JobSet.Context`, the row's tenant, key (tenant, `job:<name>`, occurrence) | docs |

No change to `IAggregateStore`, `ChangePipeline`, `IOutbox` or the reaction generator.

### 15.4 Tests

* `Tarol.Changes.Tests/JobTests.cs` (8): the generated definitions; an occurrence runs as `System("Jobs.<name>")`, no causation,
  key text `2026-11-16T01:00:00.0000000Z`; the same occurrence replays, the next commits; tenant occurrences and a `NotFound`;
  parts of keys; whole seconds and `Local`; `Combine` and duplicates; an unknown job throws.
* `Tarol.Changes.Generators.Tests`: the `jobs` snapshot (2 jobs, a nested type, a pinned name, `Missed.All`, `PerTenant`); the
  generated code compiles without warnings; no set without jobs; incrementality (editing a factory body or moving it
  regenerates nothing; a schedule change regenerates only `GeneratedJobs.g.cs`); diagnostics TCHG009 (7 shapes + not a change +
  3 attribute values), TCHG010 (16 invalid expressions, each message asserted), 5 valid expressions, TCHG011.
* `Tarol.Validations.Generators.Tests`: TVAL006 exempt in a `[Scheduled]` factory, reported in a foreign attribute's.
* `tests/Persistence/Shared/JobRowsContract.cs` (18, run on the in-memory rows here and on PostgreSQL in 14B): sync, one claim
  per due row, oldest first, move/advance, a move restarts the next occurrence's attempts, a lost lease changes nothing, the heartbeat, retry backoff, bury, re-drive first and
  never beside the row, a re-drive that fails again, a schedule change drops the claim, disable/re-enable without catch-up,
  undeclared rows disabled, tenants, duplicates and sub-second times throw, the cursor round trip.
* `Tarol.Sagas.Tests/BuilderTests`: `IJobRows` is the store's `Jobs`.
* Smoke and package apps: `Tarol.Changes.AotSmoke` and `Tarol.Changes.PackageTests` run a Vienna nightly job and its replay;
  `Tarol.Persistence.AotSmoke` syncs, claims and advances the in-memory rows.

### 15.5 Skill checklist (`tarol-changes`, updated)

Rule 10 and three mistakes added (TCHG009, TCHG010, `o.Local` for the zone's day); the evaluation tasks of §12 rows 1, 5 and 8
need `tarol-jobs` (14C). Size 9.0 KB (was 8.0 KB).

### 15.6 Suppressions

None.

### 15.7 Verification (SDK 11.0.100, 2026-11-17) **[verified]**

At `9357564` (the code of this PR; the notes followed), in a separate worktree, with Docker and the smoke containers:
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors;
* `dotnet format Tarol.slnx --verify-no-changes`: clean;
* `dotnet test --solution Tarol.slnx` with Docker: **1506 of 1506** (1442 at the start, + 64);
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **21 apps, 0 IL warnings**, all checks passed (the new checks
  of `Tarol.Changes.AotSmoke` and `Tarol.Persistence.AotSmoke` natively);
* `dotnet pack -c Release` + `eng/verify-packages.sh`: OK (`Tarol.Changes.PackageTests` runs the job from the package);
* `main`'s CI before the start (run 164 at `bfd8d88`, after #47): `build`, `aot-smoke`, `integration` green.

After this run, `d23457d` changed `Move` (the next occurrence's attempts start at 1, found while writing 14C's runner) and added
a contract test: `Tarol.Persistence.Tests` 116 of 116. The full verification of 14B (§16.7) includes it.

## 16. Implementation 14B: the job rows on PostgreSQL

`persistence.npgsql: job rows`, stacked on 14A. Labels as in the conventions.

### 16.1 Public API

* `NpgsqlJobRows : IJobRows` (internal constructor), exposed as `NpgsqlAggregateStore.Jobs`; `UseNpgsqlStore` registers it as
  `IJobRows` (through 14A's `UseStore(factory, outbox, jobs)`).
* Two tables in the version 1 script (`CREATE TABLE IF NOT EXISTS`, additive; P-4), `NpgsqlSchema.Version` stays 1:
  * `jobs (tenant, name, schedule, enabled, next_at, claim_id, lease_until, attempts, last_error)`, `PRIMARY KEY (tenant, name)`,
    partial index `jobs_due (next_at) WHERE enabled`;
  * `jobs_dead (id identity, tenant, name, occurrence, attempts, last_error, dead_at, redrive)`, partial index
    `jobs_dead_redrive (tenant, name, id) WHERE redrive`.

### 16.2 Deliberate differences from §6 and §11

| Sketch | Built | Why |
|---|---|---|
| `jobs_dead (id, tenant, name, occurrence, attempts, error, dead_at)` | + `redrive boolean` | 14A's re-drive is a pending flag, claimed under the row's own claim |
| `Bury` = `DELETE … RETURNING` into `jobs_dead` + advance | one statement: `UPDATE jobs … WHERE claim_id` + `INSERT INTO jobs_dead … WHERE EXISTS` (or, for a re-drive, `UPDATE jobs_dead`) in data-modifying CTEs | the job row stays (it is the cursor); only the occurrence goes to the dead jobs |
| `Redrive` "in one statement" | `UPDATE jobs_dead SET redrive = true WHERE id = $1 AND NOT redrive` | the run happens at the row's next claim |
| claim of a due row | one statement: a pending re-drive of a free, enabled row first (`FOR UPDATE OF j SKIP LOCKED` on the join), else the oldest due cursor (`FOR UPDATE SKIP LOCKED`, `jobs_due`) | one round trip; the re-drive index is empty almost always |
| `Sync` in one transaction | + `pg_advisory_xact_lock(hashtext('<schema>.jobs'))` | two starting hosts would otherwise race on the same inserts and disables; claims are not blocked |
| `Move` keeps the claim | also `attempts = 1, last_error = NULL` | found while writing 14C's runner: the occurrence after a success inherited the failures of the one that ran; fixed in 14A's port (`d23457d`) and here |

Every claim-guarded statement returns `count(*)` of the rows it owned, so `false` means the claim was lost. All times are
written in UTC (Npgsql refuses a `DateTimeOffset` with an offset for `timestamptz`); `Overdue` reads `min(next_at)` with a typed
reader (`ExecuteScalar` returns a `DateTime`).

### 16.3 Changes against steps 7–13

| Where | Change | Kind |
|---|---|---|
| `NpgsqlSchema.Script` | `jobs`, `jobs_dead` and their indexes | additive schema (an existing database gets them at the next `EnsureSchema`) |
| `NpgsqlAggregateStore` | `Jobs` property | additive API |
| `UseNpgsqlStore` | registers `IJobRows` | additive registration |

### 16.4 Tests (`Tarol.Persistence.Npgsql.IntegrationTests`, Testcontainers PostgreSQL 17)

* `NpgsqlJobRowsContractTests`: 14A's contract (18) on a schema per test.
* `NpgsqlJobRowsTests` (5):
  * the script creates both tables and indexes, idempotently;
  * 20 racing claimers on 5 due rows get 5 distinct rows;
  * the µs round trip (§5 item 1): a whole-second cursor reads back with the same ticks and key text, `next_at` equals the
    literal; the probe's `…22.1793033` is refused;
  * a crash between the commit and the move: the next claim (after the lease) gets the same occurrence, the key is seen, a
    second append with it is refused, 1 stream; the old claim's `Advance` is refused;
  * 4 concurrent syncs of 50 rows add each row once.
* `NpgsqlJobRowsChaosTests` (1): two worker processes (`Tarol.Persistence.Npgsql.ChaosWorker`, a console app started with
  `dotnet`), 3 jobs every second, a random worker killed with -9 every 0.5–1.5 s and each worker crashing (`FailFast`) right after
  every 7th commit, before its cursor moves. Bounded by occurrences: it stops when every cursor passed 25 occurrences (3-minute
  guard). Then for every job every occurrence before the cursor committed exactly once, at most one after it (a kill between
  commit and move), and streams = keys.
* `Tarol.Persistence.Npgsql.AotSmoke` (with `TAROL_PG`): sync, a claim and a second claim that gets nothing, bury, re-drive and
  advance, natively.

**Chaos in Release, 10 runs, alone** (not next to the suite) **[verified]**: 10 of 10 passed, each 75 occurrences checked, 0 gaps,
0 second commits; 14–19 kills and 5–8 crashes after a commit per run; 25–27 s per run. The contract and store tests passed in
Release too (23 of 23).

### 16.5 Skill checklist

`tarol-persistence-npgsql`: rule 10 (job rows: read `Rows`/`DeadJobs`/`Overdue`, `Redrive(id)`, never update `tarol.jobs` by hand)
and one mistake (an `UPDATE tarol.jobs` to pause a job). The job tasks of §12 are evaluated with `tarol-jobs` (14C).

### 16.6 Suppressions

None.

### 16.7 Verification (SDK 11.0.100, 2026-11-17) **[verified]**

At `c47fff2` (the code of this PR and §16.1–§16.6), in a separate worktree, with Docker and the smoke containers:
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors;
* `dotnet format Tarol.slnx --verify-no-changes`: clean;
* `dotnet test --solution Tarol.slnx` with Docker: **1531 of 1531** (14A's 1506, + 1 contract test in memory, + 24 on PostgreSQL);
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **21 apps, 0 IL warnings**, all checks passed (the job rows of
  `Tarol.Persistence.Npgsql.AotSmoke` natively);
* `dotnet pack -c Release` + `eng/verify-packages.sh`: OK;
* the chaos test 10 times in Release, alone: 10 of 10 (§16.4).

This run also covers 14A's `d23457d` (a move starts the next occurrence's attempts), pushed to 14A after its own run (§15.7).

## 17. Implementation 14C: the job host

`jobs: the job worker, cron, JobTest; samples and docs`, stacked on 14B. Labels as in the conventions.

### 17.1 Public API

`Tarol.Jobs` (new; `src/Jobs/Tarol.Jobs`, references `Tarol.Sagas`):
* schedules: `abstract class Schedule` (`Text`, `After(after, zone)`, `static Every(TimeSpan)`), `sealed class Cron : Schedule`
  (`Parse`, `TryParse(expression, out cron, out error)`);
* `IJob` (`ValueTask<Result> Run(JobRun, CancellationToken)`), `JobRun` (`Occurrence`, `Attempt`, `Context`, `Change<T>(change)`
  keyed `occurrence/target`, `Create<T>(creation, part)` keyed `occurrence/part`);
* `JobOptions` (`Name`, `TimeZone`, `Missed`, `MaxCatchUp` 100, `Grace` 1 min, `PerTenant`, `Enabled`, `Policy`),
  `JobHostOptions` (`Workers` 2, `PollInterval` 1 s, `Lease` 1 min, at least 3 s; `TenantSyncEvery` 1 h);
* `JobsBuilder` (`Add(JobSet)`, `Add<TJob>(cron | schedule, configure)`, `Configure(name, configure)`),
  `JobServiceCollectionExtensions.AddJobs(this TarolBuilder, jobs, configure)`;
* `JobHost : BackgroundService` (`ActivitySourceName = "Tarol.Jobs"`, `Jobs`), `ITenantDirectory`, `JobAttempt`;
* `JobTest` (`At`, `Add(job, cron | schedule)`, `Given`, `Load`, `WithConfiguration`, `WithTenants`, `Sync`, `Advance`, `AdvanceTo`,
  `Down`, `Runs`, `Reactions`, `DeadJobs`, `Redrive`, `JobRows`, `Store`, `Rows`, `Pipeline`, `Clock`, `Now`);
* logs 14001 retry, 14002 dead job, 14003 lease lost, 14004 schedule changed, 14005 missed occurrences skipped, 14006 rows
  synced, 14007 loop failed, 14008 undeclared row, 14009 sync failed, 14010 a system job's count;
* skill `tarol-jobs`, README.

`Tarol.Persistence`: `MaintenanceTask(Name, Every, Run)`, `IMaintenanceHost`. `Tarol.Messaging.Npgsql`: `NpgsqlBusOptions.Retention`
(7 days), `PurgeBatch` (1000), `NpgsqlMessagingBuilderExtensions.PurgeMessagesJob`.

### 17.2 Deliberate differences from §6 and §11

| Sketch | Built | Why |
|---|---|---|
| `JobTest` over the container | job classes are added as **instances** (`test.Add(new MyJob(fakes), cron)`); `j.Add<TJob>` in a test throws with that hint | .NET 11's base framework has the DI abstractions, not the container; a runtime package takes no `Microsoft.Extensions.DependencyInjection` reference (J-8, no new dependency) |
| `JobTest.Drain()` | reactions are drained after every run when a `ReactionSet` is given (`Reactions`) | one call less, and `Advance` stays the only verb |
| (not sketched) | `JobTest.Down(Δ)` (workers down), `Given`, `Load`, `Sync`, `WithTenants`, `WithConfiguration` | the missed-run policies, tenants and the override need them |
| system jobs scheduled by Tarol.Jobs | `MaintenanceTask` + `IMaintenanceHost` in `Tarol.Persistence`: `Tarol.Messaging.Npgsql` registers its retention, `Tarol.Sagas` checks for the host | `Tarol.Jobs` references `Tarol.Sagas`, and neither `Tarol.Sagas` nor `Tarol.Messaging.Npgsql` may reference `Tarol.Jobs` |
| `tarol.purge-keys` "hourly" | `Schedule.Every(1 h)` (on the hour, UTC), `Latest`, only when the store implements `IIdempotencyKeyPurge` (the Npgsql store) | the in-memory store purges nothing |
| configuration override | also refuses a `Tarol:Jobs:<name>` section of no declared job, and a non-boolean `Enabled` | a typo would silently do nothing |
| a row of a removed job | `Sync` disables it; a row of another process's version (rolling deploy) is released for a lease (14008), never buried | only a process that declares the job may decide about it |
| `Pick` | at claim time: a run longer than the interval leaves its passed occurrences to the next claim's policy | the cursor must be known before the first `Move` |
| Cron: "a bitmask walk (14C)" | bitmasks per field (shared `CronSyntax.cs`), walking days → hours → minutes; months skip whole months | `Next` is called once per occurrence |
| `Every(TimeSpan)` anchor | the Unix epoch in UTC (whole seconds, at least 1 s) | a stable anchor across restarts and processes |
| (not sketched) | a cron that can never match (`0 0 30 2 *`) is refused at parse time | it would loop for 10 years at 02:00 |
| a differential against a recorded list | against the probe's own parser (`MiniCron`, copied into the tests as the oracle): 14 expressions × UTC, Vienna, New York × 2 years, **273,665 occurrences, all equal** | an independent implementation of the same rules; the design's 273,703 counted max(Cronos, ours) per pair and contained Cronos' 38 extra runs in the repeated hour |

### 17.3 Changes against steps 7–13

| Where | Change | Kind |
|---|---|---|
| `Tarol.Sagas` `AddTarolReactions` | with an `IMaintenanceHost` (a job host) `ReactionOptions.PurgeKeys` is not set from the store: `tarol.purge-keys` purges once per cluster (J-9) | **behaviour**, announced in §10 |
| `Tarol.Messaging.Npgsql` `UseNpgsqlBus` | registers the `MaintenanceTask` `tarol.purge-messages`: with a job host, delivered messages older than `Retention` are deleted hourly | **behaviour** (nothing purged before), additive options |
| `Tarol.Persistence` | `MaintenanceTask`, `IMaintenanceHost` | additive API |
| `Tarol.Changes.Generators/CronSyntax.cs` | `#if NETSTANDARD2_0` for one `Substring`, `IndexOfAny` for `#?`: the file compiles into `Tarol.Jobs` too (net11 analyzers) | none |
| `docs/architecture/sagas.md` | §4 keys, §10, the open question on recurring work | docs |

### 17.4 Tests

* `Tarol.Jobs.Tests` (59):
  * `CronTests` (24): the DST table of §2.2 (spring gap, fall back once, hourly in the repeated hour, steps through the gap, from
    the second pass), Vixie OR and AND, `*/n` as a star, names and weekday 7, strictly after and whole seconds, leap days and an
    impossible day, 8 invalid expressions with the TCHG010 messages, intervals (also before the epoch), `Latest`, the
    differential against the probe;
  * `JobTestTests` (23): the design's example with the drained reaction, a conversation per occurrence, Latest/All/the cap of
    100/Skip with its grace, retry → dead job → re-drive, `Invalid` at once, an exception retried, nothing to do, a fan-out that
    replays what committed, the override of cron and zone, a schedule change at startup, disable/re-enable without catch-up, 4
    invalid configurations, declaration checks, `Configure`, per-tenant rows following the directory, a failing tenant that
    blocks no other, `Create` keyed by its part;
  * `HostTests` (8, `FakeTimeProvider`): a run three leases long keeps its row (steps of 5 s, below lease/3) and Latest skips
    what passed; a run whose claim was dropped by a schedule change is cancelled and writes nothing; an undeclared row is
    left alone; `tarol.purge-keys` with the reaction options' retention and batch and a registered `MaintenanceTask`; `AddJobs`
    end to end with a container (generated entry, a DI job class, the key purge taken over from `ReactionHost`); without a job
    host `ReactionHost` keeps its purge; `AddJobs` twice and a per-tenant job without a directory; host options;
  * `SkillExampleTests` (1): the skill's example verbatim.
  Release, alone: **15 of 15** runs green.
* `Tarol.Messaging.Npgsql.IntegrationTests`: the retention task deletes nothing within `Retention`, then everything, in batches.
* `Tarol.Jobs.AotSmoke` (new, 22 apps): the DST rules, a generated entry and a class through `JobTest`, and with `TAROL_PG` the
  host on PostgreSQL running an `Every(1 s)` job with one commit per occurrence, natively.
* `Tarol.Jobs.PackageTests` (new): the generated set and `JobTest` from the packed package; the skill is listed.
* `samples/Sagas` through the Aspire AppHost (5 auctions, JIT, NATS): the per-tenant settlement report ran once as
  `System Jobs.SettlementReport` and counted 5 settled auctions; rows `SettlementReport (a tenant)` and `tarol.purge-keys`, no dead
  jobs, one `job SettlementReport` span.

### 17.5 Skill evaluation (`tarol-jobs`, `tarol-changes`; §12) **[verified]**

A fresh agent read only the two `SKILL.md` files (no repository, no web) and answered the eight tasks of §12.

| Task | Used | Mistakes of §12 |
|---|---|---|
| close the books at 02:00 Vienna | `[Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]`, `o.Local` for yesterday, `AddJobs(j => j.Add(GeneratedJobs.Set))` | none |
| settle every ended auction | an `IJob` with `run.Change(new SettleAuction(id))` per target, registered with `Add<T>(cron)` | none |
| a report by e-mail | a scheduled creation that raises an event; the mail from its publication | none (no mail in the body) |
| a report per tenant | `PerTenant = true` + `ITenantDirectory` | none |
| once after downtime / every missed day | default `Latest` / `Missed = Missed.All` | none |
| pause in production | `Tarol__Jobs__CloseDay__Enabled=false`, noting a typo fails | none |
| test it | `JobTest.At(…).Advance(…)`, the occurrence 01:00Z (02:00 CET) | none (no sleeping, no wall clock) |
| nothing to do | `return Result.Success()`, not a Conflict | none |

8 of 8 used the expected API; none made a listed mistake. A run without the skills was not repeated (13C showed the
baseline: no Tarol names at all). The gaps the agent named, closed in `tarol-jobs`:
* the signature of `ITenantDirectory`, and that the tenant is `o.Tenant` / `run.Occurrence.Tenant`;
* the return types of `run.Change`/`Create` and of `Advance`, the members of `JobAttempt`, `Store.Recorded` and `Load` on `JobTest`;
* what `part` is, that a failed target retries the occurrence and the committed ones replay, that a `[Scheduled]` creation is
  keyed;
* how to read a projection from a job (`IReadModels`, skill `tarol-queries`);
* `Schedule.Every` goes through `Add<TJob>(schedule)`, the `JobHostOptions` and the types of `Grace` and `Policy`;
* configuration is read at startup (restart the workers), names are case-sensitive.

And in `tarol-changes`: the example of an internal run by hand was labelled "a job" with `Actor.System("Jobs.AuctionSettlement")`,
which read as the way to write a job; it now points to `[Scheduled]` and `JobRun.Change`. `tarol-jobs` is 8.5 KB.

### 17.6 Suppressions

None. The probe's parser in the tests (`ProbeCron.cs`) is copied with its logic unchanged; it compiles under the test
analyzers as is.
