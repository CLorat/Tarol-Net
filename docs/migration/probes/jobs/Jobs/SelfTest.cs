using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
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

internal static class SelfTest
{
    public static Task<int> Run(string kind) => kind switch
    {
        "cron" => Cron(),
        "test" => Test(),
        "postgres" => Postgres(),
        _ => throw new ArgumentException(kind),
    };

    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static string Fmt(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture);

    private static List<DateTimeOffset> Take(ISchedule s, DateTimeOffset from, int n, TimeZoneInfo zone)
    {
        var list = new List<DateTimeOffset>();
        for (var t = s.Next(from, zone); t is { } at && list.Count < n; t = s.Next(at, zone)) list.Add(at);
        return list;
    }

    // ---------------------------------------------------------------- cron: DST, differential, intervals
    private static Task<int> Cron()
    {
        var checks = new Checks("cron");
        var impls = new List<(string Name, Func<string, ISchedule> Make)> { ("MiniCron", e => new MiniCron(e)) };
#if CRONOS
        impls.Insert(0, ("Cronos 0.12.0", e => new CronosSchedule(e)));
#endif
        var spring = new DateTimeOffset(2027, 3, 27, 0, 0, 0, TimeSpan.FromHours(1));   // Vienna jumps 02:00 → 03:00 on 2027-03-28
        var fall = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.FromHours(2));    // Vienna falls 03:00 → 02:00 on 2026-10-25
        Console.WriteLine("| schedule (Europe/Vienna) | window | " + string.Join(" | ", impls.Select(i => i.Name)) + " |");
        Console.WriteLine("|---|---|" + string.Concat(impls.Select(_ => "---|")));
        void Row(string expr, string window, DateTimeOffset from, int n)
            => Console.WriteLine($"| `{expr}` | {window} | " + string.Join(" | ", impls.Select(i => string.Join("<br>", Take(i.Make(expr), from, n, Vienna).Select(Fmt)))) + " |");
        Row("30 2 * * *", "spring forward 2027-03-28", spring, 3);
        Row("30 2 * * *", "fall back 2026-10-25", fall, 3);
        Row("0 2 * * *", "fall back 2026-10-25", fall, 3);
        Row("0 * * * *", "fall back, 01:00–04:00", new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.FromHours(2)), 5);
        Row("*/30 * * * *", "spring forward, 01:00–03:30", new DateTimeOffset(2027, 3, 28, 0, 45, 0, TimeSpan.FromHours(1)), 5);

        foreach (var (name, make) in impls)
        {
            var s = Take(make("30 2 * * *"), spring, 3, Vienna);
            checks.Check($"{name}: a 02:30 job on the spring-forward day runs once, at 03:00+02:00 (the end of the gap)",
                s.Count == 3 && s[1] == new DateTimeOffset(2027, 3, 28, 3, 0, 0, TimeSpan.FromHours(2)), string.Join(", ", s.Select(Fmt)));
            var f = Take(make("30 2 * * *"), fall, 3, Vienna);
            checks.Check($"{name}: a 02:30 job on the fall-back day runs once, at the first 02:30 (+02:00)",
                f.Count == 3 && f[1] == new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)) && f[2].Day == 26, string.Join(", ", f.Select(Fmt)));
        }

