# Scheduled jobs

Step 14 ([architecture/jobs.md](../architecture/jobs.md), decisions J-1 … J-9 in [migration/jobs.md](../migration/jobs.md) §9).
Recurring work that is not a reaction runs as the system, **once per occurrence across every worker process**, from one row
per (tenant, job). A crash, a kill or a frozen process costs a late run, never a lost or a doubled commit.

| You want | Write | Package |
|---|---|---|
| one change per occurrence (close a day, open a period) | `[Scheduled(cron)]` on the change's static `From(JobOccurrence)` | `Tarol.Changes` (generated `GeneratedJobs.Set`) |
| many changes per occurrence (settle everything that ended, a report) | an `IJob` class with `run.Change(…)` per target | `Tarol.Jobs` |
| one deadline of one aggregate (a saga timeout) | **not a job**: `[OnEvent<T>(DueAt = …)]` | `Tarol.Sagas` |
| run the jobs | `.AddJobs(j => …)` in the worker | `Tarol.Jobs` |

## Declare

```csharp
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]           // Name = "…", Missed = Missed.All, PerTenant = true
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}

public sealed class SettlementReport(IReadModels reads) : IJob      // samples/Sagas/Worker/Reports.cs
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken ct)
    {
        var tenant = run.Occurrence.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty;   // PerTenant = true
        var page = await reads.Store<AuctionRow>().Search(new ListQuery(tenant) { Filters = [new ColumnFilter("settled", true)], Count = true, Limit = 1 }, ct);
        if (!page.TryGetValue(out Page<AuctionRow>? settled))
            return page.TryGetValue(out Violation? refused) ? refused : Result.Success();
        return (await run.Create(new WriteDayReport(run.Occurrence.At, settled.Total ?? 0), "report", ct)).TryGetValue(out Violation? v) ? v : Result.Success();
    }
}
```

* `JobOccurrence`: `At` (UTC, whole seconds), `Local` (in the zone), `Zone`, `Tenant`, `Job`.
* `JobRun`: `Occurrence`, `Attempt`, `Context`, `Change(change)` keyed (occurrence, target), `Create(creation, part)` keyed
  (occurrence, part). A re-run after a crash replays what committed and continues with the rest.
* A job finds things to do; each change decides on its own aggregate (QR-6). Nothing to do is `Result.Success()`.
* Mail and HTTP go through events and their publications or effects (step 11): the body runs again after a crash.

## Run

```csharp
// the worker process (the API needs no AddJobs)
builder.Services.AddTarol(AppJson.Default, codec => …)
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddJobs(j => j
        .Add(GeneratedJobs.Set)                                                       // JobSet.Combine(a, b) for several assemblies
        .Add<SettlementReport>("15 2 * * *", o => o.TimeZone = "Europe/Vienna")
        .Configure("CloseDay", o => o.Policy = new DeliveryPolicy { MaxAttempts = 3 }),
        o => { o.Workers = 2; o.PollInterval = TimeSpan.FromSeconds(1); o.Lease = TimeSpan.FromMinutes(1); });
```

At startup the host syncs the rows with the declared jobs:
* a new job starts at its first occurrence after now;
* a changed schedule (code or configuration) moves the cursor to its first occurrence after now: the old one never runs;
* a job no longer declared, or disabled, keeps its row but is not claimed;
* a re-enabled job starts at its next occurrence (no catch-up for the pause).

Then every loop claims one due row, runs its occurrences, moves the cursor after each, and beats the lease every lease/3.

## Schedules

5-field Unix cron (minute hour day-of-month month day-of-week) with Vixie cron's meaning:

| Expression | Means |
|---|---|
| `0 2 * * *` | 02:00 every day |
| `*/15 9-17 * * MON-FRI` | every quarter hour 09:00–17:45 on weekdays |
| `0 2 13 * 5` | 02:00 **every Friday and every 13th** (both restricted: either; Cronos and Quartz mean Friday the 13th) |
| `0 0 1 JAN,JUL *` | midnight on 1 January and 1 July |
| `5/15 * * * *` | :05, :20, :35, :50 |
| `Schedule.Every(TimeSpan.FromMinutes(5))` | every 5 minutes from the Unix epoch, in any zone |

No seconds, no `L W # ?`, no `@daily`: an invalid literal in `[Scheduled]` is TCHG010, `Cron.Parse` throws `FormatException`.

