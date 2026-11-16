using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Persistence;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

public sealed class BuilderTests
{
    private static TarolBuilder Builder(IServiceCollection services)
    {
        var json = new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = TestJson.Default };
        return new TarolBuilder(services, json, new EventCodec(json).Add<Auction>().Add<Lot>()).UseInMemoryStore();
    }

    [Fact]
    public async Task AddProjections_hosts_each_projection_and_the_positions_see_the_host()
    {
        var services = new ServiceCollection();
        Builder(services).UseInMemoryReadModels().AddProjections(p => p.Add<AuctionRow>(o => o.Batch = 10));
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<ProjectionHost<AuctionRow>>();
        provider.GetServices<IHostedService>().ShouldContain(host);
        provider.GetRequiredService<IReadModels>().Store<AuctionRow>().ShouldBeSameAs(provider.GetRequiredService<InMemoryReadModels>().Store<AuctionRow>());
        host.Store.ShouldBeSameAs(provider.GetRequiredService<IReadModels>().Store<AuctionRow>());

        var store = provider.GetRequiredService<IAggregateStore>();
        var auction = Auction.Open("ada", "Lot", Now.AddDays(1), Now);
        (await store.Append(auction, 0, Context(A), default, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var head = await ProjectionHostTests.Head(provider.GetRequiredService<IEventFeed>());
        var positions = provider.GetRequiredService<IProjectionPositions>();
        var waiting = positions.WaitFor<AuctionRow>(head, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await host.CatchUp(TestContext.Current.CancellationToken);

        (await waiting).ShouldBeTrue();
        (await positions.Checkpoint<AuctionRow>(TestContext.Current.CancellationToken)).ShouldBe(new ProjectionCheckpoint(head, ProjectionState.Current));
    }

    [Fact]
    public async Task Without_a_host_the_positions_poll_the_stored_checkpoint()
    {
        var services = new ServiceCollection();
        Builder(services).UseInMemoryReadModels();
        await using var provider = services.BuildServiceProvider();
        var positions = provider.GetRequiredService<IProjectionPositions>();
        var store = provider.GetRequiredService<InMemoryReadModels>().Store<AuctionRow>();
        var target = new GlobalPosition(1, 1);

        (await positions.WaitFor<AuctionRow>(target, TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken)).ShouldBeFalse();
        var waiting = positions.WaitFor<AuctionRow>(target, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        (await store.Write(new ProjectionBatch<AuctionRow>([], GlobalPosition.Start, target, ProjectionState.Current), TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await waiting).ShouldBeTrue();
    }

    [Fact]
    public void A_projection_added_twice_is_refused()
    {
        var services = new ServiceCollection();
        Should.Throw<InvalidOperationException>(() => Builder(services).UseInMemoryReadModels().AddProjections(p => p.Add<AuctionRow>().Add<AuctionRow>()))
            .Message.ShouldContain("auction-list");
    }

    [Fact]
    public async Task A_sql_projection_needs_a_store_that_runs_sql()
    {
        var services = new ServiceCollection();
        Builder(services).UseInMemoryReadModels().AddProjections(p => p.Add(new Totals()));
        await using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetServices<ProjectionHost>().ToList()).Message.ShouldContain("UseNpgsqlReadModels");
    }

    [Fact]
    public async Task A_sql_projection_is_hosted_once_as_host_and_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISqlProjectionStore>(new SqlProjectionHostTests.MemorySqlStore());
        Builder(services).UseInMemoryReadModels().AddProjections(p => p.Add(new Totals()));
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetServices<ProjectionHost>().Single().ShouldBeOfType<SqlProjectionHost>();
        provider.GetServices<IHostedService>().ShouldContain(host);
        host.Name.ShouldBe("totals");
    }

    internal sealed class Totals : ISqlProjection
    {
        public string Name => "totals";

        public int Version => 1;

        public IReadOnlyCollection<string>? StreamTypes => ["auction"];

        public List<PositionedEvent> Seen { get; } = [];

        public ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<int> Apply(IReadOnlyList<PositionedEvent> events, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            Seen.AddRange(events);
            return ValueTask.FromResult(events.Count);
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.Opened))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.BidPlaced))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.Closed))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Auction.Renamed))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Lot.Registered))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AuctionRow))]
internal sealed partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;
