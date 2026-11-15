using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Messaging.Contract;
using Tarol.Sagas;

namespace Tarol.Messaging.RabbitMq.IntegrationTests;

/// <summary>The example of skills/tarol-messaging-rabbitmq/SKILL.md, verbatim (the contract domain is in tests/Messaging/Shared).</summary>
[Trait("Category", "Integration")]
public sealed class SkillExampleTests(RabbitMq rabbit)
{
    [Fact]
    public async Task The_skill_example_registers_the_transport()
    {
        var uri = rabbit.Uri("billing", "billing");

        var services = new ServiceCollection();
        services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
            .UseInMemoryStore()
            .AddReactionWorker()
            .AddMessaging("billing", m => m
                .Consume(GeneratedConsumers.Set)
                .Publish(GeneratedPublications.Set)
                .Trust("artflow", t => t.AnyTenant())
                .UseRabbitMq(new RabbitMqTransportOptions { Uri = new Uri(uri), StampUserId = true })
                .UseInMemoryDeadLetters());

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMessageTransport>().ShouldBeOfType<RabbitMqTransport>().QueueOf("billing.IssueInvoice").ShouldBe("billing.IssueInvoice");
    }
}
