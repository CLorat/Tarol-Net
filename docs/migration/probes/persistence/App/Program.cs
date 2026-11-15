using System.Diagnostics;
using System.Text.Json;
using ArtFlow;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

var cs = Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Username=postgres;Password=probe;Maximum Pool Size=150";
var mode = args.Length > 0 ? args[0] : "functional";

// AOT: the slim builder; jsonb as text needs no extra resolver (EnableJsonTypes would add JsonDocument/JsonElement)
var dataSource = new NpgsqlSlimDataSourceBuilder(cs).EnableArrays().Build();
#pragma warning disable TVAL006 // the probe builds test data
var title = new Title("Sunflowers");
#pragma warning restore TVAL006
var json = ValueObjectJson.Options(AppJson.Default);   // V-9: resolver first, then the user's context
json.MakeReadOnly();
var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
var fails = 0;
void Check(string name, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}"); if (!ok) fails++; }
EventCodec NewCodec(bool upcaster = true)
{
    var c = new EventCodec(json).Add<Auction>(Catalogs.Auction).Add<Ledger>(Catalogs.Ledger);
    return upcaster ? c.Upcast<Auction>(new OpenedV1ToV2()) : c;
}
async Task Exec(string sql) { await using var c = dataSource.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
async Task<object?> Scalar(string sql) { await using var c = dataSource.CreateCommand(sql); return await c.ExecuteScalarAsync(); }
async Task<NpgsqlAggregateStore> NewStore(string schema, Guard guard = Guard.StreamRow, bool stateLookup = false, ReactionTable? reactions = null, bool upcaster = true)
{
    await Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE");
    var lookups = new List<LookupDefinition> { stateLookup ? new StateLookup("auction", "lot", "lot") : new KeyLookup("auction", "lot", a => ((Auction)a).Lot) };
    var s = new NpgsqlAggregateStore(dataSource, NewCodec(upcaster), time,
        new NpgsqlStoreOptions { Schema = schema, Guard = guard, StateOptions = json, Lookups = lookups, Reactions = reactions });
    await s.EnsureSchema(default);
    return s;
}
OperationContext Ctx(Option<Id<ITenant>> tenant, string user = "alice") => OperationContext.Start(Actor.User(user), tenant, time);
var ct = CancellationToken.None;

Console.WriteLine($"# {RuntimeInformationText()} mode={mode} postgres={await Scalar("SHOW server_version")}");
static string RuntimeInformationText() => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";

if (mode is "functional")
    await Functional();
else if (mode is "contention1")
    await Contention(Enum.Parse<Guard>(args[1]), int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), 25);
else if (mode is "contention")
    foreach (var guard in Enum.GetValues<Guard>())
        foreach (var writers in new[] { 1, 4, 16, 64 })
            await Contention(guard, writers, attempts: writers == 1 ? 200 : 25);
Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

