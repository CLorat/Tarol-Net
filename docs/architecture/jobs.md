# Scheduled jobs

Concept note for step 14 (see [migration/jobs.md](../migration/jobs.md)): **recurring work that is not a reaction runs as the
system, once per occurrence, from a row per job.**
* A job is a change on a schedule, or a job class that runs many changes.
* Each (tenant, job) has one row in the store: the cursor of the oldest occurrence not yet run.
* Workers in any number of processes claim due rows with a lease. They run each occurrence through the `ChangePipeline` as
  `Actor.System("Jobs.<name>")`, with the occurrence as the idempotency key, and move the cursor after each.
* A crash, a kill or a frozen process costs a late run, never a lost or a doubled commit.
* After downtime, a policy per job decides which missed occurrences run.
* The clock is the injected `TimeProvider`, so a fake clock drives the whole thing in tests.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/jobs/`](../migration/probes/jobs/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: designed** (owner decisions J-1 … J-9, [migration/jobs.md](../migration/jobs.md) §9); built in PRs 14A–14C (§11): **14A built**
(scheduled entries, the job rows port; §15), **14B built** (the rows on PostgreSQL; §16).

## 1. What a user of Tarol needs

| ArtFlow use case | Shape | Built from |
|---|---|---|
| close yesterday's books at 02:00 Vienna time | one change per occurrence | `[Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]` on `CloseDay.From(JobOccurrence)` |
| settle every auction that ended | a fan-out: read a list, one change per auction | `IJob` + `run.Change(new SettleAuction(id))` |
| a monthly report per tenant | per-tenant occurrences | `PerTenant = true` + the app's `ITenantDirectory` |
| the server was down for 3 days | run once, or every missed day | `Missed = Latest` (default) or `All` |
| one auction's deadline | **not a job**: a scheduled reaction | `[OnEvent<T>(DueAt = …)]` (step 9, S-9) |
| purge old idempotency keys | a system job, once per cluster | `tarol.purge-keys` (J-9) |

## 2. One occurrence **[verified]** (`selftest postgres`, `selftest test`, chaos; JIT and Native AOT)

```text
worker process (any number of them)                      PostgreSQL
JobHost loop: Claim one due row ─────────────────────────► jobs: (tenant, name) next_at ≤ now, lease expired
              (FOR UPDATE SKIP LOCKED, lease 1 min)          → claim_id, lease_until, attempts + 1
  occurrences = Pick(policy, cursor, now)                   (Latest: the newest; All: every one, ≤ 100; Skip: within 1 min)
  heartbeat every lease/3 ──────────────────────────────► lease_until = now + lease   (only with the claim)
  for each occurrence:
    context = Start(System("Jobs.<name>"), tenant, clock)
    key     = (tenant, "job:<name>", occurrence UTC)
    pipeline.Run(change, context, key) ─────────────────► events + idempotency_keys in one commit (C-8)
    Move(cursor → next) ────────────────────────────────► next_at  (crash before this: the key replays)
  Advance(next after now) ──────────────────────────────► claim released
  failure → Retry(backoff) … after 10 attempts Bury ────► jobs_dead, cursor moves on
```

## 3. What a job is (J-1)

```csharp
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>          // one change per occurrence
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}

public sealed class SettleEndedAuctions(IReadModels reads) : IJob                 // a fan-out
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken ct)
    {
        foreach (var a in await reads.Store<AuctionRow>().Ended(run.Occurrence.At, ct))
            if ((await run.Change(new SettleAuction(a.Id), ct)).TryGetValue(out Violation? v))
                return v;                                                             // retried; what committed replays
        return Result.Success();                                                      // nothing to do is a success
    }
}
```

* **Change entry**: an attribute on a static, pure factory (C-5, S-2), generated into `GeneratedJobs.Set` by the Changes
  generator (as reactions, S-6). Name = the type name, pinned with `Name =` before a rename (as S-14).
* **Job class**: DI, may read projections to *find* targets. Each change still decides on its own aggregate (QR-6). The key of
  each change is (occurrence, target).
* **Side effects** (mail, HTTP) go through outgoing messages or effects (step 11), never in the job body. A crash after the
  commit starts the body again: 24–33 such runs per chaos window, all of them replays **[verified]**.
* **Context** (changes.md §5): `Actor.System("Jobs.<name>")`, the row's tenant, a new correlation, no causation. It is an
  internal entry: no principal, no authorization (R-6).

## 4. Schedules (J-2, J-3)

* **Declared in code** (the attribute, or `AddJobs(j => j.Add<T>(cron, o => …))`), **overridden by configuration**:
  `Tarol:Jobs:<name>:Cron`, `:TimeZone`, `:Enabled`, read and validated at startup. An invalid literal is a compile-time diagnostic.
* **Our own 5-field cron**:
  * Unix (Vixie) semantics: day-of-month and day-of-week both set means either;
  * names, no `L W # ?`, no seconds; `Every(TimeSpan)` for intervals;
  * zone default UTC (IANA ids); an unknown zone throws at registration.
