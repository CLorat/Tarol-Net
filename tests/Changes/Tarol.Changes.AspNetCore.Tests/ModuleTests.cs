using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Changes.AspNetCore.Tests.Garden;
using Tarol.Changes.AspNetCore.Tests.Kitchen;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Security;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>
/// TN-8 (with TN-9 and TN-10): an application of two assemblies, each a generated module. One <c>AddTarol(modules)</c>, one
/// <c>MapModules()</c>, one <c>ConfigureOpenApi</c>; the document has every parameter once.
/// </summary>
public sealed class ModuleTests : IAsyncLifetime
{
    private static readonly Lazy<JsonSerializerOptions> CopyOfOptions = new(() => new JsonSerializerOptions(Kitchen.GeneratedModule.Module.Json is { } k
        ? ValueObjectJson.Options([Garden.GeneratedModule.Module.Json!, k]) : throw new InvalidOperationException()));

    private static JsonSerializerOptions Copy => CopyOfOptions.Value;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private TarolBuilder _tarol = null!;

    private static TarolModule GardenModule => Garden.GeneratedModule.Module
        .With(new SecurityModule(roles => roles.Authenticated().May<LayPlot>().May<RenamePlot>().May<Read<Plot>>()));

    private static TarolModule KitchenModule => Kitchen.GeneratedModule.Module
        .With(new SecurityModule(roles => roles.Role("cook").May<WriteRecipe>().May<ScaleRecipe>().May<Read<Recipe>>()));

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        _tarol = builder.Services.AddTarol(GardenModule, KitchenModule)
            .UseInMemoryStore()
            .UseInMemoryReadModels()
            .AddProjections(p => p.AddModules())
            .AddSecurity(_ => { })
            .AddEndpoints();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestApp.HeaderScheme>("test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddOpenApi(o => _tarol.Modules.ConfigureOpenApi(o));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapOpenApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body, string? Position)> Send(HttpMethod method, string path, string? body, string roles = "cook",
        string? readAfter = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-User", "kim");
        request.Headers.Add("X-Roles", roles);
        request.Headers.Add("X-Tenant", TestApp.TenantA.ToString());
        if (readAfter is not null)
            request.Headers.Add(Positions.ReadAfterHeader, readAfter);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            response.Headers.TryGetValues(Positions.PositionHeader, out var p) ? p.Single() : null);
    }

    [Fact]
    public void One_AddTarol_takes_the_aggregates_json_and_role_maps_of_both_modules()
    {
        _tarol.Modules.Select(m => m.Name).ShouldBe(["Garden", "Tarol.Changes.AspNetCore.Tests.Kitchen"]);
        _tarol.Codec.IsRegistered<Plot>().ShouldBeTrue();
        _tarol.Codec.IsRegistered<Recipe>().ShouldBeTrue();
        // TN-10: both contexts answer through the application's options and through copies of them
        _tarol.Json.GetTypeInfo<PlotRow>().ShouldNotBeNull();
        _tarol.Json.GetTypeInfo<RecipeRow>().ShouldNotBeNull();
        JsonSerializer.Serialize(new RecipeRow(Guid.Empty, "Soup", 2), Copy.GetTypeInfo<RecipeRow>()).ShouldContain("\"servings\":2");
        _app.Services.GetRequiredService<TarolModules>().All.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Both_modules_serve_their_changes_lists_and_roles()
    {
        var plot = await Send(HttpMethod.Post, "/plots", """{"name":"Beans"}""", roles: "");
        var recipe = await Send(HttpMethod.Post, "/recipes", """{"title":"Soup"}""");
        var refused = await Send(HttpMethod.Post, "/recipes", """{"title":"Stew"}""", roles: "");

        plot.Status.ShouldBe(HttpStatusCode.Created, plot.Body);
        recipe.Status.ShouldBe(HttpStatusCode.Created, recipe.Body);
        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.Body);
        var id = JsonElement.Parse(recipe.Body).GetProperty("id").GetString();
        var scaled = await Send(HttpMethod.Put, $"/recipes/{id}/servings", """{"servings":4}""");
        scaled.Status.ShouldBe(HttpStatusCode.NoContent, scaled.Body);
        var recipes = await Send(HttpMethod.Get, "/recipes?servings=4", null, readAfter: scaled.Position);
        recipes.Body.ShouldContain("\"title\":\"Soup\"");
        (await Send(HttpMethod.Get, "/plots", null, roles: "", readAfter: plot.Position)).Body.ShouldContain("\"name\":\"Beans\"");
    }

    [Fact]
    public async Task The_document_has_every_parameter_once_with_two_assemblies()
    {
        var paths = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).GetProperty("paths");

        var operations = paths.EnumerateObject().SelectMany(p => p.Value.EnumerateObject().Select(o => (Path: p.Name, Verb: o.Name, Operation: o.Value))).ToList();
        operations.Select(o => o.Path).Distinct().Order(StringComparer.Ordinal).ShouldBe(["/plots", "/plots/{plot}/name", "/recipes", "/recipes/{recipe}/servings"]);
        foreach (var (path, verb, operation) in operations)
        {
            var parameters = operation.TryGetProperty("parameters", out var p) ? p.EnumerateArray().Select(x => $"{x.GetProperty("in").GetString()}:{x.GetProperty("name").GetString()}").ToList() : [];
            parameters.Distinct().Count().ShouldBe(parameters.Count, $"{verb} {path}: {string.Join(", ", parameters)}");
            operation.GetProperty("x-tarol-kind").GetString().ShouldNotBeNull();
        }
        var rename = paths.GetProperty("/plots/{plot}/name").GetProperty("put").GetProperty("parameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        rename.Count(n => n == "Idempotency-Key").ShouldBe(1);
        var list = paths.GetProperty("/recipes").GetProperty("get").GetProperty("parameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        list.ShouldBe(["title", "servings", "after", "limit", "offset", "count", "Tarol-Read-After"]);
    }

    [Fact]
    public void Module_names_are_unique_and_a_module_needs_json()
    {
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddTarol(GardenModule, GardenModule)).Message.ShouldContain("Garden");
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddTarol(new TarolModule("empty"))).Message.ShouldContain("JSON");
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddTarol(Array.Empty<TarolModule>()));
    }

    [Fact]
    public void A_module_is_immutable_and_keeps_one_part_per_type()
    {
        var plain = new TarolModule("m");
        var one = plain.With(new ProjectionModule(_ => { }));
        var two = one.With(new ProjectionModule(_ => { }));

        plain.Part<ProjectionModule>().ShouldBeNull();
        one.Part<ProjectionModule>().ShouldNotBeSameAs(two.Part<ProjectionModule>());
        two.Parts.Count.ShouldBe(1);
        ValueObjectJson.Options([GardenJson.Default, KitchenJson.Default]).GetTypeInfo<PlotRow>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_modules_each_assemblys_transformer_completes_its_own_operations_only()
    {
        // the composition Tatalu wrote before TN-8: each assembly's AddTarolEndpoints and MapChanges by hand (TN-9: Idempotency-Key was there twice)
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddTarol([GardenJson.Default, KitchenJson.Default], c => c.Add<Plot>().Add<Recipe>()).UseInMemoryStore().AddSecurity(_ => { }).AddEndpoints();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestApp.HeaderScheme>("test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddOpenApi(o =>
        {
            Garden.GeneratedOpenApi.AddTarolEndpoints(o);
            Kitchen.GeneratedOpenApi.AddTarolEndpoints(o);
        });
        await using var app = builder.Build();
        app.MapOpenApi();
        Garden.GeneratedEndpoints.MapChanges(app, "Garden");
        Kitchen.GeneratedEndpoints.MapChanges(app, "Kitchen");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var paths = JsonElement.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).GetProperty("paths");

        foreach (var (path, verb) in new[] { ("/plots/{plot}/name", "put"), ("/recipes/{recipe}/servings", "put"), ("/recipes", "get") })
        {
            var names = paths.GetProperty(path).GetProperty(verb).GetProperty("parameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
            names.Distinct().Count().ShouldBe(names.Count, $"{verb} {path}: {string.Join(", ", names)}");
        }
    }
}