async Task Functional()
{
    var tenant = (Option<Id<ITenant>>)Id<ITenant>.New(time);
    var other = (Option<Id<ITenant>>)Id<ITenant>.New(time);
    var reactions = new ReactionTable(e => e switch
    {
        Bid.Placed => BidReactions,
        Auction.Closed => CloseReactions,
        _ => ReadOnlySpan<string>.Empty,
    });
    var store = await NewStore("p_func", reactions: reactions);

    // --- append + load (state) + state == replay
    var id = Id<Auction>.New(time);
    var a = Auction.Open(id, "seller", title, "LOT-1", 100);
    _ = a.PlaceBid("bob", 120);
    _ = a.PlaceBid("carol", 130);
    var r = await store.Append(a, 0, Ctx(tenant), Option.None, ct);
    Check("create appends 3 events", r.IsSuccess, r.ToString() ?? "");
    AggregateChanges.Committed(a);
    var loaded = (await store.Load(id, Ctx(tenant), ct));
    Check("load from stored state", loaded.TryGetValue(out Auction? fromState) && store.StateLoads == 1 && fromState.Bids.Count == 2 && fromState.Version == 3);
    var replayed = Auction.Rehydrate(id, (await ReadHistory("p_func", id.Value)).AsSpan());
    Check("stored state == replay", AggregateState.Write(fromState!, json).AsSpan().SequenceEqual(AggregateState.Write(replayed, json)));
    Check("value object in payload is a plain string", ((string?)await Scalar($"SELECT payload->>'title' FROM p_func.events WHERE version = 1")) == "Sunflowers");
    Check("state jsonb is queryable", ((string?)await Scalar($"SELECT state->>'lot' FROM p_func.streams")) == "LOT-1"
        && Convert.ToInt64(await Scalar("SELECT jsonb_array_length(state->'bids') FROM p_func.streams")) == 2);

    // --- conflicts
    var again = Auction.Open(id, "x", title, "LOT-9", 1);
    var c0 = await store.Append(again, 0, Ctx(tenant), Option.None, ct);
    Check("expected 0 on an existing stream → Stream.VersionConflict", StoreErrors.IsVersionConflict(c0), c0.ToString() ?? "");
    var s1 = (await store.Load(id, Ctx(tenant), ct)).TryGetValue(out Auction? x1) ? x1 : null;
    var s2 = (await store.Load(id, Ctx(tenant), ct)).TryGetValue(out Auction? x2) ? x2 : null;
    _ = s1!.PlaceBid("dave", 140);
    _ = s2!.PlaceBid("erin", 150);
    var w1 = await store.Append(s1, 3, Ctx(tenant), Option.None, ct);
    var w2 = await store.Append(s2, 3, Ctx(tenant), Option.None, ct);
    Check("stale expected version → conflict, first wins", w1.IsSuccess && StoreErrors.IsVersionConflict(w2), w2.ToString() ?? "");
    Check("conflict detail carries the actual version", w2.TryGetValue(out Violation? wv) && wv is Error { Detail: "expected version 3, found 4" }, (wv as Error)?.Detail ?? "");
    Check("nothing of the loser written", (long)(await Scalar("SELECT count(*) FROM p_func.events"))! == 4);
    try { await store.Append(s2, 4, Ctx(tenant), Option.None, ct); Check("wrong expected → ArgumentException", false); }
    catch (ArgumentException) { Check("wrong expected → ArgumentException", true); }

    // --- tenant isolation
    Check("another tenant: load → None", !(await store.Load(id, Ctx(other), ct)).TryGetValue(out Auction? _));
    Check("no tenant ≠ a tenant", !(await store.Load(id, Ctx(Option.None), ct)).TryGetValue(out Auction? _));
    Check("lookup is found in its tenant", (await store.Find<Auction>("lot", "LOT-1", Ctx(tenant), ct)).TryGetValue(out Id<Auction> f) && f == id);
    Check("lookup is not found in another tenant", !(await store.Find<Auction>("lot", "LOT-1", Ctx(other), ct)).TryGetValue(out Id<Auction> _));
    var sameIdOtherTenant = Auction.Open(id, "o", title, "LOT-1", 1);
    Check("same id and lot in another tenant commits", (await store.Append(sameIdOtherTenant, 0, Ctx(other), Option.None, ct)).IsSuccess);

    // --- lookups (L-A)
    var b = Auction.Open(Id<Auction>.New(time), "s", title, "LOT-1", 1);
    var taken = await store.Append(b, 0, Ctx(tenant), Option.None, ct);
    Check("taken lookup key → Lookup.Taken, nothing written", taken.TryGetValue(out Violation? v) && v is Error { Code: StoreErrors.LookupTakenCode }
        && !(await store.Load(b.Id, Ctx(tenant), ct)).TryGetValue(out Auction? _), taken.ToString() ?? "");
    var mover = (await store.Load(id, Ctx(tenant), ct)).TryGetValue(out Auction? m) ? m : null;
    _ = mover!.Relot("LOT-2");
    Check("relot commits", (await store.Append(mover, 4, Ctx(tenant), Option.None, ct)).IsSuccess);
    Check("lookup moved", (await store.Find<Auction>("lot", "LOT-2", Ctx(tenant), ct)).TryGetValue(out Id<Auction> _)
        && !(await store.Find<Auction>("lot", "LOT-1", Ctx(tenant), ct)).TryGetValue(out Id<Auction> _));
    try { _ = await store.Find<Auction>("nope", "1", Ctx(tenant), ct); Check("undefined lookup throws", false); }
    catch (InvalidOperationException) { Check("undefined lookup throws", true); }

    // --- idempotency keys
    var key = new IdempotencyKey(tenant, "payments", "evt_1");
    var k1 = Auction.Open(Id<Auction>.New(time), "s", title, "LOT-K", 1);
    Check("append with a key", (await store.Append(k1, 0, Ctx(tenant), key, ct)).IsSuccess);
    Check("Seen returns the first outcome", (await store.Seen(key, ct)).TryGetValue(out StoredOutcome so) && so.StreamId == k1.Id.Value && so.Version == 1 && so.Events == 1);
    var k2 = Auction.Open(Id<Auction>.New(time), "s", title, "LOT-K2", 1);
    var dup = await store.Append(k2, 0, Ctx(tenant), key, ct);
    Check("key stored meanwhile → Idempotency.DuplicateKey, nothing written", dup.TryGetValue(out Violation? dv) && dv is Error { Code: StoreErrors.DuplicateKeyCode }
        && !(await store.Load(k2.Id, Ctx(tenant), ct)).TryGetValue(out Auction? _), dup.ToString() ?? "");
    var failKey = new IdempotencyKey(tenant, "payments", "evt_2");
    _ = await store.Append(Auction.Open(id, "s", title, "LOT-Z", 1), 0, Ctx(tenant), failKey, ct);
    Check("failed commit stores no key", !(await store.Seen(failKey, ct)).TryGetValue(out StoredOutcome _));
    Check("keys are tenant-scoped", !(await store.Seen(new IdempotencyKey(other, "payments", "evt_1"), ct)).TryGetValue(out StoredOutcome _));
    time.Advance(TimeSpan.FromDays(8));
    Check("retention purge deletes old keys in batches", await Retention.PurgeKeys(dataSource, time.GetUtcNow() - TimeSpan.FromDays(7), 1000, ct, "p_func") == 1
        && !(await store.Seen(key, ct)).TryGetValue(out StoredOutcome _));

    // --- metadata (RecordedEvent through ReadAll)
    var events = new List<PositionedEvent>();
    await foreach (var e in store.ReadAll(GlobalPosition.Start, 100, naive: false, ct))
        events.Add(e);
    var firstCommit = events.Where(e => e.Event.StreamId == id.Value && e.Event.Context.Tenant == tenant).Take(3).ToArray();
    Check("ReadAll returns RecordedEvents in commit order", events.Count == 7 && firstCommit.Select(e => e.Event.Event.Version).SequenceEqual([1L, 2, 3]), $"{events.Count}");
    Check("one RecordedAt per commit", firstCommit.Select(e => e.Event.RecordedAt).Distinct().Count() == 1);
    Check("event ids are UUIDv7 and distinct", firstCommit.All(e => e.Event.EventId.Version == 7) && firstCommit.Select(e => e.Event.EventId).Distinct().Count() == 3);
    Check("actor, tenant, correlation stored per event", firstCommit.All(e => e.Event.Context.Actor == Actor.User("alice") && e.Event.Context.Tenant == tenant));
    Check("entity id and parent id round-trip", firstCommit[1].Event.Event.ParentId == id.Value && firstCommit[1].Event.Event.EntityId != id.Value
        && firstCommit[0].Event.Event.ParentId == Guid.Empty);
    var caused = Ctx(tenant).CausedBy((Id<ICausation>)firstCommit[1].Event.EventId).AsSystem("Reactions.NotifySeller");
    var c3 = (await store.Load(k1.Id, Ctx(tenant), ct)).TryGetValue(out Auction? k1l) ? k1l : null;
    _ = c3!.Close();
    _ = await store.Append(c3, 1, caused, Option.None, ct);
    await foreach (var e in store.ReadAll(events[^1].Position, 10, naive: false, ct))
        Check("causation and system actor stored", e.Event.Context.CausationId == (Option<Id<ICausation>>)(Id<ICausation>)firstCommit[1].Event.EventId
            && e.Event.Context.Actor.Kind == ActorKind.System && e.Event.Context.Actor.OnBehalfOf == (Option<string>)"alice");

    // --- outbox rows written in the commit
    var outbox = (long)(await Scalar("SELECT count(*) FROM p_func.outbox"))!;
    Check("one outbox row per (event, reaction)", outbox == 2 * 3 + 1, $"{outbox}");   // 3 bids × 2 reactions + 1 close
    Check("no outbox rows of the failed commits", (long)(await Scalar("SELECT count(*) FROM p_func.outbox o WHERE NOT EXISTS (SELECT 1 FROM p_func.events e WHERE e.event_id = o.event_id)"))! == 0);

    // --- [LoadByReplay]
    var ledger = Ledger.Start(Id<Ledger>.New(time));
    _ = ledger.Book(5); _ = ledger.Book(7);
    _ = await store.Append(ledger, 0, Ctx(tenant), Option.None, ct);
    var before = store.Replays;
    Check("[LoadByReplay] replays, schema 0, no state", (await store.Load(ledger.Id, Ctx(tenant), ct)).TryGetValue(out Ledger? lg) && lg.Total == 12 && store.Replays == before + 1
        && (int)(await Scalar("SELECT schema FROM p_func.streams WHERE type = 'ledger'"))! == 0 && await Scalar("SELECT state FROM p_func.streams WHERE type = 'ledger'") is DBNull);

    // --- schema change → replay + rewrite on the next change; RebuildStates
    await Exec($"UPDATE p_func.streams SET schema = 42 WHERE type = 'auction'");
    before = store.Replays;
    var stale = (await store.Load(id, Ctx(tenant), ct)).TryGetValue(out Auction? st) ? st : null;
    Check("another schema → replay", stale is not null && store.Replays == before + 1 && stale.Lot == "LOT-2");
    _ = stale!.Relot("LOT-3");
    _ = await store.Append(stale, 5, Ctx(tenant), Option.None, ct);
    Check("next change rewrites the row with the current schema", (int)(await Scalar($"SELECT schema FROM p_func.streams WHERE id = '{id.Value}' AND tenant = '{(tenant.TryGetValue(out Id<ITenant> tg) ? tg.Value : Guid.Empty)}'"))! == Auction.StateSchema);
    var rebuilt = await store.RebuildStates<Auction>(Ctx(tenant), ct);
    Check("RebuildStates rewrites every stale row", rebuilt == 2 && (long)(await Scalar($"SELECT count(*) FROM p_func.streams WHERE type = 'auction' AND schema <> {Auction.StateSchema}"))! == 0, $"{rebuilt}");

    // --- upcaster round trip: a v1 payload written by an older release
    var oldId = Guid.CreateVersion7();
    await Exec($$"""
        INSERT INTO p_func.streams VALUES ('{{Guid.Empty}}', 'auction', '{{oldId}}', 1, 0, NULL, now());
        INSERT INTO p_func.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, payload, recorded_at, actor_kind, actor_id, correlation_id)
        VALUES ('{{Guid.Empty}}', 'auction', '{{oldId}}', 1, '{{Guid.CreateVersion7()}}', 'auction.opened', 1, '{{oldId}}',
                '{"seller":"old","title":"Irises","lot":"OLD-1","reserve":10}', now(), 'User', 'legacy', '{{Guid.CreateVersion7()}}');
        """);
    Check("v1 payload upcast on load", (await store.Load((Id<Auction>)oldId, Ctx(Option.None), ct)).TryGetValue(out Auction? up) && up.Currency == "EUR" && up.Title.Value == "Irises");
    var noUp = new NpgsqlAggregateStore(dataSource, NewCodec(upcaster: false), time, new NpgsqlStoreOptions { Schema = "p_func", StateOptions = json });
    try { _ = await noUp.Load((Id<Auction>)oldId, Ctx(Option.None), ct); Check("v1 without an upcaster throws", false); }
    catch (InvalidOperationException e) { Check("v1 without an upcaster throws", e.Message.Contains("auction.opened' v1", StringComparison.Ordinal), e.Message); }
    await Exec($"UPDATE p_func.events SET alias = 'auction.renamed' WHERE stream_id = '{oldId}'");
    try { _ = await store.Load((Id<Auction>)oldId, Ctx(Option.None), ct); Check("unknown alias throws", false); }
    catch (InvalidOperationException e) { Check("unknown alias throws", e.Message.Contains("auction.renamed", StringComparison.Ordinal), e.Message); }

    // --- codec registration fails at startup without JSON metadata
    try { _ = new EventCodec(new JsonSerializerOptions { TypeInfoResolver = JsonSerializerContextOf() }).Add<Auction>(Catalogs.Auction); Check("missing metadata throws at registration", false); }
    catch (InvalidOperationException e) { Check("missing metadata throws at registration", e.Message.Contains("Opened", StringComparison.Ordinal), e.Message); }

    await LookupsByState(tenant);
    await GuardsAndConflicts();
    await ReadAllGaps();
    await OutboxClaimsProbe();
}

