using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;

namespace Tarol.Sagas.Tests;

/// <summary>One registration (R-14): AddTarol → TarolBuilder, the store, the worker.</summary>
public sealed class BuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<TarolBuilder> configure)
    {
        var services = new ServiceCollection();
        configure(services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Settlement>()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task AddTarol_with_the_in_memory_store_runs_changes_and_the_worker_runs_their_reactions()
    {
        await using var provider = Build(tarol => tarol.UseInMemoryStore().AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20),
            GeneratedReactions.Set));
        var store = provider.GetRequiredService<InMemoryAggregateStore>();
        provider.GetRequiredService<IAggregateStore>().ShouldBeSameAs(store);
        provider.GetRequiredService<IAggregateReader>().ShouldBeSameAs(store);
        provider.GetRequiredService<IEventFeed>().ShouldBeSameAs(store);
        provider.GetRequiredService<IOutbox>().ShouldBeSameAs(store.Outbox);
        provider.GetRequiredService<IJobRows>().ShouldBeSameAs(store.Jobs);
        provider.GetService<IIdempotencyKeyPurge>().ShouldBeNull();
        var host = provider.GetServices<IHostedService>().ShouldHaveSingleItem().ShouldBeOfType<ReactionHost>();

        await host.StartAsync(Ct);
        var alice = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await provider.GetRequiredService<ChangePipeline>().Run(new SettleAuction(auction.Id), alice, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var clock = Stopwatch.StartNew();
        while (!store.Recorded.Any(e => e.Event.Event is Payout.Due) && clock.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, Ct);
        await host.StopAsync(Ct);

        store.Recorded.ShouldContain(e => e.Event.Event is Payout.Due);               // the builder's reactions reached the store
    }

    [Fact]
    public async Task The_order_of_WithReactions_and_the_store_does_not_matter_for_the_store()
    {
        await using var provider = Build(tarol => tarol.UseInMemoryStore().WithReactions(GeneratedReactions.Set));
        var store = provider.GetRequiredService<InMemoryAggregateStore>();
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        auction.Settle().IsSuccess.ShouldBeTrue();

        (await store.Append(auction, 0, OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System), Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await store.Outbox.Pending(Ct)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_builder_refuses_a_second_store_a_second_AddTarol_and_a_worker_for_a_hand_written_table()
    {
        var services = new ServiceCollection();
        var tarol = services.AddTarol(AppJson.Default, codec => codec.Add<Auction>());

        tarol.UseInMemoryStore();
        Should.Throw<InvalidOperationException>(() => tarol.UseInMemoryStore()).Message.ShouldContain("one store");
        Should.Throw<InvalidOperationException>(() => services.AddTarol(AppJson.Default, _ => { })).Message.ShouldContain("twice");
        tarol.WithReactions(OutboxReactions.From(static _ => ReadOnlySpan<string>.Empty));
        Should.Throw<InvalidOperationException>(() => tarol.AddReactionWorker()).Message.ShouldContain("WithReactions");
    }

    [Fact]
    public async Task A_worker_without_reactions_runs_the_row_handlers_other_packages_register()
    {
        var handler = new CountingRows();
        await using var provider = Build(tarol =>
        {
            tarol.UseInMemoryStore().WithOutboxRows(handler).AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20));
            tarol.OutboxTable.ShouldBeSameAs(handler);                              // no reactions: the one part as is
            tarol.Services.AddSingleton<IOutboxRowHandler>(handler);
        });
        var store = provider.GetRequiredService<InMemoryAggregateStore>();
        var host = provider.GetServices<IHostedService>().ShouldHaveSingleItem();

        await host.StartAsync(Ct);
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        (await store.Append(auction, 0, OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System), Option.None, Ct)).IsSuccess.ShouldBeTrue();
        var clock = Stopwatch.StartNew();
        while (handler.Runs == 0 && clock.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, Ct);
        await host.StopAsync(Ct);

        handler.Runs.ShouldBe(1);
        (await store.Outbox.Pending(Ct)).ShouldBe(0);
    }

    [Fact]
    public void The_store_table_joins_reactions_and_added_rows()
    {
        var tarol = new ServiceCollection().AddTarol(AppJson.Default, codec => codec.Add<Auction>());
        tarol.OutboxTable.ShouldBeNull();
        tarol.WithOutboxRows(new CountingRows()).WithReactions(GeneratedReactions.Set);
        var created = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow()).UncommittedChanges[0].Event;

        tarol.OutboxTable!.ReactionsTo(created).ToArray().ShouldBe([.. GeneratedReactions.Set.ReactionsTo(created).ToArray(), "count:created"]);
        OutboxReactions.Combine(null, null).ShouldBeNull();
    }

    private sealed class CountingRows : IOutboxRowHandler
    {
        private static readonly string[] Rows = ["count:created"];
        private int _runs;

        public int Runs => _runs;

        public IReadOnlyList<string> Names => Rows;

        public ReadOnlySpan<string> ReactionsTo(Primitives.DomainEvent domainEvent) => domainEvent is Auction.Opened ? Rows : ReadOnlySpan<string>.Empty;

        public DateTimeOffset? DueAt(Primitives.DomainEvent domainEvent, string reaction) => null;

        public bool IsScheduled(string reaction) => false;

        public string ActivityName(string reaction) => "count";

        public ValueTask<Results.Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, Primitives.RecordedEvent recorded, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return ValueTask.FromResult<Results.Result<ChangeOutcome>>(new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0));
        }
    }

    [Fact]
    public void An_app_registered_TimeProvider_is_kept()
    {
        var services = new ServiceCollection();
        var clock = new FixedClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddTarol(AppJson.Default, codec => codec.Add<Auction>()).UseInMemoryStore();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    }
}
