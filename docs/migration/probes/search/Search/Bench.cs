using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Probe.Search;

public static class Bench
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public static string Runtime => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";
    public static string PgConnection => Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55433;Username=postgres;Password=probe;Maximum Pool Size=20";
    public static string MeiliUrl => Environment.GetEnvironmentVariable("PROBE_MEILI") ?? "http://localhost:57700";
    public static string TypesenseUrl => Environment.GetEnvironmentVariable("PROBE_TYPESENSE") ?? "http://localhost:58108";
    public static string TypesenseKey => Environment.GetEnvironmentVariable("PROBE_TYPESENSE_KEY") ?? "probe-local-only";
    public static string OpenSearchUrl => Environment.GetEnvironmentVariable("PROBE_OPENSEARCH") ?? "http://localhost:59200";

    /// <summary>The data source; <paramref name="trgm"/> sets pg_trgm.word_similarity_threshold for its sessions (default 0.6).</summary>
    public static NpgsqlDataSource Db(double? trgm = null) => new NpgsqlSlimDataSourceBuilder(PgConnection
        + (trgm is { } t ? $";Options=-c pg_trgm.word_similarity_threshold={t.ToString(C)}" : "")).EnableArrays().Build();

    public static IEngine Engine(string name, NpgsqlDataSource db) => name switch
    {
        "pg" => new Pg(db),
        "meili" => new Meili(MeiliUrl),
        "typesense" => new Typesense(TypesenseUrl, TypesenseKey),
        "opensearch" => new OpenSearch(OpenSearchUrl),
        _ => throw new ArgumentException("engine: pg | meili | typesense | opensearch"),
    };

    /// <summary>The query classes: (name, match, terms). Ten terms per class, cycled.</summary>
    public static IEnumerable<(string Class, Match Match, string[] Terms, bool Facets, bool Snippets)> Classes()
    {
        string[] Words(int from) => [.. Enumerable.Range(from, 10).Select(Corpus.Rank)];
        yield return ("common (rank 10)", Match.Words, Words(10), false, false);
        yield return ("medium (rank 100)", Match.Words, Words(100), false, false);
        yield return ("rare (rank 3000)", Match.Words, Words(3000), false, false);
        yield return ("two words (100 & 300)", Match.Words, [.. Enumerable.Range(0, 10).Select(k => Corpus.Rank(100 + k) + " " + Corpus.Rank(300 + k))], false, false);
        yield return ("prefix (4 letters of rank 500)", Match.Prefix, [.. Enumerable.Range(500, 10).Select(r => Corpus.Rank(r)[..4])], false, false);
        yield return ("typo (rank 300, 1 substitution)", Match.Typo, [.. Enumerable.Range(300, 10).Select(r => Typo(Corpus.Rank(r)))], false, false);
        yield return ("medium + counts per type", Match.Words, Words(100), true, false);
        yield return ("medium + snippets", Match.Words, Words(100), false, true);
    }

    public static string Typo(string w)
    {
        var i = w.Length / 2;
        var c = w[i] == 'x' ? 'q' : 'x';
        return string.Concat(w.AsSpan(0, i), c.ToString(), w.AsSpan(i + 1));
    }

    public static async Task<int> Run(string[] args)
    {
        var engine = args[0];
        var sizes = (args.Length > 1 ? args[1] : "10000,100000,1000000").Split(',').Select(long.Parse).ToArray();
        await using var db = Db();
        await using var e = Engine(engine, db);
        Console.WriteLine($"# bench {e.Name} ({Runtime}), sizes {string.Join(" / ", sizes)}");
        await e.Reset();
        long have = 0;
        foreach (var size in sizes)
        {
            var sw = Stopwatch.StartNew();
            await e.Load(Corpus.Range(have, size));
            var load = sw.Elapsed;
            Console.WriteLine($"INFO {e.Name} load {have:N0} -> {size:N0}: {load.TotalSeconds:F1} s, {(size - have) / load.TotalSeconds:N0} docs/s{(have == 0 ? " (empty index)" : " (index maintained)")}");
            if (e is Pg pg)
            {
                if (have == 0)
                    Console.WriteLine($"INFO pg index build after the first load (gin + trgm): {(await pg.CreateIndexes()).TotalSeconds:F1} s");
                else
                    await pg.Exec($"ANALYZE {pg.Schema}.docs");
            }
            have = size;
            await Report(e, size);
            if (e is Pg p)
            {
                Console.WriteLine($"INFO pg switch to btree_gin (tenant, tsv): {(await p.SwitchIndex("btree_gin")).TotalSeconds:F1} s");
                await Report(e, size);
                await Queries(e, size, only: ["common (rank 10)", "medium (rank 100)", "rare (rank 3000)", "two words (100 & 300)", "medium + counts per type"]);
                await using var tdb = Db(trgm: 0.4);
                await using var trgm = new Pg(tdb, mode: "trgm");
                await Queries(trgm, size, only: ["prefix (4 letters of rank 500)", "typo (rank 300, 1 substitution)"]);
                await using var combined = new Pg(tdb, mode: "combined");
                await Queries(combined, size, only: ["typo (rank 300, 1 substitution)"]);
                Console.WriteLine($"INFO pg back to gin (tsv): {(await p.SwitchIndex("gin")).TotalSeconds:F1} s");
            }
            else
                await Queries(e, size);
        }

        // a feeder's writes at the final size: 20,000 changed documents in batches of 500 (one request or transaction each)
        var changed = Corpus.Range(0, 20_000).Select(d => d with { Title = d.Title + " " + Corpus.Rank(42) }).ToArray();
        var fw = Stopwatch.StartNew();
        foreach (var batch in Http.Chunks(changed, 500))
        {
            if (e is Pg pgw) await pgw.UpsertBatch(batch);
            else await e.Load(batch);
        }
        Console.WriteLine($"INFO {e.Name} feeder upserts at {have:N0}: 20,000 changed docs in batches of 500: {fw.Elapsed.TotalSeconds:F1} s, {20_000 / fw.Elapsed.TotalSeconds:N0} docs/s (each batch searchable on return)");

        // rebuild: a fresh index of every document (what a new version or a weight change costs)
        var rw = Stopwatch.StartNew();
        if (e is Pg rp)
        {
            await using var fresh = new Pg(db, schema: "search_probe_v2");
            await fresh.Reset();
            await fresh.Load(Corpus.Range(0, have));
            var copy = rw.Elapsed;
            var idx = await fresh.CreateIndexes();
            Console.WriteLine($"INFO pg rebuild of {have:N0} into a new table: load {copy.TotalSeconds:F1} s + indexes {idx.TotalSeconds:F1} s = {rw.Elapsed.TotalSeconds:F1} s");
            await fresh.Exec("DROP SCHEMA search_probe_v2 CASCADE");
        }
        else
        {
            await e.Reset();
            await e.Load(Corpus.Range(0, have));
            Console.WriteLine($"INFO {e.Name} rebuild of {have:N0} into an empty index: {rw.Elapsed.TotalSeconds:F1} s, {have / rw.Elapsed.TotalSeconds:N0} docs/s");
            Console.WriteLine($"INFO {e.Name} size after rebuild: {await e.Size() / 1e6:F0} MB");
        }
        return 0;
    }

    private static async Task Report(IEngine e, long size)
    {
        var bytes = await e.Size();
        var detail = "";
        if (e is Pg pg)
        {
            var (t, g, tr) = await pg.Sizes();
            detail = $" (table+toast {t / 1e6:F0} MB, {pg.IndexKind} {g / 1e6:F0} MB, title trgm {tr / 1e6:F0} MB)";
        }
        Console.WriteLine($"INFO {e.Name} size at {size:N0}: {bytes / 1e6:F0} MB{detail}");
        if (e is not Pg) return;
        await Queries(e, size, only: ["common (rank 10)", "medium (rank 100)", "rare (rank 3000)", "two words (100 & 300)", "prefix (4 letters of rank 500)",
            "typo (rank 300, 1 substitution)", "medium + counts per type", "medium + snippets"]);
    }

    private static readonly (string Name, Guid? Tenant)[] Scopes = [("tenant 10 %", Corpus.Big), ("tenant 0.1 %", Corpus.Small), ("all tenants", null)];

    public static async Task Queries(IEngine e, long size, string[]? only = null)
    {
        Console.WriteLine($"| docs | engine | query | scope | n | p50 ms | p99 ms | hits |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var (cls, match, terms, facets, snippets) in Classes())
        {
            if (only is not null && !only.Contains(cls)) continue;
            foreach (var (scope, tenant) in Scopes)
            {
                Hits? last = null;
                var samples = new List<double>();
                for (var round = 0; round < 2; round++)          // round 1 warms up; round 2 is reported
                {
                    samples.Clear();
                    var budget = Stopwatch.StartNew();
                    for (var i = 0; i < 100 && (i < 10 || budget.Elapsed < TimeSpan.FromSeconds(8)); i++)
                    {
                        var q = new Query(terms[i % terms.Length], tenant, match, 20, facets, Snippets: snippets);
                        var sw = Stopwatch.StartNew();
                        last = await e.Search(q);
                        samples.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }
                samples.Sort();
                var hits = last is null ? "" : last.Total >= 0 ? $"{last.Items.Count} of {last.Total:N0}" : $"{last.Items.Count}";
                Console.WriteLine($"| {size:N0} | {e.Name} | {cls} | {scope} | {samples.Count} | {P(samples, 0.5):F2} | {P(samples, 0.99):F2} | {hits} |");
            }
        }
    }

    /// <summary>Queries only, over the table a finished <c>bench pg</c> left (1M documents): bounded ranking at 1,000 and 10,000 candidates,
    /// both tenant index strategies.</summary>
    public static async Task<int> Bounded()
    {
        await using var db = Db();
        var pg = new Pg(db);
        var size = await pg.Scalar<long>($"SELECT count(*) FROM {pg.Schema}.docs");
        Console.WriteLine($"# bench pg bounded ranking ({Runtime}) over {size:N0} documents");
        string[] only = ["common (rank 10)", "medium (rank 100)", "rare (rank 3000)", "prefix (4 letters of rank 500)", "medium + counts per type", "medium + snippets"];
        foreach (var kind in new[] { "gin", "btree_gin" })
        {
            Console.WriteLine($"INFO pg index {kind}: {(await pg.SwitchIndex(kind)).TotalSeconds:F1} s");
            foreach (var n in new int?[] { null, 10_000, 1_000 })
            {
                pg.Candidates = n;
                await Queries(pg, size, only);
            }
        }
        pg.Candidates = null;

        // the trigram fallback with the tenant leading its index: GIN (tenant, title gin_trgm_ops) through btree_gin
        var sw = Stopwatch.StartNew();
        await pg.Exec($"DROP INDEX IF EXISTS {pg.Schema}.docs_title_trgm; CREATE INDEX docs_tenant_title_trgm ON {pg.Schema}.docs USING gin (tenant, title gin_trgm_ops); ANALYZE {pg.Schema}.docs");
        Console.WriteLine($"INFO pg trigram index on (tenant, title): {sw.Elapsed.TotalSeconds:F1} s, {await pg.Scalar<long>($"SELECT pg_relation_size('{pg.Schema}.docs_tenant_title_trgm')") / 1e6:F0} MB");
        await using var tdb = Db(trgm: 0.4);
        await using var trgm = new Pg(tdb, mode: "trgm");
        await Queries(trgm, size, only: ["prefix (4 letters of rank 500)", "typo (rank 300, 1 substitution)"]);
        await pg.Exec($"DROP INDEX {pg.Schema}.docs_tenant_title_trgm; CREATE INDEX docs_title_trgm ON {pg.Schema}.docs USING gin (title gin_trgm_ops)");
        return 0;
    }

    public static double P(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    /// <summary>Write → searchable (and delete → gone) for one document, 50 samples, over whatever the index holds.</summary>
    public static async Task<int> Lag(string[] args)
    {
        await using var db = Db();
        await using var e = Engine(args[0], db);
        Console.WriteLine($"| engine | write | n | p50 ms | p99 ms | max ms |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var variant in e is OpenSearch ? new[] { "upsert", "upsert refresh=wait_for", "delete" } : ["upsert", "delete"])
        {
            var samples = new List<double>();
            for (var i = 0; i < 50; i++)
            {
                var token = "zqlag" + new string([.. $"{i:D3}".Select(ch => (char)('a' + ch - '0'))]) + variant.Length;
                var probe = e is Meili ? "\"" + token + "\"" : token;   // Meilisearch: a phrase, so typo tolerance cannot match the previous sample
                var doc = new Doc(Corpus.Big, Guid.NewGuid(), "auction", "lag " + token, "probe");
                if (variant == "delete")
                {
                    await e.Upsert(doc);
                    while ((await e.Search(new Query(probe, Corpus.Big, Limit: 1))).Items.Count == 0) await Task.Delay(1);
                }
                var sw = Stopwatch.StartNew();
                if (variant == "delete") await e.Delete([doc.Id]);
                else if (variant.EndsWith("wait_for", StringComparison.Ordinal)) await ((OpenSearch)e).UpsertWaitFor(doc);
                else await e.Upsert(doc);
                while (((await e.Search(new Query(probe, Corpus.Big, Limit: 1))).Items.Count > 0) != (variant != "delete"))
                    await Task.Delay(1);
                samples.Add(sw.Elapsed.TotalMilliseconds);
                if (variant != "delete") await e.Delete([doc.Id]);
            }
            samples.Sort();
            Console.WriteLine($"| {e.Name} | {variant} | {samples.Count} | {P(samples, 0.5):F1} | {P(samples, 0.99):F1} | {samples[^1]:F1} |");
        }
        return 0;
    }
}