static System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver JsonSerializerContextOf()
    => System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine();

async Task<StreamEvent[]> ReadHistory(string schema, Guid id)
{
    var codec = NewCodec();
    await using var cmd = dataSource.CreateCommand($"SELECT version, entity_id, parent_id, alias, alias_version, payload::text FROM {schema}.events WHERE stream_id = $1 ORDER BY version");
    cmd.Parameters.Add(new() { Value = id });
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<StreamEvent>();
    while (await r.ReadAsync())
        list.Add(new(r.GetInt64(0), r.GetGuid(1), r.IsDBNull(2) ? Guid.Empty : r.GetGuid(2), codec.Decode("auction", r.GetString(3), r.GetInt32(4), System.Text.Encoding.UTF8.GetBytes(r.GetString(5)))));
    return [.. list];
}

async Task LookupsByState(Option<Id<ITenant>> tenant)
{
    // L-B: a partial unique expression index on the state JSON
    var store = await NewStore("p_lb", stateLookup: true);
    var a = Auction.Open(Id<Auction>.New(time), "s", title, "LOT-1", 1);
    _ = await store.Append(a, 0, Ctx(tenant), Option.None, ct);
    Check("L-B: Find through the state index", (await store.Find<Auction>("lot", "LOT-1", Ctx(tenant), ct)).TryGetValue(out Id<Auction> f) && f == a.Id);
    var b = Auction.Open(Id<Auction>.New(time), "s", title, "LOT-1", 1);
    var taken = await store.Append(b, 0, Ctx(tenant), Option.None, ct);
    Check("L-B: duplicate → 23505 on the index → Lookup.Taken", taken.TryGetValue(out Violation? v) && v is Error { Code: StoreErrors.LookupTakenCode }, taken.ToString() ?? "");
    var plan = string.Join(" | ", await Lines("EXPLAIN SELECT id FROM p_lb.streams WHERE tenant = '00000000-0000-0000-0000-000000000000' AND type = 'auction' AND state->>'lot' = 'LOT-1'"));
    Check("L-B: Find uses the expression index", plan.Contains("lookup_auction_lot", StringComparison.Ordinal), plan);
}

