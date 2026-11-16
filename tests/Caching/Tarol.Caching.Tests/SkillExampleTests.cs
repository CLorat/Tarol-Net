using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tarol.Persistence;
using Tarol.Queries;
using AppJson = Tarol.Caching.Tests.CachingJson;

namespace Tarol.Caching.Tests;

/// <summary>The example of skills/tarol-caching/SKILL.md, as written there (the registration with the in-memory stores).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_unit_test_example_of_the_skill_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Lab.TenantA;
        var id = Guid.CreateVersion7(Lab.Now);
        IReadOnlyList<Versioned<AuctionRow>> rows = [new(new RowKey(tenant, id), 1, new AuctionRow(id, "Lot", "ada", Lab.Now.AddDays(1), AuctionStatus.Open, 0m))];

        // from the skill
        var time = new FakeTimeProvider();
        var inner = new InMemoryReadModels();
        using var cache = new MemoryPageCache(new CacheOptions { IdleExpiry = TimeSpan.FromMinutes(5) }, time);
        var store = new CachedReadModels(inner, new ProjectionPositions(inner), cache, AppJson.Default.Options).Store<AuctionRow>();
        await store.Write(new ProjectionBatch<AuctionRow>(rows, GlobalPosition.Start, new GlobalPosition(1, 1), ProjectionState.Current), ct);
        var first = await store.Search(new ListQuery(tenant), ct);    // a miss: searches the in-memory store
        var second = await store.Search(new ListQuery(tenant), ct);   // a hit: the same Page<AuctionRow> instance
        time.Advance(TimeSpan.FromMinutes(6));                        // idle: the next search misses
        // Search returns Result<Page<AuctionRow>>: first.TryGetValue(out Page<AuctionRow>? page)
        // end of the skill

        first.TryGetValue(out Page<AuctionRow>? a).ShouldBeTrue();
        second.TryGetValue(out Page<AuctionRow>? b).ShouldBeTrue();
        b.ShouldBeSameAs(a);
        (await store.Search(new ListQuery(tenant), ct)).TryGetValue(out Page<AuctionRow>? c).ShouldBeTrue();
        c.ShouldNotBeSameAs(a);
    }

    [Fact]
    public async Task The_registration_of_the_skill_builds()
    {
        var services = new ServiceCollection();
        new TarolBuilder(services, new System.Text.Json.JsonSerializerOptions(AppJson.Default.Options), new EventCodec(AppJson.Default.Options))
            .UseInMemoryReadModels()
            .AddCaching(o => { o.MaxEntries = 20_000; o.IdleExpiry = TimeSpan.FromMinutes(10); });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<CacheOptions>().MaxEntries.ShouldBe(20_000);
    }
}
