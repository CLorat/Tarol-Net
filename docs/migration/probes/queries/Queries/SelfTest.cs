using Npgsql;
using Tarol.Persistence;
using static Probe.Queries.Lab;

namespace Probe.Queries;

/// <summary>Functional checks of the candidates: memory (the ProjectionTest shape) and PostgreSQL (the production shape).</summary>
internal static class SelfTest
{
    public static async Task<int> Run(string kind)
    {
        var t = new Checks($"selftest {kind}, {Runtime}");
        var postgres = kind == "postgres";
        Console.WriteLine($"# selftest {kind} ({Runtime})");
        var schema = "q_selftest";
        IAggregateStore store;
        IEventFeed feed;
        if (postgres) { var pg = await Store(schema); (store, feed) = (pg, pg); }
        else { var mem = new InMemoryAggregateStore(Time, new InMemoryStoreOptions { Codec = Codec() }); (store, feed) = (mem, mem); }
        IReadModelStore<AuctionRow> Table(string name = "auction-list", bool tx = true)
            => postgres ? new NpgsqlAuctionTable(Db, schema, name, 1) { CheckpointInTransaction = tx } : new InMemoryAuctionTable();

        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (ca, cb) = (Context(a), Context(b, "bob"));
        var now = Time.GetUtcNow();
        var auctions = new List<Auction>();
        GlobalPosition last = default;
        for (var i = 0; i < 23; i++)
        {
            var x = Auction.Open(i % 3 == 0 ? "sam" : "sue", $"lot {i}", now.AddHours(i % 7).AddMinutes(i), now);
            last = await Commit(store, x, 0, ca);
            auctions.Add(x);
        }
        for (var i = 0; i < 23; i++)
            for (var k = 1; k <= i % 4; k++)
                last = await Do(store, auctions[i], ca, x => x.Bid("bidder", 10m * k + i));
        for (var i = 0; i < 23; i += 5)
            last = await Do(store, auctions[i], ca, x => x.Close());
        var other = Auction.Open("bea", "tenant b", now, now);
        await Commit(store, other, 0, cb);
        last = await Commit(store, Lot.Register("ignored", now), 0, ca);
        if (!postgres) last = await LastPosition(feed);

        var table = Table();
        await table.Ensure(default);
        var queries = (IAuctionQueries)table;
        var host = new ProjectionHost<AuctionRow>(feed, new AuctionList(), table, new() { Batch = 7 });
        var read = await host.CatchUp();
        var all = await Collect(queries, a, new AuctionSearch(Limit: 5));
        t.Check("projection from the feed: one row per auction of tenant A, other stream types ignored", all.Count == 23, $"feed events {read}, rows {all.Count}");
        t.Check("rows equal the aggregates (bids, highest, status)", auctions.All(x => all.Any(r => r.Id == x.Id.Value && r.Bids == x.Bids && r.HighestBid == x.HighestBid && r.Status == x.Status)));
        var bRows = await Collect(queries, b, new AuctionSearch());
        t.Check("tenant isolation: tenant B sees only its row", bRows.Count == 1 && bRows[0].Title == "tenant b");
        t.Check("cursor pages (5 per page) = the full ordered list, no duplicate", all.Select(r => r.Id).Distinct().Count() == 23
            && all.SequenceEqual(all.OrderBy(r => r.EndsAt).ThenBy(r => r.Id)));
        var byOffset = new List<AuctionRow>();
        for (var o = 0; o < 23; o += 5) byOffset.AddRange((await queries.Search(a, new AuctionSearch(Offset: o, Limit: 5), default)).Items);
        t.Check("offset pages = cursor pages while nothing changes", byOffset.SequenceEqual(all));
        var open = await Collect(queries, a, new AuctionSearch(Status: AuctionStatus.Open, Sort: AuctionSort.HighestBidDesc, Limit: 4));
        t.Check("filter + descending sort with id tie-break", open.Count == auctions.Count(x => x.Status == AuctionStatus.Open)
            && open.SequenceEqual(open.OrderByDescending(r => r.HighestBid).ThenByDescending(r => r.Id)));
        var p1 = await queries.Search(a, new AuctionSearch(Limit: 5), default);
        var rejected = false;
        try { await queries.Search(a, new AuctionSearch(Sort: AuctionSort.HighestBidDesc, After: p1.Next), default); } catch (ArgumentException) { rejected = true; }
        var rejected2 = false;
        try { await queries.Search(a, new AuctionSearch(Seller: "sam", After: p1.Next), default); } catch (ArgumentException) { rejected2 = true; }
        t.Check("a cursor is bound to its sort and filters (another sort or filter is refused, never a wrong page)", rejected && rejected2);
        var again = await queries.Search(a, new AuctionSearch(Limit: 5), default);
        t.Check("page ETag stable while the page's rows do not change", again.ETag == p1.ETag);
        var first = auctions.First(x => x.Id.Value == p1.Items[0].Id);
        if (first.Status == AuctionStatus.Open)
            last = await Do(store, first, ca, x => x.Bid("late", 1000m));
        else { var x2 = auctions.First(x => x.Status == AuctionStatus.Open && p1.Items.Any(r => r.Id == x.Id.Value)); last = await Do(store, x2, ca, x => x.Bid("late", 1000m)); }
        if (!postgres) last = await LastPosition(feed);
        var waited = await host.WaitFor(last, TimeSpan.FromMilliseconds(200));
        t.Check("read-your-writes: WaitFor(commit position) times out while no host runs", !waited);
        using (var stop = new CancellationTokenSource())
        {
            var run = host.Run(stop.Token);
            var ok = await host.WaitFor(last, TimeSpan.FromSeconds(5));
            await stop.CancelAsync(); await run;
            t.Check("read-your-writes: WaitFor returns once the host passed the commit's position", ok);
        }
        var changed = await queries.Search(a, new AuctionSearch(Limit: 5), default);
        t.Check("page ETag changes when a row of the page changes", changed.ETag != p1.ETag);

        // idempotence: the same events applied again change nothing (per-stream version guard)
        var replay = Table("replay", tx: false);
        await replay.Ensure(default);
        var replayHost = new ProjectionHost<AuctionRow>(feed, new AuctionList(), replay, new() { Batch = 11 });
        await replayHost.CatchUp();
        if (postgres) await Exec($"UPDATE {schema}.projections SET tx = '0', pos = 0 WHERE name = 'replay' AND version = 1");
        var before = await Collect((IAuctionQueries)replay, a, new AuctionSearch(Limit: 200));
        if (postgres)
        {
            var replayAgain = new ProjectionHost<AuctionRow>(feed, new AuctionList(), replay, new() { Batch = 11 });
            await replayAgain.CatchUp();
        }
        var after = await Collect((IAuctionQueries)replay, a, new AuctionSearch(Limit: 200));
        t.Check("replay from an older checkpoint is idempotent (row version guard; A2: rows and checkpoint in separate statements): bids unchanged", before.SequenceEqual(after) && after.Sum(r => r.Bids) == auctions.Sum(x => x.Bids));

        if (postgres)
            await PostgresOnly(t, schema, store, feed, ca, auctions);
        return t.Done();
    }

