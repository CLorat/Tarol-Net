using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas.Tests;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas.IntegrationTests;

/// <summary>The one registration on PostgreSQL: the schema is ensured when the host starts, the worker purges through the store.</summary>
[Trait("Category", "Integration")]
public sealed class BuilderIntegrationTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UseNpgsqlStore_ensures_the_schema_at_start_and_the_worker_runs_and_purges_through_the_store()
    {
        var schema = postgres.NewSchema();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Settlement>())
            .WithReactions(GeneratedReactions.Set)
            .UseNpgsqlStore(postgres.ConnectionString, new NpgsqlStoreOptions { Schema = schema })
            .AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20));
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<ReactionOptions>();

        await host.StartAsync(Ct);                                                     // EnsureSchema, then the worker
        var store = host.Services.GetRequiredService<NpgsqlAggregateStore>();
        store.Schema.ShouldBe(schema);
        host.Services.GetRequiredService<IIdempotencyKeyPurge>().ShouldBeSameAs(store);
        options.PurgeKeys.ShouldNotBeNull();                                             // the store's, wired by the host factory
        var alice = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await host.Services.GetRequiredService<ChangePipeline>().Run(new SettleAuction(auction.Id), alice, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var clock = Stopwatch.StartNew();
        while (await store.Outbox.Pending(Ct) > 1 && clock.Elapsed < TimeSpan.FromSeconds(20))    // the settlement timeout stays (due in 3 days)
            await Task.Delay(20, Ct);
        await host.StopAsync(Ct);

        (await store.Outbox.Pending(Ct)).ShouldBe(1);
        var payouts = 0;
        await foreach (var e in store.ReadAll(GlobalPosition.Start, 1000, Ct))
            if (e.Event.Event.Event is Payout.Due)
                payouts++;
        payouts.ShouldBe(1);
    }

    [Fact]
    public async Task UseNpgsqlStore_takes_a_data_source_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddTarol(AppJson.Default, codec => codec.Add<Auction>()).UseNpgsqlStore(_ => postgres.DataSource,
            new NpgsqlStoreOptions { Schema = postgres.NewSchema() }, ensureSchema: false);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetServices<IHostedService>().ShouldBeEmpty();
        await provider.GetRequiredService<NpgsqlAggregateStore>().EnsureSchema(Ct);
        (await provider.GetRequiredService<IAggregateReader>().Load(Id<Auction>.New(), OperationContext.Start(Actor.User("a"), Option.None, TimeProvider.System), Ct))
            .IsNone.ShouldBeTrue();
    }
}