#if CRONOS
        // differential: our parser against Cronos, 2 years, 3 zones
        string[] exprs = ["0 2 * * *", "30 2 * * *", "*/15 * * * *", "0 */6 * * *", "0 9 * * 1-5", "0 0 1 * *", "15 3 1,15 * *", "0 12 * * 0",
                          "5 4 * * 7", "0 22 * * 1-5", "*/7 2-3 * * *", "0 2 13 * 5", "45 1-3 * * *", "0 0 29 2 *"];
        long compared = 0, mismatches = 0, repeatedHour = 0, domAndDow = 0;
        var examples = new List<string>();
        foreach (var zone in new[] { TimeZoneInfo.Utc, Vienna, NewYork })
            foreach (var e in exprs)
            {
                ISchedule a = new CronosSchedule(e), b = new MiniCron(e);
                var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var to = from.AddYears(2);
                var x = Schedules.Between(a, from, to, zone).ToList();
                var y = Schedules.Between(b, from, to, zone).ToList();
                compared += Math.Max(x.Count, y.Count);
                var setX = x.ToHashSet();
                var setY = y.ToHashSet();
                var onlyX = x.Where(t => !setY.Contains(t)).ToList();
                var onlyY = y.Where(t => !setX.Contains(t)).ToList();
                mismatches += onlyX.Count + onlyY.Count;
                var f = e.Split(' ');
                var both = f[2] != "*" && f[4] != "*";
                foreach (var t in onlyX.Concat(onlyY))
                    if (both) domAndDow++;
                    else if (zone.IsAmbiguousTime(TimeZoneInfo.ConvertTime(t, zone).DateTime)) repeatedHour++;
                if (onlyX.Count + onlyY.Count > 0 && examples.Count < 8)
                    examples.Add($"`{e}` {zone.Id}: Cronos only {onlyX.Count} (e.g. {string.Join(", ", onlyX.Take(2).Select(Fmt))}); ours only {onlyY.Count} (e.g. {string.Join(", ", onlyY.Take(2).Select(Fmt))})");
            }
        Console.WriteLine($"INFO differential MiniCron vs Cronos: {exprs.Length} expressions × 3 zones × 2 years = {compared} occurrences, {mismatches} differ: {repeatedHour} in the repeated fall-back hour (Cronos runs interval fields in both), {domAndDow} where day-of-month and day-of-week are both set (Cronos: AND; Vixie cron: OR)");
        foreach (var ex in examples) Console.WriteLine("INFO   " + ex);
        checks.Check("MiniCron and Cronos differ only in the repeated fall-back hour and in day-of-month + day-of-week", mismatches == repeatedHour + domAndDow, $"{mismatches} = {repeatedHour} + {domAndDow}");
