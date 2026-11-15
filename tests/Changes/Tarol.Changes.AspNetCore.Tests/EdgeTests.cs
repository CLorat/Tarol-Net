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
        invalid.Body.ShouldBe("""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","status":400,"title":"One or more fields are invalid.","errors":{"title":["Length"],"reserve":["Range"]}}""");
        refused.Body.ShouldBe("""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","status":403,"title":"Auction.NotSeller","code":"Auction.NotSeller"}""");
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
