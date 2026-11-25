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

    [Fact]
    public async Task A_host_whose_storage_cannot_be_prepared_names_the_failure_and_clears_it_once_it_runs()
    {
        // Tatalu FB-F05.8-3: CREATE TABLE failed (a reserved column name) and the projection stayed "building" with no reason
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 3, A);
        var store = new FailingPrepare(new InMemoryReadModels().Store<AuctionRow>());
        using var host = new ProjectionHost<AuctionRow>(test.Store!, store,
            new ProjectionOptions { Backoff = TimeSpan.FromMilliseconds(5), MaxBackoff = TimeSpan.FromMilliseconds(20), Poll = TimeSpan.FromMilliseconds(10) });
        var hosts = new ProjectionHosts([host]);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Until(() => host.LastFailure is { Failures: >= 2 });

            host.LastFailure!.Prepared.ShouldBeFalse();
            host.LastFailure.Message.ShouldBe("InvalidOperationException: 42601: syntax error at or near \"from\"");
            hosts.Failing.ShouldHaveSingleItem().ShouldStartWith("auction-list v1: InvalidOperationException: 42601: syntax error at or near \"from\" (");
            host.State.ShouldBe(ProjectionState.Building);

            store.Fail = false;
            (await host.WaitUntilCurrent(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue();
            await Until(() => host.LastFailure is null);
            hosts.Failing.ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void The_readiness_check_names_a_failing_projection_next_to_the_building_ones()
    {
        var services = new ServiceCollection();
        var store = new InMemoryAggregateStore(TimeProvider.System);
        services.AddSingleton<IEventFeed>(store);
        services.AddSingleton<IReadModels>(new InMemoryReadModels());
        new ProjectionsBuilder(services).Add<AuctionRow>().ReadyWhenCurrent();
        using var provider = services.BuildServiceProvider();
        var check = provider.GetServices<ReadinessCheck>().Single(c => c.Name == "projections");

        check.Problem(provider).ShouldBe("building: auction-list v1");   // no failure recorded yet: nothing more to say
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FailingPrepare(IReadModelStore<AuctionRow> inner) : IReadModelStore<AuctionRow>
    {
        public volatile bool Fail = true;

        public ValueTask Prepare(CancellationToken cancellationToken)
            => Fail ? throw new InvalidOperationException("42601: syntax error at or near \"from\"") : inner.Prepare(cancellationToken);

        public ValueTask Replace(IReadOnlyList<Versioned<AuctionRow>> rows, CancellationToken cancellationToken) => inner.Replace(rows, cancellationToken);

        public ValueTask<int> Remove(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken) => inner.Remove(keys, cancellationToken);

        public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => inner.Checkpoint(cancellationToken);

        public ValueTask<IReadOnlyDictionary<RowKey, Versioned<AuctionRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
            => inner.Load(keys, cancellationToken);

        public ValueTask<bool> Write(ProjectionBatch<AuctionRow> batch, CancellationToken cancellationToken) => inner.Write(batch, cancellationToken);

        public ValueTask<Versioned<AuctionRow>?> Find(RowKey key, CancellationToken cancellationToken) => inner.Find(key, cancellationToken);

        public ValueTask<Tarol.Results.Result<Page<AuctionRow>>> Search(ListQuery query, CancellationToken cancellationToken) => inner.Search(query, cancellationToken);
    }

    private sealed class One(object store) : IReadModels
    {
        public IReadModelStore<TRow> Store<TRow>()
            where TRow : class, IProjection<TRow>
            => (IReadModelStore<TRow>)store;
    }
}
