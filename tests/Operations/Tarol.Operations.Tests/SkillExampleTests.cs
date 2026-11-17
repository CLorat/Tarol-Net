using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;

namespace Tarol.Operations.Tests;

/// <summary>The example of skills/tarol-operations/SKILL.md compiles and runs (the in-memory store has no dead letters).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = OperationsJson.Default };
        var tarol = new TarolBuilder(builder.Services, json, new EventCodec(json));                 // the skill's AddTarol(…)

        // --- skill example
        tarol
            .UseInMemoryStore()
            .AddOperations(o =>
            {
                o.PollEvery = TimeSpan.FromSeconds(15);
                o.Thresholds.ReactionsDegraded = TimeSpan.FromSeconds(30);
            });
        builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));
        var app = builder.Build();
        app.MapTarolHealth();
        app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");

        var admin = app.Services.GetRequiredService<IDeadLetterAdmin>();
        var page = await admin.List(DeadLetterKinds.Reactions, new DeadLetterQuery { Limit = 50 }, ct);
        foreach (var dead in page.Items)
            _ = await admin.Redrive(DeadLetterKinds.Reactions, dead.Id, "ops-alice", ct);
        // --- end

        page.Items.ShouldBeEmpty();
        admin.Kinds.ShouldBe([DeadLetterKinds.Jobs, DeadLetterKinds.Reactions]);
        await app.DisposeAsync();
    }
}
