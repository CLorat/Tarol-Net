using Tarol.Messaging.Contract;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Tarol.Messaging.RabbitMq.IntegrationTests.RabbitMq))]

namespace Tarol.Messaging.RabbitMq.IntegrationTests;

/// <summary>
/// One RabbitMQ 4 per test run, with two more users whose permissions bind each to its own exchange and queues
/// (docs/migration/probes/messaging/Brokers/rabbit-users.sh). RabbitMQ sometimes fails its first boot in a fresh container
/// (Erlang prelaunch, §5 item 13): a failed start is retried with a new container.
/// </summary>
public sealed class RabbitMq : IAsyncLifetime
{
    private RabbitMqContainer _container = null!;

    public string Uri(string user = "rabbitmq", string password = "rabbitmq")
        => new UriBuilder(_container.GetConnectionString()) { UserName = user, Password = password }.Uri.ToString();

    public async ValueTask InitializeAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
            try
            {
                await _container.StartAsync();
                break;
            }
            catch (Exception) when (attempt < 3)
            {
                await _container.DisposeAsync();                                   // a failed boot: try a fresh container
            }
        }
        await Exec("add_user", "artflow", "artflow");
        await Exec("add_user", "billing", "billing");
        await Exec("set_permissions", "-p", "/", "artflow", @"^(artflow|artflow\..*)$", @"^(artflow|artflow\..*|amq\.default)$", @"^(artflow\..*|billing)$");
        await Exec("set_permissions", "-p", "/", "billing", @"^(billing|billing\..*)$", @"^(billing|billing\..*|amq\.default)$", @"^(billing\..*|artflow)$");
    }

    private async Task Exec(params string[] arguments)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", .. arguments]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)}: {result.Stderr}");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>The transport contract on RabbitMQ, a prefix (exchanges, queues) per test; no user_id (one user for every system).</summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqContractTests(RabbitMq rabbit) : TransportContract
{
    protected override ConsumerSet Consumers => GeneratedConsumers.Set;

    protected override bool DropsDuplicates => false;                                          // the consumer's key replays it

    protected override Task<IMessageTransport> CreateTransport(string isolation)
        => Task.FromResult<IMessageTransport>(new RabbitMqTransport(new RabbitMqTransportOptions { Uri = new Uri(rabbit.Uri()), Prefix = isolation, StampUserId = false }));
}
