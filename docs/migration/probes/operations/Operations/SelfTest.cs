using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tarol.Changes;
using Tarol.Jobs;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Npgsql;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Probe.Operations;

internal static class SelfTest
{
    public static async Task<int> Run(string kind) => kind switch
    {
        "schema" => await Schema(),
        "dead" => await Dead(),
        "health" => await Health(),
        "spans" => await Spans(),
        _ => 2,
    };

    // ── schema: concurrent EnsureSchema, rolling deploys, upgrade steps under a lock, CONCURRENTLY
    private static async Task<int> Schema()
    {
        // (1) today's EnsureSchema takes no lock: N processes starting at once on an empty database
        int failures = 0, rounds = 20, parallel = 6;
        var states = new SortedDictionary<string, int>();
        for (var round = 0; round < rounds; round++)
        {
            await Lab.Exec("DROP SCHEMA IF EXISTS race CASCADE");
            var results = await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Task.Run(async () =>
            {
                try { await new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), Lab.Time, new NpgsqlStoreOptions { Schema = "race" }).EnsureSchema(default); return null; }
                catch (PostgresException e) { return e.SqlState + " " + e.ConstraintName; }
            })));
            foreach (var r in results.OfType<string>()) { failures++; states[r] = states.GetValueOrDefault(r) + 1; }
        }
        Lab.Info($"today's EnsureSchema, {parallel} at once on an empty schema, {rounds} rounds: {failures} of {rounds * parallel} calls failed ({string.Join(", ", states.Select(kv => $"{kv.Key.Trim()} × {kv.Value}"))})");
        Lab.Check(failures > 0, "without a lock, concurrent EnsureSchema on an empty database fails some starts (catalog race)", $"{failures} of {rounds * parallel}");

        // (2) the same through the upgrader: a blocking advisory lock deadlocks with CREATE INDEX CONCURRENTLY; polling does not
        foreach (var blocking in new[] { true, false })
        {
            failures = 0;
            var codes = new SortedDictionary<string, int>();
            for (var round = 0; round < rounds; round++)
            {
                await Lab.Exec("DROP SCHEMA IF EXISTS race CASCADE");
                var results = await Task.WhenAll(Enumerable.Range(0, parallel).Select(w => Task.Run(async () =>
                {
                    try { await new Upgrader(Lab.Db, "race", NpgsqlSchema.Script("race"), Upgrader.Version2("race"), knows: 4) { Blocking = blocking }.Ensure(); return null; }
                    catch (PostgresException e) { return e.SqlState; }
                })));
                foreach (var r in results.OfType<string>()) { failures++; codes[r] = codes.GetValueOrDefault(r) + 1; }
            }
            var ran = await Lab.Scalar<long>("SELECT count(*) FROM race.upgrade_log");
            if (blocking)
                Lab.Check(failures > 0 && codes.ContainsKey("40P01"), "blocking pg_advisory_lock + CREATE INDEX CONCURRENTLY: the waiters are transactions the build waits for → deadlock (40P01)",
                    $"{failures} of {rounds * parallel} ({string.Join(", ", codes.Select(kv => $"{kv.Key} × {kv.Value}"))})");
            else
                Lab.Check(failures == 0 && ran == 3, "polling pg_try_advisory_lock every 100 ms: 0 failures, the 3 upgrade steps ran once (last round)", $"{failures} failures, {ran} step runs");
        }

        // (3) rolling deploys: today a process that knows version 1 refuses a schema of version 2
        await Lab.Exec("UPDATE race.schema_version SET version = 2");
        string? refused = null;
        try { await new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), Lab.Time, new NpgsqlStoreOptions { Schema = "race" }).EnsureSchema(default); }
        catch (InvalidOperationException e) { refused = e.Message; }
        Lab.Check(refused is not null, "today: old code (version 1) refuses to start on a schema of version 2: a rolling deploy's old pods crash-loop", refused ?? "started");
        await Lab.Exec("UPDATE race.schema_version SET version = 4, compatible_from = 1");
        var old = await new Upgrader(Lab.Db, "race", NpgsqlSchema.Script("race"), Upgrader.Version2("race"), knows: 1).Ensure();
        Lab.Check(old.From == 4 && old.Ran == 0, "with compatible_from: code that knows 1 starts on version 4 (compatible from 1) and runs no step", $"from {old.From}, ran {old.Ran}");
        await Lab.Exec("UPDATE race.schema_version SET compatible_from = 3");
        string? contract = null;
        try { await new Upgrader(Lab.Db, "race", NpgsqlSchema.Script("race"), Upgrader.Version2("race"), knows: 2).Ensure(); }
        catch (InvalidOperationException e) { contract = e.Message; }
        Lab.Check(contract is not null, "after a contract step (compatible_from 3) code that knows 2 refuses, with the versions in the message", contract ?? "started");

        // (4) today's EnsureSchema re-runs the whole script at every start: against running commits
        var store = await Lab.Store("up", fresh: true);
        await Lab.Seed(store, 2000, 4);
        await BuryOutboxRows(store, 500);
        {
            using var stop = new CancellationTokenSource();
            var lat = new System.Collections.Concurrent.ConcurrentBag<double>();
            var writerErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ws = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var sw = Stopwatch.StartNew();
                    try { await Lab.Commit(store, Item.Create(Lab.Time.GetUtcNow()).Touched(), 0, Lab.Context(Lab.TenantA)); lat.Add(sw.Elapsed.TotalMilliseconds); }
                    catch (Exception e) { writerErrors.Add((e as PostgresException)?.SqlState ?? (e.InnerException as PostgresException)?.SqlState ?? e.GetType().Name); }
                }
            })).ToArray();
            await Task.Delay(500);
            var ensureErrors = new SortedDictionary<string, int>();
            var starts = 0;
            var sw0 = Stopwatch.StartNew();
            while (sw0.Elapsed < TimeSpan.FromSeconds(10))
            {
                starts++;
                try { await new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), Lab.Time, new NpgsqlStoreOptions { Schema = "up" }).EnsureSchema(default); }
                catch (PostgresException e) { ensureErrors[e.SqlState] = ensureErrors.GetValueOrDefault(e.SqlState) + 1; }
                await Task.Delay(100);
            }
            stop.Cancel();
            await Task.WhenAll(ws);
            var sorted = lat.Order().ToList();
            var wErr = writerErrors.GroupBy(x => x).Select(g => $"{g.Key} × {g.Count()}");
            Lab.Info($"today's EnsureSchema called {starts} times in 10 s (a process start every 100 ms) while 4 writers commit: EnsureSchema errors {(ensureErrors.Count == 0 ? "none" : string.Join(", ", ensureErrors.Select(kv => $"{kv.Key} × {kv.Value}")))}; writer errors {(writerErrors.IsEmpty ? "none" : string.Join(", ", wErr))}; {sorted.Count} commits p50 {Lab.F(Lab.Pct(sorted, 0.5))} ms, p99 {Lab.F(Lab.Pct(sorted, 0.99))} ms, max {Lab.F(sorted.Count == 0 ? double.NaN : sorted[^1])} ms");
            Lab.Check(ensureErrors.Count + writerErrors.Count == 0, "today's EnsureSchema re-run on a live store (version 1, nothing to change): no failure; its table locks show in the commit max", $"{ensureErrors.Values.Sum()} EnsureSchema + {writerErrors.Count} commit failures");
        }
        var writing = new CancellationTokenSource();
        var commits = 0;
        var writer = Task.Run(async () =>
        {
            while (!writing.IsCancellationRequested) { await Lab.Commit(store, Item.Create(Lab.Time.GetUtcNow()).Touched(), 0, Lab.Context(Lab.TenantA)); commits++; }
        });
        var both = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => new Upgrader(Lab.Db, "up", NpgsqlSchema.Script("up"), Upgrader.Version2("up"), knows: 4).Ensure()));
        writing.Cancel();
        await writer;
        var log = await Lab.Scalar<long>("SELECT count(*) FROM up.upgrade_log");
        var valid = await Lab.Scalar<long>("SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'up' AND c.relname IN ('outbox_dead_at', 'jobs_dead_at') AND i.indisvalid");
        Lab.Check(both.Sum(e => e.Ran) == 3 && log == 3 && both.All(e => e.To == 4) && valid == 2,
            "populated store (10,000 events, 500 dead letters), two upgraders at once, a writer committing: steps ran once, both end at version 4, indexes valid",
            $"ran {string.Join("+", both.Select(e => e.Ran))}, waited {string.Join(" / ", both.Select(e => Lab.F(e.Waited.TotalMilliseconds) + " ms"))}, {commits} commits meanwhile");

        // (5) CREATE INDEX CONCURRENTLY cannot run in a transaction; an interrupted build leaves an invalid index that IF NOT EXISTS keeps
        string? inTx = null;
        try
        {
            await using var c = await Lab.Db.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("CREATE INDEX CONCURRENTLY IF NOT EXISTS x ON up.events (recorded_at)", c, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException e) { inTx = e.SqlState; }
        Lab.Check(inTx == PostgresErrorCodes.ActiveSqlTransaction, "CREATE INDEX CONCURRENTLY inside a transaction fails (25001): such steps run outside one", inTx ?? "ran");
        await Lab.Exec("UPDATE up.schema_version SET version = 1; DROP INDEX up.outbox_dead_at; DROP INDEX up.jobs_dead_at; DELETE FROM up.upgrade_log");
        await Lab.Exec("INSERT INTO up.outbox_dead (id, tenant, reaction, type, stream_id, version, event_id, attempts, dead_at) SELECT -g, gen_random_uuid(), 'r', 't', gen_random_uuid(), 1, gen_random_uuid(), 1, now() FROM generate_series(1, 300000) g");
        string? interrupted = null;
        try
        {
            await using var c = await Lab.Db.OpenConnectionAsync();
            await using (var t = new NpgsqlCommand("SET statement_timeout = '30ms'", c)) await t.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand("CREATE INDEX CONCURRENTLY IF NOT EXISTS outbox_dead_at ON up.outbox_dead (dead_at, tenant, reaction, last_error)", c);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException e) { interrupted = e.SqlState; }
        var invalid = await Lab.Scalar<long>("SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'up' AND c.relname = 'outbox_dead_at' AND NOT i.indisvalid");
        Lab.Check(interrupted is not null && invalid == 1, "an interrupted CONCURRENTLY build leaves an INVALID index (IF NOT EXISTS would keep it)", $"{interrupted}, {invalid} invalid");
        var again = await new Upgrader(Lab.Db, "up", NpgsqlSchema.Script("up"), Upgrader.Version2("up"), knows: 4).Ensure();
        valid = await Lab.Scalar<long>("SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'up' AND c.relname = 'outbox_dead_at' AND i.indisvalid");
        Lab.Check(again.To == 4 && valid == 1, "the upgrader drops the invalid index and builds it again", $"to {again.To}, valid {valid}");
        return Lab.Summary("schema");
    }

    private static Item Touched(this Item i) { _ = i.Touch(); return i; }

    /// <summary>Claims and buries <paramref name="n"/> outbox rows (the dead letters of the admin checks).</summary>
    internal static async Task BuryOutboxRows(NpgsqlAggregateStore store, int n)
    {
        var buried = 0;
        while (buried < n)
        {
            var rows = await store.Outbox.Claim(Math.Min(100, n - buried), TimeSpan.FromMinutes(1), default);
            if (rows.Count == 0) break;
            foreach (var row in rows)
                if (await store.Outbox.Bury(row, "probe: given up", default)) buried++;
        }
    }

    // ── dead letters: what an admin entry needs (paging, filters, re-drive, discard, audit)
    private static async Task<int> Dead()
    {
        var store = await Lab.Store("dead", fresh: true);
        await Lab.Seed(store, 250, 1);
        await BuryOutboxRows(store, 250);
        var first = await store.Outbox.DeadLetters(100, default);
        Lab.Check(first.Count == 100, "IOutbox.DeadLetters(100) returns the first 100 of 250", $"{first.Count}, ids {first[0].Id}…{first[^1].Id}");
        Lab.Check(true, "…and has no cursor: rows 101–250 are reachable only by re-driving or raising max (finding: keyset paging needed)", "DeadLetters(int max)");
        var jobsDead = await store.Jobs.DeadJobs(100, default);
        Lab.Check(jobsDead.Count == 0, "IJobRows.DeadJobs(max) and IMessageDeadLetters.List(max) have the same shape (first page only)");

        // keyset paging with filters (the would-be admin query)
        var pages = new List<int>();
        long after = 0;
        while (true)
        {
            await using var c = Lab.Db.CreateCommand("SELECT id FROM dead.outbox_dead WHERE id > $1 AND ($2::uuid IS NULL OR tenant = $2) AND ($3::text IS NULL OR reaction = $3) ORDER BY id LIMIT 100");
            c.Parameters.Add(new() { Value = after });
            c.Parameters.Add(new() { Value = DBNull.Value, DataTypeName = "uuid" });
            c.Parameters.Add(new() { Value = DBNull.Value, DataTypeName = "text" });
            var ids = new List<long>();
            await using (var r = await c.ExecuteReaderAsync()) while (await r.ReadAsync()) ids.Add(r.GetInt64(0));
            if (ids.Count == 0) break;
            pages.Add(ids.Count);
            after = ids[^1];
        }
        Lab.Check(pages.SequenceEqual([100, 100, 50]), "keyset paging (id > after, tenant and reaction filters) reaches all 250", string.Join(" + ", pages));
        var tenantB = await Lab.Scalar<long>("SELECT count(*) FROM dead.outbox_dead WHERE tenant = $1", Lab.TenantB);
        Lab.Info($"tenant filter: {tenantB} of 250 dead letters belong to tenant B (1 in 5 seeded)");

        // re-drive with an audit row in the same statement
        await Lab.Exec("CREATE TABLE dead.operations_audit (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, at timestamptz NOT NULL, actor text NOT NULL, action text NOT NULL, target text NOT NULL, detail jsonb NULL)");
        var target = first[0];
        var redriven = await store.Outbox.Redrive(target.Id, default);
        await Lab.Exec("INSERT INTO dead.operations_audit (at, actor, action, target) VALUES (now(), $1, 'redrive', $2)", "user:ops-alice", "outbox_dead:" + target.Id);
        var back = await Lab.Scalar<long>("SELECT count(*) FROM dead.outbox WHERE event_id = $1", target.EventId);
        Lab.Check(redriven && back == 1, "IOutbox.Redrive(id) moves the row back (due now); the audit row is a second statement today", $"redriven {redriven}");
        var audited = await Lab.Scalar<long>("""
            WITH d AS (DELETE FROM dead.outbox_dead WHERE id = $1 RETURNING id, reaction, tenant),
                 a AS (INSERT INTO dead.operations_audit (at, actor, action, target, detail)
                       SELECT now(), $2, 'discard', 'outbox_dead:' || d.id, jsonb_build_object('reaction', d.reaction, 'tenant', d.tenant) FROM d RETURNING 1)
            SELECT count(*) FROM a
            """, first[1].Id, "user:ops-alice");
        Lab.Check(audited == 1, "discard (no API today) + audit in one statement: DELETE … RETURNING feeds the audit INSERT", $"{audited} audit row");
        var deleted = await Lab.Scalar<long>("SELECT count(*) FROM dead.outbox_dead WHERE id = $1", first[1].Id);
        Lab.Check(deleted == 0, "…the row is gone and the audit names who, what and when");

        // messages_dead: List(max) + Take(id)
        await Lab.Exec("DROP SCHEMA IF EXISTS deadmsg CASCADE");
        var letters = new NpgsqlMessageDeadLetters(Lab.Db, Lab.Time, "deadmsg");
        await letters.EnsureSchema(default);
        for (var i = 0; i < 120; i++)
            await letters.Add("billing.payments", new Incoming(null, 10, "artflow", new byte[] { 1, 2, 3 }, "artflow.auction-settled"), "poison", default);
        var listed = await letters.List(100, default);
        Lab.Check(listed.Count == 100, "IMessageDeadLetters.List(100) of 120: first page only, as the outbox", $"{listed.Count}");
        var idx = await Lab.Scalar<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'deadmsg' AND tablename = 'messages_dead' AND indexdef LIKE '%dead_at%'");
        Lab.Check(idx == 0, "messages_dead, outbox_dead and jobs_dead have no index on dead_at: retention scans the table (§4 measures it)");
        return Lab.Summary("dead letters");
    }

    // ── health: the snapshot, the checks and their statuses
    private static async Task<int> Health()
    {
        var store = await Lab.Store("health", fresh: true);
        var rm = new NpgsqlReadModels(Lab.Db, Lab.Json, "health_rm");
        await Lab.Exec("DROP SCHEMA IF EXISTS health_rm CASCADE");
        var host = new ProjectionHost<ItemRow>(store, rm.Store<ItemRow>(), new ProjectionOptions { Batch = 1000 });
        await Lab.Seed(store, 200, 2);
        await host.CatchUp();
        using var signals = new Signals(Lab.Db, "health", "health_rm");
        var s = await signals.Poll();
        Lab.Check(s.DatabaseUp && s.ProjectionLagSeconds < 1 && s.OutboxDead == 0, "snapshot after a catch-up: database up, projection lag < 1 s, no dead letters",
            $"rows≈{s.OutboxRows}, oldest due {Lab.F(s.OldestDueSeconds)} s, lag {Lab.F(s.ProjectionLagSeconds)} s");
        await Lab.Exec("UPDATE health.outbox SET due_at = now() - interval '10 minutes' WHERE id IN (SELECT id FROM health.outbox WHERE head LIMIT 5)");
        await BuryOutboxRows(store, 3);
        await Lab.Exec("INSERT INTO health.jobs (tenant, name, schedule, next_at) VALUES ('00000000-0000-0000-0000-000000000000', 'nightly', '0 2 * * * UTC', date_trunc('second', now()) - interval '2 minutes')");
        await Task.Delay(1100);
        await Lab.Seed(store, 50, 1);
        await Lab.Exec("UPDATE health.events SET recorded_at = recorded_at + interval '45 seconds' WHERE position > (SELECT max(position) - 50 FROM health.events)");
        s = await signals.Poll();
        Lab.Check(s.OldestDueSeconds >= 590 && s.OutboxDead == 3 && s.JobsOverdueSeconds >= 119 && s.ProjectionLagSeconds >= 44,
            "a stuck reaction (10 min), 3 dead letters, a job 2 min overdue, projection 45 s behind: the snapshot sees all four",
            $"oldest {Lab.F(s.OldestDueSeconds)} s, dead {s.OutboxDead}, overdue {Lab.F(s.JobsOverdueSeconds)} s, lag {Lab.F(s.ProjectionLagSeconds)} s");
#if HEALTH
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(signals);
        var hc = services.AddHealthChecks();
        foreach (var name in new[] { "reactions", "dead-letters", "jobs", "projections" })
            hc.Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(name, sp => new HealthChecksProbe.SnapshotCheck(signals, name), null, ["ready"]));
        hc.AddCheck("process", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), ["live"]);
        await using var sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        var ready = await service.CheckHealthAsync(r => r.Tags.Contains("ready"));
        var live = await service.CheckHealthAsync(r => r.Tags.Contains("live"));
        Console.WriteLine("| check | status | description |");
        Console.WriteLine("|---|---|---|");
        foreach (var (k, v) in ready.Entries) Console.WriteLine($"| {k} (ready) | {v.Status} | {v.Description} |");
        foreach (var (k, v) in live.Entries) Console.WriteLine($"| {k} (live) | {v.Status} | {v.Description} |");
        Lab.Check(ready.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy && live.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            "HealthCheckService (Microsoft.Extensions.Diagnostics.HealthChecks): readiness Unhealthy (the stuck reaction), liveness Healthy: a restart would not fix a backlog");
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) _ = await service.CheckHealthAsync(r => r.Tags.Contains("ready"));
        Lab.Info($"a readiness report over the cached snapshot: {Lab.F(sw.Elapsed.TotalMilliseconds * 1000 / 1000)} µs per probe (4 checks, no database round trip)");
