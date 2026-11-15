using System.Collections.Concurrent;
using System.Diagnostics;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Sagas;
using Tarol.StronglyTypedId;
using static ArtFlow.Lab;

namespace ArtFlow;

/// <summary>
/// Latency (commit of Auction.Settled → published → IssueInvoice committed in billing) at a steady rate, and throughput of a
/// backlog, per transport. Both systems on the Npgsql store in one PostgreSQL, one process (one clock).
/// </summary>
internal static class Bench
{
    public static async Task<int> Run(string[] kinds, int seconds, int rate, int burst)
    {
        Console.WriteLine($"# bench: {Runtime}, {Environment.ProcessorCount} cores, {seconds} s at {rate}/s after a warm-up, backlog {burst}; publisher: 2 loops (4 for the backlog), consumer concurrency 4 (8)");
        Console.WriteLine("| transport | outbox wake-up | commit → published p50 / p99 | published → committed p50 / p99 | end to end p50 / p99 | backlog: published/s | backlog: consumed/s |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var kind in kinds)
            foreach (var notify in new[] { false, true })
                await One(kind, notify, seconds, rate, burst);
        return 0;
    }

    private static async Task One(string kind, bool notify, int seconds, int rate, int burst)
    {
        var time = TimeProvider.System;
        var suffix = $"b{Environment.ProcessId}_{kind.Replace("+", "_", StringComparison.Ordinal)}_{(notify ? "n" : "p")}";
        var t1 = Id<ITenant>.New();
        var alice = OperationContext.Start(Actor.User("alice"), t1, time);
        await using var transport = Transport(kind, time, "bus_" + suffix);
        var only = new ConsumerSet([new ConsumerInfo("IssueInvoice", ["artflow.auction-settled"])], Billing.GeneratedConsumers.Set.Runner);
        await using var artflow = await Sys.Create("artflow", suffix, true, time, transport, GeneratedPublications.Create(), GeneratedConsumers.Set,
            ArtFlowJson.Default.Options, c => c.Add<Auction>(), [], _ => { }, notify: notify);
        await using var billing = await Sys.Create("billing", suffix, true, time, transport, Billing.GeneratedPublications.Create(), only,
            Billing.BillingJson.Default.Options, c => c.Add<Billing.Invoice>().Add<Billing.Ledger>(), [], o => o.Sources["artflow"] = new SourceTrust { AnyTenant = true });
        var done = new ConcurrentDictionary<Guid, DateTimeOffset>();
        billing.Consumers.Observed = a => { if (a.Verdict == Verdict.Done && a.Incoming.Message is { } m) done.TryAdd(m.Id, time.GetUtcNow()); };
        var subs = billing.Consumers.Subscriptions(4);
        await Reset(kind, subs.Select(s => s.Name));
        await transport.Prepare(artflow.Publications.Topics, subs, default);

        async Task<Id<Auction>[]> Open(int n)
        {
            var ids = new Id<Auction>[n];
            for (var i = 0; i < n; i++)
                ids[i] = (Id<Auction>)(await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow().StreamId;
            return ids;
        }
        async Task Paced(Id<Auction>[] ids, int perSecond)
        {
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < ids.Length; i++)
            {
                var due = TimeSpan.FromSeconds((double)i / perSecond);
                if (due > clock.Elapsed)
                    await Task.Delay(due - clock.Elapsed);
                (await artflow.Pipeline.Run(new SettleAuction(ids[i], "bob", 100), alice)).GetValueOrThrow();
            }
        }

        var warm = await Open(rate * 2);
        var timed = await Open(rate * seconds);
        artflow.Start(publishers: 2, consume: false);
        billing.Start(publishers: 1, concurrency: 4);
        await Paced(warm, rate);                                                       // two warm-up seconds (tiering, pools, broker)
        await Until(() => done.Count >= warm.Length, TimeSpan.FromSeconds(60));
        var skip = artflow.Published.Count;
        await Paced(timed, rate);
        var all = await Until(() => done.Count >= warm.Length + timed.Length, TimeSpan.FromSeconds(120));
        var published = artflow.Published.Skip(skip).ToList();
        var toPublish = published.Select(p => (p.At - p.Message.Time).TotalMilliseconds).ToList();
        var toCommit = published.Where(p => done.ContainsKey(p.Message.Id)).Select(p => (done[p.Message.Id] - p.At).TotalMilliseconds).ToList();
        var endToEnd = published.Where(p => done.ContainsKey(p.Message.Id)).Select(p => (done[p.Message.Id] - p.Message.Time).TotalMilliseconds).ToList();

        // backlog: committed while nobody publishes or consumes, then drained by 4 publisher loops and consumer concurrency 8
        await artflow.Stop();
        await billing.Stop();
        var backlog = await Open(burst);
        foreach (var id in backlog)
            (await artflow.Pipeline.Run(new SettleAuction(id, "bob", 100), alice)).GetValueOrThrow();
        var before = done.Count;
        var publishedBefore = artflow.Published.Count;
        var clock = Stopwatch.StartNew();
        artflow.Start(publishers: 4, consume: false);
        billing.Start(publishers: 1, concurrency: 8);
        await Until(() => artflow.Published.Count >= publishedBefore + burst, TimeSpan.FromSeconds(300));
        var publishSeconds = clock.Elapsed.TotalSeconds;
        var drained = await Until(() => done.Count >= before + burst, TimeSpan.FromSeconds(300));
        var consumeSeconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"| {transport.Name} | {(notify ? "NOTIFY" : "poll 100 ms")} | {P(toPublish, 50)} / {P(toPublish, 99)} ms | {P(toCommit, 50)} / {P(toCommit, 99)} ms | {P(endToEnd, 50)} / {P(endToEnd, 99)} ms{(all ? "" : " (incomplete)")} | {burst / publishSeconds:F0} | {(drained ? $"{burst / consumeSeconds:F0}" : "incomplete")} |");
    }

    private static string P(List<double> values, int percentile)
    {
        if (values.Count == 0)
            return "-";
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)Math.Ceiling(percentile / 100.0 * values.Count) - 1)].ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }
}
