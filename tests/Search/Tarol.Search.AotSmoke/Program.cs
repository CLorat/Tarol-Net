using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Search;
using Tarol.Search.Contract;
using Tarol.StronglyTypedId;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok)
        failures++;
}

var tenant = (Id<ITenant>)Guid.CreateVersion7();
var context = OperationContext.Start(Actor.User("smoke"), tenant, TimeProvider.System);

// the generated documents of an aggregate and its entity, natively
var art = Artwork.Create("Sunset over harbour", "watercolour of a lake below glaciers", owner: "Ada Lovelace", seller: "ada");
var edition = art.AddEdition("museum edition");
var keys = new InMemoryKeyStore(new ConfiguredKeyEncryptionKey("smoke", RandomNumberGenerator.GetBytes(32)), TimeProvider.System);
var test = SearchTest.Create(s => s.Add<Artwork>().Add<Artist>(), keys: keys, codec: ContractJson.Codec());
test.Given(art, tenant).Given(Artist.Create("Harbour painter", "paints the sea"), tenant);
await test.CatchUp();

var page = (await test.Find(new SearchQuery("harbour") { Counts = true }, tenant)).GetValueOrThrow();
Check("both types found, counts per type", page.Items.Count == 2 && page.Counts?.Count == 2 && !page.CountsCapped);
Check("the title ranks first", page.Items[0].Type == Artwork.StreamType);
var hit = (await test.Find("museum", tenant)).GetValueOrThrow().Items;
Check("an entity hit is shown through its aggregate", hit is [{ } e] && e.Id == edition.Id.Value && e.Aggregate == art.Id.Value && e.Title == "Sunset over harbour");
Check("prefix", (await test.Find(new SearchQuery("glac") { Prefix = true }, tenant)).GetValueOrThrow().Items.Count == 1);
Check("typo", (await test.Find("harbuor", tenant)).GetValueOrThrow().Items.Count == 2);
var snippet = (await test.Find(new SearchQuery("glaciers") { Snippets = true }, tenant)).GetValueOrThrow().Items[0];   // BC-8: text and offsets
Check("snippet", snippet.Snippet is { } text && snippet.Highlights.Count == 1
    && text.Substring(snippet.Highlights[0].Start, snippet.Highlights[0].Length) == "glaciers" && !text.Contains('<', StringComparison.Ordinal));
Check("audience", (await test.Find(new SearchQuery("sunset") { Audience = ["bob"] }, tenant)).GetValueOrThrow().Items.Count == 0);
Check("exact token, never text", (await test.Find("ada lovelace", tenant)).GetValueOrThrow().Items.Count == 1
                                 && (await test.Find("lovelace", tenant)).GetValueOrThrow().Items.Count == 0);
var first = (await test.Find(new SearchQuery("harbour") { Limit = 1 }, tenant)).GetValueOrThrow();
var second = (await test.Find(new SearchQuery("harbour") { Limit = 1, After = first.Next }, tenant)).GetValueOrThrow();
Check("cursor pages", first.Next is not null && second.Items.Count == 1 && second.Items[0].Id != first.Items[0].Id && second.Next is null);

await keys.EraseSubject(tenant.Value, art.Id.Value, CancellationToken.None);
await test.Host.Reindex(await test.Store.StreamsOf(tenant.Value, art.Id.Value, CancellationToken.None), CancellationToken.None);
Check("an erased subject is no longer found", (await test.Find("ada lovelace", tenant)).GetValueOrThrow().Items.Count == 0);

// AddSearch through DI, the meter
var services = new ServiceCollection();
var meters = new SmokeMeters();
services.AddSingleton<IMeterFactory>(meters);
new TarolBuilder(services, new JsonSerializerOptions(ContractJson.Default.Options), ContractJson.Codec()).UseInMemoryStore()
    .AddSearch(s => s.Add<Artwork>()).UseInMemorySearch();
await using (var provider = services.BuildServiceProvider())
{
    var store = provider.GetRequiredService<IAggregateStore>();
    var other = Artwork.Create("Mountain lake", "glaciers");
    Check("append", (await store.Append(other, 0, context, Option.None, CancellationToken.None)).IsSuccess);
    await provider.GetRequiredService<SearchHost>().CatchUp(CancellationToken.None);
    var found = (await provider.GetRequiredService<ISearch>().Search(new SearchQuery("lake"), context, CancellationToken.None)).GetValueOrThrow();
    Check("AddSearch: the host indexes, ISearch finds", found.Items.Count == 1);
    Check("the meter counts the search", meters.Queries == 1);
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

internal sealed class SmokeMeters : IMeterFactory
{
    private readonly MeterListener _listener = new();
    private readonly List<Meter> _meters = [];

    public SmokeMeters()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "tarol.search.queries" && _meters.Contains(instrument.Meter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Queries += value);
        _listener.Start();
    }

    public long Queries { get; private set; }

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var meter in _meters)
            meter.Dispose();
    }
}
