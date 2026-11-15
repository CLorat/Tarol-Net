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
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var test = MessageTest.At(DateTimeOffset.UnixEpoch, GeneratedConsumers.Set, ShopJson.Default.Options, "shop").Trust("artflow", t => t.AnyTenant());
        var message = test.Message("artflow.auction-settled", 1, new AuctionSettledV1(Guid.NewGuid(), 80m), ShopJson.Default.AuctionSettledV1)
            with
        { Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

        await test.Send(message);
        await test.Drain();

        var span = spans.First(s => s.GetTagItem("tarol.consumer") as string == "IssueInvoice");
        span.DisplayName.ShouldBe("consume artflow.auction-settled");
        span.Kind.ShouldBe(ActivityKind.Consumer);
        span.ParentId.ShouldBeNull();
        span.Links.ShouldHaveSingleItem().Context.TraceId.ToString().ShouldBe("0af7651916cd43dd8448eb211c80319c");
        span.GetTagItem("messaging.message.id").ShouldBe(message.Id.ToString());
        span.GetTagItem("messaging.consumer.group.name").ShouldBe("shop.IssueInvoice");
        span.GetTagItem("tarol.verdict").ShouldBe("Done");
    }
}
