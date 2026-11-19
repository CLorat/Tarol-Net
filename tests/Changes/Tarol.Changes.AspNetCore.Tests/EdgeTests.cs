using System.Net;
using System.Text.Json;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>The edge's options: hand-written problem details, the actor and tenant hooks (R-16), OpenAPI (R-12).</summary>
public sealed class EdgeTests
{
    [Fact]
    public async Task Without_IProblemDetailsService_the_problem_details_are_written_directly()
    {
        await using var app = await Start(problemDetailsService: false);
        var id = await app.Created(Alice);

        var invalid = await app.Post("/auctions", """{"title":"x","reserve":0,"endsAt":"2030-01-01T00:00:00Z"}""", Alice);
        var refused = await app.Post($"/auctions/{id}/close", null, Bob);

        invalid.Response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var trace = invalid.Response.Headers.GetValues(TarolHeaders.RequestId).Single();                    // BC-11
        invalid.Body.ShouldBe($$"""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","status":400,"title":"One or more fields are invalid.","errors":{"title":["Length"],"reserve":["Range"]},"errorDetails":{"title":[{"code":"Length","detail":"3..60"}],"reserve":[{"code":"Range","detail":"1..1000000"}]},"traceId":"{{trace}}"}""");
        refused.Body.ShouldBe($$"""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","status":403,"title":"Auction.NotSeller","code":"Auction.NotSeller","traceId":"{{refused.Response.Headers.GetValues(TarolHeaders.RequestId).Single()}}"}""");
    }

    [Fact]
    public async Task The_apps_actor_rule_and_tenant_mapping_replace_the_defaults()
    {
        var mapped = Id<ITenant>.New();
        await using var app = await Start(endpoints: o =>
        {
            o.Actor = user => user.IsInRole("service") ? Actor.Service(user.FindFirst("sub")!.Value) : Actor.User(user.FindFirst("sub")!.Value);
            o.Tenant = value => value == "org-42" ? mapped : Option.None;
        });

        var created = await app.Post("/auctions", Auction(), new Caller("machine-7", null, "seller service") with { });
        var orgCreated = await app.Send(HttpMethod.Post, "/auctions", Auction(), new Caller("alice", null, "seller"), h => h.Add("X-Tenant", "org-42"));
        var unknownOrg = await app.Send(HttpMethod.Post, "/auctions", Auction(), new Caller("alice", null, "seller"), h => h.Add("X-Tenant", "org-1"));

        created.Status.ShouldBe(HttpStatusCode.Created);
        app.Store.Recorded[0].Context.Actor.ShouldBe(Actor.Service("machine-7"));
        orgCreated.Status.ShouldBe(HttpStatusCode.Created);
        app.Store.Recorded[^1].Context.Tenant.ShouldBe(mapped);
        unknownOrg.Status.ShouldBe(HttpStatusCode.Unauthorized);                          // Auth.Tenant: not a tenant of this app
    }

