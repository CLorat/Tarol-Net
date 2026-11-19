using System.Security.Cryptography;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Search;
using Tarol.Search.Contract;
using Tarol.Search.Npgsql;
using Tarol.StronglyTypedId;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

var script = NpgsqlSearchSchema.Prepare("smoke", 3);
Check("the DDL: a GIN led by the tenant, the title trigram, the versions table", script.Contains("smoke.search_v3 USING gin (tenant, tsv)", StringComparison.Ordinal)
    && script.Contains("gin (tenant, a gin_trgm_ops)", StringComparison.Ordinal) && script.Contains("smoke.search_versions", StringComparison.Ordinal));
Check("languages map to the Tarol configurations", NpgsqlSearchSchema.Configuration("smoke", "de") == "smoke.german" && NpgsqlSearchSchema.Configuration("smoke", null) == "smoke.simple");

var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
    Console.WriteLine("skip database checks: TAROL_PG is not set");
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"search_smoke_{Environment.ProcessId}";
    try
    {
        var tenant = (Id<ITenant>)Guid.CreateVersion7();
        var context = OperationContext.Start(Actor.User("smoke"), tenant, TimeProvider.System);
        await using var keys = new NpgsqlKeyStore(db, new ConfiguredKeyEncryptionKey("smoke", RandomNumberGenerator.GetBytes(32)), TimeProvider.System,
            new NpgsqlKeyStoreOptions { Schema = schema });
        var store = new NpgsqlAggregateStore(db, ContractJson.Codec(), TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        await store.EnsureSchema(SchemaMode.Upgrade, default);
        var index = new NpgsqlSearchIndex(db, schema);
        var registry = new SearchRegistry().Add<Artwork>().Add<Artist>();
        var host = new SearchHost(registry, index, store, store, new SearchTokens(keys));
        var search = new SearchService(registry, index, new SearchTokens(keys));

        var art = Artwork.Create("Sunset over harbour", "watercolour of a lake below glaciers", owner: "Ada Lovelace");
        var edition = art.AddEdition("museum edition");
        var last = (await store.Append(art, 0, context, Option.None, default)).GetValueOrThrow();
        Tarol.Primitives.AggregateChanges.Committed(art);
        var painter = Artist.Create("Harbour painter", "paints the sea");
        var head = (await store.Append(painter, 0, context, Option.None, default)).GetValueOrThrow();
        last = head > last ? head : last;
        while (host.Position < last)
            await host.CatchUp(default);

        async Task<SearchPage> Find(SearchQuery query) => (await search.Search(query, context, default)).GetValueOrThrow();
        var page = await Find(new SearchQuery("harbour") { Counts = true });
        Check("both types found, the title first, counts per type", page.Items.Count == 2 && page.Items[0].Type == Artwork.StreamType && page.Counts?.Count == 2);
        Check("an entity hit is shown through its aggregate", (await Find(new SearchQuery("museum"))).Items is [{ } e] && e.Id == edition.Id.Value && e.Aggregate == art.Id.Value);
        Check("prefix", (await Find(new SearchQuery("glac") { Prefix = true })).Items.Count == 1);
        Check("typo fallback", (await Find(new SearchQuery("harbuor"))).Items.Count == 2);
        var hit = (await Find(new SearchQuery("glaciers") { Snippets = true })).Items[0];               // BC-8: text and offsets
        Check("snippet", hit.Snippet is { } text && hit.Highlights.Count > 0 && !text.Contains("<b>", StringComparison.Ordinal)
            && hit.Highlights.All(h => text.Substring(h.Start, h.Length).Contains("glacier", StringComparison.OrdinalIgnoreCase)));
        Check("exact token", (await Find(new SearchQuery("ada lovelace"))).Items.Count == 1 && (await Find(new SearchQuery("lovelace"))).Items.Count == 0);
        await keys.EraseSubject(tenant.Value, art.Id.Value, default);
        await host.Reindex(await store.StreamsOf(tenant.Value, art.Id.Value, default), default);
        Check("an erased subject is no longer found", (await Find(new SearchQuery("ada lovelace"))).Items.Count == 0);
        await host.Remove(await store.DeleteStreams(Artist.StreamType, TimeProvider.System.GetUtcNow().AddDays(1), 500, default), default);
        Check("retention removes the documents", (await Find(new SearchQuery("painter"))).Items.Count == 0);
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
