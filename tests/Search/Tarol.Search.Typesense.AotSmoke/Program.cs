using System.Security.Cryptography;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Search;
using Tarol.Search.Contract;
using Tarol.Search.Typesense;
using Tarol.StronglyTypedId;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

Check("the collection name of a version", new TypesenseIndex(new HttpClient(), new InMemorySearchCheckpoints()).CollectionOf(3) == "tarol_search_v3");
var url = Environment.GetEnvironmentVariable("TAROL_TYPESENSE");
var key = Environment.GetEnvironmentVariable("TAROL_TYPESENSE_KEY");
if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
    Console.WriteLine("skip engine checks: TAROL_TYPESENSE / TAROL_TYPESENSE_KEY are not set");
else
{
    using var http = TypesenseTarolBuilderExtensions.Client(new Uri(url), key);
    var prefix = $"smoke_{Environment.ProcessId}";
    try
    {
        var keys = new InMemoryKeyStore(new ConfiguredKeyEncryptionKey("smoke", RandomNumberGenerator.GetBytes(32)), TimeProvider.System);
        var index = new TypesenseIndex(http, new InMemorySearchCheckpoints(), new TypesenseOptions { CollectionPrefix = prefix });
        var test = SearchTest.Create(s => s.Add<Artwork>().Add<Artist>(), index, keys, ContractJson.Codec());
        var tenant = (Id<ITenant>)Guid.CreateVersion7();
        var art = Artwork.Create("Sunset over harbour", "watercolour of a lake below glaciers", owner: "Ada Lovelace");
        var edition = art.AddEdition("museum edition");
        test.Given(art, tenant).Given(Artist.Create("Harbour painter", "paints the sea"), tenant);
        await test.CatchUp();

        async Task<SearchPage> Find(SearchQuery query) => (await test.Find(query, tenant)).GetValueOrThrow();
        var page = await Find(new SearchQuery("harbour") { Counts = true });
        Check("both types (two title matches), counts", page.Items.Count == 2 && page.Items.Select(h => h.Type).Distinct().Count() == 2 && page.Counts?.Count == 2);
        Check("an entity through its aggregate", (await Find(new SearchQuery("museum"))).Items is [{ } e] && e.Id == edition.Id.Value && e.Aggregate == art.Id.Value);
        Check("prefix", (await Find(new SearchQuery("glac") { Prefix = true })).Items.Count == 1);
        Check("typo", (await Find(new SearchQuery("harbuor"))).Items.Count == 2);
        Check("snippet", (await Find(new SearchQuery("glaciers") { Snippets = true })).Items[0].Snippet?.Contains("<b>", StringComparison.Ordinal) == true);
        Check("exact token, never text", (await Find(new SearchQuery("ada lovelace"))).Items.Count == 1 && (await Find(new SearchQuery("lovelace"))).Items.Count == 0);
        await keys.EraseSubject(tenant.Value, art.Id.Value, default);
        await test.Host.Reindex(await test.Store.StreamsOf(tenant.Value, art.Id.Value, default), default);
        Check("an erased subject is no longer found", (await Find(new SearchQuery("ada lovelace"))).Items.Count == 0);
        Check("the server answers: no readiness problem", index.Problem is null);
    }
    finally
    {
        using var _ = await http.DeleteAsync($"/collections/{prefix}_v1");
        using var __ = await http.DeleteAsync($"/collections/{prefix}_versions");
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