Zones are IANA ids (default `UTC`), read from the OS. Daylight saving time (Europe/Vienna):

| Schedule, night | Runs at |
|---|---|
| `30 2 * * *`, spring forward | 03:00+02 (the end of the gap) |
| `30 2 * * *`, fall back | once, at the first 02:30 (+02) |
| `0 * * * *`, fall back | 01:00+02, 02:00+02, 03:00+01 (once per clock time) |

## Missed runs, failures, tenants

| | Default | Options |
|---|---|---|
| downtime | `Missed.Latest`: the newest missed occurrence once | `Missed.All` (each, `MaxCatchUp` 100 per claim), `Missed.Skip` (none unless within `Grace`, 1 min) |
| a failing occurrence | retried 1 s, 2 s … 5 min, 10 attempts, then a dead job and the cursor moves on | `Policy = new DeliveryPolicy { … }`; `Invalid` and `Forbidden` are dead at once |
| tenants | one tenant-less row | `PerTenant = true` + your `ITenantDirectory`: a row and a key per tenant, synced at startup and hourly; a failing tenant blocks no other |

Dead jobs: `IJobRows.DeadJobs(max, ct)` lists them (job, tenant, occurrence, attempts, error); `Redrive(id, ct)` runs the
occurrence again at its row's next claim, with its key, never beside the row's own run.

## Configuration

```json
{ "Tarol": { "Jobs": {
    "CloseDay":         { "Cron": "30 3 * * *", "TimeZone": "Europe/Berlin" },
    "SettlementReport": { "Enabled": false } } } }
```

Read when the host is resolved; an invalid cron, an unknown zone, a non-boolean `Enabled` or a name that is no declared job
fails the start. The sample's AppHost sets `Tarol__Jobs__SettlementReport__Cron` to `* * * * *`, so a run shows the report.

## System jobs

| Job | Does | When |
|---|---|---|
| `tarol.purge-keys` | deletes idempotency keys older than `ReactionOptions.KeyRetention` (7 days) | hourly, when the store purges keys (Npgsql); `ReactionHost` then skips its per-process purge |
| `tarol.purge-messages` | deletes bus messages every subscription received, older than `NpgsqlBusOptions.Retention` (7 days) | hourly, when `UseNpgsqlBus` is registered |

Other packages add their own with a `MaintenanceTask(name, every, run)` singleton. They are configurable like any job
(`Tarol:Jobs:tarol.purge-keys:Enabled`).

## Test

```csharp
var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set,
        reactions: GeneratedReactions.Set, stateOptions: AppJson.Default.Options)
    .Add(new SettlementReport(fakeReads), "15 2 * * *", o => o.TimeZone = "Europe/Vienna");
await test.Advance(TimeSpan.FromHours(2));                    // stops at every due time; reactions drained after each run
await test.Down(TimeSpan.FromDays(3)).Advance(TimeSpan.Zero); // three nights down: Latest runs one per job
test.Runs;                                                    // JobAttempt: job, tenant, occurrence, attempt, verdict, reason
```

`WithTenants(…)`, `WithConfiguration(…)` and `Sync()` act like a restarted host; `DeadJobs()` and `Redrive(id)` work on the
in-memory rows. Never count runs inside a wall-clock window.

## Observe

* Logs 14001 retry, 14002 dead job, 14003 lease lost, 14004 schedule changed, 14005 missed occurrences skipped, 14006 rows
  synced, 14007 loop failed, 14008 a row this process does not declare, 14009 sync failed, 14010 a system job's count.
* `ActivitySource "Tarol.Jobs"`: one activity per occurrence (`job <name>`; `tarol.job`, `tarol.occurrence`, `tarol.tenant`,
  `tarol.attempt`, `tarol.verdict`); the commits carry its `traceparent`.
* `IJobRows.Overdue(ct)`: the oldest due cursor (now − it = how late jobs run). Meters and health checks: step 15.

## Why not …

* **a `BackgroundService` with `Task.Delay`**: runs in every process, drifts, forgets missed runs, has no key.
* **one leader process**: a frozen leader (GC, a stalled VM) stops every job (jobs.md §5).
* **the outbox as a scheduler**: a job waited 11.5 s behind 20,000 reactions, a long run ran 4 times, and a changed schedule
  still ran the old occurrence.
* **Cronos**: it ANDs day-of-month and day-of-week and runs interval fields in both repeated hours (401 of 273,703 occurrences
  differ over two years).
