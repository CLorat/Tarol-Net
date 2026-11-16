using System.Diagnostics;
using System.Globalization;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Npgsql;
using Tarol.StronglyTypedId;

namespace Probe.Jobs;

/// <summary>
/// <c>chaos &lt;variant&gt; &lt;seconds&gt; &lt;workers&gt; &lt;jobs&gt; &lt;none|kill|freeze|both&gt; [crashEvery]</c>: worker processes run
/// <c>jobs</c> schedules that fire every second; the orchestrator kills (-9) or freezes (SIGSTOP 2–6 s) a random worker every
/// 1.5–3 s and restarts the killed ones. At the end it waits for every schedule to pass the last occurrence and counts, per
/// occurrence: commits (Recorded events), attempted runs (the runs table), and lateness (first start − occurrence).
/// </summary>
internal static class Chaos
{
    public static async Task Setup(string variant, string schema, int jobs, DateTimeOffset first)
    {
        var store = await Lab.Store(schema, fresh: true, variant == "D" ? GeneratedReactions.Set : null);
        var ctx = OperationContext.Start(Actor.System("Probe.Setup"), (Id<ITenant>)Lab.TenantA, TimeProvider.System);
        var rows = new NpgsqlJobRows(Lab.Db, schema, TimeProvider.System);
        await rows.EnsureSchema();
        for (var k = 0; k < jobs; k++)
        {
            var name = Worker.JobName(k);
            var id = Lab.StreamOf(Lab.TenantA, name);
            await Lab.Commit(store, Tally.Open(id, name), 0, ctx);
            if (variant == "D")
                await Lab.Commit(store, Schedule.Start(id, name, first), 0, ctx);
            else
                await rows.Register(Lab.TenantA, name, first);
        }
    }

