using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Caching;

/// <summary>Raises the highest bid by one: the amount depends on the loaded state, so a stale load decides wrongly.</summary>
public sealed record BidUp(Id<Auction> Target, string Bidder) : IChange<Auction>
{
    public Result Execute(Auction aggregate, ChangeContext context) => aggregate.Bid(Bidder, aggregate.HighestBid + 1m);
}

internal static class Bench
{
    public const string Schema = "ca_bench";

    public static Task Run(string[] args) => args switch
    {
        ["seed", var n] => Seed(int.Parse(n)),
        ["load", var s] => Load(int.Parse(s)),
        ["find", var s] => Find(int.Parse(s)),
        ["micro"] => Micro(),
        ["list", var s] => List(int.Parse(s)),
        ["hits", var s] => Hits(int.Parse(s)),
        ["stale", var n] => Stale(int.Parse(n)),
        ["conflict", var s] => Conflict(int.Parse(s)),
        ["http", var s] => Http.Bench(Schema, int.Parse(s)),
        ["l2", var kind, var s] => Hybrid.Bench(kind, kind switch { "redis" => Lab.Redis, "garnet" => Lab.Garnet, _ => null }, int.Parse(s)),
        _ => throw new ArgumentException(string.Join(' ', args)),
    };

    public static async Task<List<Guid>> Ids(string schema, Guid tenant, int n, string type = "auction")
    {
        var ids = new List<Guid>();
        await using var c = Lab.Db.CreateCommand($"SELECT id FROM {schema}.streams WHERE tenant = $1 AND type = $2 ORDER BY id LIMIT $3");
        c.Parameters.Add(new() { Value = tenant }); c.Parameters.Add(new() { Value = type }); c.Parameters.Add(new() { Value = n });
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        return ids;
    }

    private static string Ms(List<double> us) => Lab.Pct(us.Select(x => x / 1000).ToList());