* **DST** **[verified]** (Europe/Vienna):

  | Schedule, night | Runs at |
  |---|---|
  | `30 2 * * *`, spring forward | 03:00+02 (the end of the gap) |
  | `30 2 * * *`, fall back | once, at the first 02:30 (+02) |
  | `0 * * * *`, fall back | 02:00 once (Cronos would run 02:00+02 and 02:00+01) |
* **Why not Cronos** **[verified]**: it is AOT-clean and fast (0.04–0.12 µs vs ours 0.11–2.9 µs), but means something else in
  2 places. Over 273,703 occurrences, 363 differ because Cronos ANDs day-of-month and day-of-week, and 38 because it runs
  interval fields in both repeated hours.
* **A changed schedule** (deploy or configuration): the row stores the schedule text; at startup a different text moves the cursor
  to the first occurrence after now. On the outbox the old occurrence still ran **[verified]**.
* **Occurrences are whole seconds in UTC.** An occurrence with more precision than `timestamptz` gave a key text that no longer
  matched the cursor read back, and committed twice **[verified]**.

## 5. Exactly once per occurrence (J-4)

| Fault (3 processes, 10 jobs every second, 610 occurrences) | Gaps | Occurrences committed twice **[verified]** |
|---|---|---|
| none | 0 | 0 |
| kill -9, SIGSTOP 2–6 s, crash after the commit (≈ 11 / 12 / 22) | **0** | **0** (24 without the occurrence key) |
| the same under Native AOT | 0 | 0 |

* **The commit is once**: the key is stored in the change's commit (C-8). A run after a crash, or by a worker whose frozen
  predecessor lost the lease, replays it.
* **The cursor is once**: it only moves forward, after every occurrence, and only with the claim. The key lives 7 days (P-11), the
  cursor forever: after a purge the same occurrence would commit again **[verified]**, but no claim offers it again.
* **One run at a time per row**: one claim per row and a heartbeat at lease/3. A 5 s run on a 2 s lease ran once with the
  heartbeat and twice without it **[verified]**.
* **One row per claim**: a loop that claimed 16 rows ran them in turn. Lateness under chaos was p50 12 s with 16 rows per claim
  and 0.35 s with 1 **[verified]**.
* **Not on the outbox** (variant D, rejected):
  * it delayed a job 11.5 s behind 20,000 waiting reactions;
  * it ran one occurrence 4 times when a run outlived the lease;
  * it ran the old occurrence after a schedule change;
  * it costs 2 events and 2 rows per occurrence **[verified]**.
* **Not one leader** (variant B, rejected): a frozen leader stops every job, and there is no parallelism. It took over in 19–62 ms
  after a kill **[verified]**.

## 6. Downtime, failures, tenants (J-5, J-6, J-7)

* **Missed runs**, per job **[verified]** (3 days down, a nightly job):

  | Policy | What runs |
  |---|---|
  | `Latest` (default) | 1 |
  | `All` | 3, at most `MaxCatchUp` (100) per claim |
  | `Skip` | 0, unless within the grace (1 min) |

  The cursor then sits at the next future occurrence.
* **Failures**: classified as for reactions (C-9, S-7). The same occurrence is retried with the backoff 1 s … 5 min, 10 attempts
  (S-13); then it is a dead job in `jobs_dead` and the cursor moves on. A human re-drives it; its key keeps a committed part from
  repeating. A job that finds nothing to do returns `Success`.
* **Tenants**: a job is tenant-less by default (one row). `PerTenant = true` asks the application's `ITenantDirectory` and keeps one
  row per tenant (synced at startup and hourly): a failing tenant never blocks another. 10,000 tenants due at once took
  38 / 12 / 9.2 s with 1 / 4 / 8 loops **[verified]**.

## 7. Hosting, logs, tracing (J-8, J-9)

```csharp
builder.Services.AddTarol(AppJson.Default, codec => …)
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddJobs(j => j
        .Add(GeneratedJobs.Set)                                         // [Scheduled] change entries
        .Add<SettleEndedAuctions>("15 2 * * *", o => o.TimeZone = "Europe/Vienna"),
        o => { o.Workers = 2; o.PollInterval = TimeSpan.FromSeconds(1); o.Lease = TimeSpan.FromMinutes(1); });
```

