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
using Tarol.Results;
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
        .With(new SecurityModule(roles => roles.Role("cook").May<WriteRecipe>().May<ScaleRecipe>().May<PlanRecipe>().May<PriceRecipe>().May<FeastRecipe>().May<Read<Recipe>>()));

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
        string? readAfter = null, string? confirm = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (confirm is not null)
            request.Headers.Add("Tarol-Confirm", confirm);
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
        operations.Select(o => o.Path).Distinct().Order(StringComparer.Ordinal).ShouldBe(["/plots", "/plots/{plot}/label", "/plots/{plot}/name", "/recipes", "/recipes/{recipe}/feast", "/recipes/{recipe}/label", "/recipes/{recipe}/plan", "/recipes/{recipe}/price", "/recipes/{recipe}/servings"]);
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
    public async Task Same_named_contract_types_of_two_assemblies_keep_their_own_schemas()
    {
        // Tatalu FB-INT-1 / FB-INT2-1: Garden.Label (1..80) and Pantry.Label (1..120), Garden.Shade and Pantry.Shade were one
        // schema "Label" / "Shade", whichever came last; the client checked the other assembly's rules.
        var document = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var paths = document.GetProperty("paths");
        var schemas = document.GetProperty("components").GetProperty("schemas");

        JsonElement Resolve(JsonElement schema)
            => schema.TryGetProperty("$ref", out var r) ? schemas.GetProperty(r.GetString()!["#/components/schemas/".Length..]) : schema;
        JsonElement Body(string path) => Resolve(paths.GetProperty(path).GetProperty("put").GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema")).GetProperty("properties");

        var plot = Body("/plots/{plot}/label");
        var recipe = Body("/recipes/{recipe}/label");
        Resolve(plot.GetProperty("label")).GetProperty("maxLength").GetInt32().ShouldBe(80);
        Resolve(recipe.GetProperty("label")).GetProperty("maxLength").GetInt32().ShouldBe(120);
        Resolve(plot.GetProperty("shade")).GetProperty("enum").EnumerateArray().Select(e => e.ToString()).ShouldBe(["Sun", "HalfShade", "FullShade"]);
        Resolve(recipe.GetProperty("shade")).GetProperty("enum").EnumerateArray().Select(e => e.ToString()).ShouldBe(["Light", "Dark"]);
        recipe.GetProperty("label").GetProperty("$ref").GetString().ShouldBe("#/components/schemas/PantryLabel");
        schemas.TryGetProperty("OptionOfLabel", out _).ShouldBeFalse();
        // TN-33 (Tatalu FB-4, FB-F02.3-2): a BCL enum is written by name with UseStringEnumConverter, so its schema has the names
        var day = Resolve(recipe.GetProperty("day"));
        day.GetProperty("type").GetString().ShouldBe("string");
        day.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(Enum.GetNames<DayOfWeek>());
    }

    [Fact]
    public async Task A_value_object_of_an_assembly_without_endpoints_has_its_type_and_rules()
    {
        // Tatalu FB-F01.1-3 / FB-F02.1-4 / FB-INT-3: Pantry.PostalCode was {} and Option<Pantry.Label> "object"
        var document = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        JsonElement Resolve(JsonElement schema)
            => schema.TryGetProperty("$ref", out var r) ? schemas.GetProperty(r.GetString()!["#/components/schemas/".Length..]) : schema;
        var body = Resolve(document.GetProperty("paths").GetProperty("/recipes/{recipe}/label").GetProperty("put").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")).GetProperty("properties");

        var from = Resolve(body.GetProperty("from"));
        from.GetProperty("type").GetString().ShouldBe("string");
        from.GetProperty("pattern").GetString().ShouldBe(@"^\d{5}$");
        // TN-31 (Tatalu FB-3, FB-F17.3-3): Option<T> of a component is T or null in place
        var note = body.GetProperty("note");
        note.GetProperty("oneOf")[0].GetProperty("type").GetString().ShouldBe("null");
        note.GetProperty("oneOf")[1].GetProperty("$ref").GetString().ShouldBe("#/components/schemas/PantryLabel");
    }

    [Fact]
    public async Task Scale_is_multipleOf_in_the_schema_and_a_400_with_code_Scale()
    {
        // TN-53 (Tatalu E08–E10): decimal stays a JSON number; [Scale(2)] gives multipleOf 0.01 and the rule with its step
        var document = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        JsonElement Resolve(JsonElement schema)
            => schema.TryGetProperty("$ref", out var r) ? schemas.GetProperty(r.GetString()!["#/components/schemas/".Length..]) : schema;
        var body = Resolve(document.GetProperty("paths").GetProperty("/recipes/{recipe}/price").GetProperty("put").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")).GetProperty("properties");

        var price = Resolve(body.GetProperty("price"));
        price.GetProperty("multipleOf").GetDecimal().ShouldBe(0.01m);
        price.GetProperty("minimum").GetDecimal().ShouldBe(0m);
        price.GetProperty("x-tarol-rules").EnumerateArray().Single(r => r.GetProperty("rule").GetString() == "Scale").GetProperty("step").GetDecimal().ShouldBe(0.01m);
        body.GetProperty("weight").GetProperty("multipleOf").GetDecimal().ShouldBe(0.1m);

        var recipe = await Send(HttpMethod.Post, "/recipes", """{"title":"Soup"}""");
        var id = JsonElement.Parse(recipe.Body).GetProperty("id").GetString();
        var refused = await Send(HttpMethod.Put, $"/recipes/{id}/price", """{"price":12.505,"weight":1.25}""");
        var priced = await Send(HttpMethod.Put, $"/recipes/{id}/price", """{"price":12.50,"weight":1.2}""");

        refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.Body);
        var errors = JsonElement.Parse(refused.Body).GetProperty("errors");
        errors.GetProperty("price").EnumerateArray().Single().GetString().ShouldStartWith("Scale");
        errors.GetProperty("weight").EnumerateArray().Single().GetString().ShouldStartWith("Scale");
        priced.Status.ShouldBe(HttpStatusCode.NoContent, priced.Body);
    }

    [Fact]
    public async Task A_warning_is_409_with_confirm_and_passes_when_the_request_confirms_its_code()
    {
        var recipe = await Send(HttpMethod.Post, "/recipes", """{"title":"Soup"}""");
        var id = JsonElement.Parse(recipe.Body).GetProperty("id").GetString();

        var warned = await Send(HttpMethod.Put, $"/recipes/{id}/feast", """{"servings":20}""");
        var other = await Send(HttpMethod.Put, $"/recipes/{id}/feast", """{"servings":20}""", confirm: "Recipe.Other");
        var confirmed = await Send(HttpMethod.Put, $"/recipes/{id}/feast", """{"servings":20}""", confirm: "Recipe.Other, Recipe.LargeFeast");

        warned.Status.ShouldBe(HttpStatusCode.Conflict, warned.Body);
        var problem = JsonElement.Parse(warned.Body);
        problem.GetProperty("code").GetString().ShouldBe("Recipe.LargeFeast");
        var confirm = problem.GetProperty("confirm");
        confirm.GetProperty("code").GetString().ShouldBe("Recipe.LargeFeast");
        confirm.GetProperty("args").GetProperty("servings").GetString().ShouldBe("20");
        confirm.GetProperty("args").GetProperty("limit").GetString().ShouldBe("12");
        other.Status.ShouldBe(HttpStatusCode.Conflict);
        confirmed.Status.ShouldBe(HttpStatusCode.NoContent, confirmed.Body);

        var document = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        document.GetProperty("paths").GetProperty("/recipes/{recipe}/feast").GetProperty("put").GetProperty("x-tarol-errors").EnumerateArray()
            .Select(e => e.GetString()).ShouldContain("Recipe.LargeFeast");
        TarolHeaders.Allowed.ShouldContain("Tarol-Confirm");                             // CORS
    }

    [Fact]
    public async Task A_computed_member_that_is_not_nullable_is_required()
    {
        // TN-27 (Tatalu FB-F04.6-2): RecipeRow.Scaled => … was optional (scaled?: boolean) although it is always written
        var schemas = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))
            .GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("RecipeRow").GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("scaled");
    }

    [Fact]
    public async Task A_member_missing_in_a_nested_record_is_required()
    {
        // TN-23 (Tatalu FB-F02.1-3, FB-F03.9-1): only top-level members were Required; a nested DateOnly arrived as 0001-01-01,
        // a nested enum as its first value
        var recipe = await Send(HttpMethod.Post, "/recipes", """{"title":"Soup"}""");
        var id = JsonElement.Parse(recipe.Body).GetProperty("id").GetString();

        var missing = await Send(HttpMethod.Put, $"/recipes/{id}/plan", """{"first":{"note":null},"steps":[{"on":"2026-11-25","shade":"Dark"},{"shade":"Light"}]}""");
        var complete = await Send(HttpMethod.Put, $"/recipes/{id}/plan", """{"first":{"on":"2026-11-25","shade":"Dark"},"steps":[]}""");

        missing.Status.ShouldBe(HttpStatusCode.BadRequest, missing.Body);
        var errors = JsonElement.Parse(missing.Body).GetProperty("errors");
        errors.EnumerateObject().Select(e => $"{e.Name}:{e.Value[0].GetString()}").Order(StringComparer.Ordinal)
            .ShouldBe(["first.on:Required", "first.shade:Required", "steps[1].on:Required"]);
        complete.Status.ShouldBe(HttpStatusCode.NoContent, complete.Body);           // a missing nullable member is fine
    }

    [Fact]
    public async Task Contract_types_and_constants_of_both_assemblies_are_in_the_document()
    {
        // TN-35 (Tatalu FB-F02.6-2, FB-F13.5-1): an enum no operation uses and a backend constant reached the client only as a copy
        var document = JsonElement.Parse(await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        document.GetProperty("x-tarol-types").EnumerateArray().Select(t => t.GetString()).ShouldBe(["Doneness"]);
        document.GetProperty("components").GetProperty("schemas").GetProperty("Doneness").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(["Rare", "WellDone"]);
        var constants = document.GetProperty("x-tarol-constants");
        constants.GetProperty("KitchenRulesMaxServings").GetInt32().ShouldBe(12);
        constants.GetProperty("RecipeFormat").GetString().ShouldBe("v2");
        constants.GetProperty("GardenRulesOrganic").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_refusal_with_several_reasons_lists_them_in_the_problem()
    {
        // TN-36 (Tatalu FB-F03.4-5): every missing prerequisite at once; Tatalu put "License,Measles" into detail and split it
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = _app.Services };
        using var body = new MemoryStream();
        http.Response.Body = body;

        await _app.Services.GetRequiredService<ChangeEndpoints>().Problem(http, Error.Conflict("Contract.Prerequisites", ["Contract.NoLicense", "Contract.NoMeasles"]));

        var problem = JsonElement.Parse(body.ToArray());
        http.Response.StatusCode.ShouldBe(409);
        problem.GetProperty("code").GetString().ShouldBe("Contract.Prerequisites");
        problem.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldBe(["Contract.NoLicense", "Contract.NoMeasles"]);
        Error.Conflict("Contract.Prerequisites", ["Contract.NoLicense"]).ShouldBe(Error.Conflict("Contract.Prerequisites", ["Contract.NoLicense"]));
        Error.Conflict("Contract.Prerequisites", ["Contract.NoLicense"]).ShouldNotBe(Error.Conflict("Contract.Prerequisites"));
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

    [Fact]
    public async Task With_three_assemblies_a_list_answer_is_one_page_not_a_page_of_pages()
    {
        // Tatalu FB-F06.3-1 (preview.5): each assembly's transformer wrapped every list answer again; nine contexts, nine levels
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
            o.AddTarolEndpoints();                                                       // a third assembly (this one's)
        });
        await using var app = builder.Build();
        app.MapOpenApi();
        Garden.GeneratedEndpoints.MapChanges(app, "Garden");
        Kitchen.GeneratedEndpoints.MapChanges(app, "Kitchen");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var paths = JsonElement.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).GetProperty("paths");

        foreach (var path in new[] { "/plots", "/recipes" })
        {
            var page = paths.GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema");
            page.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldBe(["items", "next", "total"], ignoreOrder: true);
            var row = page.GetProperty("properties").GetProperty("items").GetProperty("items");
            row.TryGetProperty("$ref", out _).ShouldBeTrue($"{path}: the items are rows, not pages: {row}");
        }
    }
}

