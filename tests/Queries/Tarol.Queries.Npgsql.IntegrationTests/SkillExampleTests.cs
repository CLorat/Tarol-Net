using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Npgsql.IntegrationTests.Skill;

// skills/tarol-queries-npgsql/SKILL.md, the SQL projection verbatim
public sealed class SellerTotals(string schema) : ISqlProjection
{
    public string Name => "seller-totals";
    public int Version => 1;
    public IReadOnlyCollection<string>? StreamTypes => ["auction"];

    public async ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {schema}.seller_totals (tenant uuid, seller text, auctions integer, PRIMARY KEY (tenant, seller))",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
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
        command.Parameters.Add(new NpgsqlParameter { Value = opened.Select(e => e.Event.Context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = opened.Select(e => ((Auction.Opened)e.Event.Event.Event).Seller).ToArray() });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

[Trait("Category", "Integration")]
public sealed class SkillExampleTests(Postgres postgres)
{
    [Fact]
    public async Task The_skill_registration_and_sql_projection_run()
    {
        var schema = $"skill_{Environment.ProcessId}";
        var services = new ServiceCollection();
        services.AddSingleton(postgres.DataSource);
        new TarolBuilder(services, Postgres.Json, new EventCodec(Postgres.Json).Add<Auction>())
            .UseNpgsqlStore(static p => p.GetRequiredService<NpgsqlDataSource>(), new NpgsqlStoreOptions { Schema = schema, NotifyFeed = true })
            .UseNpgsqlReadModels()
            .AddProjections(p => p.Add<AuctionRow>().Add(new SellerTotals(schema)));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var appended = await provider.GetRequiredService<IAggregateStore>().Append(Auction.Open("ada", "Vase", Postgres.Now.AddDays(1), Postgres.Now), 0,
                Setup.Context(Setup.TenantA), default, TestContext.Current.CancellationToken);
            appended.TryGetValue(out GlobalPosition position).ShouldBeTrue();
            var host = provider.GetServices<ProjectionHost>().Single(h => h.Name == "seller-totals");
            (await host.WaitFor(position, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).ShouldBeTrue();

            (await postgres.Scalar($"SELECT auctions FROM {schema}.seller_totals WHERE seller = 'ada'")).ShouldBe(1);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }
}
