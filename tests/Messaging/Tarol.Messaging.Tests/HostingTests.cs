using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

public sealed class HostingTests
{
    private static TarolBuilder Tarol(IServiceCollection services)
        => services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>()).UseInMemoryStore();

    [Fact]
    public async Task AddMessaging_hosts_the_consumers_on_the_transport()
    {
        var builder = Host.CreateApplicationBuilder();
        Tarol(builder.Services).AddMessaging("shop", m => m
            .Consume(GeneratedConsumers.Set)
            .Trust("artflow", t => t.AnyTenant())
            .UseInMemoryTransport()
            .UseInMemoryDeadLetters());
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var transport = host.Services.GetRequiredService<IMessageTransport>();
        var feed = (InMemoryAggregateStore)host.Services.GetRequiredService<IAggregateStore>();
        await Task.Delay(100, TestContext.Current.CancellationToken);                       // the host declares the subscriptions

        await transport.Publish(new Message
        {
            Id = Guid.CreateVersion7(),
            Source = "artflow",
            Type = "artflow.auction-settled",
            Version = 1,
            Time = TimeProvider.System.GetUtcNow(),
            Correlation = Id<ICorrelation>.New(),
            Data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AuctionSettledV1(Guid.NewGuid(), 80m), ShopJson.Default.AuctionSettledV1),
        }, TestContext.Current.CancellationToken);
        var deadline = Stopwatch.StartNew();
        while (feed.Recorded.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        feed.Recorded.Select(r => r.Event.Event.GetType().Name).Order().ShouldBe(["Issued", "SaleRecorded"]);
        host.Services.GetRequiredService<MessageConsumers>().Subscriptions.Select(s => s.Name).ShouldBe(["shop.IssueInvoice", "shop.RecordPayment", "shop.Sales"]);
    }

    private static MessageConsumers Consumers() => new(GeneratedConsumers.Set,
        new ChangePipeline(new InMemoryAggregateStore(TimeProvider.System), TimeProvider.System), new InMemoryMessageDeadLetters(TimeProvider.System),
        new MessagingOptions { Application = "shop" }, ShopJson.Default.Options);

    [Fact]
    public async Task When_one_subscription_fails_the_others_are_stopped_and_the_failure_is_thrown()
    {
        var transport = new OneFails("shop.RecordPayment");
        var consumers = Consumers();

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            consumers.Run(transport, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        failure.Message.ShouldBe("the broker closed shop.RecordPayment");
        transport.Stopped.Order().ShouldBe(["shop.IssueInvoice", "shop.Sales"]);           // the healthy ones were stopped, not left running
    }

    [Fact]
    public async Task The_host_consumes_every_subscription_again_after_one_failed()
    {
        var transport = new OneFails("shop.RecordPayment");
        using var host = new MessagingHost(transport, Consumers(), TimeProvider.System) { RestartDelay = TimeSpan.FromMilliseconds(10) };

        await host.StartAsync(TestContext.Current.CancellationToken);
        var deadline = Stopwatch.StartNew();
        while (transport.Prepared < 3 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        transport.Prepared.ShouldBeGreaterThanOrEqualTo(3);                               // declared and consumed again, not stuck
        transport.Consumed("shop.RecordPayment").ShouldBeGreaterThanOrEqualTo(3);
    }

    /// <summary>A transport whose <c>Consume</c> of one subscription fails at once; the others run until cancelled.</summary>
    private sealed class OneFails(string failing) : IMessageTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _consumed = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _stopped = new();
        private int _prepared;

        public int Prepared => Volatile.Read(ref _prepared);

        public IReadOnlyList<string> Stopped => [.. _stopped];

        public int Consumed(string subscription) => _consumed.GetValueOrDefault(subscription);

        public Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepared);
            return Task.CompletedTask;
        }

        public Task Publish(Message message, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
        {
            _consumed.AddOrUpdate(subscription.Name, 1, static (_, n) => n + 1);
            await Task.Yield();
            if (subscription.Name == failing)
                throw new InvalidOperationException("the broker closed " + failing);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                _stopped.Enqueue(subscription.Name);
            }
        }
    }

    [Fact]
    public void AddMessaging_needs_a_transport_and_a_dead_letter_store_and_runs_once()
    {
        Should.Throw<InvalidOperationException>(() => Tarol(new ServiceCollection()).AddMessaging("shop", m => m.Consume(GeneratedConsumers.Set).UseInMemoryDeadLetters()))
            .Message.ShouldContain("Choose a message transport");
        Should.Throw<InvalidOperationException>(() => Tarol(new ServiceCollection()).AddMessaging("shop", m => m.Consume(GeneratedConsumers.Set).UseInMemoryTransport()))
            .Message.ShouldContain("Choose where dead letters go");
        Should.Throw<InvalidOperationException>(() => Tarol(new ServiceCollection()).AddMessaging("", m => m.UseInMemoryTransport().UseInMemoryDeadLetters()))
            .Message.ShouldContain("Application is required");
        Should.Throw<InvalidOperationException>(() => Tarol(new ServiceCollection()).AddMessaging("shop", m => m.UseInMemoryTransport().UseInMemoryTransport()));

        var twice = Tarol(new ServiceCollection()).AddMessaging("shop", m => m.Consume(GeneratedConsumers.Set).UseInMemoryTransport().UseInMemoryDeadLetters());
        Should.Throw<InvalidOperationException>(() => twice.AddMessaging("shop", m => m.UseInMemoryTransport().UseInMemoryDeadLetters()))
            .Message.ShouldContain("called twice");
    }

    [Fact]
    public async Task A_consumed_message_runs_in_a_consumer_activity_linked_to_the_publisher()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == MessageConsumers.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                    spans.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var test = MessageTest.At(DateTimeOffset.UnixEpoch, GeneratedConsumers.Set, ShopJson.Default.Options, "shop").Trust("artflow", t => t.AnyTenant());
        var message = test.Message("artflow.auction-settled", 1, new AuctionSettledV1(Guid.NewGuid(), 80m), ShopJson.Default.AuctionSettledV1)
            with
        { Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

        await test.Send(message);
        await test.Drain();

        Activity[] seen;
        lock (spans)
            seen = [.. spans];                                                   // listeners are process-wide: pick this test's span
        var span = seen.First(s => s.GetTagItem("tarol.consumer") as string == "IssueInvoice" && s.GetTagItem("messaging.message.id") as string == message.Id.ToString());
        span.DisplayName.ShouldBe("consume artflow.auction-settled");
        span.Kind.ShouldBe(ActivityKind.Consumer);
        span.ParentId.ShouldBeNull();
        span.Links.ShouldHaveSingleItem().Context.TraceId.ToString().ShouldBe("0af7651916cd43dd8448eb211c80319c");
        span.GetTagItem("messaging.message.id").ShouldBe(message.Id.ToString());
        span.GetTagItem("messaging.consumer.group.name").ShouldBe("shop.IssueInvoice");
        span.GetTagItem("tarol.verdict").ShouldBe("Done");
    }
}
