using System.Diagnostics;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Probe.Jobs;

internal static class Bench
{
    public static Task Run(string[] args) => args switch
    {
        ["claim"] => Claim(),
        ["tenants", var n] => Tenants(int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)),
        ["backlog", var n] => Backlog(int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)),
        ["lease"] => Lease(),
        ["cron"] => CronCost(),
        _ => throw new ArgumentException(string.Join(' ', args)),
    };

    private static readonly TimeProvider Time = TimeProvider.System;

    // ------------------------------------------------ claim cost of variant A by table size (16 due, the rest in the future)
    private static async Task Claim()
    {
        Console.WriteLine("| job rows (tenants × jobs) | due | claim 16 + roll back (p50 / p99) |");
        Console.WriteLine("|---|---|---|");
        foreach (var total in new[] { 10, 1_000, 100_000 })
        {
            const string schema = "bench_claim";
            await Lab.Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema};");
            await Lab.Exec(NpgsqlJobRows.Script(schema));
            await Lab.Exec($"""
                INSERT INTO {schema}.jobs (tenant, name, next_at)
                SELECT gen_random_uuid(), 'job-' || (g % 10), CASE WHEN g <= 16 THEN now() - interval '1 second' ELSE now() + (g || ' seconds')::interval END
                FROM generate_series(1, $1) g
                """, total);
            await Lab.Exec($"ANALYZE {schema}.jobs");
            var xs = new List<double>();
            await using var conn = await Lab.Db.OpenConnectionAsync();
            for (var i = 0; i < 600; i++)
            {
                await using var tx = await conn.BeginTransactionAsync();
                var sw = Stopwatch.StartNew();
                await using (var c = new NpgsqlCommand($"""
                    WITH c AS (SELECT tenant, name FROM {schema}.jobs WHERE next_at <= $1 AND (lease_until IS NULL OR lease_until <= $1)
                               ORDER BY next_at LIMIT 16 FOR UPDATE SKIP LOCKED)
                    UPDATE {schema}.jobs j SET claim_id = gen_random_uuid(), lease_until = $2, attempts = j.attempts + 1
                    FROM c WHERE j.tenant = c.tenant AND j.name = c.name RETURNING j.tenant
                    """, conn, tx))
                {
                    c.Parameters.Add(new() { Value = DateTimeOffset.UtcNow });
                    c.Parameters.Add(new() { Value = DateTimeOffset.UtcNow.AddMinutes(1) });
                    await using var r = await c.ExecuteReaderAsync();
                    while (await r.ReadAsync()) { }
                }
                if (i >= 100) xs.Add(sw.Elapsed.TotalMilliseconds);
                await tx.RollbackAsync();
            }
            Console.WriteLine($"| {total:N0} | 16 | {Lab.Pct(xs)} |");
        }
    }

    // ------------------------------------------------ N tenants, one nightly job each, all due at once (02:00 for everybody)
    private static async Task Tenants(int tenants)
    {
        Console.WriteLine("| variant | tenants due at once | loops | all done after | occurrences/s | commits |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var loops in new[] { 1, 4, 8 })
        {
            const string schema = "bench_tenants";
            var store = await Lab.Store(schema, fresh: true, GeneratedReactions.Set);
            var rows = new NpgsqlJobRows(Lab.Db, schema, Time);
            await rows.EnsureSchema();
            var due = Lab.Second(DateTimeOffset.UtcNow).AddSeconds(-1);
            var ids = new Guid[tenants];
            for (var t = 0; t < tenants; t++) ids[t] = Guid.CreateVersion7();
            await Seed(store, ids, "nightly", schedule: false);
            await Lab.Exec($"INSERT INTO {schema}.jobs (tenant, name, next_at) SELECT unnest($1::uuid[]), 'nightly', $2", ids, due);
            var pipeline = new ChangePipeline(store, Time);
            var spec = new JobSpec("nightly", new IntervalSchedule(TimeSpan.FromDays(1), due), TimeZoneInfo.Utc, Missed.All, (run, ct) =>
                pipeline.Run(new RecordOccurrence((Id<Tally>)Lab.StreamOf((Guid)run.Tenant.GetValueOrThrow(), "nightly"), run.Occurrence, "bench"), run.Context, new ChangeOptions(run.Key), ct).AsTask());
            var specs = new Dictionary<string, JobSpec> { ["nightly"] = spec };
            var sw = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource();
            var workers = Enumerable.Range(0, loops).Select(_ => new JobRunner(rows, specs, Time) { Heartbeat = false }.Run(TimeSpan.FromMilliseconds(100), stop.Token)).ToArray();
            while (await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.jobs WHERE next_at <= $1", due) > 0) await Task.Delay(20);
            var took = sw.Elapsed;
            stop.Cancel();
            await Task.WhenAll(workers);
            var commits = await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.events WHERE jsonb_exists(payload, 'occurrence')");
            Console.WriteLine($"| A rows | {tenants:N0} | {loops} | {took.TotalSeconds:F2} s | {tenants / took.TotalSeconds:N0} | {commits:N0} |");
        }
        foreach (var loops in new[] { 1, 4, 8 })
        {
            const string schema = "bench_tenants_d";
            var set = GeneratedReactions.Set;
            var store = await Lab.Store(schema, fresh: true, set);
            var ids = new Guid[tenants];
            for (var t = 0; t < tenants; t++) ids[t] = Guid.CreateVersion7();
            // seed an hour ahead (a row is never due before its commit), then make every first occurrence due at once
            await Seed(store, ids, "nightly", schedule: true, Lab.Second(DateTimeOffset.UtcNow).AddHours(1));
            var due = Lab.Second(DateTimeOffset.UtcNow);
            await Lab.Exec($"UPDATE {schema}.outbox SET due_at = $1", due);
            var pipeline = new ChangePipeline(store, Time);
            var sw = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource();
            var options = new ReactionOptions { PollInterval = TimeSpan.FromMilliseconds(100) };
            var workers = Enumerable.Range(0, loops).Select(_ => new ReactionWorker(store.Outbox, set, pipeline, Time, options).Run(stop.Token)).ToArray();
            while (await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.outbox WHERE due_at <= $1", due) > 0) await Task.Delay(20);
            var took = sw.Elapsed;
            stop.Cancel();
            await Task.WhenAll(workers);
            var commits = await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.events WHERE jsonb_exists(payload, 'occurrence')");
            Console.WriteLine($"| D outbox | {tenants:N0} | {loops} | {took.TotalSeconds:F2} s | {tenants / took.TotalSeconds:N0} | {commits:N0} (+ {tenants:N0} Due) |");
        }
    }

    /// <summary>One Tally per tenant (and a Schedule for variant D), committed in parallel.</summary>
    private static async Task Seed(NpgsqlAggregateStore store, Guid[] tenants, string job, bool schedule, DateTimeOffset due = default)
    {
        await Parallel.ForEachAsync(tenants, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (t, _) =>
        {
            var ctx = OperationContext.Start(Actor.System("Probe.Setup"), (Id<ITenant>)t, Time);
            var id = Lab.StreamOf(t, job);
            await Lab.Commit(store, Tally.Open(id, job), 0, ctx);
            if (schedule) await Lab.Commit(store, Schedule.Start(id, job, due), 0, ctx);
        });
    }

    // ------------------------------------------------ a reaction backlog in front of a due job
    private static async Task Backlog(int backlog)
    {
        Console.WriteLine("| variant | reaction rows due before the job | job due → job started | backlog drained after |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var variant in new[] { "D", "A" })
        {
            var schema = "bench_backlog_" + variant.ToLowerInvariant();
            var set = GeneratedReactions.Set;
            var store = await Lab.Store(schema, fresh: true, set);
            var rows = new NpgsqlJobRows(Lab.Db, schema, Time);
            await rows.EnsureSchema();
            var ctx = OperationContext.Start(Actor.System("Probe.Setup"), (Id<ITenant>)Lab.TenantA, Time);
            // the backlog: Pinged events on 200 streams (AckPing runs per row, head per stream)
            var streams = Enumerable.Range(0, 200).Select(i => Lab.StreamOf(Lab.TenantA, "ping-" + i)).ToArray();
            await Parallel.ForEachAsync(streams, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (id, _) =>
            {
                var t = Tally.Open(id, "ping");
                for (var i = 0; i < backlog / streams.Length; i++) t.Ping();
                await Lab.Commit(store, t, 0, ctx);
            });
            var jobId = Lab.StreamOf(Lab.TenantA, "job-0");
            var due = Lab.Second(DateTimeOffset.UtcNow).AddSeconds(1);
            await Lab.Commit(store, Tally.Open(jobId, "job-0"), 0, ctx);
            if (variant == "D") await Lab.Commit(store, Schedule.Start(jobId, "job-0", due), 0, ctx);
            else await rows.Register(Lab.TenantA, "job-0", due);
            var pipeline = new ChangePipeline(store, Time);
            using var stop = new CancellationTokenSource();
            var options = new ReactionOptions { PollInterval = TimeSpan.FromMilliseconds(100) };
            var reactionLoops = Enumerable.Range(0, 4).Select(_ => new ReactionWorker(store.Outbox, new LoggingRows(set, schema, 0), pipeline, Time, options).Run(stop.Token)).ToList();
            if (variant == "A")
            {
                var spec = new JobSpec("job-0", new IntervalSchedule(TimeSpan.FromHours(1), due), TimeZoneInfo.Utc, Missed.All, Worker.Body(schema, pipeline));
                reactionLoops.Add(new JobRunner(rows, new Dictionary<string, JobSpec> { ["job-0"] = spec }, Time).Run(TimeSpan.FromMilliseconds(100), stop.Token));
            }
            var sw = Stopwatch.StartNew();
            while (await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.runs") == 0 && sw.Elapsed < TimeSpan.FromMinutes(5)) await Task.Delay(10);
            var started = await Lab.Scalar<DateTime>($"SELECT min(started_at) FROM {schema}.runs");
            while (await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.outbox WHERE reaction = 'AckPing'") > 0) await Task.Delay(20);
            var drained = sw.Elapsed;
            stop.Cancel();
            await Task.WhenAll(reactionLoops);
            Console.WriteLine($"| {(variant == "D" ? "D outbox (4 reaction loops)" : "A job rows (4 reaction loops + 1 job loop)")} | {backlog:N0} | {(new DateTimeOffset(started, TimeSpan.Zero) - due).TotalMilliseconds:N0} ms | {drained.TotalSeconds:F1} s |");
        }
    }

    // ------------------------------------------------ a run longer than the lease
    private static async Task Lease()
    {
        Console.WriteLine("| variant | lease | run takes | workers | occurrences started | runs | most runs of one occurrence | commits |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var (variant, heartbeat) in new[] { ("A", true), ("A", false), ("D", false) })
        {
            var schema = "bench_lease_" + variant.ToLowerInvariant() + (heartbeat ? "h" : "");
            var set = GeneratedReactions.Set;
            var store = await Lab.Store(schema, fresh: true, set);
            var rows = new NpgsqlJobRows(Lab.Db, schema, Time);
            await rows.EnsureSchema();
            var ctx = OperationContext.Start(Actor.System("Probe.Setup"), (Id<ITenant>)Lab.TenantA, Time);
            var id = Lab.StreamOf(Lab.TenantA, "job-0");
            var due = Lab.Second(DateTimeOffset.UtcNow).AddSeconds(1);
            await Lab.Commit(store, Tally.Open(id, "job-0"), 0, ctx);
            if (variant == "D") await Lab.Commit(store, Schedule.Start(id, "job-0", due), 0, ctx);
            else await rows.Register(Lab.TenantA, "job-0", due);
            var pipeline = new ChangePipeline(store, Time);
            using var stop = new CancellationTokenSource();
            var lease = TimeSpan.FromSeconds(2);
            var loops = new List<Task>();
            for (var w = 0; w < 3; w++)
            {
                if (variant == "D")
                    loops.Add(new ReactionWorker(store.Outbox, new LoggingRows(set, schema, 5000), pipeline, Time,
                        new ReactionOptions { Lease = lease, PollInterval = TimeSpan.FromMilliseconds(100) }).Run(stop.Token));
                else
                {
                    var body = Worker.Body(schema, pipeline);
                    var spec = new JobSpec("job-0", new IntervalSchedule(TimeSpan.FromHours(1), due), TimeZoneInfo.Utc, Missed.All,
                        async (run, ct) => { await Task.Delay(5000, ct); return await body(run, ct); });
                    loops.Add(new JobRunner(rows, new Dictionary<string, JobSpec> { ["job-0"] = spec }, Time) { Lease = lease, Heartbeat = heartbeat }.Run(TimeSpan.FromMilliseconds(100), stop.Token));
                }
            }
            await Task.Delay(9000);
            stop.Cancel();
            await Task.WhenAll(loops);
            // variant D's schedule fires every second (AdvanceSchedule: At + 1 s), A's once: count every occurrence that started
            var started = await Lab.Scalar<long>($"SELECT count(DISTINCT occurrence) FROM {schema}.runs");
            var runs = await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.runs");
            var most = await Lab.Scalar<long>($"SELECT max(n) FROM (SELECT count(*) n FROM {schema}.runs GROUP BY occurrence) x");
            var commits = await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.events WHERE stream_id = $1 AND jsonb_exists(payload, 'occurrence')", id);
            Console.WriteLine($"| {variant}{(variant == "A" ? heartbeat ? " + heartbeat" : " no heartbeat" : " outbox (every second)")} | 2 s | 5 s | 3 | {started} | {runs} | {most} | {commits} |");
        }
    }

    // ------------------------------------------------ next-occurrence cost
    private static Task CronCost()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        Console.WriteLine("| schedule | expression | Next() in UTC | Next() in Europe/Vienna |");
        Console.WriteLine("|---|---|---|---|");
        var list = new List<(string, ISchedule)>
        {
            ("interval", new IntervalSchedule(TimeSpan.FromMinutes(15), Worker.Anchor)),
            ("MiniCron", new MiniCron("0 2 * * *")),
            ("MiniCron", new MiniCron("*/15 9-17 * * 1-5")),
        };
#if CRONOS
        list.Insert(1, ("Cronos", new CronosSchedule("0 2 * * *")));
        list.Insert(2, ("Cronos", new CronosSchedule("*/15 9-17 * * 1-5")));
#endif
        foreach (var (name, s) in list)
        {
            string M(TimeZoneInfo z)
            {
                var t = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
                for (var i = 0; i < 2000; i++) t = s.Next(t, z)!.Value;                 // warm up
                var sw = Stopwatch.StartNew();
                const int n = 100_000;
                for (var i = 0; i < n; i++) t = s.Next(t, z)!.Value;
                return Lab.F(sw.Elapsed.TotalMilliseconds * 1000 / n, "F2") + " µs";
            }
            Console.WriteLine($"| {name} | `{s}` | {M(TimeZoneInfo.Utc)} | {M(zone)} |");
        }
        return Task.CompletedTask;
    }
}