async Task<List<string>> Lines(string sql)
{
    await using var c = dataSource.CreateCommand(sql);
    await using var r = await c.ExecuteReaderAsync();
    var l = new List<string>();
    while (await r.ReadAsync()) l.Add(r.GetString(0));
    return l;
}

async Task GuardsAndConflicts()
{
    foreach (var guard in Enum.GetValues<Guard>())
    {
        var store = await NewStore("p_guard", guard);
        var a = Auction.Open(Id<Auction>.New(time), "s", title, "L", 1);
        _ = await store.Append(a, 0, Ctx(Option.None), Option.None, ct);
        var create = await store.Append(Auction.Open(a.Id, "s", title, "L2", 1), 0, Ctx(Option.None), Option.None, ct);
        var l1 = (await store.Load(a.Id, Ctx(Option.None), ct)).TryGetValue(out Auction? x) ? x : null;
        var l2 = (await store.Load(a.Id, Ctx(Option.None), ct)).TryGetValue(out Auction? y) ? y : null;
        _ = l1!.Relot("A"); _ = l2!.Relot("B");
        var r1 = await store.Append(l1, 1, Ctx(Option.None), Option.None, ct);
        var r2 = await store.Append(l2, 1, Ctx(Option.None), Option.None, ct);
        Check($"{guard}: duplicate creation and stale append → VersionConflict", StoreErrors.IsVersionConflict(create) && r1.IsSuccess && StoreErrors.IsVersionConflict(r2), $"{create} / {r2}");
    }
}

