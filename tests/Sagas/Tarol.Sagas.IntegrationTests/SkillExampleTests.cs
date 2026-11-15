using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Sagas.Tests;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas.IntegrationTests;

/// <summary>The host lines of skills/tarol-sagas/SKILL.md, verbatim, on PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class SkillExampleTests(Postgres postgres)
{
    [Fact]
    public async Task The_skill_host_example_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await postgres.Store(TimeProvider.System);
        var builder = Host.CreateApplicationBuilder();

        // host
        builder.Services.AddSingleton<IOutbox>(store.Outbox);
        builder.Services.AddSingleton(new ChangePipeline(store, TimeProvider.System));
        builder.Services.AddTarolReactions(GeneratedReactions.Set, o => o.PurgeKeys = store.PurgeKeys);

        using var host = builder.Build();
        await host.StartAsync(ct);
        var alice = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        auction.Settle().IsSuccess.ShouldBeTrue();
        (await store.Append(auction, 0, alice, Option.None, ct)).IsSuccess.ShouldBeTrue();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((await store.Load((Id<Settlement>)auction.Id.Value, alice, ct)).IsNone && clock.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(20, ct);
        await host.StopAsync(ct);

        (await store.Load((Id<Settlement>)auction.Id.Value, alice, ct)).IsSome.ShouldBeTrue();
    }
}
