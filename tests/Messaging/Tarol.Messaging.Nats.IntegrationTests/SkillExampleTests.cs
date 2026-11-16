using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Messaging.Contract;
using Tarol.Sagas;

namespace Tarol.Messaging.Nats.IntegrationTests;

/// <summary>The example of skills/tarol-messaging-nats/SKILL.md, verbatim (the contract domain is in tests/Messaging/Shared).</summary>
[Trait("Category", "Integration")]
public sealed class SkillExampleTests(Nats nats)
{
    [Fact]
    public async Task The_skill_example_registers_the_transport()
    {
        var url = nats.SecuredUrl;
        var password = "billing";

        var services = new ServiceCollection();
        services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
            .UseInMemoryStore()
            .AddReactionWorker()
            .AddMessaging("billing", m => m
                .Consume(GeneratedConsumers.Set)
                .Publish(GeneratedPublications.Set)
                .Trust("artflow", t => t.AnyTenant())
                .UseNats(new NatsTransportOptions { Url = url, Username = "billing", Password = password })
                .UseInMemoryDeadLetters());

        await using var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<IMessageTransport>().ShouldBeOfType<NatsTransport>();
        await transport.Prepare([], provider.GetRequiredService<MessageConsumers>().Subscriptions, TestContext.Current.CancellationToken);   // billing may declare consumers
    }
}