async Task Contention(Guard guard, int writers, int attempts)
{
    var store = await NewStore("p_cont", guard);
    var id = Id<Auction>.New(time);
    _ = await store.Append(Auction.Open(id, "s", title, "L", 1), 0, Ctx(Option.None), Option.None, ct);
    int ok = 0, conflict = 0, other = 0, thrown = 0;
    string? firstError = null;
    var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
    {
        for (var i = 0; i < attempts; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                var a = (await store.Load(id, Ctx(Option.None), ct)).TryGetValue(out Auction? x) ? x : throw new InvalidOperationException("missing");
                var expected = a.Version;
                _ = a.Relot($"L-{w}-{i}");
                var r = await store.Append(a, expected, Ctx(Option.None), Option.None, ct);
                if (r.IsSuccess) Interlocked.Increment(ref ok);
                else if (StoreErrors.IsVersionConflict(r)) Interlocked.Increment(ref conflict);
                else Interlocked.Increment(ref other);
            }
            catch (Exception e) { if (Interlocked.Increment(ref thrown) == 1) firstError = $"{e.GetType().Name}: {(e as PostgresException)?.SqlState} {e.Message.Split('\n')[0]}"; }
            latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
    })));
    sw.Stop();
    var version = (long)(await Scalar($"SELECT version FROM p_cont.streams"))!;
    var gapless = (bool)(await Scalar("SELECT count(*) = max(version) AND min(version) = 1 FROM p_cont.events"))!;
    var sorted = latencies.Order().ToArray();
    Console.WriteLine($"| {guard,-15} | {writers,3} | {writers * attempts,5} | {ok,5} | {conflict,5} | {other} | {thrown} | {(version == 1 + ok && gapless ? "yes" : "NO")} | {writers * attempts / sw.Elapsed.TotalSeconds,8:F0} | {sorted[sorted.Length / 2],6:F2} | {sorted[(int)(sorted.Length * 0.99)],6:F2} |");
    if (firstError is not null) Console.WriteLine($"  first exception: {firstError}");
    if (!(version == 1 + ok && gapless && other == 0 && thrown == 0)) fails++;
}

