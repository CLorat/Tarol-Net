using System.Collections.Concurrent;
using System.Diagnostics;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;
using Tarol.Testing.Metrics;

namespace Tarol.Sagas.Tests;

/// <summary>The reactions' meter and the activity tree of a change a reaction runs (step 15B; operations.md §11).</summary>
public sealed class ObservabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_reaction_counts_its_outcome_and_records_its_duration_and_lateness_without_the_tenant()
    {
        using var meters = new Measurements();
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryAggregateStore(clock, new InMemoryStoreOptions { StateOptions = AppJson.Default.Options, Reactions = GeneratedReactions.Set });
        var alice = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), clock);
        var auction = Auction.Create("alice", 100, clock.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, clock.GetUtcNow()).GetValueOrThrow();
        auction.Settle().IsSuccess.ShouldBeTrue();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        clock.Now = clock.Now.AddSeconds(42);                                                   // the worker starts late

        var attempts = await new ReactionWorker(store.Outbox, GeneratedReactions.Set, new ChangePipeline(store, clock), clock, meters: meters).Drain(Ct);

        attempts.Count.ShouldBe(2);
        var runs = meters.Of("tarol.reactions.runs");
        runs.Select(m => (string)m.Tags["tarol.outcome"]!).ShouldAllBe(o => o == "completed");
        runs.Select(m => (string)m.Tags["tarol.reaction"]!).Order().ShouldBe(attempts.Select(a => a.Row.Reaction).Order());
        runs.ShouldAllBe(m => m.Unit == "{row}" && m.Meter == ReactionWorker.MeterName);
        meters.Of("tarol.reactions.duration").ShouldAllBe(m => m.Unit == "s");
        meters.Of("tarol.reactions.lateness").Select(m => m.Value).ShouldAllBe(v => v == 42);
        meters.All.ShouldAllBe(m => m.Tags.Keys.All(k => k == "tarol.reaction" || k == "tarol.outcome"));
    }

    [Fact]
    public async Task The_change_a_reaction_runs_is_a_child_span_of_the_reaction()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is ReactionWorker.ActivitySourceName or ChangePipeline.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var store = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions { StateOptions = AppJson.Default.Options, Reactions = GeneratedReactions.Set });
        var alice = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        auction.Settle().IsSuccess.ShouldBeTrue();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        var won = store.Recorded.Single(e => e.Event.Event is Bid.Won).EventId.ToString();

        _ = await new ReactionWorker(store.Outbox, GeneratedReactions.Set, new ChangePipeline(store, TimeProvider.System), TimeProvider.System).Drain(Ct);

        // the listener is process-wide: keep this event's reactions
        var payout = spans.Single(a => a.DisplayName == "reaction MarkPayoutDue" && (string?)a.GetTagItem("messaging.message.id") == won);
        var change = spans.Single(a => a.Source.Name == ChangePipeline.ActivitySourceName && a.ParentSpanId == payout.SpanId);
        change.TraceId.ShouldBe(payout.TraceId);
        change.DisplayName.ShouldStartWith("change ");
        change.GetTagItem("tarol.outcome").ShouldBe("committed");
    }
}
