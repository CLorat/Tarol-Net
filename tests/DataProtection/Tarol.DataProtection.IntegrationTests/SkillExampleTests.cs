using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tarol.DataProtection.Tests;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;

namespace Tarol.DataProtection.IntegrationTests;

/// <summary>The skill's registration compiles and resolves (skills/tarol-data-protection/SKILL.md; AddTarol is TarolBuilder here).</summary>
[Trait("Category", "Integration")]
public sealed class SkillExampleTests(Postgres postgres)
{
    [Fact]
    public async Task The_Skills_Registration_Resolves_Data_Subjects()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        var json = new JsonSerializerOptions(TestJson.Default.Options);
        var cs = postgres.ConnectionString;
        _ = new TarolBuilder(services, json, new EventCodec(json).Add<Customer>())
            .UseNpgsqlStore(cs, new NpgsqlStoreOptions { Lookups = new StoreLookups().DefinePersonal<Customer>("email", c => c.Email?.ToLowerInvariant()) })
            .UseNpgsqlKeyStore()
            .AddDataProtection(o =>
            {
                o.Retention["order"] = 365;
                o.MasterKey = App.MasterKey;
            });
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDataSubjects>().ShouldBeOfType<DataSubjects>().Participants.Select(static p => p.Name).ShouldBe(["personal-lookups"]);
        provider.GetRequiredService<RetentionPolicy>().Days.ShouldContainKeyAndValue("customer", 30);
    }
}
