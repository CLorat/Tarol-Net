---
name: tarol-jobs
description: >-
  Use when a Tarol app must do recurring work on a schedule (nightly closing, settling everything that ended, a report per
  tenant, retention): [Scheduled] change entries and GeneratedJobs.Set, IJob classes with JobRun.Change/Create, AddJobs,
  JobsBuilder, JobOptions (TimeZone, Missed, MaxCatchUp, Grace, PerTenant, Enabled, Policy), JobHostOptions, Cron and
  Schedule.Every, ITenantDirectory, the Tarol:Jobs:<name> configuration override, dead jobs (IJobRows.DeadJobs/Redrive),
  system jobs, and JobTest. Do not write a BackgroundService with Task.Delay or a PeriodicTimer, do not add a cron package
  or Hangfire/Quartz, do not decompile or web-search.
---
# Tarol.Jobs

## Rules

1. **One change per occurrence = a scheduled factory** on the change (skill `tarol-changes`):
   `[Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")] public static CloseDay From(JobOccurrence o) => new(…);`
   The generator puts it in `GeneratedJobs.Set`. `o.At` is UTC whole seconds; `o.Local` gives the zone's day and time.
2. **Many changes per occurrence (a fan-out, a report) = an `IJob` class** with DI:
   `ValueTask<Result> Run(JobRun run, CancellationToken ct)`. Find targets (a projection may be read), then
   `await run.Change(new SettleAuction(id), ct)` per target (keyed occurrence/target) or `run.Create(creation, "part", ct)`
   (`part`: a string naming the creation within the occurrence). Both return `ValueTask<Result<ChangeOutcome>>`; return the
   first violation to retry the occurrence: what committed replays (`Replayed`), the rest runs. A `[Scheduled]` creation
   is keyed by the occurrence too. Find targets with `IReadModels` (`Store<TRow>().Search(new ListQuery(tenant) {…})`,
   skill `tarol-queries`); each change decides on its own aggregate.
3. **Register in the worker**: `.AddJobs(j => j.Add(GeneratedJobs.Set).Add<SettleEndedAuctions>("15 2 * * *", o => o.TimeZone = "Europe/Vienna"),
   o => o.Workers = 2)` after `UseNpgsqlStore(…)` (or `UseInMemoryStore()`). One `AddJobs` call; several assemblies:
   `JobSet.Combine(a, b)`. An interval: `.Add<TJob>(Schedule.Every(TimeSpan.FromMinutes(5)))` (not in `[Scheduled]`).
   `JobHostOptions`: `Workers` 2, `PollInterval` 1 s, `Lease` 1 min, `TenantSyncEvery` 1 h. `JobOptions.Grace` is a
   `TimeSpan`, `Policy` a `DeliveryPolicy` (`Tarol.Sagas`). Any number of processes may run it: every occurrence commits once.
4. **Cron** is 5-field Unix (Vixie): `min hour day-of-month month day-of-week`, lists, ranges, steps, `JAN`/`MON`, weekday
   7 = Sunday; day-of-month and day-of-week both restricted = **either**. No seconds, no `L W # ?`. Intervals:
   `Schedule.Every(TimeSpan.FromMinutes(15))` (aligned to the epoch). DST: a skipped time runs at the end of the gap, a
   repeated time once (its first instant). Default zone UTC; IANA ids.
5. **Missed runs** (downtime): `Missed.Latest` (default: the newest once), `Missed.All` (each, `MaxCatchUp` 100 per claim),
   `Missed.Skip` (none unless within `Grace`, 1 min). The cursor then sits at the next future occurrence.
6. **Failures** as reactions: `Conflict`, `NotFound`, 422, unavailable, an exception → the same occurrence again after
   1 s, 2 s … 5 min; after `Policy.MaxAttempts` (10) a dead job and the cursor moves on. `Invalid`/`Forbidden` → dead at
   once. **Nothing to do = `Result.Success()`**. Re-drive: `rows.DeadJobs(max, ct)`, `rows.Redrive(id, ct)` (`IJobRows`).
7. **Side effects** (mail, HTTP) never in the body: it runs again after a crash. Raise an event; its publication or
   effect does the call (`tarol-messaging`).
8. **Per tenant**: `PerTenant = true` (attribute or `JobOptions`) and register your
   `ITenantDirectory` (`ValueTask<IReadOnlyList<Id<ITenant>>> Tenants(CancellationToken)`); the host reads it at startup and
   every `TenantSyncEvery` and keeps one row per tenant. The tenant is `o.Tenant` / `run.Occurrence.Tenant` and the change's
   context tenant. Without a directory the host fails at startup.
