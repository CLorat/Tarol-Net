using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class HostingTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Auctions per (tenant, seller): a table keyed by something other than one stream.</summary>
    internal sealed class SellerTotals(string schema) : ISqlProjection
    {
        public string Name => "seller-totals";

        public int Version => 1;

        public IReadOnlyCollection<string>? StreamTypes => ["auction"];

        public async ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"CREATE TABLE IF NOT EXISTS {schema}.seller_totals (tenant uuid NOT NULL, seller text NOT NULL, auctions integer NOT NULL, PRIMARY KEY (tenant, seller))";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async ValueTask<int> Apply(IReadOnlyList<PositionedEvent> events, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            var opened = events.Where(e => e.Event.Event.Event is Auction.Opened).ToList();
            if (opened.Count == 0)
                return 0;
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {schema}.seller_totals AS t (tenant, seller, auctions)
                SELECT tenant, seller, count(*) FROM unnest($1::uuid[], $2::text[]) AS u(tenant, seller) GROUP BY tenant, seller
                ON CONFLICT (tenant, seller) DO UPDATE SET auctions = t.auctions + excluded.auctions
                """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = opened.Select(e => e.Event.Context.Tenant.TryGetValue(out Tarol.StronglyTypedId.Id<Tarol.Kernel.ITenant> t) ? t.Value : Guid.Empty).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = opened.Select(e => ((Auction.Opened)e.Event.Event.Event).Seller).ToArray() });
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_sql_projection_writes_in_the_batch_transaction_exactly_once_with_two_runners()
    {
        var setup = await postgres.Create();
        await setup.Seed(40, TenantA);
        await setup.Seed(10, TenantB);
        var projection = new SellerTotals(setup.Schema);
        var one = new SqlProjectionHost(projection, setup.Store, setup.ReadModels, new ProjectionOptions { Batch = 9 });
        var two = new SqlProjectionHost(projection, setup.Store, setup.ReadModels, new ProjectionOptions { Batch = 9 });

        await Task.WhenAll(Task.Run(() => setup.CatchUp(one), Ct), Task.Run(() => setup.CatchUp(two), Ct));
        await setup.CatchUp(one);

        (await postgres.Scalar($"SELECT auctions FROM {setup.Schema}.seller_totals WHERE tenant = '{TenantA.Value}' AND seller = 'ada'")).ShouldBe(20);
        (await postgres.Scalar($"SELECT sum(auctions) FROM {setup.Schema}.seller_totals")).ShouldBe(50L);
        var checkpoint = await setup.ReadModels.Checkpoint(projection, Ct);
        checkpoint.State.ShouldBe(ProjectionState.Current);
        checkpoint.Position.ShouldBeGreaterThanOrEqualTo(setup.Last);
    }

    [Fact]
    public async Task UseNpgsqlReadModels_follows_the_store_schema_listens_when_it_notifies_and_hosts_the_projections()
    {
        var schema = $"hosted_{Environment.ProcessId}";
        var services = new ServiceCollection();
        services.AddSingleton(postgres.DataSource);
        var codec = new EventCodec(Postgres.Json).Add<Auction>().Add<Lot>();
        new TarolBuilder(services, Postgres.Json, codec)
            .UseNpgsqlStore(static provider => provider.GetRequiredService<NpgsqlDataSource>(), new NpgsqlStoreOptions { Schema = schema, NotifyFeed = true })
            .UseNpgsqlReadModels()
            .AddProjections(p => p.Add<AuctionRow>());                                 // poll 1 s with the signal
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NpgsqlReadModels>().Schema.ShouldBe(schema);
        provider.GetRequiredService<IFeedSignal>().ShouldBeOfType<NpgsqlFeedSignal>().Channel.ShouldBe(schema + "_feed");
        provider.GetRequiredService<ISqlProjectionStore>().ShouldBeSameAs(provider.GetRequiredService<IReadModels>());
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(Ct);
        try
        {
            var store = provider.GetRequiredService<IAggregateStore>();
            var positions = provider.GetRequiredService<IProjectionPositions>();
            for (var i = 0; i < 3; i++)
            {
                var appended = await store.Append(Auction.Open("ada", $"Hosted {i}", Postgres.Now.AddDays(1), Postgres.Now), 0, Context(TenantA), default, Ct);
                appended.TryGetValue(out GlobalPosition position).ShouldBeTrue();
                (await positions.WaitFor<AuctionRow>(position, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
            }
            var page = (await provider.GetRequiredService<IReadModels>().Store<AuctionRow>().Search(Query(TenantA) with { Count = true }, Ct)).Ok();
            page.Total.ShouldBe(3);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_the_feed_notify_the_hosts_poll()
    {
        var services = new ServiceCollection();
        services.AddSingleton(postgres.DataSource);
        new TarolBuilder(services, Postgres.Json, new EventCodec(Postgres.Json).Add<Auction>())
            .UseNpgsqlStore(static provider => provider.GetRequiredService<NpgsqlDataSource>(), new NpgsqlStoreOptions { Schema = $"polled_{Environment.ProcessId}" })
            .UseNpgsqlReadModels();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IFeedSignal>().ShouldNotBeOfType<NpgsqlFeedSignal>();
    }

    [Fact]
    public void A_row_type_missing_from_the_json_context_is_named()
        => Should.Throw<InvalidOperationException>(() => new NpgsqlReadModelStore<AuctionRow>(postgres.DataSource,
                new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine() }))
            .Message.ShouldContain("[JsonSerializable(typeof(AuctionRow))]");
}
