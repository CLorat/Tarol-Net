using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;

namespace Tarol.Sagas.Tests;

public sealed class WorkerTests
{
    private static readonly string[] Ghost = ["RenamedAway"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (InMemoryAggregateStore Store, ChangePipeline Pipeline, OperationContext Alice) Setup(TimeProvider time, IOutboxReactions? reactions = null)
    {
        var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { StateOptions = AppJson.Default.Options, Reactions = reactions ?? GeneratedReactions.Set });
        return (store, new ChangePipeline(store, time), OperationContext.Start(Actor.User("alice"), Option.None, time));
    }

    private static async Task<Auction> Won(InMemoryAggregateStore store, OperationContext alice, TimeProvider time)
    {
        var auction = Auction.Create("alice", 100, time.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, time.GetUtcNow()).GetValueOrThrow();
        auction.Settle().IsSuccess.ShouldBeTrue();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        return auction;
    }

    [Fact]
    public async Task Each_row_runs_in_a_consumer_activity_linked_to_the_committing_trace()
    {
        var stopped = new List<Activity>();
        using var source = new ActivitySource("Tarol.Sagas.Tests.Http");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is ReactionWorker.ActivitySourceName or "Tarol.Sagas.Tests.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        var (store, pipeline, alice) = Setup(TimeProvider.System);
        ActivityTraceId request;
        using (var http = source.StartActivity("POST /auctions/{id}/settle")!)
        {
            _ = await Won(store, alice, TimeProvider.System);
            request = http.TraceId;
        }

        _ = await new ReactionWorker(store.Outbox, GeneratedReactions.Set, pipeline, TimeProvider.System).Drain(Ct);

        Activity[] reactions;
        lock (stopped)
            reactions = [.. stopped.Where(a => a.Source.Name == ReactionWorker.ActivitySourceName)];
        reactions.Length.ShouldBe(2);
        var payout = reactions.Single(a => a.DisplayName == "reaction MarkPayoutDue");
        payout.Kind.ShouldBe(ActivityKind.Consumer);
        payout.ParentId.ShouldBeNull();
        payout.TraceId.ShouldNotBe(request);
        payout.Links.ShouldHaveSingleItem().Context.TraceId.ShouldBe(request);
        payout.GetTagItem("tarol.reaction").ShouldBe("MarkPayoutDue");
        payout.GetTagItem("tarol.stream.type").ShouldBe("auction");
        payout.GetTagItem("tarol.attempt").ShouldBe(1);
        payout.GetTagItem("tarol.verdict").ShouldBe("Done");
        payout.GetTagItem("messaging.message.id").ShouldBe(store.Recorded.Single(e => e.Event.Event is Bid.Won).EventId.ToString());
    }

    [Fact]
    public async Task A_worker_that_outlived_its_lease_reports_it_and_the_key_keeps_the_effect_single()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        var (store, pipeline, alice) = Setup(clock);
        _ = await Won(store, alice, clock);
        var slow = (await store.Outbox.Claim(10, TimeSpan.FromSeconds(30), Ct)).Single(r => r.Reaction == "MarkPayoutDue");
        _ = (await GeneratedReactions.Set.Run(pipeline, slow.Reaction, slow.Event.Event, Ct)).GetValueOrThrow();   // ran, then stalled
        clock.Now += TimeSpan.FromSeconds(31);

        var again = await new ReactionWorker(store.Outbox, GeneratedReactions.Set, pipeline, clock).Drain(Ct);

