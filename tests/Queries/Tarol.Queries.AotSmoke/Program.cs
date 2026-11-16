using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Queries.AotSmoke;
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

var start = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

// ProjectionTest: the generated switch and columns, the host, the in-memory store
var test = ProjectionTest<AuctionRow>.Create(options: new ProjectionOptions { Batch = 7 });
var auctions = new List<Auction>();
for (var i = 0; i < 30; i++)
{
    var auction = Auction.Open($"Lot {i}", start.AddHours(i + 1), start);
    for (var b = 1; b <= i % 3; b++)
        _ = auction.Bid(b * 10m);
    if (i % 5 == 0)
        _ = auction.Close();
    test.Given(auction);
    auctions.Add(auction);
}
await test.CatchUp();
Check("one row per stream, rows equal the aggregates", test.Rows().Count == 30
                                                      && auctions.All(a => test.Row(a.Id.Value) is { } r && r.HighestBid == a.HighestBid && r.Bids == (int)(a.Version - 1 - (a.Closed ? 1 : 0))));
Check("current after the catch-up", test.Host.State == ProjectionState.Current);

// list queries: cursor pages, filters, offset, ETag, bounds
var query = new ListQuery(Guid.Empty) { Sort = "ends_at", Limit = 8 };
var seen = new List<AuctionRow>();
for (var q = query; ;)
{
    var page = Ok(await test.Search(q));
    seen.AddRange(page.Items);
    if (page.Next is null)
        break;
    q = q with { After = page.Next };
}
Check("cursor pages = the ordered list", seen.Select(r => r.Id).SequenceEqual(auctions.Select(a => a.Id.Value)));
var closed = Ok(await test.Search(new ListQuery(Guid.Empty) { Filters = [new ColumnFilter("status", AuctionStatus.Closed)], Sort = "highest_bid", Descending = true, Count = true }));
Check("filter, descending sort, count", closed.Total == 6 && closed.Items.All(r => r.Status == AuctionStatus.Closed));
Check("offset page = cursor page", Ok(await test.Search(query with { Offset = 8 })).Items.SequenceEqual(seen.Skip(8).Take(8)));
var first = Ok(await test.Search(query));
Check("cursor bound to its sort", (await test.Search(new ListQuery(Guid.Empty) { Sort = "highest_bid", After = first.Next })).IsFailure);
Check("offset bounded", (await test.Search(query with { Offset = ListQuery.MaxOffset + 1 })).IsFailure);
_ = auctions[1].Bid(1000m);
test.Given(auctions[1]);
await test.CatchUp();
Check("page ETag changes with a bid on the page", Ok(await test.Search(query)).ETag != first.ETag);

// the hosted service with DI, read-your-writes through IProjectionPositions
var json = new JsonSerializerOptions { TypeInfoResolver = SmokeJson.Default };
var services = new ServiceCollection();
new TarolBuilder(services, json, new EventCodec(json).Add<Auction>()).UseInMemoryStore().UseInMemoryReadModels()
    .AddProjections(p => p.Add<AuctionRow>(o => o.Poll = TimeSpan.FromMilliseconds(5)));
await using (var provider = services.BuildServiceProvider())
{
    var hosted = provider.GetServices<IHostedService>().ToList();
    foreach (var service in hosted)
        await service.StartAsync(default);
    var store = provider.GetRequiredService<IAggregateStore>();
    var opened = Auction.Open("Hosted", start.AddDays(1), start);
    var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
    _ = await store.Append(opened, 0, context, Option.None, default);
    var head = GlobalPosition.Start;
    await foreach (var e in provider.GetRequiredService<IEventFeed>().ReadAll(GlobalPosition.Start, 100, default))
        head = e.Position;
    var caughtUp = await provider.GetRequiredService<IProjectionPositions>().WaitFor<AuctionRow>(head, TimeSpan.FromSeconds(15), default);
    Check("hosted projection, read-your-writes", caughtUp
                                                && (await provider.GetRequiredService<IReadModels>().Store<AuctionRow>().Find(new RowKey(Guid.Empty, opened.Id.Value), default))?.Row.Title == "Hosted");
    foreach (var service in hosted)
        await service.StopAsync(default);
}

Console.WriteLine(failures == 0 ? "Tarol.Queries AOT smoke: all checks passed" : $"Tarol.Queries AOT smoke: {failures} check(s) failed");
return failures == 0 ? 0 : 1;