#endif
#if WEB
        var web = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        web.WebHost.UseUrls("http://127.0.0.1:5199");
        web.Services.AddSingleton(signals);
        var wh = web.Services.AddHealthChecks();
        foreach (var name in new[] { "reactions", "dead-letters", "jobs", "projections" })
            wh.Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(name, _ => new HealthChecksProbe.SnapshotCheck(signals, name), null, ["ready"]));
        wh.AddCheck("process", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), ["live"]);
        var app = web.Build();
        Microsoft.AspNetCore.Builder.HealthCheckEndpointRouteBuilderExtensions.MapHealthChecks(app, "/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        Microsoft.AspNetCore.Builder.HealthCheckEndpointRouteBuilderExtensions.MapHealthChecks(app, "/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
        await app.StartAsync();
        using var http = new HttpClient();
        var l = await http.GetAsync("http://127.0.0.1:5199/health/live");
        var rd = await http.GetAsync("http://127.0.0.1:5199/health/ready");
        Lab.Check((int)l.StatusCode == 200 && (int)rd.StatusCode == 503, "Kestrel endpoints: /health/live 200, /health/ready 503", $"{(int)l.StatusCode} / {(int)rd.StatusCode} {await rd.Content.ReadAsStringAsync()}");
        await app.StopAsync();
#endif
        return Lab.Summary("health");
    }

    // ── spans: what an idle worker sends when Npgsql is traced
    private static async Task<int> Spans()
    {
        foreach (var filtered in new[] { false, true })
        {
            var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "Npgsql" || s.Name.StartsWith("Tarol", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => counts.AddOrUpdate($"{a.Source.Name} {(a.Parent is null && a.ParentId is null ? "root" : "child")}", 1, (_, n) => n + 1),
            };
            ActivitySource.AddActivityListener(listener);
            var schema = filtered ? "spans_f" : "spans";
            await Lab.Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP SCHEMA IF EXISTS {schema}_rm CASCADE");
            var dsb = new NpgsqlSlimDataSourceBuilder(Lab.Pg).EnableArrays();
            if (filtered)
                dsb.ConfigureTracing(o => o.ConfigureCommandFilter(_ => Activity.Current is not null).ConfigureBatchFilter(_ => Activity.Current is not null));
            await using var ds = dsb.Build();
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(ds);                 // UseNpgsqlReadModels resolves the data source from DI
            builder.Services.AddTarol(ProbeJson.Default, c => c.Add<Item>())
                .WithReactions(GeneratedReactions.Set)
                .UseNpgsqlStore(_ => ds, new NpgsqlStoreOptions { Schema = schema })
                .UseNpgsqlReadModels(new NpgsqlReadModelOptions { Schema = schema + "_rm" })
                .AddProjections(p => p.Add<ItemRow>())
                .AddReactionWorker()
                .AddJobs(j => j.Add<Nightly>("0 2 * * *"));
            using var host = builder.Build();
            await host.StartAsync();
            await Task.Delay(2000);
            counts.Clear();
            await Task.Delay(10_000);
            var idle = counts.ToDictionary();
            counts.Clear();
            var store = host.Services.GetRequiredService<NpgsqlAggregateStore>();
            var pipeline = host.Services.GetRequiredService<ChangePipeline>();
            for (var i = 0; i < 20; i++)
            {
                var item = Item.Create(Lab.Time.GetUtcNow());
                await Lab.Commit(store, item, 0, Lab.Context(Lab.TenantA));
                _ = await pipeline.Run(new TouchItem(item.Id), Lab.Context(Lab.TenantA));
            }
            await Task.Delay(3000);
            var busy = counts.ToDictionary();
            await host.StopAsync();
            string Show(Dictionary<string, int> d) => string.Join(", ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
            Console.WriteLine($"| {(filtered ? "Npgsql command filter: Activity.Current is not null" : "Npgsql traced (default)")} | {Show(idle)} | {Show(busy)} |");
            var idleNpgsqlRoots = idle.GetValueOrDefault("Npgsql root");
            if (!filtered)
                Lab.Check(idleNpgsqlRoots >= 50, "an idle worker (reactions, jobs, one projection) makes root Npgsql spans every second", $"{idleNpgsqlRoots} in 10 s = {idleNpgsqlRoots / 10.0:0.#}/s, {idleNpgsqlRoots * 8640:N0} a day");
            else
                Lab.Check(idleNpgsqlRoots == 0 && busy.GetValueOrDefault("Npgsql child") > 0, "with the filter: 0 idle Npgsql spans, the commands of reactions and changes stay as children", $"busy children {busy.GetValueOrDefault("Npgsql child")}");
        }
        return Lab.Summary("spans");
    }
}