        again.Single(a => a.Row.Reaction == "MarkPayoutDue").ShouldSatisfyAllConditions(
            a => a.Row.Attempts.ShouldBe(2), a => a.Verdict.ShouldBe(Verdict.Done), a => a.Kept.ShouldBeTrue());
        (await store.Outbox.Complete(slow, Ct)).ShouldBeFalse();
        store.Recorded.Count(e => e.Event.Event is Payout.Due).ShouldBe(1);
    }

    [Fact]
    public async Task A_row_of_a_reaction_that_no_longer_exists_is_retried_then_buried_with_the_reason()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        var (store, pipeline, alice) = Setup(clock, OutboxReactions.From(e => e is Auction.Opened ? Ghost : ReadOnlySpan<string>.Empty));
        (await store.Append(Auction.Create("alice", 100, clock.Now), 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        var worker = new ReactionWorker(store.Outbox, GeneratedReactions.Set, pipeline, clock, new ReactionOptions { Policy = new DeliveryPolicy { MaxAttempts = 2 } });

        (await worker.RunOnce(Ct)).ShouldHaveSingleItem().Verdict.ShouldBe(Verdict.Retry);
        clock.Now += TimeSpan.FromSeconds(5);
        var buried = (await worker.RunOnce(Ct)).ShouldHaveSingleItem();

        buried.Verdict.ShouldBe(Verdict.Bury);
        buried.Reason.GetValueOrThrow().ShouldStartWith("InvalidOperationException: No reaction named 'RenamedAway'");
        (await store.Outbox.DeadLetters(10, Ct)).ShouldHaveSingleItem().Reaction.ShouldBe("RenamedAway");
    }

    [Fact]
    public async Task The_loop_runs_rows_as_they_come_and_stops_when_cancelled()
    {
        var (store, pipeline, alice) = Setup(TimeProvider.System);
        var worker = new ReactionWorker(store.Outbox, GeneratedReactions.Set, pipeline, TimeProvider.System,
            new ReactionOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = worker.Run(stop.Token);

        _ = await Won(store, alice, TimeProvider.System);
        var clock = Stopwatch.StartNew();
        while (await store.Outbox.Pending(Ct) > 1 && clock.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, Ct);
        await stop.CancelAsync();
        await loop;

        store.Recorded.ShouldContain(e => e.Event.Event is Payout.Due);
        loop.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private sealed class FailingOnce(IOutbox inner) : IOutbox
    {
        public int Claims { get; private set; }

        public Task<IReadOnlyList<OutboxRow>> Claim(int max, TimeSpan lease, CancellationToken cancellationToken)
            => ++Claims == 1 ? throw new InvalidOperationException("database down") : inner.Claim(max, lease, cancellationToken);

        public Task<bool> Complete(OutboxRow row, CancellationToken cancellationToken) => inner.Complete(row, cancellationToken);
        public Task<bool> Retry(OutboxRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken) => inner.Retry(row, retryAt, reason, cancellationToken);
        public Task<bool> Bury(OutboxRow row, string reason, CancellationToken cancellationToken) => inner.Bury(row, reason, cancellationToken);
        public Task<IReadOnlyList<DeadLetter>> DeadLetters(int max, CancellationToken cancellationToken) => inner.DeadLetters(max, cancellationToken);
        public Task<bool> Redrive(long id, CancellationToken cancellationToken) => inner.Redrive(id, cancellationToken);
        public Task<long> Pending(CancellationToken cancellationToken) => inner.Pending(cancellationToken);
        public Task WaitForRows(TimeSpan timeout, CancellationToken cancellationToken) => inner.WaitForRows(timeout, cancellationToken);
    }

    [Fact]
    public async Task The_loop_survives_a_failing_store_and_claims_again()
    {
        var (store, pipeline, alice) = Setup(TimeProvider.System);
        _ = await Won(store, alice, TimeProvider.System);
        var outbox = new FailingOnce(store.Outbox);
        var worker = new ReactionWorker(outbox, GeneratedReactions.Set, pipeline, TimeProvider.System,
            new ReactionOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var loop = worker.Run(stop.Token);
        var clock = Stopwatch.StartNew();
        while (await store.Outbox.Pending(Ct) > 1 && clock.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, Ct);
        await stop.CancelAsync();
        await loop;

        outbox.Claims.ShouldBeGreaterThan(1);
        store.Recorded.ShouldContain(e => e.Event.Event is Payout.Due);
    }

    [Fact]
    public async Task AddTarolReactions_hosts_the_workers_and_purges_keys()
    {
        var (store, pipeline, alice) = Setup(TimeProvider.System);
        var purged = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<IOutbox>(store.Outbox);
        services.AddSingleton(pipeline);
        services.AddTarolReactions(GeneratedReactions.Set, o =>
        {
            o.Workers = 2;
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.PurgeKeys = (olderThan, batch, _) => { purged.TrySetResult(olderThan); return Task.FromResult(0); };
        });
        await using var provider = services.BuildServiceProvider();
        var host = provider.GetServices<IHostedService>().ShouldHaveSingleItem().ShouldBeOfType<ReactionHost>();

        await host.StartAsync(Ct);
        _ = await Won(store, alice, TimeProvider.System);
        var clock = Stopwatch.StartNew();
        while (await store.Outbox.Pending(Ct) > 1 && clock.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, Ct);
        await host.StopAsync(Ct);

        store.Recorded.ShouldContain(e => e.Event.Event is Payout.Due);
        (await purged.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeLessThan(TimeProvider.System.GetUtcNow().AddDays(-6));
        Should.Throw<InvalidOperationException>(() => services.AddTarolReactions(GeneratedReactions.Set));
    }
}
