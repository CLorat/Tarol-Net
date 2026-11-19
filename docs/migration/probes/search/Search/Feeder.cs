using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Probe.Search;

/// <summary>A search document as the generated map would emit it: the hit's own id and type, the aggregate it is shown through.</summary>
public sealed record SearchDoc(string Type, Guid Id, Guid Aggregate, string Title, string A, string B, string[] Blind);

/// <summary>What the generator would emit for Artwork (the probe writes it by hand): one document for the root, one per edition.</summary>
public static class ArtworkSearch
{
    public const string Type = "artwork";
    public static byte[] BlindKey = RandomNumberGenerator.GetBytes(32);   // stands in for the tenant's lookup key (DP-5)

    public static string Blind(string value) => Convert.ToHexString(HMACSHA256.HashData(BlindKey, Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant())));

    public static IEnumerable<SearchDoc> Documents(Artwork a, bool plaintextPersonal)
    {
        string[] blind = a.Owner is { } o ? [Blind(o)] : [];
        yield return new SearchDoc(Type, a.Id.Value, a.Id.Value, a.Title, a.Title, plaintextPersonal && a.Owner is { } p ? a.Description + " " + p : a.Description, blind);
        foreach (var e in a.Editions)
            yield return new SearchDoc(Type + ".edition", e.Id.Value, a.Id.Value, a.Title, e.Label, "", []);
    }
}

/// <summary>
/// The feeder prototype: one loop over <see cref="IEventFeed"/> (like a ProjectionHost), the streams of a searchable type in each batch,
/// each loaded once (its current state), its documents replaced, and the checkpoint moved in the same transaction (guarded).
/// </summary>
public sealed class SearchFeeder(NpgsqlAggregateStore store, NpgsqlDataSource db, string schema, bool plaintextPersonal = false, int loaders = 1)
{
    public GlobalPosition Checkpoint { get; private set; }
    public int Loaded { get; private set; }