async Task ReadAllGaps()
{
    var store = await NewStore("p_all");
    async Task<NpgsqlConnection> Open() => await dataSource.OpenConnectionAsync();
    string Row(string stream) => $"""
        INSERT INTO p_all.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, payload, recorded_at, actor_kind, actor_id, correlation_id)
        VALUES ('{Guid.Empty}', 'ledger', '{stream}', 1, '{Guid.CreateVersion7()}', 'ledger.started', 1, '{stream}', '{"{}"}', now(), 'User', 'u', '{Guid.CreateVersion7()}')
        """;
    async Task<List<PositionedEvent>> Read(GlobalPosition after, bool naive)
    {
        var l = new List<PositionedEvent>();
        await foreach (var e in store.ReadAll(after, 100, naive, ct)) l.Add(e);
        return l;
    }
    async Task Sql(NpgsqlConnection c, string sql) { await using var cmd = new NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }

    // case 1: A takes a position, B commits a later position first
    foreach (var naive in new[] { true, false })
    {
        await Exec("TRUNCATE p_all.events RESTART IDENTITY");
        await using var a = await Open(); await using var b = await Open();
        await Sql(a, "BEGIN"); await Sql(a, Row(Guid.NewGuid().ToString()));
        await Sql(b, "BEGIN"); await Sql(b, Row(Guid.NewGuid().ToString())); await Sql(b, "COMMIT");
        var first = await Read(GlobalPosition.Start, naive);
        var checkpoint = first.Count > 0 ? first[^1].Position with { Transaction = naive ? 0 : first[^1].Position.Transaction } : GlobalPosition.Start;
        await Sql(a, "COMMIT");
        var second = await Read(checkpoint, naive);
        var seen = first.Count + second.Count;
        Check($"ReadAll {(naive ? "naive (position > checkpoint)" : "safe (xmin, (tx, position))")}: late commit {(naive ? "is skipped" : "is not skipped")}",
            naive ? seen == 1 : seen == 2 && first.Count == 0, $"first read {first.Count}, second read {second.Count}");
    }
    // case 2: B has the older transaction id but the later position; the position-only checkpoint skips A, (tx, position) does not
    await Exec("TRUNCATE p_all.events RESTART IDENTITY");
    {
        await using var a = await Open(); await using var b = await Open();
        await Sql(b, "BEGIN"); await Sql(b, "SELECT pg_current_xact_id()");
        await Sql(a, "BEGIN"); await Sql(a, "SELECT pg_current_xact_id()"); await Sql(a, Row(Guid.NewGuid().ToString()));
        await Sql(b, Row(Guid.NewGuid().ToString())); await Sql(b, "COMMIT");
        var first = await Read(GlobalPosition.Start, naive: false);
        await Sql(a, "COMMIT");
        var byPosition = await Read(first[^1].Position with { Transaction = ulong.MaxValue / 2 }, naive: false);   // emulates "position > p" only
        var byPair = await Read(first[^1].Position, naive: false);
        Check("ReadAll safe: older xid with later position is returned first", first.Count == 1 && first[0].Position.Position == 2, $"{first.Count}");
        Check("ReadAll: (tx, position) checkpoint returns the late commit", byPair.Count == 1 && byPair[0].Position.Position == 1, $"{byPair.Count}");
        Check("ReadAll: a position-only checkpoint would have skipped it", byPosition.Count == 0 || byPosition[0].Position.Position == 1, $"{byPosition.Count}");
    }
}

