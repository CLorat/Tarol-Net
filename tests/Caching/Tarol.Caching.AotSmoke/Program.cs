using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tarol.Caching;
using Tarol.Caching.AotSmoke;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Results;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

static Page<AuctionRow> Ok(Result<Page<AuctionRow>> result) => result.TryGetValue(out Page<AuctionRow>? page) ? page : throw new InvalidOperationException(result.ToString());

var json = new JsonSerializerOptions(SmokeJson.Default.Options);
var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
var tenantA = Guid.Parse("0196f000-0000-7000-8000-00000000000a");
var tenantB = Guid.Parse("0196f000-0000-7000-8000-00000000000b");

// the in-memory read stores through DI, as an API process registers them
var hits = 0L;
using var listener = new MeterListener();
listener.InstrumentPublished = (instrument, l) =>
{
    if (instrument.Meter.Name == MemoryPageCache.MeterName && instrument.Name == "tarol.caching.hits")
        l.EnableMeasurementEvents(instrument);
};
listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref hits, value));
listener.Start();

var services = new ServiceCollection();
new TarolBuilder(services, json, new EventCodec(json).Add<Auction>()).UseInMemoryReadModels().AddCaching(o => o.MaxEntries = 100);
await using (var provider = services.BuildServiceProvider())
{
    var readModels = provider.GetRequiredService<IReadModels>();
    Check("AddCaching decorates IReadModels", readModels is CachedReadModels);
    var store = readModels.Store<AuctionRow>();
    var position = GlobalPosition.Start;
    async Task<GlobalPosition> Write(Guid tenant, AuctionRow row, long version)
    {
        var to = new GlobalPosition(position.Transaction + 1, position.Position + 1);
        await store.Write(new ProjectionBatch<AuctionRow>([new(new RowKey(tenant, row.Id), version, row)], position, to, ProjectionState.Current), default);
        return position = to;
    }

    var lot = new AuctionRow(Guid.CreateVersion7(now), "Lot 1", now.AddHours(1), 0m);
    await Write(tenantA, lot, 1);
    await Write(tenantB, new AuctionRow(Guid.CreateVersion7(now), "Lot B", now.AddHours(1), 0m), 1);
    var query = new ListQuery(tenantA) { Sort = "ends_at" };
    var first = Ok(await store.Search(query, default));
    Check("a second search at the same checkpoint is a hit", ReferenceEquals(first, Ok(await store.Search(query, default))) && Interlocked.Read(ref hits) == 1);
    Check("the tenant is in the key", Ok(await store.Search(new ListQuery(tenantB), default)).Items is [{ Title: "Lot B" }]);
    await Write(tenantA, lot with { HighestBid = 30m }, 2);
    var after = Ok(await store.Search(query, default));
    Check("a batch makes the next search miss and show it", !ReferenceEquals(first, after) && after.Items[0].HighestBid == 30m);
    var cache = (MemoryPageCache)provider.GetRequiredService<IPageCache>();
    Check("entries and bytes are counted", cache.Count == 2 && cache.Bytes > 0);
}

var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"caching_smoke_{Environment.ProcessId}";
    var store = new NpgsqlAggregateStore(db, new EventCodec(json).Add<Auction>(), TimeProvider.System, new NpgsqlStoreOptions { Schema = schema });
    try
    {
        await store.EnsureSchema(default);
        var worker = new NpgsqlReadModels(db, json, schema, pushCheckpoints: true);
        var host = new ProjectionHost<AuctionRow>(store, worker.Store<AuctionRow>());
        await using var positions = new NpgsqlProjectionPositions(db, schema);
        using var pages = new MemoryPageCache();
        var api = new CachedReadModels(new NpgsqlReadModels(db, json, schema), positions, pages, json).Store<AuctionRow>();
        var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var auction = Auction.Open("Lot 1", now.AddHours(1), now);
        var appended = await store.Append(auction, 0, context, Option.None, default);
        Tarol.Primitives.AggregateChanges.Committed(auction);
        await host.CatchUp(default);
        Check("the pushed checkpoint reaches the API process",
            appended.TryGetValue(out GlobalPosition opened) && await positions.WaitFor<AuctionRow>(opened, TimeSpan.FromSeconds(30), default));
        var query = new ListQuery(Guid.Empty);
        var first = Ok(await api.Search(query, default));
        Check("a hit over the read table", ReferenceEquals(first, Ok(await api.Search(query, default))));
        auction.Bid(40m);
        var bid = await store.Append(auction, 1, context, Option.None, default);
        await host.CatchUp(default);
        var pushed = bid.TryGetValue(out GlobalPosition bidAt) && await positions.WaitFor<AuctionRow>(bidAt, TimeSpan.FromSeconds(30), default);
        Check("the batch's NOTIFY makes the next search miss and show the bid", pushed && Ok(await api.Search(query, default)).Items[0].HighestBid == 40m);
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "Tarol.Caching AOT smoke: all checks passed" : $"Tarol.Caching AOT smoke: {failures} check(s) failed");
return failures == 0 ? 0 : 1;
