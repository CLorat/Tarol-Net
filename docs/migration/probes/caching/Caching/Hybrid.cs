using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Probe.Caching;

/// <summary>A source-generated serializer for HybridCache: the AOT-safe way to cache a typed value.</summary>
internal sealed class ViewSerializer : IHybridCacheSerializer<AuctionView>
{
    public AuctionView Deserialize(ReadOnlySequence<byte> source)
    {
        var reader = new Utf8JsonReader(source);
        return JsonSerializer.Deserialize(ref reader, ProbeJson.Default.AuctionView)!;
    }
    public void Serialize(AuctionView value, IBufferWriter<byte> target)
    {
        using var w = new Utf8JsonWriter(target);
        JsonSerializer.Serialize(w, value, ProbeJson.Default.AuctionView);
    }
}

internal static class Hybrid
{
    /// <summary>The MemoryCache's clock hook, adapted from a TimeProvider.</summary>
    private sealed class Clock(TimeProvider time) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    public static ServiceProvider Build(string? l2, TimeSpan local, TimeSpan expiration, TimeProvider? time = null, bool memoryClock = false)
    {
        var services = new ServiceCollection();
        if (time is not null) services.AddSingleton(time);
        if (time is not null && memoryClock) services.Configure<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>(o => o.Clock = new Clock(time));
        if (l2 is not null) services.AddStackExchangeRedisCache(o => o.Configuration = l2);
        services.AddHybridCache(o =>
        {
            o.MaximumPayloadBytes = 1 << 20;
            o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = expiration, LocalCacheExpiration = local };
        }).AddSerializer<AuctionView, ViewSerializer>();
        return services.BuildServiceProvider();
    }

    public static async Task<int> SelfTest(string kind, string? l2)
    {
        Console.WriteLine($"# selftest hybrid {kind} ({Lab.Runtime})");
        var c = new Checks($"selftest hybrid {kind}");
        var run = Guid.NewGuid().ToString("N")[..8];
        await using var spA = Build(l2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        await using var spB = Build(l2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        var a = spA.GetRequiredService<HybridCache>();
        var b = spB.GetRequiredService<HybridCache>();
        var calls = 0;
        ValueTask<byte[]> Make(string v) { Interlocked.Increment(ref calls); return ValueTask.FromResult(System.Text.Encoding.UTF8.GetBytes(v)); }
        string S(byte[] x) => System.Text.Encoding.UTF8.GetString(x);

        var k1 = $"{run}:t:a:page1";
        _ = await a.GetOrCreateAsync(k1, _ => Make("v1"), tags: ["tenant:a"]);
        _ = await a.GetOrCreateAsync(k1, _ => Make("v1-again"), tags: ["tenant:a"]);
        c.Check("L1: the second GetOrCreate is a hit (factory once)", calls == 1);
        _ = await a.GetOrCreateAsync($"{run}:t:a:page1:same", async _ => { await Task.Delay(50); return await Make("x"); });
        var stampede = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => a.GetOrCreateAsync($"{run}:stampede", async ct => { await Task.Delay(50, ct); return await Make("s"); }).AsTask()));
        c.Check("stampede protection: 20 concurrent misses of one key run the factory once", calls == 3, $"factory calls {calls - 2} for 20 callers");

        if (l2 is not null)
        {
            calls = 0;
            var fromB = await b.GetOrCreateAsync(k1, _ => Make("from-b"), tags: ["tenant:a"]);
            c.Check("L2: process B reads process A's entry without running its factory", calls == 0 && S(fromB) == "v1", S(fromB));
            await a.RemoveAsync(k1);
            var staleB = await b.GetOrCreateAsync(k1, _ => Make("v2"), tags: ["tenant:a"]);
            c.Check("found: A's RemoveAsync does not reach B's L1 (no backplane): B answers the removed value", S(staleB) == "v1" && calls == 0, S(staleB));
            var sw = Stopwatch.StartNew();
            string seen;
            do { seen = S(await b.GetOrCreateAsync(k1, _ => Make("v2"), tags: ["tenant:a"])); if (seen == "v1") await Task.Delay(20); }
            while (seen == "v1" && sw.ElapsedMilliseconds < 5000);
            c.Check("B sees the removal once its L1 entry expires (LocalCacheExpiration = 1 s)", seen == "v2", $"after {sw.ElapsedMilliseconds} ms");

            // tags across processes
            var k2 = $"{run}:t:a:page2";
            _ = await a.GetOrCreateAsync(k2, _ => Make("p2-v1"), tags: ["tenant:a"]);
            _ = await b.GetOrCreateAsync(k2, _ => Make("p2-b"), tags: ["tenant:a"]);   // B holds it in L1 (from L2)
            await a.RemoveByTagAsync("tenant:a");
            calls = 0;
            var afterTagA = S(await a.GetOrCreateAsync(k2, _ => Make("p2-v2"), tags: ["tenant:a"]));
            var afterTagB = S(await b.GetOrCreateAsync(k2, _ => Make("p2-v2b"), tags: ["tenant:a"]));
            c.Check("tags: RemoveByTagAsync invalidates the entry in the process that called it", afterTagA == "p2-v2");
            Console.WriteLine($"INFO tags across processes: right after A's RemoveByTagAsync, B answers '{afterTagB}' ({(afterTagB == "p2-b" || afterTagB == "p2-v1" ? "stale: B's L1 keeps it" : "fresh")})");
            sw.Restart();
            do { seen = S(await b.GetOrCreateAsync(k2, _ => Make("p2-v2b"), tags: ["tenant:a"])); if (seen is "p2-b" or "p2-v1") await Task.Delay(20); }
            while (seen is "p2-b" or "p2-v1" && sw.ElapsedMilliseconds < 5000);
            Console.WriteLine($"INFO tags across processes: B answers '{seen}' after {sw.ElapsedMilliseconds} ms");

            // a typed value through L2 with the source-generated serializer
            var view = new AuctionView(Guid.NewGuid(), "sue", "lamp", DateTimeOffset.UnixEpoch, AuctionStatus.Open, 12m, 2, 3);
            await a.SetAsync($"{run}:view", view);
            var readBack = await b.GetOrCreateAsync($"{run}:view", _ => ValueTask.FromResult(view with { Title = "factory" }));
            c.Check("a typed value (AuctionView) through L2 with an IHybridCacheSerializer<T> over the JSON context", readBack == view);
        }

        // TimeProvider: does HybridCache's expiry follow the registered clock?
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var spT = Build(null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), fake);
        var t = spT.GetRequiredService<HybridCache>();
        calls = 0;
        _ = await t.GetOrCreateAsync($"{run}:clock", _ => Make("c1"));
        fake.Advance(TimeSpan.FromSeconds(31));
        _ = await t.GetOrCreateAsync($"{run}:clock", _ => Make("c2"));
        Console.WriteLine($"INFO HybridCache with a FakeTimeProvider in DI, +31 s on a 30 s expiry: factory ran {calls} time(s) ({(calls == 2 ? "expiry follows the TimeProvider" : "expiry does NOT follow the TimeProvider")})");
        var fake2 = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var spT2 = Build(null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), fake2, memoryClock: true);
        var t2 = spT2.GetRequiredService<HybridCache>();
        calls = 0;
        _ = await t2.GetOrCreateAsync($"{run}:clock2", _ => Make("c1"));
        fake2.Advance(TimeSpan.FromSeconds(31));
        _ = await t2.GetOrCreateAsync($"{run}:clock2", _ => Make("c2"));
        c.Check("HybridCache L1 expiry follows a TimeProvider through MemoryCacheOptions.Clock (+31 s on 30 s: factory again)", calls == 2, $"factory {calls}");
        var tagged = $"{run}:clock:tag";
        calls = 0;
        _ = await t.GetOrCreateAsync(tagged, _ => Make("t1"), tags: ["x"]);
        await t.RemoveByTagAsync("x");
        fake.Advance(TimeSpan.FromMilliseconds(1));
        _ = await t.GetOrCreateAsync(tagged, _ => Make("t2"), tags: ["x"]);
        Console.WriteLine($"INFO HybridCache tag invalidation on the fake clock: factory ran {calls} time(s)");
        return c.Done();
    }

    /// <summary>Hit latency of a cache tier for a payload, under concurrency.</summary>
    public static async Task Bench(string kind, string? l2, int seconds)
    {
        var state = new byte[320]; Random.Shared.NextBytes(state);
        var page = new byte[9_000]; Random.Shared.NextBytes(page);
        foreach (var (name, payload) in new[] { ("state 320 B", state), ("page 9 KB", page) })
        {
            if (l2 is not null)
            {
                // L2 only: the IDistributedCache the HybridCache would use (an L1 miss that hits L2)
                await using var sp = Build(l2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
                var dist = sp.GetRequiredService<IDistributedCache>();
                await dist.SetAsync("bench:" + name, payload);
                foreach (var n in new[] { 1, 16, 64 })
                {
                    var lat = await Loop(n, seconds, async () => { _ = await dist.GetAsync("bench:" + name); });
                    Console.WriteLine($"| {kind} L2 GET | {name} | {n} | {Lab.Pct(lat.Item1, "us")} | {Lab.F(lat.Item2, "F0")} ops/s |");
                }
            }
            else
            {
                await using var sp = Build(null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
                var hc = sp.GetRequiredService<HybridCache>();
                _ = await hc.GetOrCreateAsync("bench:" + name, _ => ValueTask.FromResult(payload));
                foreach (var n in new[] { 1, 16, 64 })
                {
                    var lat = await Loop(n, seconds, async () => { _ = await hc.GetOrCreateAsync("bench:" + name, _ => ValueTask.FromResult(payload)); });
                    Console.WriteLine($"| HybridCache L1 hit | {name} | {n} | {Lab.Pct(lat.Item1, "us")} | {Lab.F(lat.Item2, "F0")} ops/s |");
                }
            }
        }
    }

    public static async Task<(List<double>, double)> Loop(int concurrency, int seconds, Func<Task> op, int warm = 200)
    {
        for (var i = 0; i < warm; i++) await op();
        var all = new List<double>();
        var end = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        var total = 0L;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            var mine = new List<double>();
            while (Stopwatch.GetTimestamp() < end)
            {
                var t0 = Stopwatch.GetTimestamp();
                await op();
                mine.Add(Lab.Us(Stopwatch.GetTimestamp() - t0));
            }
            lock (all) { all.AddRange(mine); total += mine.Count; }
        })));
        return (all, total / sw.Elapsed.TotalSeconds);
    }
}
