using Microsoft.Extensions.Time.Testing;
using Npgsql;
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

internal static class SelfTest
{
    public static Task<int> Run(string kind) => kind switch
    {
        "postgres" => Postgres(),
        "redis" => Hybrid.SelfTest("redis", Lab.Redis),
        "garnet" => Hybrid.SelfTest("garnet", Lab.Garnet),
        "local" => Hybrid.SelfTest("local", null),
        "http" => Http.SelfTest(),
        _ => throw new ArgumentException(kind),
    };

    private static async Task<int> Postgres()
    {
        Console.WriteLine($"# selftest postgres ({Lab.Runtime})");
        var c = new Checks("selftest postgres");
        const string schema = "ca_self";
        var store = await Lab.Store(schema, fresh: true);
        var codec = Lab.Codec();
        var now = Lab.Time.GetUtcNow();
        var ctxA = Lab.Context(Lab.TenantA);
        var ctxB = Lab.Context(Lab.TenantB);
        var ttl = TimeSpan.FromMinutes(5);

        // ── (b) the aggregate cache ───────────────────────────────────────────────────────────────────────────
        var p1 = new CachingStore(store, codec, Lab.Time, ttl);                      // "process 1"
        var p2 = new CachingStore(store, codec, Lab.Time, ttl);                      // "process 2": its own cache, same store
        var a = Auction.Open("sue", "lamp", "L1", now.AddHours(1), now);
        await Lab.Commit(store, a, 0, ctxA);
        var loaded = (await p1.Load(a.Id, ctxA, default)).GetValueOrThrow();
        var again = (await p1.Load(a.Id, ctxA, default)).GetValueOrThrow();
        c.Check("aggregate cache: the second load is a hit", p1.States.Hits == 1 && p1.States.Misses == 1);
        c.Check("aggregate cache: tenant in the key, another tenant's id is not found (store and cache)",
            !(await p1.Load(a.Id, ctxB, default)).TryGetValue(out Auction? _) && !(await p1.Load(a.Id, ctxB, default)).TryGetValue(out Auction? _));
        _ = loaded.Bid("bob", 5m);                                                    // a decision mutates its instance …
        var third = (await p1.Load(a.Id, ctxA, default)).GetValueOrThrow();
        c.Check("aggregate cache: every hit is a fresh instance (a mutated instance does not leak into the next hit)",
            third.HighestBid == 0m && third.UncommittedChanges.Length == 0 && !ReferenceEquals(third, again));

        // stale hit → conflict → reload, through the real pipeline
        var pipe1 = new ChangePipeline(p1, Lab.Time);
        var pipe2 = new ChangePipeline(p2, Lab.Time);
        var r2 = await pipe2.Run(new PlaceBid(a.Id, "carl", 10m), ctxA);                // process 2 moves the stream on
        var stale = (await p1.Load(a.Id, ctxA, default)).GetValueOrThrow();
        c.Check("stale hit: process 1's cache still holds version 1 after process 2 committed version 2",
            r2.IsSuccess && stale.Version == 1);
        var r1 = await pipe1.Run(new PlaceBid(a.Id, "dora", 8m), ctxA);                 // decided on the stale state: 8 > 0
        c.Check("stale hit: Append refuses the decision with Stream.VersionConflict (nothing written)",
            StoreErrors.IsVersionConflict(r1), r1.ToString());
        var retried = await pipe1.Run(new PlaceBid(a.Id, "dora", 8m), ctxA);           // the caller retries (C-7): evicted → reload
        c.Check("stale hit: the retry reloads (evicted on the conflict) and decides on version 2: 8 <= 10 is refused by the guard",
            retried.TryGetValue(out Violation? v) && v is Error { Code: "Auction.BidTooLow" }, retried.ToString());
        var ok = await pipe1.Run(new PlaceBid(a.Id, "dora", 12m), ctxA);
        var truth = (await store.Load(a.Id, ctxA, default)).GetValueOrThrow();
        c.Check("stale hit: the store holds exactly the two accepted bids (10, 12): a stale hit never committed",
            ok.IsSuccess && truth.Bids == 2 && truth.HighestBid == 12m && truth.Version == 3);
        var p1View = (await p1.Load(a.Id, ctxA, default)).GetValueOrThrow();
        c.Check("write-through: after its own commit process 1's cache holds version 3", p1View.Version == 3 && p1.States.Count == 1);
        var plain2 = await new ChangePipeline(store, Lab.Time).Run(new PlaceBid(a.Id, "erin", 20m), ctxA);   // another process, no cache
        var staleView = (await p1.Load(a.Id, ctxA, default)).GetValueOrThrow();
        c.Check("found: a view (GET by id) through the aggregate cache answers a stale version until the TTL, with an ETag of that version",
            plain2.IsSuccess && staleView.Version == 3 && (await store.Load(a.Id, ctxA, default)).GetValueOrThrow().Version == 4);

        // version-keyed: one round trip for the version, then the entry (tenant, type, id, version) is immutable
        var version = await Lab.Scalar<long>($"SELECT version FROM {schema}.streams WHERE tenant = $1 AND type = 'auction' AND id = $2", Lab.TenantA, a.Id.Value);
        c.Check("version-keyed: the version probe sees version 4, so an entry for version 3 is never served", version == 4);

        // TimeProvider-driven expiry
        var fake = new FakeTimeProvider(now);
        var timed = new CachingStore(store, codec, fake, TimeSpan.FromSeconds(30));
        _ = await timed.Load(a.Id, ctxA, default); _ = await timed.Load(a.Id, ctxA, default);
        fake.Advance(TimeSpan.FromSeconds(31));
        _ = await timed.Load(a.Id, ctxA, default);
        c.Check("expiry follows the TimeProvider (FakeTimeProvider +31 s on a 30 s TTL: miss)", timed.States.Hits == 1 && timed.States.Misses == 2);

        // ── (c) lookups: a cached Find can point at the wrong aggregate ────────────────────────────────────
        var finder = new CachingStore(store, codec, Lab.Time, ttl, cacheFind: true);
        var x = Auction.Open("sue", "vase", "L7", now.AddHours(2), now);
        await Lab.Commit(store, x, 0, ctxA);
        var cachedX = (await finder.Find<Auction>("lot", "L7", ctxA, default)).GetValueOrThrow();
        var xs = (await store.Load(x.Id, ctxA, default)).GetValueOrThrow();
        var xv = xs.Version; _ = xs.Withdraw(); await Lab.Commit(store, xs, xv, ctxA);              // the lot is released …
        var y = Auction.Open("sue", "vase (relisted)", "L7", now.AddHours(3), now);
        await Lab.Commit(store, y, 0, ctxA);                                           // … and claimed by another auction
        var staleFind = (await finder.Find<Auction>("lot", "L7", ctxA, default)).GetValueOrThrow();
        var realFind = (await store.Find<Auction>("lot", "L7", ctxA, default)).GetValueOrThrow();
        var onStale = (await store.Load(staleFind, ctxA, default)).GetValueOrThrow();
        var decided = onStale.Bid("bob", 3m);
        c.Check("found: a cached Find returns the old holder of a released lookup; the decision runs on the wrong aggregate and no version guard sees it",
            staleFind == cachedX && staleFind != realFind && realFind == y.Id && decided.IsFailure, decided.ToString());

        // ── (a) list pages ───────────────────────────────────────────────────────────────────────────────
        var readModels = Lab.ReadModels(schema);
        var rows = readModels.Store<AuctionRow>();
        var generations = new TenantGenerations();
        var tagging = new TaggingStore(rows, generations);
        await using var signal = new NpgsqlFeedSignal(Lab.Db, store.FeedChannel);
        var host = new ProjectionHost<AuctionRow>(store, tagging, new ProjectionOptions { Batch = 500 }, signal: signal);
        var auctions = new List<Auction>();
        for (var i = 0; i < 20; i++)
        {
            var au = Auction.Open(i % 2 == 0 ? "sue" : "sam", $"item {i}", $"A{i}", now.AddMinutes(10 + i), now);
            auctions.Add(au);
            await Lab.Commit(store, au, 0, ctxA);
        }
        var bAuction = Auction.Open("bea", "b item", "B1", now.AddMinutes(5), now);
        var lastB = await Lab.Commit(store, bAuction, 0, ctxB);
        await host.StartAsync(default);
        c.Check("projection caught up", await host.WaitFor(lastB, TimeSpan.FromSeconds(30)));
        await Lab.CheckpointTrigger(schema);
        await using var watch = new CheckpointWatch(Lab.Db, schema, rows, TimeSpan.FromSeconds(1));

        var q = new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 5 };
        var byCheckpoint = new PageCache(rows, Validation.Checkpoint, Lab.Time, ttl);
        var pushed = new PageCache(rows, Validation.Pushed, Lab.Time, ttl, watch);
        var tagged = new PageCache(rows, Validation.TenantTag, Lab.Time, ttl, tenants: generations);
        var fakeTtl = new FakeTimeProvider(now);
        var byTtl = new PageCache(rows, Validation.Ttl, fakeTtl, TimeSpan.FromSeconds(10));
        PageCache[] all = [byCheckpoint, pushed, tagged, byTtl];
        foreach (var pc in all) { _ = await pc.Get(q); }
        await Task.Delay(300);                                                          // the watch has its first notification-free state
        var second = new List<bool>();
        foreach (var pc in all) second.Add((await pc.Get(q)).Hit);
        c.Check("page cache: the second request is a hit (checkpoint, pushed, tenant tag, TTL)", second.All(h => h), string.Join(",", second));
        c.Check("page cache: tenant in the key (same query, other tenant: other key)",
            PageCache.Key(q) != PageCache.Key(q with { Tenant = Lab.TenantB }));

