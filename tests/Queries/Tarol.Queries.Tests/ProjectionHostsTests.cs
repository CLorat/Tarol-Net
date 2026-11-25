using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

/// <summary>TN-26 (Tatalu FB-INT2-3): wait until every projection of the process is current; readiness on request.</summary>
public sealed class ProjectionHostsTests
{
    [Fact]
    public async Task WaitUntilCurrent_returns_once_the_host_reached_the_head_and_false_on_timeout()
    {
        var test = ProjectionTest<AuctionRow>.Create(options: new ProjectionOptions { Batch = 2 });
        Seed(test, 5, A);
        var hosts = new ProjectionHosts([test.Host]);

        hosts.Building.ShouldBe(["auction-list v1"]);
        (await hosts.WaitUntilCurrent(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken)).ShouldBeFalse(); // nobody runs it

        var waiting = hosts.WaitUntilCurrent(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await test.CatchUp();

        (await waiting).ShouldBeTrue();
        hosts.Building.ShouldBeEmpty();
        (await test.Host.WaitUntilCurrent(TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public void ReadyWhenCurrent_adds_a_readiness_check_naming_the_building_projections()
    {
        var services = new ServiceCollection();
        var store = new InMemoryAggregateStore(TimeProvider.System);
        services.AddSingleton<IEventFeed>(store);
        services.AddSingleton<IReadModels>(new InMemoryReadModels());
        new ProjectionsBuilder(services).Add<AuctionRow>().ReadyWhenCurrent();
        using var provider = services.BuildServiceProvider();

        var check = provider.GetServices<ReadinessCheck>().Single(c => c.Name == "projections");

        check.Problem(provider).ShouldBe("building: auction-list v1");
        provider.GetRequiredService<ProjectionHosts>().All.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_erasure_waits_until_the_hosts_projection_is_current_before_it_refolds()
    {
        // TN-25 (Tatalu FB-F17.5-4): a batch the building host read before the key was erased must be written first
        var test = ProjectionTest<AuctionRow>.Create(options: new ProjectionOptions { Batch = 2, ErasureWait = TimeSpan.FromMilliseconds(30) });
        var auctions = Seed(test, 5, A);
        var services = new ServiceCollection()
            .AddSingleton<ProjectionHost>(test.Host)
            .AddSingleton<IReadModels>(new One(test.ReadModels))
            .AddSingleton<IDataProtectionStore>(test.Store!)
            .BuildServiceProvider();
        var eraser = new ProjectionEraser<AuctionRow>(services);
        var streams = auctions.Select(a => new StreamRef(A.GetValueOrThrow().Value, Auction.StreamType, a.Id.Value)).ToArray();

        await Should.ThrowAsync<TimeoutException>(async () => await eraser.Forget(A.GetValueOrThrow().Value, Guid.NewGuid(), streams, TestContext.Current.CancellationToken));

        var catchUp = test.CatchUp();
        await eraser.ForgetTenant(A.GetValueOrThrow().Value, TestContext.Current.CancellationToken);
        await catchUp;
        test.Host.State.ShouldBe(ProjectionState.Current);
        test.Rows(A).Count.ShouldBe(5);
    }

    private sealed class One(object store) : IReadModels
    {
        public IReadModelStore<TRow> Store<TRow>()
            where TRow : class, IProjection<TRow>
            => (IReadModelStore<TRow>)store;
    }
}