    private static async Task PostgresOnly(Checks t, string schema, IAggregateStore store, IEventFeed feed, Tarol.Kernel.OperationContext ca, List<Auction> auctions)
    {
        var a = ca.Tenant.GetValueOrThrow().Value;
        // crash between compute and write: nothing written, the checkpoint unchanged, the next run applies once
        var crashT = new NpgsqlAuctionTable(Db, schema, "crash", 1);
        await crashT.Ensure(default);
        var crash = new ProjectionHost<AuctionRow>(feed, new AuctionList(), crashT, new() { Batch = 10 }) { CrashBeforeWrite = n => n > 0 };
        var threw = false;
        try { await crash.RunOnce(); } catch (InvalidOperationException) { threw = true; }
        var rowsAfterCrash = await Scalar<long>($"SELECT count(*) FROM {crashT.Table}");
        var resumed = new ProjectionHost<AuctionRow>(feed, new AuctionList(), crashT, new() { Batch = 10 });
        await resumed.CatchUp();
        var resumedRows = await Collect(crashT, a, new AuctionSearch(Limit: 200));
        t.Check("crash before the write: no rows, checkpoint unchanged; the restart applies every event once",
            threw && rowsAfterCrash == 0 && resumedRows.Sum(r => r.Bids) == auctions.Sum(x => x.Bids), $"rows after crash {rowsAfterCrash}");

        // two runners of the same projection: the guarded checkpoint serializes them, no event is applied twice
        var twoT = new NpgsqlAuctionTable(Db, schema, "two", 1);
        await twoT.Ensure(default);
        var h1 = new ProjectionHost<AuctionRow>(feed, new AuctionList(), twoT, new() { Batch = 3 });
        var h2 = new ProjectionHost<AuctionRow>(feed, new AuctionList(), twoT, new() { Batch = 3 });
        await Task.WhenAll(Task.Run(() => h1.CatchUp()), Task.Run(() => h2.CatchUp()));
        await h1.CatchUp(); await h2.CatchUp();
        var two = await Collect(twoT, a, new AuctionSearch(Limit: 200));
        t.Check("two runners at once: exact rows (checkpoint guard + version guard)", two.Sum(r => r.Bids) == auctions.Sum(x => x.Bids) && two.Count == 23,
            $"conflicts {h1.Conflicts + h2.Conflicts}, applied {h1.Applied}+{h2.Applied}");

        // offset under a concurrent insert: page 2 repeats a row of page 1; the cursor does not
        var main = new NpgsqlAuctionTable(Db, schema, "auction-list", 1);
        var mainHost = new ProjectionHost<AuctionRow>(feed, new AuctionList(), main);
        await mainHost.CatchUp();
        var page1 = await main.Search(a, new AuctionSearch(Limit: 5), default);
        var early = Auction.Open("sam", "early bird", Time.GetUtcNow().AddDays(-1), Time.GetUtcNow());
        await Commit(store, early, 0, ca);
        await mainHost.CatchUp();
        var off2 = await main.Search(a, new AuctionSearch(Offset: 5, Limit: 5), default);
        var cur2 = await main.Search(a, new AuctionSearch(After: page1.Next, Limit: 5), default);
        t.Check("offset under an insert before the page: page 2 repeats the last row of page 1 (found)", off2.Items[0].Id == page1.Items[^1].Id);
        t.Check("cursor under the same insert: page 2 continues after page 1, no repeat, no gap", cur2.Items.All(r => page1.Items.All(p => p.Id != r.Id))
            && cur2.Items[0].EndsAt >= page1.Items[^1].EndsAt);

        // rebuild: v2 builds into its own table from the start while v1 serves; switching is choosing the table
        var v2 = new NpgsqlAuctionTable(Db, schema, "auction-list", 2);
        await v2.Ensure(default);
        var v1Before = await Collect(main, a, new AuctionSearch(Limit: 200));
        var v2Host = new ProjectionHost<AuctionRow>(feed, new AuctionList(2), v2, new() { Batch = 13 });
        await v2Host.CatchUp();
        var v2Rows = await Collect(v2, a, new AuctionSearch(Limit: 200));
        t.Check("rebuild as a new version: v2 built from the start equals v1, v1 served unchanged meanwhile", v2Rows.SequenceEqual(v1Before) && v2Host.Position == mainHost.Position);

        // a typed column cannot be generated from the jsonb document when the cast is not immutable
        string? genError = null;
        try { await Exec($"CREATE TABLE {schema}.gen (doc jsonb, ends_at timestamptz GENERATED ALWAYS AS ((doc->>'endsAt')::timestamptz) STORED)"); }
        catch (PostgresException e) { genError = $"{e.SqlState} {e.MessageText}"; }
        t.Check("found: a timestamptz column cannot be GENERATED from jsonb (not immutable): the host writes typed columns", genError?.StartsWith("42P17", StringComparison.Ordinal) == true, genError);

        // xmin: an open transaction anywhere in the cluster holds the feed back (persistence.md §9), so it holds every projection back
        await using (var blocker = await Db.OpenConnectionAsync())
        {
            await using (var begin = new NpgsqlCommand("BEGIN; SELECT pg_current_xact_id();", blocker)) await begin.ExecuteNonQueryAsync();
            var bidder = auctions.First(x => x.Status == AuctionStatus.Open);
            var token = await Do(store, bidder, ca, x => x.Bid("held", 5000m));
            await mainHost.CatchUp();
            var held = mainHost.Position < token;
            await using (var end = new NpgsqlCommand("COMMIT", blocker)) await end.ExecuteNonQueryAsync();
            await mainHost.CatchUp();
            t.Check("an open transaction elsewhere holds the projection back until it ends", held && mainHost.Position >= token);

            // read-your-writes across processes: the API polls the checkpoint row (no in-process signal)
            var token2 = await Do(store, bidder, ca, x => x.Bid("ryw", 6000m));
            using var stop = new CancellationTokenSource();
            var run = mainHost.Run(stop.Token);
            var seen = await PollCheckpoint(schema, "auction-list", 1, token2, TimeSpan.FromSeconds(5));
            await stop.CancelAsync(); await run;
            var row = (await main.Search(a, new AuctionSearch(Status: AuctionStatus.Open, Sort: AuctionSort.HighestBidDesc, Limit: 1), default)).Items[0];
            t.Check("read-your-writes across processes: polling the checkpoint row ≥ the commit's position, then the query sees the bid", seen && row.HighestBid == 6000m);
        }

        // the stored state answers simple lists without a projection, but its paths are the aggregate's internals
        var state = await Scalar<string>($"SELECT state::text FROM {schema}.streams WHERE type = 'auction' LIMIT 1");
        var openByState = await Scalar<long>($"SELECT count(*) FROM {schema}.streams WHERE tenant = $1 AND type = 'auction' AND state->>'status' = 'Open'", a);
        var misspelled = await Scalar<long>($"SELECT count(*) FROM {schema}.streams WHERE tenant = $1 AND type = 'auction' AND state->>'Status' IS NOT NULL", a);
        var openRows = (await Collect(main, a, new AuctionSearch(Status: AuctionStatus.Open, Limit: 200))).Count;
        t.Check("stored state: the open count matches the projection; a wrong path is silently empty (no error)", openByState == openRows && misspelled == 0,
            $"state {state}");
    }

    public static async Task<bool> PollCheckpoint(string schema, string name, int version, GlobalPosition token, TimeSpan timeout)
    {
        var table = new NpgsqlAuctionTable(Db, schema, name, version);
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (await table.Checkpoint(default) >= token) return true;
            await Task.Delay(5);
        }
        return false;
    }

    public static async Task<List<AuctionRow>> Collect(IAuctionQueries q, Guid tenant, AuctionSearch search)
    {
        var rows = new List<AuctionRow>();
        var page = await q.Search(tenant, search, default);
        rows.AddRange(page.Items);
        while (page.Next is { } next)
        {
            page = await q.Search(tenant, search with { After = next }, default);
            rows.AddRange(page.Items);
        }
        return rows;
    }

    private static async Task<GlobalPosition> LastPosition(IEventFeed feed)
    {
        GlobalPosition p = default;
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, int.MaxValue, default)) p = e.Position;
        return p;
    }
}