    // ── seed: auctions of tenant A and B (open + 8 bids in one commit), ledgers of 10 and 100 events; the projection caught up
    private static async Task Seed(int auctions)
    {
        var store = await Lab.Store(Schema, fresh: true);
        var now = Lab.Time.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var next = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) <= auctions)
            {
                var tenant = i % 5 == 0 ? Lab.TenantB : Lab.TenantA;
                var a = Auction.Open(i % 3 == 0 ? "sue" : "sam", $"item {i}", $"lot-{i}", now.AddMinutes(i), now);
                for (var b = 1; b <= 8; b++) _ = a.Bid($"bidder{b}", b * 10m);
                await Lab.Commit(store, a, 0, Lab.Context(tenant));
            }
        })));
        Console.WriteLine($"INFO seed: {auctions} auctions (open + 8 bids, one commit each; 1 in 5 of tenant B) in {Lab.F(sw.Elapsed.TotalSeconds)} s");
        foreach (var events in new[] { 10, 100, 1000 })
            for (var i = 0; i < 200; i++)
            {
                var l = Ledger.Open(now);
                for (var e = 1; e < events; e++) _ = l.Post(1m);
                await Lab.Commit(store, l, 0, Lab.Context(Lab.TenantA, $"ledger{events}"));
            }
        Console.WriteLine("INFO seed: 200 ledgers each of 10, 100, 1000 events ([LoadByReplay]: no stored state)");
        var rows = Lab.ReadModels(Schema).Store<AuctionRow>();
        var host = new ProjectionHost<AuctionRow>(store, rows, new ProjectionOptions { Batch = 2000 });
        sw.Restart();
        var applied = await host.CatchUp();
        Console.WriteLine($"INFO seed: projection auction-list caught up ({applied} events) in {Lab.F(sw.Elapsed.TotalSeconds)} s");
    }

    // ── (b) loads: stored state vs replay vs the aggregate cache vs HybridCache L1 / L2 of the state bytes
    private static async Task Load(int seconds)
    {
        var store = await Lab.Store(Schema, fresh: false);
        var codec = Lab.Codec();
        var ctx = Lab.Context(Lab.TenantA);
        var auctions = (await Ids(Schema, Lab.TenantA, 1000)).Select(g => (Id<Auction>)g).ToArray();
        var ledgers = new Dictionary<int, Id<Ledger>[]>();
        await using (var c = Lab.Db.CreateCommand($"SELECT id, version FROM {Schema}.streams WHERE type = 'ledger'"))
        await using (var r = await c.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var v = (int)r.GetInt64(1);
                ledgers[v] = [.. ledgers.GetValueOrDefault(v) ?? [], (Id<Ledger>)r.GetGuid(0)];
            }
        var cache = new CachingStore(store, codec, Lab.Time, TimeSpan.FromHours(1));
        foreach (var id in auctions) _ = await cache.Load(id, ctx, default);
        await using var spL1 = Hybrid.Build(null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await using var spRedis = Hybrid.Build(Lab.Redis, TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1));   // L1 1 ms: every read goes to L2
        var l1 = spL1.GetRequiredService<HybridCache>();
        var redis = spRedis.GetRequiredService<HybridCache>();
        var opts = new HybridCacheEntryOptions { LocalCacheExpiration = TimeSpan.FromMilliseconds(1), Flags = HybridCacheEntryFlags.DisableLocalCacheWrite };
        foreach (var id in auctions)
        {
            var bytes = AggregateState.Write((await store.Load(id, ctx, default)).GetValueOrThrow(), Lab.Json);
            await l1.SetAsync("s:" + id.Value, bytes);
            await redis.SetAsync("s:" + id.Value, bytes, opts);
        }
        var stateSize = AggregateState.Write((await store.Load(auctions[0], ctx, default)).GetValueOrThrow(), Lab.Json).Length;
        var n = 0;
        Id<Auction> NextA() => auctions[(int)((uint)Interlocked.Increment(ref n) % auctions.Length)];
        Id<Ledger> NextL(int v) => ledgers[v][(int)((uint)Interlocked.Increment(ref n) % ledgers[v].Length)];
        var cases = new (string, Func<Task>)[]
        {
            ($"Load, stored state ({stateSize} B, 9 events)", async () => _ = await store.Load(NextA(), ctx, default)),
            ("Load, replay 10 events", async () => _ = await store.Load(NextL(10), ctx, default)),
            ("Load, replay 100 events", async () => _ = await store.Load(NextL(100), ctx, default)),
            ("Load, replay 1000 events", async () => _ = await store.Load(NextL(1000), ctx, default)),
            ("version probe (SELECT version)", async () =>
            {
                await using var c = Lab.Db.CreateCommand($"SELECT version FROM {Schema}.streams WHERE tenant = $1 AND type = 'auction' AND id = $2");
                c.Parameters.Add(new() { Value = Lab.TenantA }); c.Parameters.Add(new() { Value = NextA().Value });
                _ = await c.ExecuteScalarAsync();
            }),
            ("aggregate cache hit (bytes → fresh instance)", async () => _ = await cache.Load(NextA(), ctx, default)),
            ("HybridCache L1 hit + ReadState", async () =>
            {
                var id = NextA();
                var b = await l1.GetOrCreateAsync<byte[]>("s:" + id.Value, _ => throw new InvalidOperationException("miss"));
                _ = AggregateState.Read(id, b, Lab.Json);
            }),
            ("HybridCache L2 (Redis) hit + ReadState", async () =>
            {
                var id = NextA();
                var b = await redis.GetOrCreateAsync<byte[]>("s:" + id.Value, _ => throw new InvalidOperationException("miss"), opts);
                _ = AggregateState.Read(id, b, Lab.Json);
            }),
        };
        Console.WriteLine("| load | concurrency | latency | throughput |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var (name, op) in cases)
            foreach (var c in new[] { 1, 16, 64 })
            {
                var (lat, ops) = await Hybrid.Loop(c, seconds, op);
                Console.WriteLine($"| {name} | {c} | {Ms(lat)} | {Lab.F(ops, "F0")} ops/s |");
            }
        Console.WriteLine($"INFO aggregate cache: {cache.States.Count} entries, hits {cache.States.Hits}, misses {cache.States.Misses}; process working set {Environment.WorkingSet / 1048576} MB");
    }

    // ── (c) lookups
    private static async Task Find(int seconds)
    {
        var store = await Lab.Store(Schema, fresh: false);
        var cache = new CachingStore(store, Lab.Codec(), Lab.Time, TimeSpan.FromHours(1), cacheFind: true);
        var ctx = Lab.Context(Lab.TenantA);
        var lots = new List<string>();
        await using (var c = Lab.Db.CreateCommand($"SELECT key FROM {Schema}.lookups WHERE tenant = $1 LIMIT 1000"))
        {
            c.Parameters.Add(new() { Value = Lab.TenantA });
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) lots.Add(r.GetString(0));
        }
        foreach (var l in lots) _ = await cache.Find<Auction>("lot", l, ctx, default);
        var n = 0;
        string Next() => lots[(int)((uint)Interlocked.Increment(ref n) % lots.Count)];
        Console.WriteLine("| lookup | concurrency | latency | throughput |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var (name, op) in new (string, Func<Task>)[]
        {
            ("Find (store)", async () => _ = await store.Find<Auction>("lot", Next(), ctx, default)),
            ("Find (cached)", async () => _ = await cache.Find<Auction>("lot", Next(), ctx, default)),
        })
            foreach (var c in new[] { 1, 16, 64 })
            {
                var (lat, ops) = await Hybrid.Loop(c, seconds, op);
                Console.WriteLine($"| {name} | {c} | {Ms(lat)} | {Lab.F(ops, "F0")} ops/s |");
            }
    }

    // ── (c) codec metadata and the CPU parts of a hit
    private static async Task Micro()
    {
        var codec = Lab.Codec();
        var now = Lab.Time.GetUtcNow();
        var a = Auction.Open("sue", "lamp", "L1", now, now);
        for (var b = 1; b <= 8; b++) _ = a.Bid("x", b * 10m);
        AggregateChanges.Committed(a);
        var state = AggregateState.Write(a, Lab.Json);
        var payload = new ArrayBufferWriter<byte>();
        var type = codec.Encode<Auction>(new Auction.BidPlaced("bob", 12m), payload);
        var bytes = payload.WrittenSpan.ToArray();
        var rows = Enumerable.Range(0, 50).Select(i => new AuctionRow(Guid.NewGuid(), "sue", $"item {i}", now.AddMinutes(i), AuctionStatus.Open, i, i)).ToArray();
        var page = new Page<AuctionRow>(rows, "cursor-text-of-forty-characters-0123456", "W/\"abc\"", new GlobalPosition(1, 1));
        var q = new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50, Filters = [new ColumnFilter("status", AuctionStatus.Open)] };
        var pageBytes = PageCache.Serialize(page);
        await Task.Yield();
        Console.WriteLine($"INFO sizes: state {state.Length} B, event payload {bytes.Length} B, page of 50 {pageBytes.Length} B");
        Console.WriteLine("| operation | per op |");
        Console.WriteLine("|---|---|");
        void Time(string name, int iterations, Action op)
        {
            for (var i = 0; i < 1000; i++) op();
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) op();
            Console.WriteLine($"| {name} | {Lab.F(sw.Elapsed.TotalNanoseconds / iterations, "F0")} ns |");
        }
        Time("codec.StreamTypeOf<Auction>()", 2_000_000, () => _ = codec.StreamTypeOf<Auction>());
        Time("codec.Decode (BidPlaced)", 500_000, () => _ = codec.Decode("auction", type, bytes));
        Time("AggregateState.Read (9 events' state)", 500_000, () => _ = AggregateState.Read(a.Id, state, Lab.Json));
        Time("AggregateState.Write", 500_000, () => _ = AggregateState.Write(a, Lab.Json));
        Time("AuctionView from aggregate + serialize", 500_000, () => _ = JsonSerializer.SerializeToUtf8Bytes(AuctionView.From(a), ProbeJson.Default.AuctionView));
        Time("page of 50 rows: serialize", 50_000, () => _ = PageCache.Serialize(page));
        Time("page of 50 rows: deserialize", 50_000, () => _ = JsonSerializer.Deserialize(pageBytes, ProbeJson.Default.PageBody));
        Time("PageCache.Key (canonical query)", 1_000_000, () => _ = PageCache.Key(q));
        Time("Ledger replay of 100 decoded events (Rehydrate)", 20_000, () =>
        {
            var l = Ledger.Open(now); for (var e = 1; e < 100; e++) _ = l.Post(1m);
        });
    }

    // ── (a) list pages: the endpoint's two round trips vs the page cache
    private static async Task List(int seconds)
    {
        var rows = Lab.ReadModels(Schema).Store<AuctionRow>();
        await Lab.CheckpointTrigger(Schema);
        await using var watch = new CheckpointWatch(Lab.Db, Schema, rows, TimeSpan.FromSeconds(1));
        var byCheckpoint = new PageCache(rows, Validation.Checkpoint, Lab.Time, TimeSpan.FromHours(1));
        var pushed = new PageCache(rows, Validation.Pushed, Lab.Time, TimeSpan.FromHours(1), watch);
        await using var spRedis = Hybrid.Build(Lab.Redis, TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1));
        var redis = spRedis.GetRequiredService<HybridCache>();
        var l2 = new HybridCacheEntryOptions { LocalCacheExpiration = TimeSpan.FromMilliseconds(1), Flags = HybridCacheEntryFlags.DisableLocalCacheWrite };
        // ten distinct pages: sorts, a filter, cursor pages
        var queries = new List<ListQuery>();
        var baseQ = new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50 };
        var q = baseQ;
        for (var i = 0; i < 5; i++) { queries.Add(q); q = q with { After = (await rows.Search(q, default)).GetValueOrThrow().Next }; }
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "highest_bid", Descending = true, Limit = 50 });
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50, Filters = [new ColumnFilter("status", AuctionStatus.Open)] });
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "ends_at", Descending = true, Limit = 50 });
        queries.Add(new ListQuery(Lab.TenantB) { Sort = "ends_at", Limit = 50 });
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 200 });
        foreach (var x in queries)
        {
            _ = await byCheckpoint.Get(x); _ = await pushed.Get(x);
            await redis.SetAsync(PageCache.Key(x), (await byCheckpoint.Get(x)).Body, l2);
        }
        var n = 0;
        ListQuery Next() => queries[(int)((uint)Interlocked.Increment(ref n) % (queries.Count - 1))];   // the 200-row page separately
        Console.WriteLine("| list page (50 rows; 9 pages round robin) | concurrency | latency | throughput |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var (name, op) in new (string, Func<Task>)[]
        {
            ("Search only (1 round trip, rows deserialized)", async () => _ = await rows.Search(Next(), default)),
            ("endpoint today: Checkpoint + Search (2 round trips) + serialize", async () =>
            {
                _ = await rows.Checkpoint(default);
                _ = PageCache.Serialize((await rows.Search(Next(), default)).GetValueOrThrow());
            }),
            ("page cache hit, checkpoint read (1 round trip)", async () => _ = await byCheckpoint.Get(Next())),
            ("page cache hit, checkpoint pushed (0 round trips)", async () => _ = await pushed.Get(Next())),
            ("HybridCache L2 (Redis) hit of the page bytes", async () => _ = await redis.GetOrCreateAsync<byte[]>(PageCache.Key(Next()), _ => throw new InvalidOperationException("miss"), l2)),
            ("Search, page of 200 rows", async () => _ = await rows.Search(queries[^1], default)),
            ("page cache hit, pushed, page of 200 rows", async () => _ = await pushed.Get(queries[^1])),
        })
            foreach (var c in new[] { 1, 16, 64 })
            {
                var (lat, ops) = await Hybrid.Loop(c, seconds, op);
                Console.WriteLine($"| {name} | {c} | {Ms(lat)} | {Lab.F(ops, "F0")} ops/s |");
            }
        Console.WriteLine($"INFO page cache: pushed hits {pushed.Hits}, misses {pushed.Misses}; checkpoint hits {byCheckpoint.Hits}, misses {byCheckpoint.Misses}");
    }

    // ── a running projection under writes: what share of list requests a cache answers, per invalidation
    private static async Task Hits(int seconds)
    {
        var store = await Lab.Store(Schema, fresh: false);
        var rows = Lab.ReadModels(Schema).Store<AuctionRow>();
        await Lab.CheckpointTrigger(Schema);
        var gens = new TenantGenerations();
        var tagging = new TaggingStore(rows, gens);
        await using var signal = new NpgsqlFeedSignal(Lab.Db, store.FeedChannel);
        var host = new ProjectionHost<AuctionRow>(store, tagging, new ProjectionOptions { Batch = 2000 }, signal: signal);
        await host.CatchUp();
        await host.StartAsync(default);
        await using var watch = new CheckpointWatch(Lab.Db, Schema, rows, TimeSpan.FromSeconds(1));
        var idsA = (await Ids(Schema, Lab.TenantA, 5000)).Select(g => (Id<Auction>)g).ToArray();
        var idsB = (await Ids(Schema, Lab.TenantB, 2000)).Select(g => (Id<Auction>)g).ToArray();
        var queries = new List<ListQuery>();
        var q = new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50 };
        for (var i = 0; i < 5; i++) { queries.Add(q); q = q with { After = (await rows.Search(q, default)).GetValueOrThrow().Next }; }
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "highest_bid", Descending = true, Limit = 50 });
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "ends_at", Descending = true, Limit = 50 });
        queries.Add(new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50, Filters = [new ColumnFilter("status", AuctionStatus.Open)] });
        Console.WriteLine("| writes/s (tenant) | invalidation | hit ratio | latency (16 readers) | commits | stale answers |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var (rate, tenantB) in new[] { (0, false), (10, false), (50, false), (200, false), (50, true) })
            foreach (var mode in new[] { Validation.Checkpoint, Validation.Pushed, Validation.TenantTag, Validation.Ttl })
            {
                var pc = new PageCache(rows, mode, Lab.Time, TimeSpan.FromSeconds(1), watch, gens);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                var commits = 0;
                var writer = Task.Run(async () =>
                {
                    if (rate == 0) return;
                    var pipe = new ChangePipeline(store, Lab.Time);
                    var interval = TimeSpan.FromSeconds(1.0 / rate);
                    var next = Stopwatch.GetTimestamp();
                    var ctx = Lab.Context(tenantB ? Lab.TenantB : Lab.TenantA);
                    var ids = tenantB ? idsB : idsA;
                    while (!stop.IsCancellationRequested)
                    {
                        var r = await pipe.Run(new BidUp(ids[Random.Shared.Next(ids.Length)], "w"), ctx);
                        if (r.IsSuccess) Interlocked.Increment(ref commits);
                        next += (long)(interval.TotalSeconds * Stopwatch.Frequency);
                        var wait = next - Stopwatch.GetTimestamp();
                        if (wait > 0) try { await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), stop.Token); } catch (OperationCanceledException) { }
                    }
                });
                var lat = new List<double>();
                long stale = 0, n = 0;
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
                {
                    var mine = new List<double>();
                    while (!stop.IsCancellationRequested)
                    {
                        var query = queries[(int)((uint)Interlocked.Increment(ref n) % queries.Count)];
                        var t0 = Stopwatch.GetTimestamp();
                        var ans = await pc.Get(query);
                        mine.Add(Lab.Us(Stopwatch.GetTimestamp() - t0));
                        // stale = a hit whose position is behind the checkpoint the uncached path would read now (sampled 1 in 50)
                        if (ans.Hit && mine.Count % 50 == 0 && (await rows.Checkpoint(default)).Position > ans.Position)
                            Interlocked.Increment(ref stale);
                    }
                    lock (lat) lat.AddRange(mine);
                })));
                await writer;
                var total = pc.Hits + pc.Misses;
                Console.WriteLine($"| {rate}{(rate > 0 ? tenantB ? " (B)" : " (A)" : "")} | {mode}{(mode == Validation.Ttl ? " 1 s" : "")} | {Lab.F(100.0 * pc.Hits / total)} % of {total} | {Ms(lat)} | {commits} | {stale} of {total / 50} sampled hits |");
            }
        await host.StopAsync(default);
    }

    // ── staleness: commit returned → the cached answer shows it, per invalidation (and the uncached path = projection lag)
    private static async Task Stale(int samples)
    {
        var store = await Lab.Store(Schema, fresh: false);
        var rows = Lab.ReadModels(Schema).Store<AuctionRow>();
        await Lab.CheckpointTrigger(Schema);
        var gens = new TenantGenerations();
        var remoteGens = new TenantGenerations();
        await using var spA = Hybrid.Build(Lab.Redis, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));   // the worker process
        await using var spB = Hybrid.Build(Lab.Redis, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));   // the API process
        var hcA = spA.GetRequiredService<HybridCache>();
        var hcB = spB.GetRequiredService<HybridCache>();
        var tagging = new TaggingStore(rows, gens, Lab.Db, $"{Schema}_tenants", async touched =>
        {
            foreach (var t in touched) await hcA.RemoveByTagAsync("tenant:" + t.ToString("N"));
        });
        await using var signal = new NpgsqlFeedSignal(Lab.Db, store.FeedChannel);
        var host = new ProjectionHost<AuctionRow>(store, tagging, new ProjectionOptions { Batch = 2000 }, signal: signal);
        await host.CatchUp();
        await host.StartAsync(default);
        await using var watch = new CheckpointWatch(Lab.Db, Schema, rows, TimeSpan.FromSeconds(1));
        await using var tenantWatch = new TenantWatch(Lab.Db, $"{Schema}_tenants", remoteGens);
        var q = new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50 };
        var ctx = Lab.Context(Lab.TenantA);
        var ttl = TimeSpan.FromSeconds(1);
        var caches = new (string Name, Func<ListQuery, Task<string>> Get)[]
        {
            ("uncached Search (= projection lag)", async x => System.Text.Encoding.UTF8.GetString(PageCache.Serialize((await rows.Search(x, default)).GetValueOrThrow()))),
            ("checkpoint read per request", Wrap(new PageCache(rows, Validation.Checkpoint, Lab.Time, ttl))),
            ("checkpoint pushed (NOTIFY, other connection)", Wrap(new PageCache(rows, Validation.Pushed, Lab.Time, ttl, watch))),
            ("tenant tag, in the worker process", Wrap(new PageCache(rows, Validation.TenantTag, Lab.Time, ttl, tenants: gens))),
            ("tenant tag by NOTIFY (other process)", Wrap(new PageCache(rows, Validation.TenantTag, Lab.Time, ttl, tenants: remoteGens))),
            ("TTL 1 s", Wrap(new PageCache(rows, Validation.Ttl, Lab.Time, ttl))),
            ("HybridCache L1 1 s + L2 Redis, tag removed by the worker", async x =>
                System.Text.Encoding.UTF8.GetString(await hcB.GetOrCreateAsync(PageCache.Key(x),
                    async ct => PageCache.Serialize((await rows.Search(x, ct)).GetValueOrThrow()), tags: ["tenant:" + x.Tenant.ToString("N")]))),
        };
        static Func<ListQuery, Task<string>> Wrap(PageCache pc) => async x => System.Text.Encoding.UTF8.GetString((await pc.Get(x)).Body);
        var first = (await rows.Search(q, default)).GetValueOrThrow().Items.Select(r => (Id<Auction>)r.Id).ToArray();
        var pipe = new ChangePipeline(store, Lab.Time);
        Console.WriteLine("| invalidation | commit → visible in the cached answer | never visible within 5 s |");
        Console.WriteLine("|---|---|---|");
        foreach (var (name, get) in caches)
        {
            var lat = new List<double>();
            var missed = 0;
            for (var s = 0; s < samples; s++)
            {
                _ = await get(q);                                                      // the entry exists
                var target = first[s % first.Length];
                var before = (await store.Load(target, ctx, default)).GetValueOrThrow().HighestBid;
                var r = await pipe.Run(new BidUp(target, "s"), ctx);
                if (!r.IsSuccess) continue;
                var t0 = Stopwatch.GetTimestamp();
                var needle = $"\"highestBid\":{before + 1}";
                var seen = false;
                while (Stopwatch.GetTimestamp() - t0 < 5 * Stopwatch.Frequency)
                {
                    if ((await get(q)).Contains(needle, StringComparison.Ordinal)) { seen = true; break; }
                    await Task.Delay(1);
                }
                if (seen) lat.Add(Lab.Ms(Stopwatch.GetTimestamp() - t0)); else missed++;
                await Task.Delay(Random.Shared.Next(20, 200));                          // decorrelate from TTL boundaries
            }
            Console.WriteLine($"| {name} | {Lab.Pct(lat)} | {missed} |");
        }
        await host.StopAsync(default);
    }

    // ── (b) conflicts under contention: two processes, with and without the aggregate cache
    private static async Task Conflict(int seconds)
    {
        var store = await Lab.Store(Schema, fresh: false);
        var codec = Lab.Codec();
        var ctx = Lab.Context(Lab.TenantA);
        var all = (await Ids(Schema, Lab.TenantA, 1000)).Select(g => (Id<Auction>)g).ToArray();
        Console.WriteLine("| store | writers (2 processes) | hot auctions | successes/s | conflicts per success | gave up | latency per success (incl. retries) | cache hits |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var hot in new[] { 1, 10, 1000 })
            foreach (var writers in new[] { 8, 32 })
                foreach (var variant in new[] { "plain", "cache", "cache, one process", "cache, no eviction" })
                {
                    var ttl = TimeSpan.FromMinutes(5);
                    IAggregateStore[] procs = variant switch
                    {
                        "plain" => [store, store],
                        "cache" => [new CachingStore(store, codec, Lab.Time, ttl), new CachingStore(store, codec, Lab.Time, ttl)],
                        "cache, one process" => [new CachingStore(store, codec, Lab.Time, ttl)],
                        _ => [new CachingStore(store, codec, Lab.Time, ttl, evictOnConflict: false), new CachingStore(store, codec, Lab.Time, ttl, evictOnConflict: false)],
                    };
                    if (variant == "cache, one process") procs = [procs[0], procs[0]];
                    var targets = all.Take(hot).ToArray();
                    long ok = 0, conflicts = 0, gaveUp = 0;
                    var lat = new List<double>();
                    var end = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
                    await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
                    {
                        var pipe = new ChangePipeline(procs[w % 2], Lab.Time);
                        var mine = new List<double>();
                        while (Stopwatch.GetTimestamp() < end)
                        {
                            var target = targets[Random.Shared.Next(targets.Length)];
                            var t0 = Stopwatch.GetTimestamp();
                            var done = false;
                            for (var attempt = 0; attempt < 20 && !done; attempt++)          // the caller retries (C-7)
                            {
                                var r = await pipe.Run(new BidUp(target, "w" + w), ctx);
                                if (r.IsSuccess) done = true;
                                else if (StoreErrors.IsVersionConflict(r)) Interlocked.Increment(ref conflicts);
                                else throw new InvalidOperationException(r.ToString());
                            }
                            if (done) { Interlocked.Increment(ref ok); mine.Add(Lab.Us(Stopwatch.GetTimestamp() - t0)); } else Interlocked.Increment(ref gaveUp);
                        }
                        lock (lat) lat.AddRange(mine);
                    })));
                    var hits = procs.Distinct().OfType<CachingStore>().Sum(p => p.States.Hits);
                    var misses = procs.Distinct().OfType<CachingStore>().Sum(p => p.States.Misses);
                    Console.WriteLine($"| {variant} | {writers} | {hot} | {Lab.F(ok / (double)seconds, "F0")} | {Lab.F(ok == 0 ? 0 : conflicts / (double)ok, "F2")} | {gaveUp} | {Ms(lat)} | {(hits + misses == 0 ? "-" : Lab.F(100.0 * hits / (hits + misses)) + " %")} |");
                }
    }
}
