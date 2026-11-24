using System.Net;
using System.Text.Json;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-2: an aggregate of which each tenant has exactly one ([PerTenant]): no id in changes, routes or views.</summary>
public sealed class PerTenantTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private Task<Answer> Put(string path, string body, Caller caller) => _app.Send(HttpMethod.Put, path, body, caller);

    [Fact]
    public async Task Each_tenant_opens_renames_and_reads_its_one_house_without_an_id()
    {
        var opened = await _app.Post("/house", """{"name":"Alice's"}""", Alice);
        opened.Status.ShouldBe(HttpStatusCode.Created, opened.Body);
        JsonElement.Parse(opened.Body).GetProperty("id").GetGuid().ShouldBe(TenantA);       // the id is the tenant's
        (await _app.Post("/house", """{"name":"Carol's"}""", Carol)).Status.ShouldBe(HttpStatusCode.Created);

        (await Put("/house/name", """{"name":"Alice & Bob"}""", Bob)).Status.ShouldBe(HttpStatusCode.NoContent);

        var a = await _app.Get("/house", Alice);
        a.Status.ShouldBe(HttpStatusCode.OK, a.Body);
        JsonElement.Parse(a.Body).GetProperty("name").GetString().ShouldBe("Alice & Bob");
        a.ETag.ShouldBe("\"2\"");
        JsonElement.Parse((await _app.Get("/house", Carol)).Body).GetProperty("name").GetString().ShouldBe("Carol's");
    }

    [Fact]
    public async Task A_second_creation_conflicts_and_a_change_before_the_creation_is_not_found()
    {
        (await Put("/house/name", """{"name":"x"}""", Alice)).Body.ShouldContain("House.NotFound");
        (await _app.Get("/house", Alice)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _app.Post("/house", """{"name":"one"}""", Alice)).Status.ShouldBe(HttpStatusCode.Created);

        var again = await _app.Post("/house", """{"name":"two"}""", Bob);

        again.Status.ShouldBe(HttpStatusCode.Conflict, again.Body);
        again.Body.ShouldContain("House.AlreadyExists");
    }

    [Fact]
    public async Task Without_a_tenant_the_house_is_refused()
    {
        var anonymousTenant = new Caller("sam", null, "seller");

        (await Put("/house/name", """{"name":"x"}""", anonymousTenant)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.Get("/house", anonymousTenant)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_document_has_no_id_parameter_for_the_house()
    {
        var paths = JsonElement.Parse(await _app.Client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).GetProperty("paths");

        foreach (var (path, verb) in new[] { ("/house", "get"), ("/house/name", "put") })
        {
            var operation = paths.GetProperty(path).GetProperty(verb);
            var parameters = operation.TryGetProperty("parameters", out var p) ? p.EnumerateArray().Select(x => x.GetProperty("in").GetString()).ToList() : [];
            parameters.ShouldNotContain("path");
        }
        paths.GetProperty("/house").GetProperty("get").GetProperty("x-tarol-kind").GetString().ShouldBe("view");
    }
}
