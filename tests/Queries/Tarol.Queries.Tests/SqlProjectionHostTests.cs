using Tarol.Persistence;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

public sealed class SqlProjectionHostTests
{
    /// <summary>A store that keeps checkpoints like the Npgsql one (guarded) and calls Apply without a database.</summary>
    internal sealed class MemorySqlStore : ISqlProjectionStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<(string, int), ProjectionCheckpoint> _checkpoints = [];

        public int Prepared { get; private set; }

        public ValueTask Prepare(ISqlProjection projection, CancellationToken cancellationToken)
        {
            Prepared++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProjectionCheckpoint> Checkpoint(ISqlProjection projection, CancellationToken cancellationToken)
        {
            lock (_gate)
                return ValueTask.FromResult(_checkpoints.GetValueOrDefault((projection.Name, projection.Version)));
        }

        public async ValueTask<(bool Written, int Rows)> Write(ISqlProjection projection, IReadOnlyList<PositionedEvent> events, GlobalPosition from,
            GlobalPosition through, ProjectionState state, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_checkpoints.GetValueOrDefault((projection.Name, projection.Version)).Position != from)
                    return (false, 0);
                _checkpoints[(projection.Name, projection.Version)] = new ProjectionCheckpoint(through, state);
            }
            return (true, await projection.Apply(events, null!, null!, cancellationToken));
        }
    }

    [Fact]
    public async Task The_host_hands_only_the_projection_stream_types_and_moves_the_checkpoint_past_every_event()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 3, A);
        test.Given(Lot.Register("crate", Now), A);
        var store = new MemorySqlStore();
        var totals = new BuilderTests.Totals();
        var host = new SqlProjectionHost(totals, test.Store!, store);

        await host.CatchUp(TestContext.Current.CancellationToken);

        store.Prepared.ShouldBe(1);
        totals.Seen.ShouldAllBe(e => e.StreamType == "auction");
        totals.Seen.Count.ShouldBe(test.Store!.Recorded.Count - 1);
        host.Position.ShouldBe(await ProjectionHostTests.Head(test.Store));
        host.State.ShouldBe(ProjectionState.Current);
    }

    [Fact]
    public async Task A_second_runner_is_refused_and_no_event_is_applied_twice()
    {
        var test = ProjectionTest<AuctionRow>.Create();
        Seed(test, 6, A);
        var store = new MemorySqlStore();
        var totals = new BuilderTests.Totals();
        var one = new SqlProjectionHost(totals, test.Store!, store, new ProjectionOptions { Batch = 3 });
        var two = new SqlProjectionHost(totals, test.Store!, store, new ProjectionOptions { Batch = 3 });

        await two.RunOnce(TestContext.Current.CancellationToken);           // two at 3
        await one.RunOnce(TestContext.Current.CancellationToken);           // one reads 3, moves it to 6
        await two.RunOnce(TestContext.Current.CancellationToken);           // two, still at 3: refused
        await two.CatchUp(TestContext.Current.CancellationToken);
        await one.CatchUp(TestContext.Current.CancellationToken);

        two.Conflicts.ShouldBe(1);
        totals.Seen.Select(e => e.Position).ShouldBeUnique();
        totals.Seen.Count.ShouldBe(test.Store!.Recorded.Count);
    }
}