#endif
        var every = new IntervalSchedule(TimeSpan.FromMinutes(15), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var iv = Take(every, new DateTimeOffset(2026, 10, 25, 0, 50, 0, TimeSpan.Zero), 3, Vienna);
        checks.Check("a fixed interval ignores the zone: every 15 min in UTC", iv[0].Minute == 0 && iv[1].Minute == 15, string.Join(", ", iv.Select(Fmt)));
        try { _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Atlantis"); checks.Check("an unknown zone throws at registration", false); }
        catch (TimeZoneNotFoundException) { checks.Check("an unknown zone throws TimeZoneNotFoundException (at registration, not at 02:00)", true); }
        checks.Check("time zones resolve with InvariantGlobalization (tzdata from /usr/share/zoneinfo)", Vienna.BaseUtcOffset == TimeSpan.FromHours(1));
        return Task.FromResult(checks.Done());
    }

    // ---------------------------------------------------------------- test: the in-memory story with a fake clock
    private sealed class Harness
    {
        public FakeTimeProvider Clock { get; }
        public InMemoryAggregateStore Store { get; }
        public ChangePipeline Pipeline { get; }
        public InMemoryJobRows Rows { get; }
        public List<JobRun> Runs { get; } = [];
        public Guid Tally { get; }
        public Func<JobRun, Task>? Hold { get; set; }

        public Harness(DateTimeOffset start, string cron, TimeZoneInfo zone, Missed policy)
        {
            Clock = new FakeTimeProvider(start);
            Store = new InMemoryAggregateStore(Clock, Lab.Json);
            Pipeline = new ChangePipeline(Store, Clock);
            Rows = new InMemoryJobRows(Clock);
            Tally = Lab.StreamOf(Lab.TenantA, "nightly");
            var ctx = OperationContext.Start(Actor.System("Probe.Setup"), (Id<ITenant>)Lab.TenantA, Clock);
            _ = Store.Append(Probe.Jobs.Tally.Open(Tally, "nightly"), 0, ctx, Option.None, default).AsTask().Result;
            Spec = new JobSpec("nightly", Schedules.Parse(cron), zone, policy, async (run, ct) =>
            {
                Runs.Add(run);
                if (Hold is { } hold) await hold(run);
                return await Pipeline.Run(new RecordOccurrence((Id<Tally>)Tally, run.Occurrence, "test"), run.Context, new ChangeOptions(run.Key), ct);
            });
            Rows.Register(Lab.TenantA, "nightly", Spec.Schedule.Next(start, zone)!.Value);
        }

        public JobSpec Spec { get; }
        public JobRunner Runner(IJobRows? rows = null) => new(rows ?? Rows, new Dictionary<string, JobSpec> { ["nightly"] = Spec }, Clock) { Heartbeat = false };
        public int Count => ((Tally)Store.Load((Id<Tally>)Tally, OperationContext.Start(Actor.System("t"), (Id<ITenant>)Lab.TenantA, Clock), default).AsTask().Result.GetValueOrThrow()!).Count;
    }

    private static async Task<int> Test()
    {
        var checks = new Checks("test: FakeTimeProvider + in-memory store + in-memory job rows");
        var start = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

        var h = new Harness(start, "0 2 * * *", TimeZoneInfo.Utc, Missed.All);
        var r = h.Runner();
        checks.Check("before 02:00 nothing is due", (await r.RunOnce()).Count == 0);
        h.Clock.Advance(TimeSpan.FromHours(2));
        var a = await r.RunOnce();
        checks.Check("at 02:00 the occurrence runs once and the cursor moves to tomorrow", a.Count == 1 && h.Count == 1 && h.Rows.NextOf(Lab.TenantA, "nightly") == start.AddDays(1).AddHours(2));
        checks.Check("claimed again at once: nothing", (await r.RunOnce()).Count == 0);
        var run = h.Runs[0];
        checks.Check("context: System(\"Jobs.nightly\"), the row's tenant, a new correlation, no causation, key job:nightly/<occurrence>",
            run.Context.Actor == Actor.System("Jobs.nightly") && run.Context.Tenant == (Id<ITenant>)Lab.TenantA && run.Context.CausationId.IsNone
            && run.Key.TryGetValue(out IdempotencyKey k) && k.Scope == "job:nightly" && k.Key == "2026-11-16T02:00:00.0000000Z", run.Key.ToString());

        foreach (var (policy, expect) in new[] { (Missed.All, 3), (Missed.Latest, 1), (Missed.Skip, 0) })
        {
            var d = new Harness(start, "0 2 * * *", TimeZoneInfo.Utc, policy);
            d.Clock.Advance(TimeSpan.FromDays(3));                             // down for 3 days: 3 occurrences missed
            var x = await d.Runner().RunOnce();
            checks.Check($"3 days down, policy {policy}: {expect} run(s), cursor at the next future occurrence",
                d.Count == expect && d.Rows.NextOf(Lab.TenantA, "nightly") == start.AddDays(3).AddHours(2), $"ran {d.Count}, next {Fmt(d.Rows.NextOf(Lab.TenantA, "nightly"))}");
        }
        var capped = new Harness(start, "0 * * * *", TimeZoneInfo.Utc, Missed.All);
        capped.Clock.Advance(TimeSpan.FromDays(10));                            // 240 hourly occurrences missed, cap 100
        var c1 = await capped.Runner().RunOnce();
        var c2 = await capped.Runner().RunOnce();
        var c3 = await capped.Runner().RunOnce();
        checks.Check("policy All is capped per claim (100): 240 missed hourly runs take three claims", capped.Count == 240 && c1[0].Ran.Count == 100 && c3[0].Ran.Count == 40, $"{c1[0].Ran.Count}+{c2[0].Ran.Count}+{c3[0].Ran.Count}");

        // the same occurrence twice (the cursor did not move: a crash after the commit)
        var twice = new Harness(start, "0 2 * * *", TimeZoneInfo.Utc, Missed.All);
        twice.Clock.Advance(TimeSpan.FromHours(2));
        var occ = start.AddHours(2);
        var key = new IdempotencyKey((Id<ITenant>)Lab.TenantA, "job:nightly", occ.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var first = await twice.Pipeline.Run(new RecordOccurrence((Id<Tally>)twice.Tally, occ, "crashed"), OperationContext.Start(Actor.System("Jobs.nightly"), (Id<ITenant>)Lab.TenantA, twice.Clock), new ChangeOptions(key));
        _ = await twice.Runner().RunOnce();
        checks.Check("an occurrence that committed before a crash replays its key: one commit", first.IsSuccess && twice.Count == 1 && twice.Runs.Count == 1);

        // overlap: the run outlasts the interval
        var ov = new Harness(start, "*/10 * * * *", TimeZoneInfo.Utc, Missed.Latest);
        var gate = new TaskCompletionSource();
        ov.Hold = _ => ov.Runs.Count == 1 ? gate.Task : Task.CompletedTask;     // only the first run blocks
        ov.Clock.Advance(TimeSpan.FromMinutes(10));
        var running = ov.Runner().RunOnce();                                      // blocked inside the body
        ov.Clock.Advance(TimeSpan.FromMinutes(25));                               // two more occurrences pass (lease 30 s… on the fake clock: expired!)
        var second = await ov.Runner().RunOnce();
        checks.Check("without a heartbeat, a run longer than the lease is claimed again by another worker (overlap)", second.Count == 1, $"second claim ran {second.Sum(s => s.Ran.Count)}");
        gate.SetResult();
        var firstDone = await running;
        checks.Check("…and the first worker's advance is refused (claim lost); the key keeps one commit per occurrence",
            !firstDone[0].Advanced && ov.Count == 2, $"advanced={firstDone[0].Advanced}, commits={ov.Count}, runs={ov.Runs.Count}");

        var hb = new Harness(start, "*/10 * * * *", TimeZoneInfo.Utc, Missed.Latest);
        var gate2 = new TaskCompletionSource();
        hb.Hold = _ => hb.Runs.Count == 1 ? gate2.Task : Task.CompletedTask;
        hb.Clock.Advance(TimeSpan.FromMinutes(10));
        var hbRunner = new JobRunner(hb.Rows, new Dictionary<string, JobSpec> { ["nightly"] = hb.Spec }, hb.Clock) { Heartbeat = true };
        var hbRunning = hbRunner.RunOnce();
        for (var i = 0; i < 50; i++) { hb.Clock.Advance(TimeSpan.FromSeconds(10)); await Task.Yield(); await Task.Delay(1); }   // 8 min 20 s, beats every 10 s
        var hbSecond = await hb.Runner().RunOnce();
        gate2.SetResult();
        var hbDone = await hbRunning;
        checks.Check("with a heartbeat (lease/3), a long run keeps its row: no second claim; policy Latest then skips the overlapped occurrence",
            hbSecond.Count == 0 && hbDone[0].Advanced && hb.Runs.Count == 1 && hb.Rows.NextOf(Lab.TenantA, "nightly") == start.AddMinutes(20),
            $"second claim {hbSecond.Count}, runs {hb.Runs.Count}, next {Fmt(hb.Rows.NextOf(Lab.TenantA, "nightly"))}");

        // DST with the fake clock: a 02:30 Vienna job across the spring-forward night
        var dst = new Harness(new DateTimeOffset(2027, 3, 26, 12, 0, 0, TimeSpan.Zero), "30 2 * * *", Vienna, Missed.All);
        var times = new List<DateTimeOffset>();
        for (var hour = 0; hour < 72; hour++) { dst.Clock.Advance(TimeSpan.FromHours(1)); foreach (var x in await dst.Runner().RunOnce()) times.AddRange(x.Ran); }
        checks.Check("fake clock over three Vienna nights: 02:30+01, 03:00+02 (gap), 02:30+02", times.Count == 3 && times[1] == new DateTimeOffset(2027, 3, 28, 3, 0, 0, TimeSpan.FromHours(2)),
            string.Join(", ", times.Select(t => Fmt(TimeZoneInfo.ConvertTime(t, Vienna)))));

        // a failing job: retry with backoff on the fake clock, then give up after MaxAttempts
        var fail = new Harness(start, "0 2 * * *", TimeZoneInfo.Utc, Missed.All);
        var failing = fail.Spec with { Body = (_, _) => Task.FromResult<Result<ChangeOutcome>>(Error.Unavailable("Probe.Down")), MaxAttempts = 3 };
        var fr = new JobRunner(fail.Rows, new Dictionary<string, JobSpec> { ["nightly"] = failing }, fail.Clock) { Heartbeat = false };
        fail.Clock.Advance(TimeSpan.FromHours(2));
        var attempts = 0;
        for (var i = 0; i < 20; i++) { attempts += (await fr.RunOnce()).Count; fail.Clock.Advance(TimeSpan.FromSeconds(5)); }
        checks.Check("a failing occurrence is retried with backoff (1 s, 2 s) and given up after 3 attempts; the cursor moves on",
            attempts == 3 && fail.Rows.NextOf(Lab.TenantA, "nightly") == start.AddDays(1).AddHours(2), $"attempts {attempts}");
        return checks.Done();
    }

    // ---------------------------------------------------------------- postgres: the variants on the real store
    private static async Task<int> Postgres()
    {
        var checks = new Checks("postgres");
        var time = TimeProvider.System;
        const string schema = "selftest_jobs";
        var store = await Lab.Store(schema, fresh: true, GeneratedReactions.Set);
        var rows = new NpgsqlJobRows(Lab.Db, schema, time);
        await rows.EnsureSchema();
        var pipeline = new ChangePipeline(store, time);
        var ctx = OperationContext.Start(Actor.System("Probe.Setup"), (Id<ITenant>)Lab.TenantA, time);
        async Task<long> Commits(Guid stream) => await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.events WHERE stream_id = $1 AND type = 'tally' AND jsonb_exists(payload, 'occurrence')", stream);

        // A: two workers, one due row
        var due = Lab.Second(DateTimeOffset.UtcNow).AddSeconds(-1);
        var id = Lab.StreamOf(Lab.TenantA, "job-0");
        await Lab.Commit(store, Tally.Open(id, "job-0"), 0, ctx);
        await rows.Register(Lab.TenantA, "job-0", due);
        var spec = new JobSpec("job-0", new IntervalSchedule(TimeSpan.FromHours(1), due), TimeZoneInfo.Utc, Missed.All, Worker.Body(schema, pipeline));
        var specs = new Dictionary<string, JobSpec> { ["job-0"] = spec };
        var w1 = new JobRunner(new NpgsqlJobRows(Lab.Db, schema, time), specs, time);
        var w2 = new JobRunner(new NpgsqlJobRows(Lab.Db, schema, time), specs, time);
        var both = await Task.WhenAll(w1.RunOnce(), w2.RunOnce());
        checks.Check("A: two workers claim one due row: one claims (SKIP LOCKED), one commit", both.Sum(x => x.Count) == 1 && await Commits(id) == 1);

        // A: crash after the commit (the cursor did not move): the lease expires, another worker replays the key
        var id1 = Lab.StreamOf(Lab.TenantA, "job-1");
        await Lab.Commit(store, Tally.Open(id1, "job-1"), 0, ctx);
        await rows.Register(Lab.TenantA, "job-1", due);
        var crashSpec = spec with { Name = "job-1", Body = Worker.Body(schema, pipeline) };
        var short1 = new JobRunner(rows, new Dictionary<string, JobSpec> { ["job-1"] = crashSpec }, time) { Lease = TimeSpan.FromSeconds(1) };
        var claimed = await rows.Claim(1, TimeSpan.FromSeconds(1), default);
        var key = new IdempotencyKey((Id<ITenant>)Lab.TenantA, "job:job-1", due.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _ = await crashSpec.Body(new JobRun((Id<ITenant>)Lab.TenantA, "job-1", due, Lab.System("job-1", (Id<ITenant>)Lab.TenantA, time), key), default);   // committed, never advanced
        await Task.Delay(1200);
        var again = await short1.RunOnce();
        checks.Check("A: crash between commit and advance → after the lease the occurrence runs again, the key replays: 1 commit, 2 runs",
            claimed.Count == 1 && again.Count == 1 && again[0].Advanced && await Commits(id1) == 1
            && await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.runs WHERE job = $1", id1.ToString()) == 2);

        // A without the key: the same crash commits twice
        var id2 = Lab.StreamOf(Lab.TenantA, "job-2");
        await Lab.Commit(store, Tally.Open(id2, "job-2"), 0, ctx);
        await rows.Register(Lab.TenantA, "job-2", due);
        var nokey = spec with { Name = "job-2", UseKey = false };
        _ = await rows.Claim(1, TimeSpan.FromSeconds(1), default);
        _ = await nokey.Body(new JobRun((Id<ITenant>)Lab.TenantA, "job-2", due, Lab.System("job-2", (Id<ITenant>)Lab.TenantA, time), Option.None), default);
        await Task.Delay(1200);
        _ = await new JobRunner(rows, new Dictionary<string, JobSpec> { ["job-2"] = nokey }, time).RunOnce();
        checks.Check("A without the occurrence key: the same crash commits the occurrence twice", await Commits(id2) == 2);

        // the key lives only as long as the key retention (P-11: 7 days): after a purge the same occurrence commits again
        _ = await store.PurgeKeys(DateTimeOffset.UtcNow.AddSeconds(1), 1000, default);
        _ = await crashSpec.Body(new JobRun((Id<ITenant>)Lab.TenantA, "job-1", due, Lab.System("job-1", (Id<ITenant>)Lab.TenantA, time), key), default);
        checks.Check("after the key purge an old occurrence would commit again: the cursor, not the key, must keep old occurrences from re-running", await Commits(id1) == 2);

        // B: two leaders, one lock
        await using (var dbA = new NpgsqlSlimDataSourceBuilder(Lab.Pg).Build())
        await using (var dbB = new NpgsqlSlimDataSourceBuilder(Lab.Pg).Build())
        {
            using var stop = new CancellationTokenSource();
            var la = new Leader(dbA, schema, specs, time);
            var lb = new Leader(dbB, schema, specs, time);
            var ta = la.Run(TimeSpan.FromMilliseconds(100), stop.Token);
            var tb = lb.Run(TimeSpan.FromMilliseconds(100), stop.Token);
            await Task.Delay(1500);
            var one = la.IsLeader ^ lb.IsLeader;
            var before = la.IsLeader ? "A" : "B";
            // cut the leader's lock session: the other one takes over
            await Lab.Exec("SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND granted AND pid <> pg_backend_pid()");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5) && (before == "A" ? !lb.IsLeader : !la.IsLeader)) await Task.Delay(20);
            checks.Check("B: two processes, exactly one leader; its session cut → the other leads", one && (before == "A" ? lb.IsLeader : la.IsLeader),
                $"take-over after {sw.ElapsedMilliseconds} ms");
            stop.Cancel();
            await Task.WhenAll(ta, tb);
        }

        // D: the outbox, a schedule aggregate with scheduled reactions
        var id3 = Lab.StreamOf(Lab.TenantA, "job-3");
        var start = DateTimeOffset.UtcNow;
        var firstDue = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second, TimeSpan.Zero).AddSeconds(1);
        await Lab.Commit(store, Tally.Open(id3, "job-3"), 0, ctx);
        await Lab.Commit(store, Schedule.Start(id3, "job-3", firstDue), 0, ctx);
        using (var stop = new CancellationTokenSource())
        {
            var worker = new ReactionWorker(store.Outbox, new LoggingRows(GeneratedReactions.Set, schema, 0), pipeline, time,
                new ReactionOptions { PollInterval = TimeSpan.FromMilliseconds(100), Lease = TimeSpan.FromSeconds(3) });
            var loop = worker.Run(stop.Token);
            await Task.Delay(4500);
            var n = await Commits(id3);
            checks.Check("D: a schedule aggregate + two scheduled reactions per Due: one commit per second", n is >= 3 and <= 5, $"{n} commits in 4.5 s");
            var events = await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.events WHERE stream_id = $1", id3);
            Console.WriteLine($"INFO D: {events} events for {n} occurrences (Tally.Recorded + Schedule.Due per occurrence, + 3 at the start)");
            // a cron change: the next occurrence moves, the row of the old one stays in the outbox and still runs the job
            var s = (Schedule)(await store.Load((Id<Schedule>)id3, ctx, default)).GetValueOrThrow()!;
            var old = s.Next;
            s.Reschedule(old.AddMinutes(10));
            await Lab.Commit(store, s, s.Version - 1, ctx);
            await Task.Delay(old - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(800));
            var ranOld = await Lab.Scalar<long>($"SELECT count(*) FROM {schema}.events WHERE stream_id = $1 AND type = 'tally' AND (payload->>'occurrence')::timestamptz = $2", id3, old);
            checks.Check("D: rescheduled (cron changed): the row of the old occurrence still runs the job at the old time", ranOld == 1);
            stop.Cancel();
            await loop;
        }
        return checks.Done();
    }
}
