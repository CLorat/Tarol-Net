using System.Diagnostics;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Search;
using Tarol.Search.Npgsql;

namespace Probe.Search;

/// <summary>
/// Section 12 (PR 18C): the built <see cref="NpgsqlSearchIndex"/> at 1M documents: written through <c>Replace</c> in batches (each
/// document its own aggregate, language "en"), then the query classes through <c>Search</c>, with the query's language and without
/// it (the three configurations OR-ed), per tenant scope.
/// </summary>
public static class Impl
{
    public static async Task<int> Run(string[] args)
    {
        var size = args.Length > 0 ? long.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 1_000_000;
        await using var db = Bench.Db();
        var schema = "search_impl";
        await using (var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
            await drop.ExecuteNonQueryAsync();
        var index = new NpgsqlSearchIndex(db, schema);
        var types = new[] { new SearchType("auction", "Auction", "auction"), new SearchType("artist", "Artist", "artist"), new SearchType("lot", "Lot", "auction") };
        await index.Prepare(new SearchIndexDefinition(1, "probe000000000000", types), default);
        Console.WriteLine($"# the built index ({Bench.Runtime}): {size:N0} documents through NpgsqlSearchIndex.Replace, then Search");
        var sw = Stopwatch.StartNew();
        foreach (var chunk in Http.Chunks(Corpus.Range(0, size), 2000))
            await index.Replace([.. chunk.Select(d => new StreamDocuments(d.Tenant, d.Id, 1,
                [new SearchDocument(d.Type, d.Id, d.Id, d.Title) { A = d.Title, B = d.Body, Language = "en" }]))], default);
        Console.WriteLine($"INFO written {size:N0} documents in {sw.Elapsed.TotalSeconds:F1} s, {size / sw.Elapsed.TotalSeconds:N0} docs/s (batches of 2,000, one transaction each, indexes maintained)");
        await using (var analyze = db.CreateCommand($"ANALYZE {schema}.search_v1"))
            await analyze.ExecuteNonQueryAsync();
        await using (var mark = db.CreateCommand($"UPDATE {schema}.projections SET state = 'current' WHERE name = 'search'"))
            await mark.ExecuteNonQueryAsync();
        await using (var sizeOf = db.CreateCommand($"SELECT pg_total_relation_size('{schema}.search_v1')"))
            Console.WriteLine($"INFO table with indexes: {(long)(await sizeOf.ExecuteScalarAsync())! / 1e6:F0} MB");

        Console.WriteLine("| docs | language | query | scope | n | p50 ms | p99 ms | hits |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var language in new[] { "en", null })
            foreach (var (cls, match, terms, facets, snippets) in Bench.Classes())
                foreach (var (scope, tenant) in new (string, Guid)[] { ("tenant 10 %", Corpus.Big), ("tenant 0.1 %", Corpus.Small) })
                {
                    var samples = new List<double>();
                    SearchPage? last = null;
                    for (var round = 0; round < 2; round++)
                    {
                        samples.Clear();
                        var budget = Stopwatch.StartNew();
                        for (var i = 0; i < 100 && (i < 10 || budget.Elapsed < TimeSpan.FromSeconds(8)); i++)
                        {
                            var request = new SearchRequest(tenant, terms[i % terms.Length])
                            {
                                Prefix = match == Match.Prefix, Counts = facets, Snippets = snippets, Language = language, Types = ["auction", "artist", "lot"],
                            };
                            var t = Stopwatch.StartNew();
                            last = (await index.Search(request, default)).GetValueOrThrow();
                            samples.Add(t.Elapsed.TotalMilliseconds);
                        }
                    }
                    samples.Sort();
                    var hits = last is null ? "" : last.Counts is { } c ? $"{last.Items.Count} of {c.Values.Sum():N0}{(last.CountsCapped ? "+" : "")}" : $"{last.Items.Count}";
                    Console.WriteLine($"| {size:N0} | {language ?? "none (3 OR-ed)"} | {cls} | {scope} | {samples.Count} | {Bench.P(samples, 0.5):F2} | {Bench.P(samples, 0.99):F2} | {hits} |");
                }
        return 0;
    }
}
