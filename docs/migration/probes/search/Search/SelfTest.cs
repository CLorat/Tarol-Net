using Npgsql;

namespace Probe.Search;

/// <summary>Functional checks every candidate must pass (the would-be contract suite), plus language checks.</summary>
public static class SelfTest
{
    private static int _failures, _checks;

    public static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    }

    /// <summary>A property of the engine as configured, reported but not required (languages differ by engine).</summary>
    public static void Info(string name, bool yes, string detail = "")
        => Console.WriteLine($"INFO {(yes ? "yes" : "no ")} {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");

    public static int Done(string what)
    {
        Console.WriteLine(_failures == 0 ? $"ALL {_checks} CHECKS PASSED ({what}, {Bench.Runtime})" : $"{_failures} OF {_checks} CHECKS FAILED ({what}, {Bench.Runtime})");
        return _failures == 0 ? 0 : 1;
    }

    private static readonly Guid A = Corpus.Tenant(900), B = Corpus.Tenant(901);
    private static Guid Id(int n) => new(n, 0x5e1f, 0x4000, [0x80, 0, 0, 0, 0, 0, 0, 3]);

    public static async Task<int> Run(string engine)
    {
        Console.WriteLine($"# selftest {engine} ({Bench.Runtime})");
        await using var db = Bench.Db(trgm: engine == "pg" ? 0.4 : null);
        await using var e = engine == "pg" ? new Pg(db, schema: "search_selftest", mode: "combined") : Bench.Engine(engine, db);
        await e.Reset();
        Doc[] docs =
        [
            new(A, Id(1), "auction", "Sunset over harbour", "an oil painting of boats at dusk"),
            new(A, Id(2), "artist", "Harbour painter", "paints boats and the sea"),
            new(A, Id(3), "lot", "Lot 7", "a sunset study in harbour light, signed"),
            new(B, Id(4), "auction", "Sunset over harbour", "tenant B's own copy of the same words"),
            new(A, Id(5), "auction", "Mountain lake", "watercolour of a lake below glaciers"),
        ];
        await e.Load(docs);
        if (e is Pg pg) await pg.CreateIndexes();

        async Task<Hits> S(string text, Guid? tenant, Match m = Match.Words, string[]? types = null, bool facets = false, bool snippets = false)
            => await e.Search(new Query(text, tenant, m, 20, facets, types, snippets));

        var h = await S("harbour", A);
        Check("tenant first: tenant A's search never returns tenant B's document", h.Items.Count == 3 && h.Items.All(x => x.Id != Id(4)), $"{h.Items.Count} hits");
        h = await S("harbour", B);
        Check("tenant B sees only its own", h.Items is [{ } only] && only.Id == Id(4));
        h = await S("harbour", A, types: ["auction", "lot"]);
        Check("type filter in the index (auction, lot)", h.Items.Count == 2 && h.Items.All(x => x.Type != "artist"));
        h = await S("harbour", A);
        Check("ranking: a title match ranks above a body-only match", h.Items.Count == 3 && h.Items[^1].Id == Id(3), string.Join(",", h.Items.Select(x => x.Id.ToString()[..8])));
        h = await S("sunset harbour", A);
        Check("all words must match (AND)", h.Items.Count == 2 && h.Items.All(x => x.Id is var i && (i == Id(1) || i == Id(3))));
        h = await S("moun", A, Match.Prefix);
        Check("prefix: 'moun' finds 'Mountain'", h.Items is [{ } px] && px.Id == Id(5));
        h = await S("harbuor", A, Match.Typo);
        Check("typo: 'harbuor' still finds 'harbour'", h.Items.Count >= 2, $"{h.Items.Count} hits{(engine == "pg" ? ", pg_trgm word_similarity_threshold 0.4" : "")}");
        if (engine == "pg")
        {
            await using var dflt = Bench.Db();
            await using var d6 = new Pg(dflt, schema: "search_selftest", mode: "combined");
            var h6 = await d6.Search(new Query("harbuor", A, Match.Typo));
            Info("typo with pg_trgm's default word_similarity_threshold 0.6", h6.Items.Count >= 2, $"{h6.Items.Count} hits");
        }
        h = await S("harbour", A, facets: true);
        Check("counts per type are exact", h.PerType is { } per && per.GetValueOrDefault("auction") == 1 && per.GetValueOrDefault("artist") == 1 && per.GetValueOrDefault("lot") == 1,
            h.PerType is null ? "none" : string.Join(",", h.PerType.Select(kv => kv.Key + "=" + kv.Value)));
        h = await S("glaciers", A, snippets: true);
        Check("snippet marks the matched word", h.Snippet is { } sn && sn.Contains("glacier", StringComparison.OrdinalIgnoreCase) && sn.Contains('<'), h.Snippet ?? "none");
        h = await S("paintings", A);
        Info("stemming (english): 'paintings' finds 'painting' and 'paints'", h.Items.Count >= 2, $"{h.Items.Count} hits");
        await e.Upsert(new Doc(A, Id(5), "auction", "Mountain lake at noon", "watercolour of a lake below glaciers"));
        if (e is Meili m) await m.WaitLast();
        if (e is OpenSearch os) await os.UpsertWaitFor(new Doc(A, Id(5), "auction", "Mountain lake at noon", "watercolour of a lake below glaciers"));
        h = await S("noon", A);
        Check("an upsert replaces the document (same id, new words)", h.Items is [{ } u] && u.Id == Id(5));
        await e.Delete([Id(1)]);
        if (e is Meili m2) await m2.WaitLast();
        var gone = false;
        for (var i = 0; i < 3000 && !gone; i++)
        {
            gone = (await S("sunset", A)).Items.All(x => x.Id != Id(1));
            if (!gone) await Task.Delay(1);
        }
        Check("a delete removes the document (an erased subject is not found)", gone);
        h = await S("cafe", A);
        await e.Upsert(new Doc(A, Id(6), "artist", "Café Müller", "Gemälde in Öl"));
        if (e is Meili m3) await m3.WaitLast();
        if (e is OpenSearch os2) await os2.UpsertWaitFor(new Doc(A, Id(6), "artist", "Café Müller", "Gemälde in Öl"));
        h = await S("cafe muller", A);
        Info("accents folded: 'cafe muller' finds 'Café Müller'", h.Items.Any(x => x.Id == Id(6)), $"{h.Items.Count} hits");
        h = await S("gemalden", A);
        Info("german stemming: 'gemalden' finds 'Gemälde' (index configured english / default)", h.Items.Any(x => x.Id == Id(6)), $"{h.Items.Count} hits");

        if (e is Pg p) await Languages(p);
        return Done("selftest " + engine);
    }

    /// <summary>PostgreSQL: a per-document regconfig in the generated tsvector, unaccent through a text-search configuration.</summary>
    private static async Task Languages(Pg p)
    {
        var s = p.Schema;
        await p.Exec($"""
            DROP TEXT SEARCH CONFIGURATION IF EXISTS {s}.de; DROP TEXT SEARCH CONFIGURATION IF EXISTS {s}.en;
            CREATE TEXT SEARCH CONFIGURATION {s}.de (COPY = german);
            ALTER TEXT SEARCH CONFIGURATION {s}.de ALTER MAPPING FOR hword, hword_part, word WITH unaccent, german_stem;
            CREATE TEXT SEARCH CONFIGURATION {s}.en (COPY = english);
            ALTER TEXT SEARCH CONFIGURATION {s}.en ALTER MAPPING FOR hword, hword_part, word WITH unaccent, english_stem;
            INSERT INTO {s}.docs (tenant, id, type, title, body, lang) VALUES
              ('{A}', '{Id(20)}', 'artist', 'Café Müller', 'Gemälde in Öl, Häuser am Fluss', '{s}.de'),
              ('{A}', '{Id(21)}', 'artist', 'Cafe Society', 'paintings of houses by the river', '{s}.en');
            """);
        async Task<string[]> Find(string config, string text)
        {
            await using var c = p.Db.CreateCommand($"SELECT title FROM {s}.docs WHERE tenant = '{A}' AND tsv @@ websearch_to_tsquery('{s}.{config}', $1) ORDER BY title");
            c.Parameters.Add(new() { Value = text });
            var r = new List<string>();
            await using var rd = await c.ExecuteReaderAsync();
            while (await rd.ReadAsync()) r.Add(rd.GetString(0));
            return [.. r];
        }
        Check("pg: a per-document regconfig column drives the generated tsvector (german row stemmed german)", (await Find("de", "Häusern")).Contains("Café Müller"));
        Check("pg: german + unaccent: 'Gemalde' finds 'Gemälde'", (await Find("de", "Gemalde")).Contains("Café Müller"));
        Check("pg: english + unaccent: 'café' finds 'Cafe Society', 'houses' finds 'house'", (await Find("en", "café houses")) is ["Cafe Society"]);
        var refused = false;
        try { await p.Exec($"ALTER TABLE {s}.docs ADD COLUMN bad tsvector GENERATED ALWAYS AS (to_tsvector('simple', unaccent(title))) STORED"); }
        catch (PostgresException ex) when (ex.SqlState == "42P17") { refused = true; }
        Check("pg: unaccent() itself is refused in a generated column (42P17, not immutable): the configuration is the way", refused);
        await using var q = p.Db.CreateCommand($"SELECT count(*) FROM {s}.docs WHERE tenant = '{A}' AND tsv @@ websearch_to_tsquery('{s}.en', $1)");
        q.Parameters.Add(new() { Value = "harbour -sunset" });
        Check("pg: websearch syntax: 'harbour -sunset' excludes a word", (long)(await q.ExecuteScalarAsync())! == 1);
        q.Parameters[0].Value = "\"harbour light\"";
        Check("pg: websearch syntax: a quoted phrase matches adjacent words only", (long)(await q.ExecuteScalarAsync())! == 1);
        q.Parameters[0].Value = "harbour OR glaciers";
        Check("pg: websearch syntax: OR", (long)(await q.ExecuteScalarAsync())! == 3);
        q.Parameters[0].Value = "'); DROP TABLE x; --";
        Check("pg: websearch_to_tsquery never fails on user input (no syntax error)", (long)(await q.ExecuteScalarAsync())! == 0);
    }
}