9. **Configuration** overrides by name: `Tarol:Jobs:<name>:Cron`, `:TimeZone`, `:Enabled` (`false` pauses; re-enabled it
   starts at its next occurrence). Read **at startup** (restart the workers to apply); names are case-sensitive. Invalid
   values, unknown zones and unknown names fail at startup. A changed schedule
   moves the cursor to its first occurrence after now; the old one never runs.
10. **Names** are stored in rows and keys: the change's type name, or `Name =` / `o.Name`. Pin it before renaming.

## Example

```csharp
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}

public sealed class TickJob : IJob
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken ct)
        => (await run.Change(new TickCounter(Counter.Main, run.Occurrence.At), ct)).TryGetValue(out Violation? v) ? v : Result.Success();
}

// worker: builder.Services.AddTarol(AppJson.Default, codec => …).UseNpgsqlStore(cs).AddReactionWorker()
//             .AddJobs(j => j.Add(GeneratedJobs.Set).Add<TickJob>("0 * * * *", o => o.Missed = Missed.All));

// test
var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set, stateOptions: AppJson.Default.Options)
    .Given(Counter.Start(Counter.Main))
    .Add(new TickJob(), "0 * * * *", o => { o.Name = "Ticks"; o.Missed = Missed.All; });
var runs = await test.Advance(TimeSpan.FromHours(2));      // CloseDay at 01:00Z (02:00 Vienna), Ticks at 01:00 and 02:00
var missed = await test.Down(TimeSpan.FromDays(1)).Advance(TimeSpan.Zero);   // down a day: Ticks 24 (All), CloseDay 1 (Latest)
```

`JobTest` uses an in-memory store and job rows on a standing clock. `Advance` stops at every due time up to the target
(cursors, retry backoffs) and returns all its runs as `IReadOnlyList<JobAttempt>` (`Job`, `Tenant`, `Occurrence` a UTC
`DateTimeOffset`, `Attempt`, `Verdict` (`Done`/`Retry`/`Bury`), `Reason`, `Redrive`); `Runs` holds all of them. `Down` moves
the clock with the workers stopped; `Store.Recorded`, `Load<T>(id, tenant)`, `DeadJobs()`, `Redrive(id)`, `WithTenants(…)`,
`WithConfiguration(…)` + `Sync()`.
Pass a `ReactionSet` to drain the reactions the jobs cause (`test.Reactions`). Job classes are added as instances.

## Common mistakes

| Mistake | Result |
|---|---|
| a `BackgroundService` with `Task.Delay(TimeSpan.FromDays(1))` | runs in every process, drifts, no catch-up, no key; use `[Scheduled]` |
| `DateTime.Now` / `DateTimeOffset.UtcNow` in a job | TKRN001; use `run.Occurrence.At` or `context.Now` |
| `o.At.Day` for "yesterday in Vienna" | `At` is UTC: use `o.Local` |
| one tenant-less job looping over tenants with one key | one failing tenant blocks all; use `PerTenant` + `ITenantDirectory` |
| returning `Error.Conflict("Nothing.ToDo")` | retried, then a dead job; return `Result.Success()` |
| sending mail or calling HTTP in `Run` | repeated after a crash or a lost lease; raise an event, publish it |
| `[Scheduled("0 0 2 * * *")]`, `L`, `W`, `#`, `?` | TCHG010 at compile time; `Cron.Parse` throws `FormatException` |
| `[Scheduled]` factory without `JobOccurrence` | TCHG009 |
| two jobs with one name | TCHG011, or `InvalidOperationException` at startup |
| `PerTenant = true` without `ITenantDirectory` | `InvalidOperationException` when the host is resolved |
| `Tarol:Jobs:CloseDya:Enabled` (a typo) | `InvalidOperationException`: "configures no declared job" |
| `AddJobs` in the API process only | jobs run where `AddJobs` is called: the worker; the API needs none |
| `j.Add<MyJob>(…)` inside `JobTest.At` | `InvalidOperationException`: add an instance with `test.Add(new MyJob(…), cron)` |
| counting runs in a wall-clock window in tests | use `JobTest.Advance` on the standing clock |

## AOT

No reflection: the generated `GeneratedJobs` is a `switch`, job classes are resolved from DI (`TJob` keeps its public
constructors), the parser is plain code (no cron package). Logs 14001–14010, `ActivitySource "Tarol.Jobs"` (`job <name>`,
tags `tarol.job`, `tarol.occurrence`, `tarol.tenant`, `tarol.attempt`, `tarol.verdict`). Zones resolve from the OS
(`/usr/share/zoneinfo`); an image without tzdata fails at startup for any zone but UTC.
