using RabbitMQ.Client;
using Tarol.Kernel;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.RabbitMq.IntegrationTests;

/// <summary>RabbitMQ's own rows (docs/migration/messaging.md §3.2): exchange permissions, user_id, unroutable messages.</summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqTransportTests(RabbitMq rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Message Raw(string contract) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = Message.SystemOf(contract),
        Type = contract,
        Version = 1,
        Time = TimeProvider.System.GetUtcNow(),
        Correlation = Id<ICorrelation>.New(),
        Data = "{}"u8.ToArray(),
    };

    private RabbitMqTransport As(string user) => new(new RabbitMqTransportOptions { Uri = new Uri(rabbit.Uri(user, user)) });

    [Fact]
    public async Task A_user_writes_only_its_own_exchange_and_its_user_id_is_the_authenticated_publisher()
    {
        await using var artflow = As("artflow");
        await using var billing = As("billing");
        await artflow.Prepare(["artflow.auction-settled"], [], Ct);
        await billing.Prepare(["billing.invoice-paid"], [new Subscription("billing.IssueInvoice", ["artflow.auction-settled"])], Ct);   // passive on artflow's exchange

        var refused = await Should.ThrowAsync<Exception>(() => billing.Publish(Raw("artflow.auction-settled"), Ct));
        refused.ToString().ShouldContain("ACCESS_REFUSED");
        await billing.Publish(Raw("billing.invoice-paid"), Ct);                                           // the publish channel recovered

        var settled = Raw("artflow.auction-settled");
        await artflow.Publish(settled, Ct);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Incoming? seen = null;
        await billing.Consume(new Subscription("billing.IssueInvoice", ["artflow.auction-settled"]), async (incoming, _) =>
        {
            seen = incoming;
            await stop.CancelAsync();
            return new Handled(Ack.Ack);
        }, stop.Token);
        seen!.AuthenticatedAs.ShouldBe("artflow");
        seen.Message!.Id.ShouldBe(settled.Id);
    }

    [Fact]
    public async Task The_broker_refuses_a_forged_user_id()
    {
        var factory = new ConnectionFactory { Uri = new Uri(rabbit.Uri("billing", "billing")) };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using (var declare = As("billing"))
            await declare.Prepare(["billing.invoice-paid"], [], Ct);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), Ct);

        var refused = await Should.ThrowAsync<Exception>(async () =>
            await channel.BasicPublishAsync("billing", "billing.invoice-paid", mandatory: false, new BasicProperties { UserId = "artflow" }, "{}"u8.ToArray(), Ct));
        refused.ToString().ShouldContain("PRECONDITION_FAILED");
    }

    [Fact]
    public async Task A_retry_copy_the_broker_refuses_is_delivered_again_instead_of_stalling_the_queue()
    {
        var prefix = "r" + Guid.NewGuid().ToString("N")[..8];
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { Uri = new Uri(rabbit.Uri()), Prefix = prefix, StampUserId = false });
        var subscription = new Subscription("t.Stuck", ["artflow.stuck"]);                       // concurrency 1: one prefetch slot
        await transport.Prepare(["artflow.stuck"], [subscription], Ct);
        var factory = new ConnectionFactory { Uri = new Uri(rabbit.Uri()) };
        await using (var connection = await factory.CreateConnectionAsync(Ct))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
            _ = await channel.QueueDeleteAsync(transport.QueueOf(subscription.Name) + ".retry", cancellationToken: Ct);   // the copy is unroutable
        await transport.Publish(Raw("artflow.stuck"), Ct);

        var attempts = new List<int>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await transport.Consume(subscription, async (incoming, _) =>
        {
            attempts.Add(incoming.Attempt);
            if (attempts.Count == 1)
                return new Handled(Ack.Retry, TimeSpan.FromSeconds(1));                           // its copy is returned
            await stop.CancelAsync();
            return new Handled(Ack.Ack);
        }, stop.Token);

        attempts.ShouldBe([1, 2]);                                                              // nacked and redelivered, not stuck
    }

    [Fact]
    public async Task An_unroutable_message_is_logged_and_counts_as_delivered()
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { Uri = new Uri(rabbit.Uri()), Prefix = "r" + Guid.NewGuid().ToString("N")[..8], StampUserId = false });
        await transport.Prepare(["artflow.nobody-listens"], [], Ct);

        await transport.Publish(Raw("artflow.nobody-listens"), Ct);

        transport.Unroutable.ShouldBe(1);
        transport.ExchangeOf("artflow.x").ShouldEndWith(".artflow");
    }
}
