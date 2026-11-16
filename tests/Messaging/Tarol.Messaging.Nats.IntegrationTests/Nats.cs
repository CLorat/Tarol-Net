using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Tarol.Messaging.Contract;

[assembly: AssemblyFixture(typeof(Tarol.Messaging.Nats.IntegrationTests.Nats))]

namespace Tarol.Messaging.Nats.IntegrationTests;

/// <summary>
/// Two NATS 2.11 servers with JetStream per test run: an open one for the transport contract (a prefix per test), and one
/// with two users whose subject permissions bind each to its own contract prefix (docs/migration/probes/messaging/Brokers/nats.conf).
/// </summary>
public sealed class Nats : IAsyncLifetime
{
    private const string SecuredConfig = """
        jetstream: { store_dir: /data }
        accounts: {
          APP: {
            jetstream: enabled
            users: [
              { user: artflow, password: artflow, permissions: { publish: { allow: ["artflow.>", "$JS.API.>", "$JS.ACK.>", "_INBOX.>"] }, subscribe: { allow: [">"] } } }
              { user: billing, password: billing, permissions: { publish: { allow: ["billing.>", "$JS.API.>", "$JS.ACK.>", "_INBOX.>"] }, subscribe: { allow: [">"] } } }
            ]
          }
        }
        """;

    private readonly IContainer _open = new ContainerBuilder("nats:2.11")
        .WithCommand("-js")
        .WithPortBinding(4222, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
        .Build();

    private readonly IContainer _secured = new ContainerBuilder("nats:2.11")
        .WithResourceMapping(Encoding.UTF8.GetBytes(SecuredConfig), "/etc/nats/secured.conf")
        .WithCommand("-c", "/etc/nats/secured.conf")
        .WithPortBinding(4222, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
        .Build();

    public string Url => $"nats://{_open.Hostname}:{_open.GetMappedPublicPort(4222)}";

    public string SecuredUrl => $"nats://{_secured.Hostname}:{_secured.GetMappedPublicPort(4222)}";

    public async ValueTask InitializeAsync() => await Task.WhenAll(_open.StartAsync(), _secured.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _open.DisposeAsync();
        await _secured.DisposeAsync();
    }
}

/// <summary>The transport contract on NATS JetStream, a prefix (streams, subjects, consumers) per test.</summary>
[Trait("Category", "Integration")]
public sealed class NatsContractTests(Nats nats) : TransportContract
{
    protected override ConsumerSet Consumers => GeneratedConsumers.Set;

    protected override bool DropsDuplicates => true;

    protected override Task<IMessageTransport> CreateTransport(string isolation)
        => Task.FromResult<IMessageTransport>(new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = isolation }));
}
