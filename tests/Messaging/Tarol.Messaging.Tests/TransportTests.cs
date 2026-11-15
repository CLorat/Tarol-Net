using Tarol.Kernel;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

public sealed class TransportTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Message M(string type, int n) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = Message.SystemOf(type),
        Type = type,
        Version = 1,
        Time = DateTimeOffset.UnixEpoch,
        Correlation = Id<ICorrelation>.New(),
        Data = new[] { (byte)n },
    };

    [Fact]
    public async Task Each_subscription_of_a_topic_gets_its_own_copy_in_publish_order()
    {
        var transport = new InMemoryTransport(TimeProvider.System);
        await transport.Prepare([], [new Subscription("a.One", ["x.t"]), new Subscription("a.Two", ["x.t", "x.u"])], TestContext.Current.CancellationToken);
        for (var i = 1; i <= 3; i++)
            await transport.Publish(M("x.t", i), TestContext.Current.CancellationToken);
        await transport.Publish(M("x.u", 9), TestContext.Current.CancellationToken);
        await transport.Publish(M("x.none", 0), TestContext.Current.CancellationToken);     // nobody subscribed: dropped, still listed

        var one = new List<int>();
        var two = new List<int>();
        await transport.RunDue("a.One", Record(one), TestContext.Current.CancellationToken);
        await transport.RunDue("a.Two", Record(two), TestContext.Current.CancellationToken);

        one.ShouldBe([1, 2, 3]);
        two.ShouldBe([1, 2, 3, 9]);
        transport.Published.Count.ShouldBe(5);
        transport.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task A_retry_comes_back_at_its_due_time_with_the_next_attempt()
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch);
        var transport = new InMemoryTransport(clock);
        await transport.Prepare([], [new Subscription("a.One", ["x.t"])], TestContext.Current.CancellationToken);
        await transport.Publish(M("x.t", 1), TestContext.Current.CancellationToken);
        var attempts = new List<int>();
        MessageHandler retryOnce = (incoming, _) =>
        {
            attempts.Add(incoming.Attempt);
            return ValueTask.FromResult(incoming.Attempt == 1 ? new Handled(Ack.Retry, TimeSpan.FromSeconds(10)) : new Handled(Ack.Ack));
        };

        (await transport.RunDue("a.One", retryOnce, TestContext.Current.CancellationToken)).ShouldBe(1);
        clock.Now += TimeSpan.FromSeconds(9);
        (await transport.RunDue("a.One", retryOnce, TestContext.Current.CancellationToken)).ShouldBe(0);
        clock.Now += TimeSpan.FromSeconds(1);
        (await transport.RunDue("a.One", retryOnce, TestContext.Current.CancellationToken)).ShouldBe(1);

        attempts.ShouldBe([1, 2]);
        transport.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task A_handler_that_throws_loses_nothing()
    {
        var transport = new InMemoryTransport(TimeProvider.System);
        await transport.Prepare([], [new Subscription("a.One", ["x.t"])], TestContext.Current.CancellationToken);
        await transport.Publish(M("x.t", 1), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => transport.RunDue("a.One", (_, _) => throw new InvalidOperationException("broken"), TestContext.Current.CancellationToken));

        transport.Pending.ShouldBe(1);
    }

    [Fact]
    public async Task Consume_delivers_what_is_published_while_it_runs_and_stops_on_cancellation()
    {
        var transport = new InMemoryTransport(TimeProvider.System);
        await transport.Prepare([], [new Subscription("a.One", ["x.t"])], TestContext.Current.CancellationToken);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var consuming = transport.Consume(new Subscription("a.One", ["x.t"]), (incoming, _) =>
        {
            got.TrySetResult(incoming.Message!.Data.Span[0]);
            return ValueTask.FromResult(new Handled(Ack.Ack));
        }, stop.Token);

        await transport.Publish(M("x.t", 7), TestContext.Current.CancellationToken);
        (await got.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(7);
        await stop.CancelAsync();
        await consuming.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static MessageHandler Record(List<int> into) => (incoming, _) =>
    {
        into.Add(incoming.Message!.Data.Span[0]);
        return ValueTask.FromResult(new Handled(Ack.Ack));
    };
}
