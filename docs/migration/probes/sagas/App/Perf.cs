using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Globalization;
using ArtFlow;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Sagas;
using static Lab;

/// <summary>Worker throughput and latency (real clock, real PostgreSQL). Each row: claim → load auction → append → complete.</summary>
internal static class Perf
{
    private static string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public static async Task<int> Run(string[] args)
    {
        var rows = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 2000;
        Console.WriteLine("\n## throughput: drain N preloaded rows (AcknowledgeNote: load the auction, append one event), batch 32, poll 50 ms");
        Console.WriteLine("| streams × rows | workers | rows | seconds | rows/s | max in flight |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var (streams, perStream) in new[] { (rows, 1), (16, rows / 16) })
            foreach (var workers in new[] { 1, 4, 16 })
                await Drain(streams, perStream, workers);

        Console.WriteLine("\n## latency: 4 workers, a producer commits 100 new auctions/s with one Noted each for 10 s; commit → reaction done");
        Console.WriteLine("| wake-up | rows | p50 ms | p90 ms | p99 ms | max ms |");
        Console.WriteLine("|---|---|---|---|---|---|");
        await Latency("poll 1 s", notify: false, TimeSpan.FromSeconds(1));
        await Latency("poll 100 ms", notify: false, TimeSpan.FromMilliseconds(100));
        await Latency("LISTEN/NOTIFY (fallback poll 1 s)", notify: true, TimeSpan.FromSeconds(1));

        Console.WriteLine("\n## commit cost of NOTIFY: 16 writers × 250 commits of a new auction with one Noted (one outbox row each)");
        Console.WriteLine("| trigger | commits | seconds | commits/s |");
        Console.WriteLine("|---|---|---|---|");
        await Writers(notify: false);
        await Writers(notify: true);
        return 0;
    }

    private static async Task<Env> Preload(string name, int streams, int perStream, bool notify = false)
    {
        var env = await Env.Create(name, TimeProvider.System, DeadLetterDesign.Table, notify);
        var alice = Alice(TimeProvider.System);
        await Parallel.ForEachAsync(Enumerable.Range(0, streams), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (n, ct) =>
        {
            var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
            for (var i = 0; i < perStream; i++)
                _ = auction.Note("ok");
            (await env.Store.Append(auction, 0, alice, Option.None, ct)).GetValueOrThrow();
        });
        return env;
    }

    private static async Task Drain(int streams, int perStream, int workers)
    {
        await using var env = await Preload($"tp{workers}_{streams}", streams, perStream);
        var total = streams * perStream;
        var done = 0;
        var inFlight = 0;
        var maxInFlight = 0;
        using var stop = new CancellationTokenSource();
        var options = new WorkerOptions { Batch = 32, PollInterval = TimeSpan.FromMilliseconds(50) };
        var clock = Stopwatch.StartNew();
        var loops = Enumerable.Range(0, workers).Select(_ => new ReactionWorker(env.Outbox, env.Reactions, env.Pipeline, TimeProvider.System, options)
        {
            Observed = (_, v, _) =>
            {
                if (v == Verdict.Done && Interlocked.Increment(ref done) == total)
                    stop.Cancel();
            },
        }).Select(w => Task.Run(() => w.Run(stop.Token))).ToArray();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var claimed = await env.Scalar("SELECT count(*) FROM {s}.outbox WHERE lease_until > now()");
                maxInFlight = Math.Max(maxInFlight, (int)claimed);
                try { await Task.Delay(20, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        await Task.WhenAll(loops);
        clock.Stop();
        await sampler;
        _ = inFlight;
        Console.WriteLine($"| {streams} × {perStream} | {workers} | {done} | {clock.Elapsed.TotalSeconds:0.00} | {F(done / clock.Elapsed.TotalSeconds)} | {maxInFlight} |");
    }

    private static async Task Latency(string label, bool notify, TimeSpan poll)
    {
        await using var env = await Env.Create("lat", TimeProvider.System, DeadLetterDesign.Table, notify);
        var alice = Alice(TimeProvider.System);
        var latencies = new List<double>();
        using var stop = new CancellationTokenSource();
        await using var wake = notify ? new OutboxWake(Db, env.Outbox.Channel, poll, TimeProvider.System) : null;
        if (wake is not null)
            await wake.Start();
        var options = new WorkerOptions { PollInterval = poll };
        var loops = Enumerable.Range(0, 4).Select(_ => new ReactionWorker(env.Outbox, env.Reactions, env.Pipeline, TimeProvider.System, options)
        {
            Wait = wake is null ? null : wake.Wait,
            Observed = (r, v, _) =>
            {
                if (v == Verdict.Done)
                    lock (latencies) latencies.Add((TimeProvider.System.GetUtcNow() - r.Event.RecordedAt).TotalMilliseconds);
            },
        }).Select(w => Task.Run(() => w.Run(stop.Token))).ToArray();
        var produced = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
            _ = auction.Note("ok");
            (await env.Store.Append(auction, 0, alice, Option.None, default)).GetValueOrThrow();
            produced++;
            var next = TimeSpan.FromMilliseconds(produced * 10.0) - clock.Elapsed;
            if (next > TimeSpan.Zero)
                await Task.Delay(next);
        }
        var drained = Stopwatch.StartNew();
        while (latencies.Count < produced && drained.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        await stop.CancelAsync();
        await Task.WhenAll(loops);
        var sorted = latencies.Order().ToArray();
        double P(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        Console.WriteLine($"| {label} | {sorted.Length}/{produced} | {F1(P(0.5))} | {F1(P(0.9))} | {F1(P(0.99))} | {F1(sorted.LastOrDefault())} |");
    }

    private static async Task Writers(bool notify)
    {
        await using var env = await Env.Create("wr", TimeProvider.System, DeadLetterDesign.Table, notify);
        var alice = Alice(TimeProvider.System);
        var clock = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, 16), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (n, ct) =>
        {
            for (var i = 0; i < 250; i++)
            {
                var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
                _ = auction.Note("ok");
                (await env.Store.Append(auction, 0, alice, Option.None, ct)).GetValueOrThrow();
            }
        });
        clock.Stop();
        Console.WriteLine($"| {(notify ? "NOTIFY per statement" : "none")} | 4000 | {clock.Elapsed.TotalSeconds:0.00} | {F(4000 / clock.Elapsed.TotalSeconds)} |");
    }
}

/// <summary>The worker as a hosted service (questions 7 and 8): what the Aspire AppHost starts.</summary>
internal static class Serve
{
    public static async Task<int> Run(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        var cs = builder.Configuration.GetConnectionString("tarol") ?? ConnectionString;
        var db = new Npgsql.NpgsqlSlimDataSourceBuilder(cs).EnableArrays().Build();
        var codec = Codec();
        var store = new Tarol.Persistence.Npgsql.NpgsqlAggregateStore(db, codec, TimeProvider.System,
            new Tarol.Persistence.Npgsql.NpgsqlStoreOptions { Reactions = GeneratedReactions.Set.Table });
        await store.EnsureSchema(default);
        var outbox = new PgOutbox(db, codec, TimeProvider.System, "tarol", DeadLetterDesign.Table);
        await outbox.Install(notify: false, default);
        var pipeline = new Tarol.Changes.ChangePipeline(store, TimeProvider.System);
        if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)          // set by the Aspire AppHost
            builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("worker"))
                .WithTracing(t => t.AddSource("Tarol.Sagas", "Probe.Http", "Npgsql").AddOtlpExporter());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions.AddHostedService(builder.Services, _ =>
            new ReactionHost(_ => new ReactionWorker(outbox, GeneratedReactions.Set, pipeline, TimeProvider.System, new WorkerOptions()), 4,
                ct => store.PurgeKeys(TimeProvider.System.GetUtcNow().AddDays(-7), 1000, ct), TimeSpan.FromHours(1), TimeProvider.System));
        using var host = builder.Build();
        await host.StartAsync();
        // a producer so the dashboard shows traces: one settled auction per second
        var alice = Alice(TimeProvider.System);
        var lifetime = host.Services.GetService(typeof(Microsoft.Extensions.Hosting.IHostApplicationLifetime)) as Microsoft.Extensions.Hosting.IHostApplicationLifetime;
        var produced = 0;
        while (!lifetime!.ApplicationStopping.IsCancellationRequested && (args.Length == 0 || produced < int.Parse(args[0], CultureInfo.InvariantCulture)))
        {
            using (Http.StartActivity("POST /auctions/{id}/settle"))
            {
                var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
                _ = auction.PlaceBid("bob", 150, TimeProvider.System.GetUtcNow());
                (await store.Append(auction, 0, alice, Option.None, default)).GetValueOrThrow();
                _ = await pipeline.Run(new SettleAuction(auction.Id), alice);
            }
            produced++;
            Console.WriteLine($"produced {produced}, pending {await outbox.Pending(default)}");
            try { await Task.Delay(1000, lifetime.ApplicationStopping); } catch (OperationCanceledException) { }
        }
        await host.StopAsync();
        return 0;
    }
}
