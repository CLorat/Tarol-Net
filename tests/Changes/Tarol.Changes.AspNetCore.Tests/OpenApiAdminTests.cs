using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Operations;
using Tarol.Security;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>BC-3 (f): MapTarolAdmin is in the OpenAPI document with its parameters, answers, problems and codes.</summary>
public sealed class OpenApiAdminTests
{
    [Fact]
    public async Task The_admin_entry_is_in_the_document()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Memo>())
            .UseInMemoryStore()
            .AddSecurity(_ => { })
            .AddEndpoints()
            .AddOperations();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestApp.HeaderScheme>("test", null);
        builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("admin"));
        builder.Services.AddOpenApi(o => o.AddTarolEndpoints());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        app.MapTarolAdmin("/tarol/admin", "tarol-admin");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var paths = JsonElement.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).GetProperty("paths");

        var list = paths.GetProperty("/tarol/admin/dead/{kind}").GetProperty("get");
        list.GetProperty("operationId").GetString().ShouldBe("TarolAdminDeadLetters");
        list.GetProperty("x-tarol-kind").GetString().ShouldBe("admin");
        list.GetProperty("x-tarol-errors").EnumerateArray().Select(c => c.GetString()).ShouldBe([AdminProblems.Query, AdminProblems.Kind, "Auth.Forbidden"]);
        list.GetProperty("parameters").EnumerateArray().Select(p => (p.GetProperty("name").GetString(), p.GetProperty("in").GetString()))
            .ShouldBe([("kind", "path"), ("after", "query"), ("limit", "query"), ("tenant", "query"), ("name", "query")]);
        list.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref")
            .GetString().ShouldBe("#/components/schemas/DeadLetterPageView");
        list.GetProperty("responses").GetProperty("404").GetProperty("content").GetProperty("application/problem+json").GetProperty("schema")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/TarolProblem");
        var drop = paths.GetProperty("/tarol/admin/projections/{name}/{version}").GetProperty("delete");
        drop.GetProperty("x-tarol-errors").EnumerateArray().Select(c => c.GetString()).ShouldContain(AdminProblems.ProjectionDrop);
        drop.GetProperty("responses").EnumerateObject().Select(r => r.Name).ShouldBe(["204", "401", "403", "409"], ignoreOrder: true);
        paths.GetProperty("/tarol/admin/dead").GetProperty("get").GetProperty("operationId").GetString().ShouldBe("TarolAdminKinds");
    }
}