        // a commit in tenant B: checkpoint-validated entries of tenant A miss, the tenant-tagged one still hits
        var bb = bAuction; _ = bb.Bid("x", 1m); var atB = await Lab.Commit(store, bb, 1, ctxB);
        await host.WaitFor(atB, TimeSpan.FromSeconds(30));
        await WaitUntil(() => watch.Position >= atB);
        var afterB = new List<bool>();
        foreach (var pc in all) afterB.Add((await pc.Get(q)).Hit);
        c.Check("a commit of tenant B: checkpoint and pushed entries of tenant A miss, the tenant tag and the TTL still hit",
            afterB is [false, false, true, true], string.Join(",", afterB));

        // a bid on page 1 of tenant A
        var first = auctions[0]; _ = first.Bid("bob", 42m); var atA = await Lab.Commit(store, first, 1, ctxA);
        await host.WaitFor(atA, TimeSpan.FromSeconds(30));
        await WaitUntil(() => watch.Position >= atA);
        var fresh = (await rows.Search(q, default)).GetValueOrThrow();
        var answers = new List<PageCache.Answer>();
        foreach (var pc in all) answers.Add(await pc.Get(q));
        string Body(byte[] b) => System.Text.Encoding.UTF8.GetString(b);
        c.Check("a bid on page 1: checkpoint, pushed and tenant-tag entries miss and show the bid",
            answers.Take(3).All(x => !x.Hit && x.ETag == fresh.ETag && Body(x.Body).Contains("\"highestBid\":42", StringComparison.Ordinal)));
        c.Check("found: the TTL entry still answers the page without the bid (stale until the TTL)", answers[3].Hit && answers[3].ETag != fresh.ETag);
        fakeTtl.Advance(TimeSpan.FromSeconds(11));
        c.Check("the TTL entry expires on the TimeProvider (+11 s on 10 s)", !(await byTtl.Get(q)).Hit);

