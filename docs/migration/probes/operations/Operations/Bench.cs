using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tarol.Persistence.Npgsql;
using Tarol.Queries;
using Tarol.Queries.Npgsql;

namespace Probe.Operations;

internal static partial class Bench
{
    public static async Task Run(string[] args)
    {
        switch (args)
        {
            case ["metrics"]: await Metrics(); break;
            case ["gauges"]: await Gauges(); break;
            case ["upgrade", var events]: await Upgrade(int.Parse(events, System.Globalization.CultureInfo.InvariantCulture)); break;
            case ["retention", var rows]: await Retention(int.Parse(rows, System.Globalization.CultureInfo.InvariantCulture)); break;
            case ["drop"]: await Drop(); break;
            case ["runners", var items]: await Runners(int.Parse(items, System.Globalization.CultureInfo.InvariantCulture)); break;
            default: Console.WriteLine("bench <metrics|gauges|upgrade <events>|retention <rows>|drop|runners <items>>"); break;
        }
    }

    // ── (a) instruments on the hot path, against the commit they would measure
    private static async Task Metrics()
    {
        using var meter = new Meter("Probe.Hot");
        var counter = meter.CreateCounter<long>("tarol.changes.committed", "{change}");
        var histogram = meter.CreateHistogram<double>("tarol.changes.duration", "s");
        var change = new KeyValuePair<string, object?>("tarol.change", "TouchItem");
        var outcome = new KeyValuePair<string, object?>("tarol.outcome", "committed");
        const int n = 5_000_000;
        double Ns(Action a)
        {
            var best = double.MaxValue;
            for (var run = 0; run < 3; run++)   // min of 3: the first run of the helper is still in tier 0
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < n; i++) a();
                best = Math.Min(best, sw.Elapsed.TotalNanoseconds / n);
            }
            return best;
        }
        var logger = NullLogger.Instance;
        Console.WriteLine("| operation | no listener | a MeterListener callback on |");
        Console.WriteLine("|---|---|---|");
        var offCounter = Ns(() => counter.Add(1, change, outcome));
        var offHist = Ns(() => histogram.Record(0.007, change, outcome));
        long sum = 0; double hsum = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Probe.Hot") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref sum, v));
        listener.SetMeasurementEventCallback<double>((_, v, _, _) => hsum += v);
        listener.Start();
        var onCounter = Ns(() => counter.Add(1, change, outcome));
        var onHist = Ns(() => histogram.Record(0.007, change, outcome));
        Console.WriteLine($"| Counter<long>.Add(1, 2 tags) | {Lab.F(offCounter)} ns | {Lab.F(onCounter)} ns |");
        Console.WriteLine($"| Histogram<double>.Record(v, 2 tags) | {Lab.F(offHist)} ns | {Lab.F(onHist)} ns |");
        var log = Ns(() => Log.Committed(logger, "TouchItem", 1));
        Console.WriteLine($"| [LoggerMessage] Debug, logger disabled | {Lab.F(log)} ns | – |");

        // the commit those numbers sit on
        var store = await Lab.Store("metrics", fresh: true);
        var t = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var item = Item.Create(Lab.Time.GetUtcNow());
            var sw = Stopwatch.StartNew();
            await Lab.Commit(store, item, 0, Lab.Context(Lab.TenantA));
            t.Add(sw.Elapsed.TotalMilliseconds);
        }
        t.Sort();
        Lab.Info($"one commit (Item.Create, 1 event + 0 outbox rows) on the Npgsql store: p50 {Lab.F(Lab.Pct(t, 0.5))} ms, p99 {Lab.F(Lab.Pct(t, 0.99))} ms");
        Lab.Info($"a counter + a histogram with a listener per commit: {Lab.F((onCounter + onHist) / (Lab.Pct(t, 0.5) * 1e6) * 1e6)} ppm of the p50 commit");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Change {Change} committed {Events} event(s)")]
    private static partial void Committed(ILogger logger, string change, int events);

    private static class Log
    {
        public static void Committed(ILogger logger, string change, int events) => Bench.Committed(logger, change, events);
    }

    // ── (b) observable gauges: a query per scrape vs one cached snapshot
    private static async Task Gauges()
    {
        const string s = "gauges";
        await Lab.Store(s, fresh: true);
        await Lab.Exec($"DROP SCHEMA IF EXISTS {s}_rm CASCADE; CREATE SCHEMA {s}_rm; {NpgsqlReadModelSchema.ProjectionsTable(s + "_rm")}");
        // 1,000,000 events (100,000 streams × 10) as raw rows: the feed's indexes are those of the store
        await Lab.Exec($"""
            INSERT INTO {s}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, payload, recorded_at, actor_kind, actor_id, correlation_id)
            SELECT '00000000-0000-0000-0000-000000000000', 'item', md5(g::text)::uuid, v, gen_random_uuid(), 'item.touched', 1, md5(g::text)::uuid, jsonb_build_object('n', 1),
                   now() - interval '1 hour' + (g * 10 + v) * interval '3 ms', 'user', 'alice', gen_random_uuid()
            FROM generate_series(1, 100000) g, generate_series(1, 10) v;
            UPDATE {s}.events SET recorded_at = now() - interval '1 hour' + position * interval '3 ms';   -- feed order = time order
            """);
        await Lab.Exec($"VACUUM ANALYZE {s}.events");
        await Lab.Exec($"INSERT INTO {s}_rm.projections (name, version, tx, pos, state) SELECT 'item-list', 1, tx_id, position, 'current' FROM {s}.events ORDER BY tx_id, position OFFSET 500000 LIMIT 1");
        await Lab.Exec($"INSERT INTO {s}.jobs (tenant, name, schedule, next_at) SELECT md5(g::text)::uuid, 'nightly', '0 2 * * * UTC', date_trunc('second', now()) + (g % 100) * interval '1 minute' - interval '5 minutes' FROM generate_series(1, 10000) g");
        using var signals = new Signals(Lab.Db, s, s + "_rm");
        Console.WriteLine("| outbox rows | `count(*)` (today's `Pending`) | `count(*) WHERE head` | oldest due head (`min(due_at)`, index) | estimate (`pg_class.reltuples`) | `count(*)` outbox_dead (rows/100) | snapshot (7 numbers, one round trip) |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        var have = 0;
        foreach (var size in new[] { 0, 10_000, 100_000, 1_000_000 })
        {
            if (size > have)
            {
                await Lab.Exec($"""
                    INSERT INTO {s}.outbox (tenant, reaction, type, stream_id, version, event_id, due_at, head)
                    SELECT '00000000-0000-0000-0000-000000000000', 'AckTouch', 'item', md5((g / 10)::text)::uuid, g % 10 + 1, gen_random_uuid(),
                           now() - (g % 600) * interval '1 second', g % 10 = 0
                    FROM generate_series({have + 1}, {size}) g;
                    INSERT INTO {s}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, dead_at)
                    SELECT g, '00000000-0000-0000-0000-000000000000', 'AckTouch', 'item', gen_random_uuid(), 1, gen_random_uuid(), 10, now()
                    FROM generate_series({have / 100 + 1}, {size / 100}) g;
                    ANALYZE {s}.outbox; ANALYZE {s}.outbox_dead;
                    """);
                have = size;
            }
            var store = new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), Lab.Time, new NpgsqlStoreOptions { Schema = s });
            var pending = await Lab.Median(() => store.Outbox.Pending(default));
            var heads = await Lab.Median(() => Lab.Scalar<long>($"SELECT count(*) FROM {s}.outbox WHERE head"));
            var oldest = await Lab.Median(() => Lab.Scalar<DateTime>($"SELECT min(due_at) FROM {s}.outbox WHERE head AND due_at <= now()"));
            var estimate = await Lab.Median(() => Lab.Scalar<float>($"SELECT reltuples FROM pg_class WHERE oid = '{s}.outbox'::regclass"));
            var dead = await Lab.Median(() => Lab.Scalar<long>($"SELECT count(*) FROM {s}.outbox_dead"));
            var snap = await Lab.Median(() => signals.Poll());
            Console.WriteLine($"| {Lab.N(size)} | {Lab.F(pending)} ms | {Lab.F(heads)} ms | {Lab.F(oldest)} ms | {Lab.F(estimate)} ms | {Lab.F(dead)} ms | {Lab.F(snap)} ms |");
        }
        var last = signals.Last!;
        Lab.Info($"snapshot at 1,000,000 outbox rows, 1,000,000 events, 10,000 job rows: rows≈{Lab.N(last.OutboxRows)}, oldest due {Lab.F(last.OldestDueSeconds)} s, dead {Lab.N(last.OutboxDead)}, overdue {Lab.F(last.JobsOverdueSeconds)} s, lag {Lab.F(last.ProjectionLagSeconds)} s, head {Lab.N(last.FeedHead)}");
        var lagCount = await Lab.Median(() => Lab.Scalar<long>($"SELECT count(*) FROM {s}.events e, {s}_rm.projections p WHERE p.name = 'item-list' AND (e.tx_id, e.position) > (p.tx, p.pos)"), 5);
        var jobsOverdue = await Lab.Median(() => new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), Lab.Time, new NpgsqlStoreOptions { Schema = s }).Jobs.Overdue(default), 15);
        Lab.Info($"lag as an event count (500,000 behind: count(*) after the checkpoint): {Lab.F(lagCount)} ms; lag in seconds (two index lookups, in the snapshot): included above");
        Lab.Info($"IJobRows.Overdue at 10,000 job rows (today's API, index jobs_due): {Lab.F(jobsOverdue)} ms");

        // a scrape: the meter's callbacks read the cached snapshot
        using var listener = new MeterListener();
        var observed = 0;
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Tarol.Operations") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => observed++);
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => observed++);
        listener.Start();
        const int scrapes = 100_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < scrapes; i++) listener.RecordObservableInstruments();
        Lab.Info($"a scrape of the 6 cached gauges: {Lab.F(sw.Elapsed.TotalNanoseconds / scrapes)} ns ({observed / scrapes} measurements per scrape)");
        // the alternative: the callback queries the database (synchronously: ObservableGauge callbacks are not async)
        using var meter = new Meter("Probe.Direct");
        meter.CreateObservableGauge("tarol.reactions.pending", () => Lab.Scalar<long>($"SELECT count(*) FROM {s}.outbox").GetAwaiter().GetResult(), "{row}");
        using var direct = new MeterListener();
        direct.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Probe.Direct") l.EnableMeasurementEvents(i); };
        direct.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        direct.Start();
        var blocking = await Lab.Median(() => { direct.RecordObservableInstruments(); return Task.CompletedTask; }, 9);
        Lab.Info($"a scrape whose gauge runs today's Pending (count(*)) at 1,000,000 rows: {Lab.F(blocking)} ms, blocking the exporter's thread (sync over async)");
    }

    // ── (c) schema changes on a populated store, with writers committing
    private static async Task Upgrade(int events)
    {
        const string s = "upg";
        var store = await Lab.Store(s, fresh: true);
        await Lab.Exec($"""
            INSERT INTO {s}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, payload, recorded_at, actor_kind, actor_id, correlation_id)
            SELECT '00000000-0000-0000-0000-000000000000', 'item', md5(g::text)::uuid, v, gen_random_uuid(), 'item.touched', 1, md5(g::text)::uuid, jsonb_build_object('n', 1),
                   now() - interval '1 day', 'user', 'alice', gen_random_uuid()
            FROM generate_series(1, {events / 10}) g, generate_series(1, 10) v;
            INSERT INTO {s}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, dead_at)
            SELECT g, '00000000-0000-0000-0000-000000000000', 'AckTouch', 'item', gen_random_uuid(), 1, gen_random_uuid(), 10, now() FROM generate_series(1, 100000) g;
            ANALYZE {s}.events;
            """);
        var size = await Lab.Scalar<string>($"SELECT pg_size_pretty(pg_total_relation_size('{s}.events'))");
        Lab.Info($"populated: {Lab.N(events)} events ({size} with indexes), 100,000 dead letters; 4 writers commit Item.Create + Touch (2 events, 1 outbox row) in a loop");
        var lat = new System.Collections.Concurrent.ConcurrentBag<(long At, double Ms)>();
        using var stop = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var i = Item.Create(Lab.Time.GetUtcNow());
                _ = i.Touch();
                var t0 = Stopwatch.GetTimestamp();
                await Lab.Commit(store, i, 0, Lab.Context(Lab.TenantA));
                lat.Add((t0, Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
            }
        })).ToArray();
        await Task.Delay(2000);
        Console.WriteLine("| step | DDL time | commits during it | commit p50 | commit p99 | commit max |");
        Console.WriteLine("|---|---|---|---|---|---|");
        async Task Measure(string label, Func<Task> ddl)
        {
            var from = Stopwatch.GetTimestamp();
            var sw = Stopwatch.StartNew();
            string result = "";
            try { await ddl(); } catch (PostgresException e) { result = $" ({e.SqlState})"; }
            var took = sw.Elapsed.TotalMilliseconds;
            await Task.Delay(300);
            var to = Stopwatch.GetTimestamp();
            var during = lat.Where(l => l.At >= from && l.At <= to).Select(l => l.Ms).Order().ToList();
            Console.WriteLine($"| {label}{result} | {Lab.F(took)} ms | {during.Count} | {Lab.F(Lab.Pct(during, 0.5))} ms | {Lab.F(Lab.Pct(during, 0.99))} ms | {Lab.F(during.Count == 0 ? double.NaN : during[^1])} ms |");
            await Task.Delay(700);
        }
        await Measure("(baseline, no DDL, 1 s)", () => Task.Delay(1000));
        await Measure("`ADD COLUMN x text NULL` on events", () => Lab.Exec($"ALTER TABLE {s}.events ADD COLUMN x text NULL"));
        await Measure("`ADD COLUMN y integer NOT NULL DEFAULT 0` on events", () => Lab.Exec($"ALTER TABLE {s}.events ADD COLUMN y integer NOT NULL DEFAULT 0"));
        await Measure("`ADD COLUMN z uuid NOT NULL DEFAULT gen_random_uuid()` (volatile: rewrites the table)", () => Lab.Exec($"ALTER TABLE {s}.events ADD COLUMN z uuid NOT NULL DEFAULT gen_random_uuid()"));
        await Measure("`CREATE INDEX` on events (recorded_at)", () => Lab.Exec($"CREATE INDEX events_recorded ON {s}.events (recorded_at)"));
        await Lab.Exec($"DROP INDEX {s}.events_recorded");
        await Measure("`CREATE INDEX CONCURRENTLY` on events (recorded_at)", () => Lab.Exec($"CREATE INDEX CONCURRENTLY events_recorded ON {s}.events (recorded_at)"));
        await Measure("`CREATE INDEX CONCURRENTLY` on outbox_dead (dead_at), 100,000 rows", () => Lab.Exec($"CREATE INDEX CONCURRENTLY outbox_dead_at ON {s}.outbox_dead (dead_at)"));
        // a reader holds events for 3 s (a report, a stuck transaction): the DDL waits for it, and the writers queue behind the DDL
        async Task Reader(int ms)
        {
            await using var c = await Lab.Db.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using (var q = new NpgsqlCommand($"SELECT count(*) FROM {s}.events WHERE version = 1", c, tx)) await q.ExecuteScalarAsync();
            await Task.Delay(ms);
            await tx.CommitAsync();
        }
        var reader = Reader(3000);
        await Task.Delay(200);
        await Measure("`ADD COLUMN w text NULL` behind a 3 s reader, no lock_timeout", () => Lab.Exec($"ALTER TABLE {s}.events ADD COLUMN w text NULL"));
        await reader;
        reader = Reader(3000);
        await Task.Delay(200);
        await Measure("the same with `lock_timeout = 200ms`, retried ×10 after 300 ms", async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try { await Lab.Exec($"SET lock_timeout = '200ms'; ALTER TABLE {s}.events ADD COLUMN v text NULL; RESET lock_timeout"); return; }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable && attempt < 10) { await Task.Delay(300); }
            }
        });
        await reader;
        stop.Cancel();
        await Task.WhenAll(writers);
    }

    // ── (d) retention of the dead tables
    private static async Task Retention(int rows)
    {
        const string s = "ret";
        await Lab.Store(s, fresh: true);
        await Lab.Exec("DROP SCHEMA IF EXISTS ret_msg CASCADE");
        var letters = new Tarol.Messaging.Npgsql.NpgsqlMessageDeadLetters(Lab.Db, Lab.Time, "ret_msg");
        await letters.EnsureSchema(default);
        async Task Fill()
        {
            await Lab.Exec($"""
                TRUNCATE {s}.outbox_dead, {s}.jobs_dead, ret_msg.messages_dead;
                INSERT INTO {s}.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, last_error, dead_at)
                SELECT g, '00000000-0000-0000-0000-000000000000', 'AckTouch', 'item', gen_random_uuid(), 1, gen_random_uuid(), 10, repeat('x', 200), now() - (g % 60) * interval '1 day' FROM generate_series(1, {rows}) g;
                INSERT INTO {s}.jobs_dead (tenant, name, occurrence, attempts, last_error, dead_at)
                SELECT gen_random_uuid(), 'nightly', now(), 10, repeat('x', 200), now() - (g % 60) * interval '1 day' FROM generate_series(1, {rows}) g;
                INSERT INTO ret_msg.messages_dead (subscription, topic, message_id, attempts, reason, headers, data, dead_at)
                SELECT 'billing', 'artflow.auction-settled', gen_random_uuid(), 10, 'poison', jsonb_build_object(), '\x010203', now() - (g % 60) * interval '1 day' FROM generate_series(1, {rows}) g;
                ANALYZE {s}.outbox_dead; ANALYZE {s}.jobs_dead; ANALYZE ret_msg.messages_dead;
                """);
        }
        Console.WriteLine("| table | rows | index on dead_at | purge older than 30 days (batches of 1,000) | batches | per batch | the next run, nothing to purge (100,000 young rows, after VACUUM ANALYZE) |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var indexed in new[] { false, true })
        {
            await Fill();
            if (indexed)
                await Lab.Exec($"CREATE INDEX ON {s}.outbox_dead (dead_at); CREATE INDEX ON {s}.jobs_dead (dead_at); CREATE INDEX ON ret_msg.messages_dead (dead_at)");
            foreach (var (table, key) in new[] { ($"{s}.outbox_dead", "id"), ($"{s}.jobs_dead", "id"), ("ret_msg.messages_dead", "id") })
            {
                var sw = Stopwatch.StartNew();
                int batches = 0, deleted;
                do
                {
                    deleted = (int)await Lab.Scalar<long>($"WITH d AS (DELETE FROM {table} WHERE {key} IN (SELECT {key} FROM {table} WHERE dead_at < now() - interval '30 days' LIMIT 1000) RETURNING 1) SELECT count(*) FROM d");
                    batches++;
                } while (deleted > 0);
                var took = sw.Elapsed.TotalMilliseconds;
                await Lab.Exec($"VACUUM ANALYZE {table}");   // the steady state: autovacuum has run since the last purge
                var idle = await Lab.Median(() => Lab.Scalar<long>($"WITH d AS (DELETE FROM {table} WHERE {key} IN (SELECT {key} FROM {table} WHERE dead_at < now() - interval '30 days' LIMIT 1000) RETURNING 1) SELECT count(*) FROM d"), 9);
                Console.WriteLine($"| `{table}` | {Lab.N(rows)} | {(indexed ? "yes" : "no")} | {Lab.F(took)} ms | {batches} | {Lab.F(took / batches)} ms | {Lab.F(idle)} ms |");
            }
        }
    }

    // ── (e) DropVersion while old code still reads the table
    private static async Task Drop()
    {
        const string s = "drop";
        var store = await Lab.Store(s, fresh: true);
        await Lab.Exec("DROP SCHEMA IF EXISTS drop_rm CASCADE");
        var rm = new NpgsqlReadModels(Lab.Db, Lab.Json, "drop_rm");
        await Lab.Seed(store, 5000, 3);
        var host = new ProjectionHost<ItemRow>(store, rm.Store<ItemRow>(), new ProjectionOptions { Batch = 2000 });
        await host.CatchUp();
        var table = "drop_rm." + Projections.TableOf<ItemRow>();
        Lab.Info($"projection item-list v1: {Lab.N(await Lab.Scalar<long>($"SELECT count(*) FROM {table}"))} rows in {table}");
        foreach (var timeout in new[] { 0, 200 })
        {
            var holder = Task.Run(async () =>
            {
                await using var c = await Lab.Db.OpenConnectionAsync();
                await using var tx = await c.BeginTransactionAsync();
                await using (var q = new NpgsqlCommand($"SELECT count(*) FROM {table}", c, tx)) await q.ExecuteScalarAsync();
                await Task.Delay(3000);
                await tx.CommitAsync();
            });
            await Task.Delay(200);
            var drop = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    if (timeout == 0)
                        await new NpgsqlReadModels(Lab.Db, Lab.Json, "drop_rm").DropVersion("item-list", 1, default);   // another process's instance: not "in use"
                    else
                        await Lab.Exec($"SET lock_timeout = '{timeout}ms'; DROP TABLE IF EXISTS {table}; RESET lock_timeout");
                    return (sw.Elapsed.TotalMilliseconds, "dropped");
                }
                catch (PostgresException e) { return (sw.Elapsed.TotalMilliseconds, e.SqlState); }
            });
            await Task.Delay(200);
            var sw2 = Stopwatch.StartNew();
            string reader;
            try { _ = await Lab.Scalar<long>($"SELECT count(*) FROM {table}"); reader = "read"; }
            catch (PostgresException e) { reader = e.SqlState; }
            var waited = sw2.Elapsed.TotalMilliseconds;
            var (took, result) = await drop;
            await holder;
            Console.WriteLine($"| {(timeout == 0 ? "today's `DropVersion` (no lock_timeout)" : $"`DROP TABLE` with `lock_timeout = {timeout}ms`")} | {Lab.F(took)} ms, {result} | a new reader of the old table waited {Lab.F(waited)} ms, then {reader} |");
            if (timeout == 0)
            {
                await Lab.Exec("DROP SCHEMA IF EXISTS drop_rm CASCADE");
                host = new ProjectionHost<ItemRow>(store, new NpgsqlReadModels(Lab.Db, Lab.Json, "drop_rm").Store<ItemRow>(), new ProjectionOptions { Batch = 2000 });
                await host.CatchUp();
            }
        }
    }

    // ── (f) several runners of one projection: wasted batches without a lock
    private static async Task Runners(int items)
    {
        const string s = "run";
        var store = await Lab.Store(s, fresh: true);
        await Lab.Seed(store, items, 9);
        var head = await Lab.Scalar<long>($"SELECT count(*) FROM {s}.events");
        Lab.Info($"feed: {Lab.N(head)} events ({Lab.N(items)} items × 10, one commit each)");
        Console.WriteLine("| runners | gate | catch-up time | batches run (read from the feed) | of them refused by the checkpoint guard (wasted) |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var gated in new[] { false, true })
            foreach (var k in new[] { 1, 2, 4 })
            {
                if (gated && k == 1) continue;
                var rmSchema = $"run_rm{k}{(gated ? "g" : "")}";
                await Lab.Exec($"DROP SCHEMA IF EXISTS {rmSchema} CASCADE");
                var rm = new NpgsqlReadModels(Lab.Db, Lab.Json, rmSchema);
                var hosts = Enumerable.Range(0, k).Select(_ => new ProjectionHost<ItemRow>(store, rm.Store<ItemRow>(), new ProjectionOptions { Batch = 1000 })).ToArray();
                await hosts[0].RunOnce();   // prepare the table once (the DDL lock serializes it anyway)
                long written = 1;
                var sw = Stopwatch.StartNew();
                await Task.WhenAll(hosts.Select(h => Task.Run(async () =>
                {
                    NpgsqlConnection? gate = null;
                    if (gated)
                    {
                        gate = await Lab.Db.OpenConnectionAsync();
                        while (true)
                        {
                            await using var c = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext($1))", gate);
                            c.Parameters.Add(new() { Value = $"tarol.projection:{rmSchema}:item-list:1" });
                            if ((bool)(await c.ExecuteScalarAsync())!) break;
                            if (hosts.Any(o => o.State == ProjectionState.Current)) { await gate.DisposeAsync(); return; }
                            await Task.Delay(100);
                        }
                    }
                    try
                    {
                        while (await h.RunOnce() > 0) Interlocked.Increment(ref written);
                    }
                    finally { if (gate is not null) await gate.DisposeAsync(); }
                })));
                var took = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"| {k} | {(gated ? "pg_try_advisory_lock per projection version" : "none (today: the checkpoint guard)")} | {Lab.F(took)} s | {written} | {hosts.Sum(h => h.Conflicts)} |");
            }
    }
}
