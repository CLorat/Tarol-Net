using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Caching.Tests;

public sealed class BuilderTests
{
    private static TarolBuilder Builder(IServiceCollection services)
        => new(services, new JsonSerializerOptions(CachingJson.Default.Options), new EventCodec(CachingJson.Default.Options));

    [Fact]
    public async Task AddCaching_decorates_the_read_stores_and_the_positions_resolve_without_a_cycle()
    {
        var services = new ServiceCollection();
        Builder(services).UseInMemoryReadModels().AddCaching(o => o.MaxEntries = 7);
        await using var provider = services.BuildServiceProvider();

        var cached = provider.GetRequiredService<IReadModels>().ShouldBeOfType<CachedReadModels>();
        cached.Inner.ShouldBeSameAs(provider.GetRequiredService<InMemoryReadModels>());
        cached.Cache.ShouldBeSameAs(provider.GetRequiredService<IPageCache>());
        cached.Cache.ShouldBeOfType<MemoryPageCache>();
        provider.GetRequiredService<CacheOptions>().MaxEntries.ShouldBe(7);

        var store = cached.Store<AuctionRow>();
        var checkpoint = new GlobalPosition(1, 1);
        (await store.Write(new ProjectionBatch<AuctionRow>([], GlobalPosition.Start, checkpoint, ProjectionState.Current), TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await provider.GetRequiredService<IProjectionPositions>().Checkpoint<AuctionRow>(TestContext.Current.CancellationToken)).Position.ShouldBe(checkpoint);
        (await store.Search(new ListQuery(Lab.TenantA), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        cached.Cache.ShouldBeOfType<MemoryPageCache>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_registered_page_cache_is_kept()
    {
        var services = new ServiceCollection();
        var mine = new MemoryPageCache(new CacheOptions { MaxEntries = 1 });
        services.AddSingleton<IPageCache>(mine);
        Builder(services).UseInMemoryReadModels().AddCaching();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReadModels>().ShouldBeOfType<CachedReadModels>().Cache.ShouldBeSameAs(mine);
    }

    [Fact]
    public void AddCaching_needs_the_read_stores_first_and_is_added_once()
    {
        Should.Throw<InvalidOperationException>(() => Builder(new ServiceCollection()).AddCaching())
            .Message.ShouldContain("UseNpgsqlReadModels()");
        var builder = Builder(new ServiceCollection()).UseInMemoryReadModels().AddCaching();
        Should.Throw<InvalidOperationException>(() => builder.AddCaching()).Message.ShouldContain("twice");
        Should.Throw<ArgumentOutOfRangeException>(() => Builder(new ServiceCollection()).UseInMemoryReadModels().AddCaching(o => o.MaxBytes = -1));
    }

    [Fact]
    public async Task A_read_store_registered_by_type_or_instance_is_decorated_too()
    {
        var byType = new ServiceCollection();
        byType.AddSingleton<IReadModels, InMemoryReadModels>();
        Builder(byType).AddProjectionPositions().AddCaching();
        await using (var provider = byType.BuildServiceProvider())
            provider.GetRequiredService<IReadModels>().ShouldBeOfType<CachedReadModels>().Inner.ShouldBeOfType<InMemoryReadModels>();

        var instance = new InMemoryReadModels();
        var byInstance = new ServiceCollection();
        byInstance.AddSingleton<IReadModels>(instance);
        Builder(byInstance).AddProjectionPositions().AddCaching();
        await using (var provider = byInstance.BuildServiceProvider())
            provider.GetRequiredService<IReadModels>().ShouldBeOfType<CachedReadModels>().Inner.ShouldBeSameAs(instance);
    }
}
