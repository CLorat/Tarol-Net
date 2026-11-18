using Tarol.Persistence;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

/// <summary>A host defined outside Tarol.Queries (step 18: search) gets the loop, the guard and the waits of every projection.</summary>
public sealed class DerivedHostTests
{
    /// <summary>Counts the auction events of the feed; its checkpoint and its guard live in memory.</summary>
    private sealed class CountingHost(IEventFeed feed, ProjectionOptions? options = null) : ProjectionHost("counting", 1, feed, options, null, null, null, null, null)
    {
        private ProjectionCheckpoint _stored;

        public int Prepared { get; private set; }

        public int Counted { get; private set; }

        /// <summary>When set, the next write is refused as if another runner had moved the checkpoint.</summary>
        public bool RefuseNext { get; set; }

        protected override ValueTask Prepare(CancellationToken cancellationToken)
        {
            Prepared++;
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken) => ValueTask.FromResult(_stored);

        protected override ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition to,
            ProjectionState state, CancellationToken cancellationToken)
        {
            if (RefuseNext || _stored.Position != from)
            {
                RefuseNext = false;
                return ValueTask.FromResult((false, 0));
            }
            var mine = events.Count(static e => e.StreamType == Auction.StreamType);
            Counted += mine;
            _stored = new ProjectionCheckpoint(to, state);
            return ValueTask.FromResult((true, mine));
        }
    }

    [Fact]
    public async Task A_derived_host_catches_up_and_turns_current()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var auctions = Seed(test, 6, A);
        test.Given(Lot.Register("crate", Now), A);
        var host = new CountingHost(test.Store!, new ProjectionOptions { Batch = 4 });

        await host.CatchUp(TestContext.Current.CancellationToken);

        host.Prepared.ShouldBe(1);
        host.Counted.ShouldBe(auctions.Sum(a => (int)a.Version));
        host.State.ShouldBe(ProjectionState.Current);
        host.Position.ShouldBe((await LastPosition(test.Store!)));
    }

    [Fact]
    public async Task A_refused_write_counts_a_conflict_and_the_host_reads_again()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        var auctions = Seed(test, 3, A);
        var host = new CountingHost(test.Store!) { RefuseNext = true };

        await host.RunOnce(TestContext.Current.CancellationToken);
        host.Conflicts.ShouldBe(1);
        host.Counted.ShouldBe(0);
        await host.CatchUp(TestContext.Current.CancellationToken);

        host.Counted.ShouldBe(auctions.Sum(a => (int)a.Version));
    }

    [Fact]
    public async Task WaitFor_returns_once_a_derived_host_passed_the_position()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 2, A);
        var head = await LastPosition(test.Store!);
        var host = new CountingHost(test.Store!);

        (await host.WaitFor(head, TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeFalse();
        await host.CatchUp(TestContext.Current.CancellationToken);

        (await host.WaitFor(head, TimeSpan.Zero, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private static async Task<GlobalPosition> LastPosition(IEventFeed feed)
    {
        var last = GlobalPosition.Start;
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, 10_000, TestContext.Current.CancellationToken))
            last = e.Position;
        return last;
    }
}
