using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using static Probe.Queries.Lab;

namespace Probe.Queries;

/// <summary>Measurements against PostgreSQL 17: seed, catch-up/rebuild, commit cost per candidate, query latency, lag.</summary>
internal static class Bench
{
    private const string S = "q_bench";
    private static readonly CultureInfo I = CultureInfo.InvariantCulture;
    private static readonly string[] Project = ["project:auction-list"];

    public static async Task<int> Run(string[] args) => args switch
    {
        ["seed", var n, var copies] => await Seed(int.Parse(n, I), int.Parse(copies, I)),
        ["catchup"] => await CatchUp(),
        ["rebuild"] => await Rebuild(),
        ["commit", var n] => await CommitCost(int.Parse(n, I)),
        ["query", var a, var b] => await Query(int.Parse(a, I), int.Parse(b, I)),
        ["state"] => await State(),
        ["lag", var mode, var writers, var seconds] => await Lag(mode, int.Parse(writers, I), int.Parse(seconds, I)),
        ["ryw", var n] => await ReadYourWrites(int.Parse(n, I)),
        _ => 2,
    };

    private static async Task<(GlobalPosition Token, long Ticks)> Timed(NpgsqlAggregateStore store, Auction a, Tarol.Kernel.OperationContext c, Func<Auction, Tarol.Results.Result> act)
    {
        var expected = a.Version;
        _ = act(a);
        var r = await store.Append(a, expected, c, Option.None, default);
        var ticks = Stopwatch.GetTimestamp();
        if (!r.IsSuccess) throw new InvalidOperationException(r.ToString());
        Tarol.Primitives.AggregateChanges.Committed(a);
        await using var q = Db.CreateCommand($"SELECT tx_id::text, max(position) FROM {store.Schema}.events WHERE stream_id = $1 AND version > $2 GROUP BY tx_id");
        q.Parameters.Add(new() { Value = a.Id.Value }); q.Parameters.Add(new() { Value = expected });
        await using var rd = await q.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (new GlobalPosition(ulong.Parse(rd.GetString(0), I), rd.GetInt64(1)), ticks);
    }

