using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Messaging.Contract;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;

namespace Tarol.Messaging.Npgsql.IntegrationTests;

/// <summary>The example of skills/tarol-messaging-npgsql/SKILL.md, verbatim (the contract domain is in tests/Messaging/Shared).</summary>
[Trait("Category", "Integration")]
public sealed class SkillExampleTests(Postgres postgres)
{
    [Fact]
    public async Task The_skill_example_registers_the_bus_and_the_dead_letters()
    {
        var connectionString = postgres.ConnectionString;

        var services = new ServiceCollection();
        services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
            .UseNpgsqlStore(connectionString)
            .AddReactionWorker()
            .AddMessaging("billing", m => m
                .Consume(GeneratedConsumers.Set)
                .Publish(GeneratedPublications.Set)
                .Trust("artflow", t => t.AnyTenant())
                .UseNpgsqlBus(connectionString, new NpgsqlBusOptions
                {
                    Notify = true,
                    Roles = new Dictionary<string, string> { ["artflow"] = "artflow", ["billing"] = "billing" },
                })
                .UseNpgsqlDeadLetters());

        // once, as the schema's owner, for row-level security
        var sql = NpgsqlBusSchema.Script() + NpgsqlBusSchema.RowLevelSecurityScript() + NpgsqlBusSchema.GrantScript("billing");

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMessageTransport>().ShouldBeOfType<NpgsqlBus>().Schema.ShouldBe("bus");
        provider.GetRequiredService<IMessageDeadLetters>().ShouldBeOfType<NpgsqlMessageDeadLetters>().Schema.ShouldBe("tarol");
        sql.ShouldContain("CREATE POLICY publish_own_prefix ON bus.messages");
        sql.ShouldContain("GRANT USAGE ON SCHEMA bus TO billing");
    }
}