/// <summary>TN-22 (Tatalu FB-INT-1): two types with one schema name fail the document; a pinned name and Option&lt;T&gt; are handled.</summary>
public sealed class OpenApiSchemaNameTests
{
    private static class First
    {
        public sealed record Card(string Text);
    }

    private static class Second
    {
        public sealed record Card(int Number);
    }

    [Fact]
    public void Two_types_with_one_name_fail_and_name_both()
    {
        var options = new object();
        OpenApiSchemaNames.Name(options, typeof(First.Card), "Card").ShouldBe("Card");
        OpenApiSchemaNames.Name(options, typeof(First.Card), "Card").ShouldBe("Card");
        var error = Should.Throw<InvalidOperationException>(() => OpenApiSchemaNames.Name(options, typeof(Second.Card), "Card"));
        error.Message.ShouldContain("First+Card");
        error.Message.ShouldContain("Second+Card");
        error.Message.ShouldContain("OpenApiSchemaName");
        OpenApiSchemaNames.Name(new object(), typeof(Second.Card), "Card").ShouldBe("Card");   // another document
    }

    [Fact]
    public void A_pinned_name_an_option_and_a_generic_of_a_pinned_type()
    {
        var options = new object();
        OpenApiSchemaNames.Claim(options).ShouldBeTrue();
        OpenApiSchemaNames.Claim(options).ShouldBeFalse();
        OpenApiSchemaNames.Name(options, typeof(Pantry.Label), "Label").ShouldBe("PantryLabel");
        OpenApiSchemaNames.Name(options, typeof(Garden.Label), "Label").ShouldBe("Label");
        OpenApiSchemaNames.Name(options, typeof(Tarol.Options.Option<Pantry.Label>), "OptionOfLabel").ShouldBeNull();
        OpenApiSchemaNames.Name(options, typeof(List<Pantry.Label>), "ListOfLabel").ShouldBe("ListOfPantryLabel");
        OpenApiSchemaNames.Name(options, typeof(string), null).ShouldBeNull();
    }
}