* `JobHost : BackgroundService` in the worker process, next to `ReactionHost`. It needs no hosting package reference on .NET 11
  (sagas.md §7).
* **Poll**: 1 s by default. Fire latency was p50 476 ms / p99 988 ms with a 1 s poll and 51 / 107 ms with 100 ms **[verified]**.
  A claim costs ~1 ms at any table size.
* **Logs** (`LoggerMessage` 14001–14010): retry, dead job, lease lost, schedule changed, missed occurrences skipped.
* **Tracing**: `ActivitySource "Tarol.Jobs"`, one activity per occurrence (`job <name>`; `tarol.job`, `tarol.occurrence`,
  `tarol.attempt`, `tarol.verdict`). The job's commits store its `traceparent`, so reactions link to it.
* **System jobs** (J-9): `tarol.purge-keys` replaces the per-process hourly purge of `ReactionHost`; `tarol.purge-messages` runs the
  bus retention. Each runs once per hour across the cluster.
* Metrics and health (most overdue cursor, dead jobs): step 15.

## 8. Testing

```csharp
var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set);
await test.Advance(TimeSpan.FromHours(2));          // steps through due times on the fake clock
Assert.Single(test.Runs);                           // 02:00 ran once; the cursor is at tomorrow 02:00
await test.Advance(TimeSpan.FromDays(3));           // Latest: one run for the three missed nights
```

`JobTest` uses `InMemoryAggregateStore` (its in-memory job rows), a `FakeTimeProvider`, and drains the reactions the jobs cause
(with `ReactionTest`). The probe's in-memory story ran 14 of 14 checks on JIT and Native AOT **[verified]**. Fake-clock heartbeats
beat only when time advances in steps smaller than lease/3, so `Advance` steps.

## 9. AOT and packages (J-8)

| Package | Step 14 adds |
|---|---|
| `Tarol.Changes` (+ generator) | `[Scheduled]`, `JobOccurrence`, `JobSet`, generated `GeneratedJobs.Set`, TCHG009+ (14A) |
| `Tarol.Persistence` | the port `IJobRows`, in-memory rows in `InMemoryAggregateStore` (14A) |
| `Tarol.Persistence.Npgsql` | `jobs`, `jobs_dead`, `NpgsqlJobRows` (14B) |
| `Tarol.Jobs` (new) | the cron parser, `IJob`, `JobRun`, `JobHost`, `AddJobs`, the configuration override, system jobs, `JobTest`, skill `tarol-jobs` (14C) |

The probe publishes with Native AOT with 0 IL warnings and passes its checks and chaos runs natively **[verified]**. No new
third-party dependency (J-3).

## New insights

1. **A cron job needs two kinds of "once"**: the commit is once by its key, the occurrence is once by a cursor that only moves forward.
   Either alone fails: no key, double commits under crashes; no cursor, old occurrences come back after the key purge.
2. **Exactly-once commits are not exactly-once runs.** A crash after the commit, or a frozen worker, starts the body again; only the
   pipeline's key makes that harmless. Side effects belong in messages and effects.
3. **The outbox is a queue of facts, a schedule is a cursor.** Built on the outbox, a schedule inherits order, leases, and backlog
   from reactions. In a row of its own, a schedule change is a row update.
4. **Precision is part of the key.** A time that the database rounds is no longer the same occurrence.
5. **Cron dialects are semantics, not syntax**: the same 5 fields mean "Friday the 13th" in one library and "every Friday and every
   13th" in Unix cron.

## Decisions taken (owner, 2026-11-16/17; migration/jobs.md §9)

| # | Decision |
|---|---|
| J-1 | both shapes: `[Scheduled]` change entries (generated) and `IJob` classes |
| J-2 | declared in code, overridden by configuration (`Tarol:Jobs:<name>`) |
| J-3 | our own 5-field cron parser (Vixie semantics), no Cronos |
| J-4 | job rows: one row per (tenant, job), leased claims, the occurrence key, the cursor moved per occurrence |
| J-5 | missed runs per job, default Latest; All capped; Skip with a grace |
| J-6 | tenant-less by default, per tenant through the app's `ITenantDirectory` |
| J-7 | failures like reactions, then `jobs_dead` and re-drive; no overlapping runs |
| J-8 | a new package `Tarol.Jobs`; three PRs 14A–14C |
| J-9 | Tarol's key purge and bus retention become system jobs (14C) |

## Open questions

* Seconds in schedules, priorities, process affinity, checkpoints inside a long fan-out: [migration/jobs.md](../migration/jobs.md) §14.