    public static string Ddl(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {schema}.search_v1 (
          tenant uuid NOT NULL, type text NOT NULL, id uuid NOT NULL, aggregate uuid NOT NULL, title text NOT NULL,
          a text NOT NULL, b text NOT NULL, blind text[] NOT NULL,
          tsv tsvector GENERATED ALWAYS AS (setweight(to_tsvector('english', a), 'A') || setweight(to_tsvector('english', b), 'B')) STORED,
          PRIMARY KEY (tenant, type, id));
        CREATE INDEX IF NOT EXISTS search_v1_tsv ON {schema}.search_v1 USING gin (tenant, tsv);
        CREATE INDEX IF NOT EXISTS search_v1_blind ON {schema}.search_v1 USING gin (tenant, blind);
        CREATE INDEX IF NOT EXISTS search_v1_aggregate ON {schema}.search_v1 (tenant, aggregate);
        CREATE TABLE IF NOT EXISTS {schema}.search_checkpoints (name text PRIMARY KEY, tx xid8 NOT NULL, pos bigint NOT NULL);
        INSERT INTO {schema}.search_checkpoints VALUES ('search:v1', '0', 0) ON CONFLICT DO NOTHING;
        """;

    /// <summary>Reads after the checkpoint until the feed is empty; returns the number of batches.</summary>
    public async Task<int> CatchUp(int batch = 2000)
    {
        var batches = 0;
        while (true)
        {
            var events = new List<PositionedEvent>(batch);
            await foreach (var e in store.ReadAll(Checkpoint, batch, default)) events.Add(e);
            if (events.Count == 0) return batches;
            var streams = events.Where(e => e.StreamType == Artwork.StreamType)
                .Select(e => (e.Event.Context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty, e.Event.StreamId)).Distinct().ToArray();
            await Write(streams, Checkpoint, events[^1].Position);
            Checkpoint = events[^1].Position;
            batches++;
        }
    }

    /// <summary>Loads each stream's current state and replaces its documents; moves the checkpoint (when from != to) in the same transaction.</summary>
    public async Task Write(IReadOnlyList<(Guid Tenant, Guid Stream)> streams, GlobalPosition from, GlobalPosition to)
    {
        var docs = new List<(Guid Tenant, SearchDoc Doc)>();
        var loaded = new (Guid Tenant, Guid Stream, Artwork? A)[streams.Count];
        await Parallel.ForAsync(0, streams.Count, new ParallelOptions { MaxDegreeOfParallelism = loaders }, async (i, ct) =>
        {
            var (tenant, id) = streams[i];
            var a = await store.Load((Id<Artwork>)id, Context(tenant), ct);
            loaded[i] = (tenant, id, a.TryGetValue(out Artwork? x) ? x : null);
        });
        Loaded += streams.Count;
        foreach (var (tenant, _, a) in loaded)
            if (a is not null)
                docs.AddRange(ArtworkSearch.Documents(a, plaintextPersonal).Select(d => (tenant, d)));
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        if (from != to)
        {
            await using var guard = new NpgsqlCommand($"UPDATE {schema}.search_checkpoints SET tx = $3::text::xid8, pos = $4 WHERE name = 'search:v1' AND tx = $1::text::xid8 AND pos = $2", c, tx);
            guard.Parameters.Add(new() { Value = from.Transaction.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            guard.Parameters.Add(new() { Value = from.Position });
            guard.Parameters.Add(new() { Value = to.Transaction.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            guard.Parameters.Add(new() { Value = to.Position });
            if (await guard.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("another runner moved the checkpoint");
        }
        await using (var del = new NpgsqlCommand($"DELETE FROM {schema}.search_v1 s USING unnest($1::uuid[], $2::uuid[]) k(t, a) WHERE s.tenant = k.t AND s.aggregate = k.a", c, tx))
        {
            del.Parameters.Add(new() { Value = streams.Select(s => s.Tenant).ToArray() });
            del.Parameters.Add(new() { Value = streams.Select(s => s.Stream).ToArray() });
            await del.ExecuteNonQueryAsync();
        }
        if (docs.Count > 0)
        {
            await using var ins = new NpgsqlCommand($"""
                INSERT INTO {schema}.search_v1 (tenant, type, id, aggregate, title, a, b, blind)
                SELECT t, ty, i, ag, ti, a, b, CASE WHEN bl = '' THEN ARRAY[]::text[] ELSE ARRAY[bl] END
                FROM unnest($1::uuid[], $2::text[], $3::uuid[], $4::uuid[], $5::text[], $6::text[], $7::text[], $8::text[]) x(t, ty, i, ag, ti, a, b, bl)
                """, c, tx);
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Tenant).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.Type).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.Id).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.Aggregate).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.Title).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.A).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.B).ToArray() });
            ins.Parameters.Add(new() { Value = docs.Select(d => d.Doc.Blind.FirstOrDefault() ?? "").ToArray() });
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public static OperationContext Context(Guid tenant) => OperationContext.Start(Actor.System("search"), (Id<ITenant>)tenant, TimeProvider.System);
}

public static class Feeder
{
    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var c = db.CreateCommand(sql);
        c.CommandTimeout = 3600;
        await c.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string Type, Guid Id, Guid Aggregate, string Title)>> Find(NpgsqlDataSource db, string schema, Guid tenant, string text)
    {
        await using var c = db.CreateCommand($"""
            SELECT type, id, aggregate, title FROM {schema}.search_v1, websearch_to_tsquery('english', $2) q
            WHERE tenant = $1 AND (tsv @@ q OR blind @> ARRAY[$3]) ORDER BY ts_rank(tsv, q) DESC, id LIMIT 20
            """);
        c.Parameters.Add(new() { Value = tenant });
        c.Parameters.Add(new() { Value = text });
        c.Parameters.Add(new() { Value = ArtworkSearch.Blind(text) });
        var r = new List<(string, Guid, Guid, string)>();
        await using var rd = await c.ExecuteReaderAsync();
        while (await rd.ReadAsync()) r.Add((rd.GetString(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3)));
        return r;
    }

    public static async Task<(NpgsqlAggregateStore Store, NpgsqlKeyStore Keys)> Store(NpgsqlDataSource db, string schema)
    {
        await Exec(db, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        var keys = new NpgsqlKeyStore(db, new ConfiguredKeyEncryptionKey("probe", RandomNumberGenerator.GetBytes(32)), TimeProvider.System, new NpgsqlKeyStoreOptions { Schema = schema });
        var store = new NpgsqlAggregateStore(db, new EventCodec(new JsonSerializerOptions(ProbeJson.Default.Options)).Add<Artwork>().Add<Note>(), TimeProvider.System,
            new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        await store.EnsureSchema(SchemaMode.Upgrade, default);
        await Exec(db, SearchFeeder.Ddl(schema));
        return (store, keys);
    }

    private static async Task<GlobalPosition> Commit<T>(NpgsqlAggregateStore store, T aggregate, long expected, Guid tenant)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var r = await store.Append(aggregate, expected, SearchFeeder.Context(tenant), Option.None, default);
        AggregateChanges.Committed(aggregate);
        return r.GetValueOrThrow();
    }

    public static async Task<int> Run(string[] args)
    {
        if (args is ["bench", var n]) return await RunBench(int.Parse(n, System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine($"# feeder selftest ({Bench.Runtime}): Tarol.Persistence.Npgsql store with sealing, the feeder prototype, the index in the store's database");
        await using var db = Bench.Db();
        const string schema = "search_feeder";
        var (store, keys) = await Store(db, schema);
        await using var keysScope = keys;
        Guid ta = Corpus.Tenant(700), tb = Corpus.Tenant(701);
        var now = DateTimeOffset.UtcNow;
        var sunset = Artwork.Create("Sunset over the harbour", "oil on canvas, boats at dusk", "Ada Lovelace", now);
        var e1 = sunset.AddEdition("first print run");
        var e2 = sunset.AddEdition("museum edition");
        await Commit(store, sunset, 0, ta);
        var lake = Artwork.Create("Mountain lake", "watercolour, glaciers", "Grace Hopper", now);
        await Commit(store, lake, 0, ta);
        var other = Artwork.Create("Sunset over the harbour", "tenant B's work", "Ada Lovelace", now);
        await Commit(store, other, 0, tb);
        await Commit(store, Note.Write("sunset harbour note", now), 0, ta);
        var feeder = new SearchFeeder(store, db, schema);
        await feeder.CatchUp();

        var h = await Find(db, schema, ta, "harbour");
        SelfTest.Check("aggregate found by its own fields; another stream type (Note) is not indexed", h.Count == 1 && h[0].Type == "artwork" && h[0].Id == sunset.Id.Value);
        h = await Find(db, schema, ta, "museum");
        SelfTest.Check("an entity is found by its own field and shown through its aggregate (type, aggregate id, the aggregate's title)",
            h is [var m] && m.Type == "artwork.edition" && m.Id == e2.Id.Value && m.Aggregate == sunset.Id.Value && m.Title == "Sunset over the harbour");
        h = await Find(db, schema, tb, "harbour");
        SelfTest.Check("tenant first: tenant B finds only its own", h is [var b] && b.Id == other.Id.Value);
        await using (var raw = db.CreateCommand($"SELECT count(*) FROM {schema}.search_v1 WHERE a LIKE '%Ada%' OR b LIKE '%Ada%' OR title LIKE '%Ada%'"))
            SelfTest.Check("a sealed [PersonalData] field never lands in the index as text (blind token only)", (long)(await raw.ExecuteScalarAsync())! == 0);
        h = await Find(db, schema, ta, "ada lovelace");
        SelfTest.Check("blind index: the exact value (normalised) finds its aggregate", h is [var bl] && bl.Id == sunset.Id.Value);
        h = await Find(db, schema, ta, "lovelace");
        SelfTest.Check("blind index: a part of the value does not (exact match only)", h.Count == 0);

        var v = sunset.Version;
        _ = sunset.Rename("Dawn over the harbour");
        await Commit(store, sunset, v, ta);
        await feeder.CatchUp();
        h = await Find(db, schema, ta, "dawn");
        var hd = await Find(db, schema, ta, "museum");
        SelfTest.Check("a change re-indexes the aggregate and its entities (the edition shows the new title)", h.Count == 1 && hd is [var ed] && ed.Title == "Dawn over the harbour");

        var replay = new SearchFeeder(store, db, schema);   // starts at Start: a replay of everything
        try { await replay.CatchUp(); SelfTest.Check("a second runner from an older checkpoint is refused by the guard", false); }
        catch (InvalidOperationException) { SelfTest.Check("a second runner from an older checkpoint is refused by the guard", true); }
        await Exec(db, $"UPDATE {schema}.search_checkpoints SET tx = '0', pos = 0");
        await replay.CatchUp();
        await using (var cnt = db.CreateCommand($"SELECT count(*) FROM {schema}.search_v1"))
            SelfTest.Check("a replay from the start is idempotent (documents replaced per stream, not duplicated)", (long)(await cnt.ExecuteScalarAsync())! == 5);

        // erasure: the key's tombstone, then the index participant re-indexes the subject's streams
        var sw = Stopwatch.StartNew();
        await keys.EraseSubject(ta, sunset.Id.Value, default);
        var tomb = sw.Elapsed;
        h = await Find(db, schema, ta, "ada lovelace");
        SelfTest.Check("after the tombstone alone the blind token still finds the subject (the index must take part in erasure)", h.Count == 1);
        sw.Restart();
        var subjectStreams = await store.StreamsOf(ta, sunset.Id.Value, default);
        await new SearchFeeder(store, db, schema).Write([.. subjectStreams.Select(s => (s.Tenant, s.Id))], default, default);
        var reindex = sw.Elapsed;
        h = await Find(db, schema, ta, "ada lovelace");
        SelfTest.Check("after the participant re-indexed the subject's streams it is no longer found", h.Count == 0, $"tombstone {tomb.TotalMilliseconds:F1} ms, find streams + re-index {reindex.TotalMilliseconds:F1} ms");
        h = await Find(db, schema, ta, "dawn");
        SelfTest.Check("the rest of the erased aggregate stays searchable", h.Count == 1);

        // the plaintext variant: what a plain index of a personal field would hold until the participant runs
        var (store2, keys2) = await Store(db, schema + "_plain");
        await using var keys2Scope = keys2;
        var ada = Artwork.Create("Portrait", "a portrait", "Ada Lovelace", now);
        await Commit(store2, ada, 0, ta);
        var plain = new SearchFeeder(store2, db, schema + "_plain", plaintextPersonal: true);
        await plain.CatchUp();
        SelfTest.Check("plaintext variant: the personal value is found by a partial word", (await Find(db, schema + "_plain", ta, "lovelace")).Count == 1);
        await keys2.EraseSubject(ta, ada.Id.Value, default);
        SelfTest.Check("plaintext variant: after the tombstone the plaintext is still in the index", (await Find(db, schema + "_plain", ta, "lovelace")).Count == 1);
        await plain.Write([(ta, ada.Id.Value)], default, default);
        SelfTest.Check("plaintext variant: after the re-index it is gone", (await Find(db, schema + "_plain", ta, "lovelace")).Count == 0);

        // export: what the index holds of a subject = the documents of the subject's streams
        await using (var ex = db.CreateCommand($"SELECT count(*) FROM {schema}.search_v1 WHERE tenant = $1 AND aggregate = ANY($2)"))
        {
            ex.Parameters.Add(new() { Value = ta });
            ex.Parameters.Add(new() { Value = subjectStreams.Select(s => s.Id).ToArray() });
            SelfTest.Check("export: the subject's streams locate its documents through (tenant, aggregate)", (long)(await ex.ExecuteScalarAsync())! == 3);
        }

        // retention: the streams are deleted, the participant deletes their documents
        var deleted = await store.DeleteStreams(Artwork.StreamType, DateTimeOffset.UtcNow.AddDays(1), 500, default);
        await new SearchFeeder(store, db, schema).Write([.. deleted.Select(s => (s.Tenant, s.Id))], default, default);
        await using (var left = db.CreateCommand($"SELECT count(*) FROM {schema}.search_v1"))
            SelfTest.Check("retention: deleted streams load as none and their documents go", deleted.Count == 3 && (long)(await left.ExecuteScalarAsync())! == 0);
        await Exec(db, $"DROP SCHEMA {schema} CASCADE; DROP SCHEMA {schema}_plain CASCADE");
        return SelfTest.Done("feeder");
    }

    /// <summary>Seeds n artworks (created + 2 editions + a rename = 2 commits) with 8 writers, then rebuilds the index from the feed.</summary>
    private static async Task<int> RunBench(int n)
    {
        await using var db = Bench.Db();
        const string schema = "search_feeder_bench";
        var (store, keys) = await Store(db, schema);
        await using var keysScope = keys;
        var sw = Stopwatch.StartNew();
        await Parallel.ForAsync(0, n, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, ct) =>
        {
            var r = new Random(i);
            var a = Artwork.Create(Corpus.Rank(1 + r.Next(5000)) + " " + Corpus.Rank(1 + r.Next(5000)), Corpus.Make(i).Body, "owner " + i, DateTimeOffset.UtcNow);
            a.AddEdition("edition " + Corpus.Rank(1 + r.Next(5000)));
            a.AddEdition("edition " + Corpus.Rank(1 + r.Next(5000)));
            var tenant = Corpus.TenantOf(i);
            await Commit(store, a, 0, tenant);
            var v = a.Version;
            _ = a.Rename(a.Title + " " + Corpus.Rank(7));
            await Commit(store, a, v, tenant);
        });
        Console.WriteLine($"INFO seeded {n:N0} artworks (4 events, 2 commits each, sealed owner) with 8 writers in {sw.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine("| loaders | streams | documents | s | streams/s | docs/s |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var loaders in new[] { 1, 4, 8, 1, 4, 8 })     // two rounds; the second is reported in the note
        {
            await Exec(db, $"TRUNCATE {schema}.search_v1; UPDATE {schema}.search_checkpoints SET tx = '0', pos = 0");
            var f = new SearchFeeder(store, db, schema, loaders: loaders);
            sw.Restart();
            await f.CatchUp();
            Console.WriteLine($"| {loaders} | {f.Loaded:N0} | {n * 3:N0} | {sw.Elapsed.TotalSeconds:F1} | {f.Loaded / sw.Elapsed.TotalSeconds:N0} | {n * 3 / sw.Elapsed.TotalSeconds:N0} |");
        }
        var load = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var t = Stopwatch.StartNew();
            await using var c = db.CreateCommand($"SELECT tenant, id FROM {schema}.streams WHERE type = 'artwork' OFFSET {i * 7 % n} LIMIT 1");
            await using var rd = await c.ExecuteReaderAsync();
            await rd.ReadAsync();
            var (tn, id) = (rd.GetGuid(0), rd.GetGuid(1));
            t.Restart();
            _ = await store.Load((Id<Artwork>)id, SearchFeeder.Context(tn), default);
            load.Add(t.Elapsed.TotalMilliseconds);
        }
        load.Sort();
        Console.WriteLine($"INFO one Load<Artwork> (stored state, sealed owner, keys cached after the first): p50 {Bench.P(load, 0.5):F2} ms, p99 {Bench.P(load, 0.99):F2} ms");
        await Exec(db, $"DROP SCHEMA {schema} CASCADE");
        return 0;
    }
}