    [Fact]
    public async Task A_tenant_claim_that_is_not_a_tenant_id_is_401()
    {
        await using var app = await Start();
        (await app.Send(HttpMethod.Post, "/auctions", Auction(), new Caller("alice", null, "seller"), h => h.Add("X-Tenant", "not-a-guid")))
            .Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_openapi_document_describes_every_entry_completely()
    {
        await using var app = await Start();

        var document = await app.Get("/openapi/v1.json", Nobody);
        var root = JsonElement.Parse(document.Body);
        var paths = root.GetProperty("paths");
        var bids = paths.GetProperty("/auctions/{auction}/bids").GetProperty("post");
        var view = paths.GetProperty("/auctions/{auction}").GetProperty("get");
        var create = paths.GetProperty("/auctions").GetProperty("post");
        var schemas = root.GetProperty("components").GetProperty("schemas");

        document.Status.ShouldBe(HttpStatusCode.OK);
        bids.GetProperty("operationId").GetString().ShouldBe("PlaceBid");
        bids.GetProperty("parameters").EnumerateArray().Select(p => (p.GetProperty("name").GetString(), p.GetProperty("in").GetString()))
            .ShouldBe([("auction", "path"), ("If-Match", "header"), ("Idempotency-Key", "header")]);
        bids.GetProperty("parameters")[0].GetProperty("schema").GetProperty("format").GetString().ShouldBe("uuid");
        Statuses(bids).ShouldBe(["204", "400", "401", "403", "404", "409", "415", "422"]);
        bids.GetProperty("description").GetString()!.ShouldContain("Policy: auctions:bid");
        Statuses(create).ShouldContain("201");
        create.GetProperty("responses").GetProperty("201").GetProperty("headers").TryGetProperty("ETag", out _).ShouldBeTrue();
        Statuses(view).ShouldBe(["200", "304", "400", "401", "403", "404"]);
        schemas.GetProperty("PlaceBid").GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldBe(["amount"]);   // no route member
        schemas.GetProperty("Amount").GetProperty("type").GetString().ShouldBe("number");
        schemas.GetProperty("Amount").GetProperty("minimum").ToString().ShouldBe("1");
        schemas.GetProperty("Title").GetProperty("minLength").GetInt32().ShouldBe(3);
        schemas.GetProperty("Title").GetProperty("maxLength").GetInt32().ShouldBe(60);
        paths.EnumerateObject().Select(p => p.Name).ShouldNotContain("/webhooks/payments");     // webhooks are not the API
    }

    [Fact]
    public async Task The_openapi_document_is_complete_enough_for_a_typed_client()      // BC-3
    {
        await using var app = await Start(search: true);

        var root = JsonElement.Parse((await app.Get("/openapi/v1.json", Nobody)).Body);
        var paths = root.GetProperty("paths");
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var bids = paths.GetProperty("/auctions/{auction}/bids").GetProperty("post");
        var create = paths.GetProperty("/auctions").GetProperty("post");
        var view = paths.GetProperty("/auctions/{auction}").GetProperty("get");
        var list = paths.GetProperty("/auctions").GetProperty("get");
        var search = paths.GetProperty("/search").GetProperty("get");

        // (a) every problem response names the TarolProblem schema
        var problem = schemas.GetProperty("TarolProblem");
        problem.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["type", "status", "title"], ignoreOrder: true);
        problem.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldBe(["type", "status", "title", "code", "detail", "traceId", "errors", "errorDetails"], ignoreOrder: true);
        foreach (var operation in new[] { bids, create, view, list, search })
            foreach (var response in operation.GetProperty("responses").EnumerateObject().Where(r => r.Name[0] is '4' or '5' && r.Name != "401"))
                response.Value.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString()
                    .ShouldBe("#/components/schemas/TarolProblem", $"{operation.GetProperty("operationId")} {response.Name}");
        // (b) the kind of every operation
        new[] { bids, create, view, list, search }.Select(o => o.GetProperty("x-tarol-kind").GetString()).ShouldBe(["change", "creation", "view", "list", "search"]);
        // (c) the static codes: Tarol's own and the domain's literal Error codes
        Codes(bids).ShouldBe(["Auction.NotFound", "Auction.NotOpen", "Auth.Forbidden", "Bid.OwnAuction", "Bid.TooLow", "Idempotency.DuplicateKey",
            "Idempotency.KeyReused", "Stream.VersionConflict"]);
        Codes(view).ShouldBe(["Auction.NotFound", "Auth.Forbidden"]);
        Codes(search).ShouldContain(SearchEndpoints.BuildingCode);
        // (d) the aggregate of an id path parameter and of an Id<T> schema
        bids.GetProperty("parameters")[0].GetProperty("x-tarol-id").GetString().ShouldBe("Auction");
        // (e) the headers a client reads
        Headers(bids, "204").ShouldBe(["ETag", "Idempotent-Replayed", "Tarol-Position", "Tarol-Request-Id"], ignoreOrder: true);
        Headers(create, "201").ShouldBe(["ETag", "Idempotent-Replayed", "Location", "Tarol-Position", "Tarol-Request-Id"], ignoreOrder: true);
        Headers(list, "200").ShouldBe(["ETag", "Tarol-Position", "Tarol-Stale", "Tarol-Request-Id"], ignoreOrder: true);
        Headers(search, "200").ShouldBe(["ETag", "Tarol-Position", "Tarol-Stale", "Tarol-Request-Id"], ignoreOrder: true);
        Headers(bids, "409").ShouldContain("Tarol-Request-Id");
        // (f) search with its parameters and its page; (g) numbers are numbers; (h) Option<T> is T or null
        search.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString())
            .ShouldBe(["q", "types", "prefix", "counts", "snippets", "limit", "after", "lang", "Tarol-Read-After"]);
        var hit = search.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
        hit.GetProperty("highlights").GetProperty("items").GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["start", "length"], ignoreOrder: true);
        var summary = schemas.GetProperty("AuctionSummary").GetProperty("properties");
        summary.GetProperty("highestBid").GetProperty("type").GetString().ShouldBe("number");
        summary.GetProperty("highestBid").TryGetProperty("pattern", out _).ShouldBeFalse();
        summary.GetProperty("bids").GetProperty("type").GetString().ShouldBe("integer");
        var payment = summary.GetProperty("payment");
        var option = payment.TryGetProperty("$ref", out var reference) ? schemas.GetProperty(reference.GetString()!["#/components/schemas/".Length..]) : payment;
        option.GetProperty("type").EnumerateArray().Select(t => t.GetString()).ShouldBe(["null", "string"], ignoreOrder: true);
    }

    private static string[] Codes(JsonElement operation) => [.. operation.GetProperty("x-tarol-errors").EnumerateArray().Select(c => c.GetString()!)];

    private static string[] Headers(JsonElement operation, string status)
        => [.. operation.GetProperty("responses").GetProperty(status).GetProperty("headers").EnumerateObject().Select(h => h.Name)];

    [Fact]
    public async Task Every_answer_has_a_request_id_and_a_problem_names_it()          // BC-11
    {
        await using var app = await Start();
        var id = await app.Created(Alice);

        var view = await app.Get($"/auctions/{id}", Rita);
        var problem = await app.Get($"/auctions/{Guid.CreateVersion7()}", Rita);
        var list = await app.Get("/auctions", Alice);
        var tooLarge = await app.Post("/auctions", $$"""{"title":"{{new string('x', 70_000)}}"}""", Alice);

        foreach (var answer in new[] { view, problem, list, tooLarge })
            answer.Response.Headers.GetValues(TarolHeaders.RequestId).Single().ShouldNotBeNullOrWhiteSpace();
        JsonElement.Parse(problem.Body).GetProperty("traceId").GetString().ShouldBe(problem.Response.Headers.GetValues(TarolHeaders.RequestId).Single());
        view.Response.Headers.GetValues(TarolHeaders.RequestId).Single().ShouldNotBe(problem.Response.Headers.GetValues(TarolHeaders.RequestId).Single());
    }

    [Fact]
    public async Task Me_answers_who_the_caller_is_by_the_entries_rules()               // BC-13
    {
        await using var app = await Start();

        var alice = JsonElement.Parse((await app.Get("/me", Alice)).Body);
        var service = JsonElement.Parse((await app.Get("/me", new Caller("billing-sa", TenantA, Client: "billing-sa"))).Body);
        var anonymous = await app.Get("/me", Nobody);
        var document = JsonElement.Parse((await app.Get("/openapi/v1.json", Nobody)).Body).GetProperty("paths").GetProperty("/me").GetProperty("get");

        alice.GetProperty("subject").GetString().ShouldBe("alice");
        (alice.GetProperty("actor").GetProperty("kind").GetString(), alice.GetProperty("actor").GetProperty("id").GetString()).ShouldBe(("User", "alice"));
        alice.GetProperty("tenant").GetGuid().ShouldBe(TenantA);
        alice.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(Alice.Roles.Split(' ').Order(StringComparer.Ordinal));
        service.GetProperty("actor").GetProperty("kind").GetString().ShouldBe("Service");
        anonymous.Status.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        document.GetProperty("x-tarol-kind").GetString().ShouldBe("me");
        document.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["subject", "actor", "tenant", "roles"], ignoreOrder: true);
    }

    private static string[] Statuses(JsonElement operation)
        => [.. operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void JsonPath_converts_member_paths_and_keeps_header_names()
    {
        ChangeEndpoints.JsonPath("Lines[1].Sku", JsonNamingPolicy.CamelCase).ShouldBe("lines[1].sku");
        ChangeEndpoints.JsonPath("If-Match", JsonNamingPolicy.CamelCase).ShouldBe("If-Match");
        ChangeEndpoints.JsonPath("", JsonNamingPolicy.CamelCase).ShouldBe("");
    }

    [Fact]
    public async Task MapChanges_fails_at_startup_without_the_authorization()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.UseTestServer(builder.WebHost);
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>()).UseInMemoryStore().AddEndpoints();
        await using var app = builder.Build();

        Should.Throw<InvalidOperationException>(() => app.MapChanges()).Message.ShouldContain("AddSecurity");
    }
}