async Task OutboxClaimsProbe()
{
    var store = await NewStore("p_out", reactions: e => e is Auction.Relotted ? NotifyReactions : ReadOnlySpan<string>.Empty);
    var claims = new OutboxClaims(dataSource, "p_out");
    var ids = new List<Id<Auction>>();
    for (var s = 0; s < 10; s++)
    {
        var a = Auction.Open(Id<Auction>.New(time), "s", title, $"L{s}", 1);
        for (var i = 0; i < 5; i++) _ = a.Relot($"L{s}-{i}");
        _ = await store.Append(a, 0, Ctx(Option.None), Option.None, ct);
        ids.Add(a.Id);
    }
    // O-1: 4 concurrent workers, no row claimed twice
    var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        while (true)
        {
            var rows = await claims.Claim(3, TimeSpan.FromMinutes(1), time.GetUtcNow(), OutboxOrder.None, ct);
            if (rows.Count == 0) break;
            foreach (var r in rows) claimed.Add(r.Id);
        }
    })));
    Check("O-1: 4 workers with SKIP LOCKED claim every row exactly once", claimed.Count == 50 && claimed.Distinct().Count() == 50, $"{claimed.Count}");

    // O-2: head per (stream, reaction): workers complete rows; per stream the versions are processed in order
    await Exec("UPDATE p_out.outbox SET lease_until = NULL, attempts = 0");
    var processed = new System.Collections.Concurrent.ConcurrentQueue<(Guid Stream, long Version)>();
    var maxParallelPerStream = 0;
    var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        var idle = 0;
        while (idle < 3)
        {
            var rows = await claims.Claim(5, TimeSpan.FromMinutes(1), time.GetUtcNow(), OutboxOrder.HeadPerStream, ct);
            if (rows.Count == 0) { idle++; await Task.Delay(5); continue; }
            idle = 0;
            foreach (var r in rows)
            {
                var n = inFlight.AddOrUpdate(r.StreamId, 1, (_, c) => c + 1);
                lock (inFlight) maxParallelPerStream = Math.Max(maxParallelPerStream, n);
                processed.Enqueue((r.StreamId, r.Version));
                await Task.Delay(1);
                inFlight.AddOrUpdate(r.StreamId, 0, (_, c) => c - 1);
                await claims.Complete(r.Id, ct);
            }
        }
    })));
    var inOrder = processed.GroupBy(p => p.Stream).All(g => g.Select(p => p.Version).SequenceEqual(g.Select(p => p.Version).Order()));
    Check("O-2: every row once, per stream in version order, one in flight per stream", processed.Count == 50 && inOrder && maxParallelPerStream == 1, $"{processed.Count}, max {maxParallelPerStream}");
    var plan = string.Join(" | ", await Lines("EXPLAIN SELECT o.id FROM p_out.outbox o WHERE o.due_at <= now() AND NOT EXISTS (SELECT 1 FROM p_out.outbox p WHERE p.tenant = o.tenant AND p.type = o.type AND p.stream_id = o.stream_id AND p.reaction = o.reaction AND p.id < o.id) ORDER BY o.id LIMIT 10"));
    Console.WriteLine($"  O-2 plan: {plan}");
}

// generated reaction tables return static arrays: a collection expression in a span-returning lambda is CS9203
internal static partial class Program
{
    private static readonly string[] BidReactions = ["NotifySeller", "UpdateLeaderboard"];
    private static readonly string[] CloseReactions = ["SettleAuction"];
    private static readonly string[] NotifyReactions = ["Notify"];
}