        // an open transaction elsewhere holds the checkpoint: the cache never shows less than the uncached path
        await using (var open = await Lab.Db.OpenConnectionAsync())
        {
            await using var tx = await open.BeginTransactionAsync();
            await using (var xid = new NpgsqlCommand("SELECT pg_current_xact_id()", open, tx)) await xid.ExecuteScalarAsync();
            var second2 = auctions[1]; _ = second2.Bid("bob", 77m); var held = await Lab.Commit(store, second2, 1, ctxA);
            var passed = await host.WaitFor(held, TimeSpan.FromSeconds(2));
            var uncached = (await rows.Search(q, default)).GetValueOrThrow();
            var cachedNow = await byCheckpoint.Get(q);
            c.Check("open transaction elsewhere: the projection does not pass the commit; cached page = uncached page (both without the bid)",
                !passed && cachedNow.ETag == uncached.ETag && !Body(cachedNow.Body).Contains("\"highestBid\":77", StringComparison.Ordinal));
            await tx.RollbackAsync();
            c.Check("open transaction ended: the projection passes, the cached page misses and shows the bid",
                await host.WaitFor(held, TimeSpan.FromSeconds(30)) && Body((await byCheckpoint.Get(q)).Body).Contains("\"highestBid\":77", StringComparison.Ordinal));
        }
        c.Check("the checkpoint watch received notifications (no reliance on the fallback poll)", watch.Notifications > 0, $"notifications {watch.Notifications}, polls {watch.Polls}");
        await host.StopAsync(default);
        return c.Done();
    }

    public static async Task WaitUntil(Func<bool> condition, int maxMs = 10_000)
    {
        for (var i = 0; i < maxMs / 5 && !condition(); i++) await Task.Delay(5);
    }
}
