# Tarol.Jobs

Scheduled jobs for [Tarol](https://github.com/CLorat/Tarol-Net): recurring work that is not a reaction runs as the system,
**once per occurrence across any number of worker processes**, from one row per (tenant, job) in the store. A crash, a
kill or a frozen process costs a late run, never a lost or a doubled commit. A 5-field Unix cron parser of its own (IANA
zones, the DST rules of Vixie cron), missed-run policies, retries and dead jobs, a configuration override, Tarol's own
maintenance as system jobs, and `JobTest` on a standing clock. No third-party dependency. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.Jobs     # brings Tarol.Sagas, Tarol.Changes, Tarol.Persistence
```

## 30 seconds

```csharp
// one change per occurrence: a scheduled factory on the change (Tarol.Changes generates GeneratedJobs.Set)
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}

// many changes per occurrence: a job class with DI
public sealed class SettleEndedAuctions(IReadModels reads) : IJob
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken ct)
    {
        foreach (var a in await reads.Store<AuctionRow>().Ended(run.Occurrence.At, ct))
            if ((await run.Change(new SettleAuction(a.Id), ct)).TryGetValue(out Violation? v))
                return v;                                    // retried; what committed replays
        return Result.Success();                             // nothing to do is a success
    }
}

// the worker process
builder.Services.AddTarol(AppJson.Default, codec => …)
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddJobs(j => j
        .Add(GeneratedJobs.Set)
        .Add<SettleEndedAuctions>("15 2 * * *", o => o.TimeZone = "Europe/Vienna"),
        o => o.Workers = 2);
```

## How it works

| Piece | Behaviour |
|---|---|
| rows | one per (tenant, job): the stored schedule text and the cursor (the oldest occurrence not yet run); synced at startup |
| claim | `JobHostOptions.Workers` loops per process; each claims **one** due row with a lease (1 min) and beats it every lease/3 |
| run | `Actor.System("Jobs.<name>")`, the row's tenant, a new correlation; the key (tenant, `job:<name>`, occurrence) is stored with the commit, so a re-run replays |
| cursor | moved after every occurrence, only with the claim; an old occurrence never comes back, even after the key's 7 days |
| missed | `Missed.Latest` (default, the newest once), `All` (each, 100 per claim), `Skip` (none, unless within 1 min) |
| failures | as reactions (`Delivery.Classify`, `DeliveryPolicy`: 10 attempts, 1 s … 5 min), then a dead job and the cursor moves on; `IJobRows.Redrive(id)` |
| tenants | `PerTenant = true` + the app's `ITenantDirectory`: one row and one key per tenant, synced at startup and hourly |
| configuration | `Tarol:Jobs:<name>:Cron`, `:TimeZone`, `:Enabled`, validated at startup; a changed schedule moves the cursor |
| system jobs | `tarol.purge-keys` (the store's key purge, hourly, once per cluster; `ReactionHost` then skips its own) and every registered `MaintenanceTask` (`tarol.purge-messages` of `Tarol.Messaging.Npgsql`) |

Schedules: `Cron.Parse("*/15 9-17 * * MON-FRI")` (Unix semantics: day-of-month and day-of-week both restricted = either;
names; no seconds, no `L W # ?`) or `Schedule.Every(TimeSpan.FromMinutes(5))`. A skipped local time (spring forward) runs at
the end of the gap; a repeated one (fall back) runs once.

Side effects (mail, HTTP) never go in a job body: it runs again after a crash. Raise an event and publish it.

## Testing

```csharp
var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set, stateOptions: AppJson.Default.Options);
await test.Advance(TimeSpan.FromHours(2));           // 02:00 Vienna ran once; the cursor is at tomorrow 02:00
await test.Down(TimeSpan.FromDays(3)).Advance(TimeSpan.Zero);   // three nights down: Latest runs one
```

`Advance` stops at every due time, `Down` moves the clock with the workers stopped; `Runs`, `DeadJobs()`, `Redrive(id)`,
`WithTenants(…)`, `WithConfiguration(…)` + `Sync()`; job classes are added as instances (`test.Add(new MyJob(…), cron)`).

## Observability

Logs `LoggerMessage` 14001–14010 (retry, dead job, lease lost, schedule changed, missed occurrences skipped, rows synced),
`ActivitySource "Tarol.Jobs"`: one activity per occurrence, `job <name>`, tags `tarol.job`, `tarol.occurrence`, `tarol.tenant`,
`tarol.attempt`, `tarol.verdict`; the job's commits store its `traceparent`, so reactions link to it.

## AOT

No reflection: generated entries are a `switch`, job classes are resolved from DI, the parser is plain code. Zones resolve
from the OS (`/usr/share/zoneinfo`): a container image without tzdata fails at startup for any zone but UTC. The smoke app
publishes with 0 IL warnings.