    // ── seed: N auctions x (open + 8 bids + close) through the store, 8 writers, one commit per event; then SQL copies ──
    private static async Task<int> Seed(int auctions, int copies)
    {
        var store = await Store(S);
        var tenants = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
        var sw = Stopwatch.StartNew();
        long commits = 0;
        await Parallel.ForAsync(0, auctions, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            var c = Context(tenants[i % 4]);
            var now = Time.GetUtcNow();
            var a = Auction.Open($"seller{i % 500}", $"lot {i}", now.AddMinutes(Random.Shared.Next(1, 500_000)), now);
            await Commit(store, a, 0, c);
            for (var k = 1; k <= 8; k++) await Do(store, a, c, x => x.Bid($"b{k}", 10m * k + Random.Shared.Next(0, 9)));
            await Do(store, a, c, x => x.Close());
            Interlocked.Add(ref commits, 10);
        });
        Console.WriteLine(string.Create(I, $"INFO seed: {commits} commits (one event each) by 8 writers in {sw.Elapsed.TotalSeconds:F1} s = {commits / sw.Elapsed.TotalSeconds:F0} commits/s"));
        sw.Restart();
        await Exec($"CREATE TABLE {S}.originals AS SELECT max(position) AS p FROM {S}.events; CREATE TABLE {S}.original_streams AS SELECT tenant, type, id FROM {S}.streams");
        for (var k = 1; k < copies; k++)
            await Exec($"""
                INSERT INTO {S}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, parent_id, payload, recorded_at,
                    actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent)
                SELECT tenant, type, md5(stream_id::text || '{k}')::uuid, version, gen_random_uuid(), alias, alias_version,
                    CASE WHEN entity_id = stream_id THEN md5(stream_id::text || '{k}')::uuid ELSE entity_id END, parent_id, payload, recorded_at,
                    actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent
                FROM {S}.events WHERE position <= (SELECT p FROM {S}.originals) ORDER BY position;
                INSERT INTO {S}.streams SELECT s.tenant, s.type, md5(s.id::text || '{k}')::uuid, s.version, s.schema,
                    jsonb_set(s.state, ARRAY['id'], to_jsonb(md5(s.id::text || '{k}')::uuid::text)), s.updated_at
                FROM {S}.streams s JOIN {S}.original_streams o USING (tenant, type, id);
                """);
        await Exec($"ANALYZE {S}.events; ANALYZE {S}.streams");
        var events = await Scalar<long>($"SELECT count(*) FROM {S}.events");
        var aliases = await Scalar<string>($"SELECT string_agg(DISTINCT alias, ', ') FROM {S}.events");
        var size = await Scalar<string>($"SELECT pg_size_pretty(pg_total_relation_size('{S}.events'))");
        Console.WriteLine(string.Create(I, $"INFO seed: copied to {events} events ({copies} x) in {sw.Elapsed.TotalSeconds:F1} s; aliases {aliases}; events table {size}"));
        return 0;
    }

    private static NpgsqlAggregateStore Existing(string schema = S) => new(Db, Codec(), Time, new NpgsqlStoreOptions { Schema = schema });

    // ── catch-up from the start: 1e6 events; A1 (checkpoint in the batch transaction) vs A2 (separate), batch sizes ──
    private static async Task<int> CatchUp()
    {
        var store = Existing();
        var events = await Scalar<long>($"SELECT count(*) FROM {S}.events");
        foreach (var (name, batch, tx) in new[] { ("cu_a1_500", 500, true), ("cu_a1_2000", 2000, true), ("cu_a1_5000", 5000, true), ("cu_a2_2000", 2000, false) })
        {
            var table = new NpgsqlAuctionTable(Db, S, name, 1) { CheckpointInTransaction = tx };
            await Exec($"DROP TABLE IF EXISTS {table.Table}; DELETE FROM {S}.projections WHERE name = '{name}'", null).ContinueWith(_ => { });
            await table.Ensure(default);
            var host = new ProjectionHost<AuctionRow>(store, new AuctionList(), table, new() { Batch = batch });
            var sw = Stopwatch.StartNew();
            var read = await host.CatchUp();
            var s = sw.Elapsed.TotalSeconds;
            var rows = await Scalar<long>($"SELECT count(*) FROM {table.Table}");
            var size = await Scalar<string>($"SELECT pg_size_pretty(pg_total_relation_size('{table.Table}'))");
            Console.WriteLine(string.Create(I, $"INFO catch-up {(tx ? "A1" : "A2")} batch {batch}: {read} events in {s:F1} s = {read / s:F0} events/s, {rows} rows, table {size} (feed {events})"));
            if (name != "cu_a1_2000") await Exec($"DROP TABLE {table.Table}");
        }
        // the feed alone (read + decode), to split the cost
        var sw2 = Stopwatch.StartNew();
        long n = 0; var p = GlobalPosition.Start;
        while (true)
        {
            var got = 0;
            await foreach (var e in store.ReadAll(p, 2000, default)) { p = e.Position; got++; }
            if (got == 0) break;
            n += got;
        }
        Console.WriteLine(string.Create(I, $"INFO feed alone (ReadAll 2000, decode): {n} events in {sw2.Elapsed.TotalSeconds:F1} s = {n / sw2.Elapsed.TotalSeconds:F0} events/s"));
        return 0;
    }

    // ── rebuild: truncate + replay (readers see a partial table) vs a new version + switch (rename swap under query load) ──
    private static async Task<int> Rebuild()
    {
        var store = Existing();
        var v1 = new NpgsqlAuctionTable(Db, S, "cu_a1_2000", 1);
        var tenant = await Scalar<Guid>($"SELECT tenant FROM {v1.Table} GROUP BY tenant ORDER BY count(*) DESC LIMIT 1");
        var full = await Scalar<long>($"SELECT count(*) FROM {v1.Table}");
        // truncate + replay, sampled by a reader
        var copy = new NpgsqlAuctionTable(Db, S, "rb_truncate", 1);
        await Exec($"DROP TABLE IF EXISTS {copy.Table}; DELETE FROM {S}.projections WHERE name = 'rb_truncate'");
        await copy.Ensure(default);
        var host = new ProjectionHost<AuctionRow>(store, new AuctionList(), copy, new() { Batch = 2000 });
        using var stop = new CancellationTokenSource();
        var samples = new List<long>();
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) { samples.Add(await Scalar<long>($"SELECT count(*) FROM {copy.Table}")); await Task.Delay(500); }
        });
        var sw = Stopwatch.StartNew();
        await host.CatchUp();
        var took = sw.Elapsed.TotalSeconds;
        await stop.CancelAsync(); await reader;
        Console.WriteLine(string.Create(I, $"INFO rebuild by truncate + replay: {took:F1} s; a reader saw {samples.Count} partial counts, e.g. {string.Join(", ", samples.Take(6))} … of {full}"));
        // swap by rename while 4 readers query page 1 of v1's name
        var lat = new List<double>();
        using var stop2 = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var q = new NpgsqlAuctionTable(Db, S, "cu_a1_2000", 1);
            while (!stop2.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                try { await q.Search(tenant, new AuctionSearch(Status: AuctionStatus.Closed), default); } catch (PostgresException) { }
                lock (lat) lat.Add(Ms(Stopwatch.GetTimestamp() - t0));
            }
        })).ToArray();
        await Task.Delay(1000);
        var t1 = Stopwatch.GetTimestamp();
        await Exec($"BEGIN; ALTER TABLE {v1.Table} RENAME TO rm_old; ALTER TABLE {copy.Table} RENAME TO {v1.Table[(S.Length + 1)..]}; ALTER TABLE {S}.rm_old RENAME TO {copy.Table[(S.Length + 1)..]}; COMMIT");
        var swap = Ms(Stopwatch.GetTimestamp() - t1);
        await Task.Delay(1000);
        await stop2.CancelAsync(); await Task.WhenAll(readers);
        Console.WriteLine(string.Create(I, $"INFO swap by rename (3 renames in one transaction) under 4 query loops: {swap:F1} ms; queries meanwhile {Pct(lat)}"));
        return 0;
    }

    // ── commit cost per candidate: baseline, +outbox row per event (P-B), +inline trigger upsert (P-C), +NOTIFY trigger ──
    private static async Task<int> CommitCost(int n)
    {
        var tenant = Guid.CreateVersion7();
        var c = Context(tenant);
        foreach (var kind in new[] { "baseline", "outbox-row", "inline-trigger", "notify-trigger", "baseline" })
        {
            var schema = "q_commit";
            var store = await Store(schema, notify: kind == "notify-trigger",
                reactions: kind == "outbox-row" ? OutboxReactions.From(e => e is Auction.BidPlaced or Auction.Opened or Auction.Closed ? Project : ReadOnlySpan<string>.Empty) : null);
            if (kind == "inline-trigger")
                await Exec($"""
                    CREATE TABLE {schema}.rm_inline (tenant uuid, id uuid, version bigint, doc jsonb, PRIMARY KEY (tenant, id));
                    CREATE FUNCTION {schema}.project() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                      INSERT INTO {schema}.rm_inline VALUES (NEW.tenant, NEW.stream_id, NEW.version, NEW.payload)
                      ON CONFLICT (tenant, id) DO UPDATE SET version = excluded.version, doc = {schema}.rm_inline.doc || excluded.doc;
                      RETURN NULL; END $$;
                    CREATE TRIGGER project AFTER INSERT ON {schema}.events FOR EACH ROW EXECUTE FUNCTION {schema}.project();
                    """);
            var now = Time.GetUtcNow();
            var auctions = new List<Auction>();
            for (var i = 0; i < 200; i++) { var a = Auction.Open("s", "t", now.AddDays(1), now); await Commit(store, a, 0, c); auctions.Add(a); }
            var lat = new List<double>();
            for (var i = 0; i < n; i++)
            {
                var a = auctions[i % 200];
                var expected = a.Version;
                _ = a.Bid("b", a.HighestBid + 1);
                var t0 = Stopwatch.GetTimestamp();
                var r = await store.Append(a, expected, c, Option.None, default);
                lat.Add(Ms(Stopwatch.GetTimestamp() - t0));
                if (!r.IsSuccess) throw new InvalidOperationException(r.ToString());
                Tarol.Primitives.AggregateChanges.Committed(a);
            }
            Console.WriteLine($"INFO commit {kind}: {Pct(lat)}");
            if (kind == "outbox-row")
            {
                // the reaction worker's loop for projection rows: claim 100, upsert the row, complete each row
                var table = new NpgsqlAuctionTable(Db, schema, "outbox", 1);
                await table.Ensure(default);
                var pending = await store.Outbox.Pending(default);
                var sw = Stopwatch.StartNew();
                long done = 0;
                while (true)
                {
                    var rows = await store.Outbox.Claim(100, TimeSpan.FromMinutes(1), default);
                    if (rows.Count == 0) break;
                    foreach (var row in rows)
                    {
                        var key = (ProjectionHost<AuctionRow>.Tenant(row.Event), row.Event.Event.StreamId);
                        var current = (await table.Load([key], default)).GetValueOrDefault(key);
                        var next = new AuctionList().Apply(current?.Row, row.Event);
                        var cp = await table.Checkpoint(default);
                        if (next is not null) await table.Write([new(key.Item1, key.Item2, row.Event.Event.Event.Version, next)], cp, cp, default);
                        await store.Outbox.Complete(row, default);
                        done++;
                    }
                }
                Console.WriteLine(string.Create(I, $"INFO outbox projection rows (claim 100, load + upsert + complete per row, one loop): {done} of {pending} rows in {sw.Elapsed.TotalSeconds:F1} s = {done / sw.Elapsed.TotalSeconds:F0} rows/s"));
            }
        }
        return 0;
    }

    // ── query latency: offset vs cursor at depth, 1e6 rows of tenant A + 1e5 of tenant B in the same table ──
    private static async Task<int> Query(int big, int small)
    {
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var t = new NpgsqlAuctionTable(Db, S, "q-rows", 1);
        await Exec($"DROP TABLE IF EXISTS {t.Table}; DELETE FROM {S}.projections WHERE name = 'q-rows'");
        await t.Ensure(default);
        var sw = Stopwatch.StartNew();
        foreach (var (tenant, n) in new[] { (a, big), (b, small) })
            await Exec($"""
                INSERT INTO {t.Table}
                SELECT '{tenant}'::uuid, id, 3, 'lot ' || i, 'seller' || (i % 1000), ends, (i % 2)::smallint, hb, i % 20,
                       jsonb_build_object('id', id, 'title', 'lot ' || i, 'seller', 'seller' || (i % 1000), 'endsAt', ends, 'status', i % 2, 'highestBid', hb, 'bids', i % 20)
                FROM (SELECT i, gen_random_uuid() AS id, timestamptz '2026-01-01' + (random() * 365 * 86400) * interval '1 second' AS ends,
                             round((random() * 10000)::numeric, 2) AS hb FROM generate_series(1, {n}) i) x
                """);
        await Exec($"VACUUM ANALYZE {t.Table}");
        var size = await Scalar<string>($"SELECT pg_size_pretty(pg_total_relation_size('{t.Table}'))");
        Console.WriteLine(string.Create(I, $"INFO query table: {big} rows (tenant A) + {small} (tenant B) in {sw.Elapsed.TotalSeconds:F1} s, {size} with 4 indexes"));
        Console.WriteLine("| tenant rows | query | depth | offset | cursor |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var (tenant, n) in new[] { (a, big), (b, small) })
            foreach (var (label, q) in new[] { ("all, endsAt", new AuctionSearch(Limit: 50)), ("status=Closed, endsAt", new AuctionSearch(Status: AuctionStatus.Closed, Limit: 50)),
                                              ("all, highestBid desc", new AuctionSearch(Sort: AuctionSort.HighestBidDesc, Limit: 50)) })
            {
                var max = label.StartsWith("status", StringComparison.Ordinal) ? n / 2 : n;
                foreach (var depth in new[] { 0, 1_000, 10_000, 100_000, 500_000, 900_000 }.Where(d => d < max))
                {
                    var reps = depth >= 100_000 ? 20 : 100;
                    var off = new List<double>();
                    for (var i = 0; i < reps + 3; i++)
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        var page = await t.Search(tenant, q with { Offset = depth }, default);
                        if (i >= 3) off.Add(Ms(Stopwatch.GetTimestamp() - t0));
                        if (page.Items.Count != 50) throw new InvalidOperationException("short page");
                    }
                    var cursor = depth == 0 ? null : await CursorAt(t, tenant, q, depth);
                    var cur = new List<double>();
                    for (var i = 0; i < reps + 3; i++)
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        var page = await t.Search(tenant, q with { After = cursor }, default);
                        if (i >= 3) cur.Add(Ms(Stopwatch.GetTimestamp() - t0));
                        if (page.Items.Count != 50) throw new InvalidOperationException("short page");
                    }
                    Console.WriteLine($"| {n:N0} | {label} | {depth:N0} | {P50P99(off)} | {P50P99(cur)} |".Replace(",", " ", StringComparison.Ordinal).Replace("  ", ", ", StringComparison.Ordinal));
                }
            }
        foreach (var (tenant, n) in new[] { (a, big), (b, small) })
        {
            var cnt = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                _ = await Scalar<long>($"SELECT count(*) FROM {t.Table} WHERE tenant = $1 AND status = 1", tenant);
                if (i > 0) cnt.Add(Ms(Stopwatch.GetTimestamp() - t0));
            }
            Console.WriteLine(string.Create(I, $"INFO total count (status=Closed) of a {n} row tenant: {P50P99(cnt)}"));
        }
        await using (var plan = Db.CreateCommand($"EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) SELECT id, version, doc::text FROM {t.Table} WHERE tenant = $1 ORDER BY ends_at, id LIMIT 51 OFFSET 500000"))
        {
            plan.Parameters.Add(new() { Value = a });
            await using var r = await plan.ExecuteReaderAsync();
            while (await r.ReadAsync()) Console.WriteLine("INFO plan offset 500000: " + r.GetString(0));
        }
        return 0;
    }

    private static string P50P99(List<double> xs)
    {
        xs.Sort();
        return string.Create(I, $"{xs[xs.Count / 2]:F2} / {xs[Math.Min(xs.Count - 1, (int)Math.Ceiling(0.99 * xs.Count) - 1)]:F2} ms");
    }

    private static async Task<string> CursorAt(NpgsqlAuctionTable t, Guid tenant, AuctionSearch q, int depth)
    {
        var page = await t.Search(tenant, q with { Offset = depth - 50, Limit = 50 }, default);
        return page.Next!;
    }

    // ── the stored state as the read model: page 1 of a filtered, sorted list straight from streams.state ──
    private static async Task<int> State()
    {
        var tenant = await Scalar<Guid>($"SELECT tenant FROM {S}.streams GROUP BY tenant ORDER BY count(*) DESC LIMIT 1");
        var n = await Scalar<long>($"SELECT count(*) FROM {S}.streams WHERE tenant = $1", tenant);
        var all = await Scalar<long>($"SELECT count(*) FROM {S}.streams");
        var rm = new NpgsqlAuctionTable(Db, S, "cu_a1_2000", 1);
        string Sql(string extra) => $"SELECT id, version, state::text FROM {S}.streams WHERE tenant = $1 AND type = 'auction' {extra} ORDER BY (state->>'highestBid')::numeric DESC, id DESC LIMIT 51";
        async Task<string> Time(string sql, int reps = 50)
        {
            var xs = new List<double>();
            for (var i = 0; i < reps + 3; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                await using var c = Db.CreateCommand(sql);
                c.Parameters.Add(new() { Value = tenant });
                await using (var r = await c.ExecuteReaderAsync()) while (await r.ReadAsync()) { }
                if (i >= 3) xs.Add(Ms(Stopwatch.GetTimestamp() - t0));
            }
            return P50P99(xs);
        }
        Console.WriteLine(string.Create(I, $"INFO stored state: tenant with {n} of {all} streams"));
        Console.WriteLine($"INFO state, highestBid desc, no index: {await Time(Sql(""), 10)}");
        await Exec($"CREATE INDEX IF NOT EXISTS streams_hb ON {S}.streams (tenant, type, ((state->>'highestBid')::numeric) DESC, id DESC)");
        await Exec($"ANALYZE {S}.streams");
        Console.WriteLine($"INFO state, highestBid desc, expression index: {await Time(Sql(""))}");
        Console.WriteLine($"INFO projection table, highestBid desc, SQL only: {await Time($"SELECT id, version, doc::text FROM {rm.Table} WHERE tenant = $1 ORDER BY highest_bid DESC, id DESC LIMIT 51")}");
        Console.WriteLine($"INFO projection table, highestBid desc, Search (SQL + 51 rows deserialized + cursor + ETag): {await TimeSearch(rm, tenant, new AuctionSearch(Sort: AuctionSort.HighestBidDesc))}");
        Console.WriteLine($"INFO state, status='Open' + highestBid desc (all closed: empty), expression index: {await Time(Sql("AND state->>'status' = 'Open'"), 10)}");
        return 0;

        static async Task<string> TimeSearch(NpgsqlAuctionTable t, Guid tenant, AuctionSearch q)
        {
            var xs = new List<double>();
            for (var i = 0; i < 53; i++) { var t0 = Stopwatch.GetTimestamp(); await t.Search(tenant, q, default); if (i >= 3) xs.Add(Ms(Stopwatch.GetTimestamp() - t0)); }
            return P50P99(xs);
        }
    }

    // ── lag: commit → the projection's checkpoint passed it; 50 commits/s by 1 or 4 writers; poll 100 ms / 10 ms / NOTIFY ──
    private static async Task<int> Lag(string mode, int writers, int seconds)
    {
        var schema = "q_lag";
        var store = await Store(schema, notify: true);
        var table = new NpgsqlAuctionTable(Db, schema, "lag", 1);
        await table.Ensure(default);
        var tenant = Guid.CreateVersion7();
        var c = Context(tenant);
        var now = Time.GetUtcNow();
        var auctions = new List<Auction>();
        for (var i = 0; i < 400; i++) { var a = Auction.Open("s", "t", now.AddDays(1), now); await Commit(store, a, 0, c); auctions.Add(a); }
        await using var listener = new Listener(Db, $"{schema}_feed");
        var options = mode switch
        {
            "poll100" => new ProjectionOptions { Poll = TimeSpan.FromMilliseconds(100) },
            "poll10" => new ProjectionOptions { Poll = TimeSpan.FromMilliseconds(10) },
            _ => new ProjectionOptions { Poll = TimeSpan.FromSeconds(1), Wait = listener.Wait },
        };
        var host = new ProjectionHost<AuctionRow>(store, new AuctionList(), table, options) { Trace = [] };
        await host.CatchUp();
        using var stop = new CancellationTokenSource();
        var run = host.Run(stop.Token);
        var commits = new List<(GlobalPosition Token, long Ticks)>();
        var perWriter = 50.0 / writers;
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            var mine = auctions.Where((_, i) => i % writers == w).ToList();
            var interval = Stopwatch.Frequency / perWriter;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < perWriter * seconds; i++)
            {
                var due = start + (long)(i * interval);
                var wait = due - Stopwatch.GetTimestamp();
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
                var a = mine[i % mine.Count];
                var r = await Timed(store, a, c, x => x.Bid("b", x.HighestBid + 1));
                lock (commits) commits.Add(r);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        await Task.Delay(1500);
        await stop.CancelAsync(); await run;
        var trace = host.Trace!;
        var lags = new List<double>();
        foreach (var (token, ticks) in commits.Skip(commits.Count / 10))       // skip the first 10 % (warm-up)
        {
            var hit = trace.FirstOrDefault(x => x.Position >= token);
            if (hit.Ticks != 0) lags.Add(Ms(hit.Ticks - ticks));
        }
        Console.WriteLine(string.Create(I, $"| {mode} | {writers} | {Pct(lags)} | {trace.Count} batches |"));
        return 0;
    }

    // ── read-your-writes: commit, then wait until the projection passed it: in process (signal) vs another process (poll the checkpoint row) ──
    private static async Task<int> ReadYourWrites(int n)
    {
        var schema = "q_ryw";
        var store = await Store(schema, notify: true);
        var table = new NpgsqlAuctionTable(Db, schema, "ryw", 1);
        await table.Ensure(default);
        var c = Context(Guid.CreateVersion7());
        var now = Time.GetUtcNow();
        var a = Auction.Open("s", "t", now.AddDays(1), now);
        await Commit(store, a, 0, c);
        await using var listener = new Listener(Db, $"{schema}_feed");
        var host = new ProjectionHost<AuctionRow>(store, new AuctionList(), table, new() { Poll = TimeSpan.FromSeconds(1), Wait = listener.Wait });
        using var stop = new CancellationTokenSource();
        var run = host.Run(stop.Token);
        var inProcess = new List<double>();
        var polled = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var (token, ticks) = await Timed(store, a, c, x => x.Bid("b", x.HighestBid + 1));
            if (i % 2 == 0)
            {
                if (!await host.WaitFor(token, TimeSpan.FromSeconds(2))) throw new TimeoutException();
                inProcess.Add(Ms(Stopwatch.GetTimestamp() - ticks));
            }
            else
            {
                if (!await SelfTest.PollCheckpoint(schema, "ryw", 1, token, TimeSpan.FromSeconds(2))) throw new TimeoutException();
                polled.Add(Ms(Stopwatch.GetTimestamp() - ticks));
            }
        }
        await stop.CancelAsync(); await run;
        Console.WriteLine($"INFO read-your-writes, NOTIFY host, in process (WaitFor): {Pct(inProcess)}");
        Console.WriteLine($"INFO read-your-writes, NOTIFY host, other process (poll checkpoint row every 5 ms): {Pct(polled)}");
        return 0;
    }
}
