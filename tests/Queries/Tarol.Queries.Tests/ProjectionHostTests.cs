using Tarol.Persistence;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

public sealed class ProjectionHostTests
{
    [Fact]
    public async Task One_row_per_auction_and_other_stream_types_are_ignored()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var auctions = Seed(test, 5, A);
        test.Given(Lot.Register("crate", Now), A);

        await test.CatchUp();

        test.Rows(A).Count.ShouldBe(5);
        test.Rows(A).Select(r => r.Id).Order().ShouldBe(auctions.Select(a => a.Id.Value).Order());
        test.Host.State.ShouldBe(ProjectionState.Current);
    }

    [Fact]
    public async Task Rows_equal_the_aggregates()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var auctions = Seed(test, 12, A);

        await test.CatchUp();

        for (var i = 0; i < auctions.Count; i++)
        {
            var row = test.Row(auctions[i].Id.Value, A).ShouldNotBeNull();
            row.Bids.ShouldBe(i % 4);
            row.HighestBid.ShouldBe(auctions[i].HighestBid);
            row.Status.ShouldBe(auctions[i].Status);
            row.Seller.ShouldBe(auctions[i].Seller);
            row.Winner.ShouldBe(i % 4 == 0 ? null : "carla");
        }
        test.ReadModels.Rows.Single(r => r.Key.Id == auctions[3].Id.Value).Version.ShouldBe(auctions[3].Version);
    }

    [Fact]
    public async Task Tenants_are_isolated()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 3, A);
        Seed(test, 2, B);

        await test.CatchUp();

        test.Rows(A).Count.ShouldBe(3);
        test.Rows(B).Count.ShouldBe(2);
        test.Rows().ShouldBeEmpty();
        test.ReadModels.Rows.Select(r => r.Key.Tenant).Distinct().Order().ShouldBe([TenantA.Value, TenantB.Value]);
    }

    [Fact]
    public async Task Small_batches_give_the_same_rows_and_the_projection_turns_current_at_the_head()
    {
        var big = ProjectionTest<AuctionRow>.Create();
        var small = ProjectionTest<AuctionRow>.Over(big.Store!, new ProjectionOptions { Batch = 3 });
        Seed(big, 10, A);

        await big.CatchUp();
        (await small.Host.RunOnce(TestContext.Current.CancellationToken)).ShouldBe(3);
        small.Host.State.ShouldBe(ProjectionState.Building);
        (await small.ReadModels.Checkpoint(TestContext.Current.CancellationToken)).State.ShouldBe(ProjectionState.Building);
        await small.CatchUp();

        small.Host.State.ShouldBe(ProjectionState.Current);
        small.Rows(A).ShouldBe(big.Rows(A));
        small.Host.Position.ShouldBe(big.Host.Position);
    }

    [Fact]
    public async Task An_empty_feed_turns_the_projection_current()
    {
        var test = ProjectionTest<AuctionRow>.Create();

        (await test.Host.RunOnce(TestContext.Current.CancellationToken)).ShouldBe(0);

        (await test.ReadModels.Checkpoint(TestContext.Current.CancellationToken)).ShouldBe(new ProjectionCheckpoint(GlobalPosition.Start, ProjectionState.Current));
        (await test.Host.RunOnce(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task A_replay_from_an_older_checkpoint_changes_nothing()
    {
        var first = ProjectionTest<AuctionRow>.Create();
        Seed(first, 8, A);
        await first.CatchUp();

        // a second store holds the same rows at the start checkpoint: every event is at or below a row's version
        var replay = ProjectionTest<AuctionRow>.Over(first.Store!);
        (await replay.ReadModels.Write(new ProjectionBatch<AuctionRow>(first.ReadModels.Rows, GlobalPosition.Start, GlobalPosition.Start, ProjectionState.Building),
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        var before = replay.ReadModels.Rows.ToDictionary(r => r.Key);

        await replay.CatchUp();

        replay.ReadModels.Rows.ToDictionary(r => r.Key).ShouldBe(before);
        replay.Host.Position.ShouldBe(first.Host.Position);
    }

    [Fact]
    public async Task An_event_the_projection_ignores_keeps_the_row_and_its_version()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var auction = Auction.Open("ada", "Lot", Now.AddDays(1), Now);
        test.Given(auction, A);
        await test.CatchUp();
        var before = test.ReadModels.Rows.Single();

        auction.Rename("Another title").IsSuccess.ShouldBeTrue();
        test.Given(auction, A);
        await test.CatchUp();

        test.ReadModels.Rows.Single().ShouldBeSameAs(before);
    }

    [Fact]
    public async Task A_crash_before_the_write_leaves_rows_and_checkpoint_and_the_restart_applies_every_event_once()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 6, A);
        test.Host.CrashBeforeWrite = _ => true;

        await Should.ThrowAsync<InvalidOperationException>(() => test.CatchUp());
        test.ReadModels.Rows.ShouldBeEmpty();
        (await test.ReadModels.Checkpoint(TestContext.Current.CancellationToken)).Position.ShouldBe(GlobalPosition.Start);

        var restarted = ProjectionTest<AuctionRow>.Over(test.Store!);
        await restarted.CatchUp();
        restarted.Rows(A).Sum(r => r.Bids).ShouldBe(Enumerable.Range(0, 6).Sum(i => i % 4));
    }

    [Fact]
    public async Task A_second_runner_is_refused_by_the_checkpoint_guard_and_reads_again()
    {
        var test = ProjectionTest<AuctionRow>.Create(options: new ProjectionOptions { Batch = 4 });
        Seed(test, 10, A);
        var other = new ProjectionHost<AuctionRow>(test.Store!, test.ReadModels, new ProjectionOptions { Batch = 4 });

        (await test.Host.RunOnce(TestContext.Current.CancellationToken)).ShouldBe(4);   // this runner is at 4 …
        (await other.RunOnce(TestContext.Current.CancellationToken)).ShouldBe(4);       // … the other reads 4 and moves it to 8 …
        (await test.Host.RunOnce(TestContext.Current.CancellationToken)).ShouldBe(4);   // … so this runner, still at 4, is refused
        test.Host.Conflicts.ShouldBe(1);
        test.ReadModels.Refused.ShouldBe(1);
        await test.Host.CatchUp(TestContext.Current.CancellationToken);
        await other.CatchUp(TestContext.Current.CancellationToken);

        test.Rows(A).Sum(r => r.Bids).ShouldBe(Enumerable.Range(0, 10).Sum(i => i % 4));
        test.Host.Position.ShouldBe(other.Position);
    }

    [Fact]
    public async Task Two_runners_at_once_give_exact_rows()
    {
        var test = ProjectionTest<AuctionRow>.Create(options: new ProjectionOptions { Batch = 2 });
        Seed(test, 40, A);
        var other = new ProjectionHost<AuctionRow>(test.Store!, test.ReadModels, new ProjectionOptions { Batch = 2 });

        await Task.WhenAll(Task.Run(() => test.Host.CatchUp(TestContext.Current.CancellationToken)), Task.Run(() => other.CatchUp(TestContext.Current.CancellationToken)));
        await test.Host.CatchUp(TestContext.Current.CancellationToken);

        test.Rows(A).Count.ShouldBe(40);
        test.Rows(A).Sum(r => r.Bids).ShouldBe(Enumerable.Range(0, 40).Sum(i => i % 4));
    }

    [Fact]
    public async Task WaitFor_times_out_without_a_batch_and_returns_once_the_host_passed_the_commit()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 2, A);
        var head = await Head(test.Store!);

        (await test.Host.WaitFor(head, TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken)).ShouldBeFalse();
        var waiting = test.Host.WaitFor(head, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await test.CatchUp();

        (await waiting).ShouldBeTrue();
        (await test.Host.WaitFor(GlobalPosition.Start, TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_hosted_loop_projects_new_commits_and_recovers_from_a_failing_feed()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var feed = new FlakyFeed(test.Store!, failures: 2);
        var readModels = new InMemoryReadModelStore<AuctionRow>();
        using var host = new ProjectionHost<AuctionRow>(feed, readModels, new ProjectionOptions { Poll = TimeSpan.FromMilliseconds(5), Backoff = TimeSpan.FromMilliseconds(5) });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var auction = Auction.Open("ada", "Lot", Now.AddDays(1), Now);
        test.Given(auction, A);
        (await host.WaitFor(await Head(test.Store!), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).ShouldBeTrue();
        auction.Bid("bob", 5m).IsSuccess.ShouldBeTrue();
        test.Given(auction, A);
        (await host.WaitFor(await Head(test.Store!), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);

        feed.Reads.ShouldBeGreaterThan(2);
        readModels.Rows.Single().Row.Bids.ShouldBe(1);
    }

    [Fact]
    public void Options_are_validated()
    {
        var store = new InMemoryAggregateStore(TimeProvider.System);
        Should.Throw<ArgumentOutOfRangeException>(() => new ProjectionHost<AuctionRow>(store, new InMemoryReadModelStore<AuctionRow>(), new ProjectionOptions { Batch = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new ProjectionHost<AuctionRow>(store, new InMemoryReadModelStore<AuctionRow>(), new ProjectionOptions { Poll = TimeSpan.Zero }));
        Should.Throw<ArgumentOutOfRangeException>(() => new ProjectionHost<AuctionRow>(store, new InMemoryReadModelStore<AuctionRow>(),
            new ProjectionOptions { Backoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(1) }));
    }

    internal static async Task<GlobalPosition> Head(IEventFeed feed)
    {
        var head = GlobalPosition.Start;
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, int.MaxValue, CancellationToken.None))
            head = e.Position;
        return head;
    }
}