    public static ProcessStartInfo Start(params string[] args)
    {
        var path = Environment.ProcessPath!;
        var info = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(path) == "dotnet") info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Probe.Jobs.dll"));
        foreach (var a in args) info.ArgumentList.Add(a);
        return info;
    }

    private static void Signal(int pid, string signal) => Process.Start("kill", ["-" + signal, pid.ToString(CultureInfo.InvariantCulture)])!.WaitForExit();

    public static async Task<int> Run(string variant, int seconds, int workers, int jobs, string mode, int crashEvery)
    {
        var schema = "chaos_" + variant.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var first = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddSeconds(3);
        await Setup(variant, schema, jobs, first);
        var procs = new Process?[workers];
        var env = new Dictionary<string, string> { ["PROBE_PG"] = Lab.Pg, ["PROBE_CRASH_EVERY"] = crashEvery.ToString(CultureInfo.InvariantCulture) };
        void Spawn(int i)
        {
            var info = Start("worker", variant, schema, "w" + i.ToString(CultureInfo.InvariantCulture), jobs.ToString(CultureInfo.InvariantCulture));
            foreach (var (k, v) in env) info.Environment[k] = v;
            foreach (var k in new[] { "PROBE_LEASE_MS", "PROBE_WORK_MS", "PROBE_POLL_MS", "PROBE_RUNTIME", "PROBE_BATCH" })
                if (Environment.GetEnvironmentVariable(k) is { } v) info.Environment[k] = v;
            var p = Process.Start(info)!;
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            procs[i] = p;
        }
        for (var i = 0; i < workers; i++) Spawn(i);

        int kills = 0, freezes = 0, crashes = 0;
        var frozen = new HashSet<int>();
        var rnd = new Random(14);
        var end = first.AddSeconds(seconds);
        var sw = Stopwatch.StartNew();
        while (DateTimeOffset.UtcNow < end)
        {
            await Task.Delay(rnd.Next(1500, 3000));
            for (var i = 0; i < workers; i++)
                if (procs[i] is { HasExited: true } dead && !frozen.Contains(i))
                {
                    if (dead.ExitCode != 137 && dead.ExitCode != -1) crashes++;     // FailFast: restart it
                    Spawn(i);
                }
            if (mode == "none") continue;
            var victim = rnd.Next(workers);
            if (frozen.Contains(victim) || procs[victim] is not { HasExited: false } p) continue;
            var freeze = mode == "freeze" || (mode == "both" && rnd.Next(2) == 0);
            if (freeze)
            {
                freezes++;
                frozen.Add(victim);
                Signal(p.Id, "STOP");
                var hold = rnd.Next(2000, 6000);
                _ = Task.Run(async () => { await Task.Delay(hold); Signal(p.Id, "CONT"); lock (frozen) frozen.Remove(victim); });
            }
            else
            {
                kills++;
                p.Kill();
                await p.WaitForExitAsync();
                await Task.Delay(300);
                Spawn(victim);
            }
        }
        // let every schedule pass the last occurrence (≤ end); frozen workers resume meanwhile
        var last = end;
        var caught = Stopwatch.StartNew();
        while (caught.Elapsed < TimeSpan.FromSeconds(120))
        {
            for (var i = 0; i < workers; i++)
                if (procs[i] is { HasExited: true } && !frozen.Contains(i)) { crashes++; Spawn(i); }
            var done = await Lab.Scalar<long>($"""
                SELECT count(*) FROM (SELECT stream_id, count(DISTINCT (payload->>'occurrence')::timestamptz) n FROM {schema}.events
                                      WHERE type = 'tally' AND jsonb_exists(payload, 'occurrence') AND (payload->>'occurrence')::timestamptz <= $1
                                      GROUP BY stream_id) x WHERE n >= $2
                """, last, (long)seconds + 1);
            if (done >= jobs) break;
            await Task.Delay(500);
        }
        await Task.Delay(Worker.Lease + TimeSpan.FromSeconds(1));                     // late duplicates of frozen workers land now
        foreach (var p in procs) { try { if (p is { HasExited: false }) { Signal(p.Id, "CONT"); p.Kill(); } } catch (InvalidOperationException) { } }
        await Report(variant, schema, mode, first, last, jobs, workers, kills, freezes, crashes, crashEvery, sw.Elapsed);
        return 0;
    }

    private static async Task Report(string variant, string schema, string mode, DateTimeOffset first, DateTimeOffset last, int jobs, int workers,
        int kills, int freezes, int crashes, int crashEvery, TimeSpan took)
    {
        var expected = (long)(last - first).TotalSeconds + 1;
        await using var c = Lab.Db.CreateCommand($"""
            WITH occ AS (SELECT s.id::text AS job, g AS occurrence FROM {schema}.streams s
                         CROSS JOIN generate_series($1::timestamptz, $2::timestamptz, interval '1 second') g WHERE s.type = 'tally'),
            commits AS (SELECT stream_id::text AS job, (payload->>'occurrence')::timestamptz AS occurrence, count(*) n FROM {schema}.events
                        WHERE type = 'tally' AND jsonb_exists(payload, 'occurrence') GROUP BY 1, 2),
            runs AS (SELECT job, occurrence, count(*) n, min(started_at) first_start FROM {schema}.runs GROUP BY 1, 2)
            SELECT count(*) FILTER (WHERE coalesce(c.n, 0) = 0),
                   count(*) FILTER (WHERE c.n > 1),
                   coalesce(sum(c.n - 1) FILTER (WHERE c.n > 1), 0)::bigint,
                   count(*) FILTER (WHERE r.n > 1),
                   coalesce(sum(r.n), 0)::bigint,
                   count(*)
            FROM occ o LEFT JOIN commits c ON c.job = o.job AND c.occurrence = o.occurrence
                       LEFT JOIN runs r ON r.job = o.job AND r.occurrence = o.occurrence
            """);
        c.Parameters.Add(new() { Value = first });
        c.Parameters.Add(new() { Value = last });
        await using var r = await c.ExecuteReaderAsync();
        await r.ReadAsync();
        long gaps = r.GetInt64(0), dupOcc = r.GetInt64(1), dupCommits = r.GetInt64(2), dupRuns = r.GetInt64(3), runs = r.GetInt64(4), total = r.GetInt64(5);
        await r.DisposeAsync();
        var late = new List<double>();
        await using (var l = Lab.Db.CreateCommand($"SELECT extract(epoch FROM min(started_at) - occurrence) * 1000 FROM {schema}.runs WHERE occurrence BETWEEN $1 AND $2 GROUP BY job, occurrence"))
        {
            l.Parameters.Add(new() { Value = first });
            l.Parameters.Add(new() { Value = last });
            await using var lr = await l.ExecuteReaderAsync();
            while (await lr.ReadAsync()) late.Add((double)lr.GetDecimal(0));
        }
        Console.WriteLine($"| {variant} | {mode}{(crashEvery > 0 ? $" + crash after commit every {crashEvery}" : "")} | {workers}×2 loops, {jobs} jobs, {expected} s | " +
                          $"{total} | {kills} / {freezes} / {crashes} | **{gaps}** | **{dupOcc}** ({dupCommits} extra) | {dupRuns} ({runs} runs) | {Lab.Pct(late)} |");
    }
}
