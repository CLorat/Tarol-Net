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
    public async Task A_view_may_have_a_member_named_From_when_it_implements_the_factory_explicitly()
    {
        // Tatalu FB-F06.4-1: CS0102 with the factory From; ChildAbsenceView used FirstDay/LastDay
        (await _app.Post("/house", """{"name":"Alice's"}""", Alice)).Status.ShouldBe(HttpStatusCode.Created);

        var season = await _app.Get("/house/season", Alice);

        season.Status.ShouldBe(HttpStatusCode.OK, season.Body);
        JsonElement.Parse(season.Body).GetProperty("from").GetString().ShouldBe("2026-04-01");
        JsonElement.Parse(season.Body).GetProperty("until").GetString().ShouldBe("2026-10-31");
    }

    [Fact]
    public async Task One_row_of_a_projection_is_read_for_the_tenant_or_by_id_with_ETag_read_after_and_404()
    {
        // TN-54 (Tatalu FB-F01.2-4): GET /tpp/aenderungen was a list of one row; the frontend took items[0]
        (await _app.Get("/house/row", Alice)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _app.Post("/house", """{"name":"Alice's"}""", Alice)).Status.ShouldBe(HttpStatusCode.Created);
        var renamed = await Put("/house/name", """{"name":"Alice & Bob"}""", Bob);
        var position = renamed.Response.Headers.GetValues(Positions.PositionHeader).Single();

        var row = await _app.Get("/house/row", Alice, h => h.Add(Positions.ReadAfterHeader, position));

        row.Status.ShouldBe(HttpStatusCode.OK, row.Body);
        var json = JsonElement.Parse(row.Body);
        json.GetProperty("name").GetString().ShouldBe("Alice & Bob");
        json.GetProperty("renames").GetInt32().ShouldBe(1);
        row.ETag.ShouldBe("\"2\"");
        (await _app.Get("/house/row", Alice, h => h.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"2\"")))).Status
            .ShouldBe(HttpStatusCode.NotModified);
        var byId = await _app.Get($"/house-rows/{TenantA}", Alice);
        byId.Status.ShouldBe(HttpStatusCode.OK, byId.Body);
        (await _app.Get($"/house-rows/{Guid.NewGuid()}", Alice)).Body.ShouldContain("House.NotFound");
        (await _app.Get("/house/row", Carol)).Status.ShouldBe(HttpStatusCode.NotFound);            // another tenant's row is not seen

        var document = JsonElement.Parse((await _app.Get("/openapi/v1.json", Alice)).Body);
        var get = document.GetProperty("paths").GetProperty("/house/row").GetProperty("get");
        get.GetProperty("x-tarol-kind").GetString().ShouldBe("row");
        get.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/HouseRow");
        get.GetProperty("x-tarol-errors").EnumerateArray().Select(e => e.GetString()).ShouldContain("House.NotFound");
    }

    [Fact]
    public async Task A_change_starts_a_missing_target_answers_201_and_a_view_shows_the_started_state()
    {
        // TN-42 (Tatalu FB-F02.5-2, FB-F15.5-2, FB-F13.4-2): load-or-start per change, 204 without an id, 404 before the first event
        var shelf = Guid.CreateVersion7();
        var empty = await _app.Get($"/shelves/{shelf}", Alice);
        empty.Status.ShouldBe(HttpStatusCode.OK, empty.Body);
        JsonElement.Parse(empty.Body).GetProperty("books").GetInt32().ShouldBe(0);
        empty.ETag.ShouldBe("\"0\"");

        var first = await Put($"/shelves/{shelf}/books", """{"title":"Dune"}""", Alice);
        var second = await Put($"/shelves/{shelf}/books", """{"title":"Emma"}""", Alice);

        first.Status.ShouldBe(HttpStatusCode.Created, first.Body);
        JsonElement.Parse(first.Body).GetProperty("id").GetGuid().ShouldBe(shelf);
        JsonElement.Parse(first.Body).GetProperty("version").GetInt64().ShouldBe(2);
        first.Response.Headers.Location.ShouldBeNull();                                      // not a creation entry
        second.Status.ShouldBe(HttpStatusCode.NoContent, second.Body);
        var view = await _app.Get($"/shelves/{shelf}", Alice);
        JsonElement.Parse(view.Body).GetProperty("books").GetInt32().ShouldBe(2);
        view.ETag.ShouldBe("\"3\"");
        (await _app.Get($"/shelves/{shelf}", Carol)).Status.ShouldBe(HttpStatusCode.OK);    // another tenant: its own (unstarted) shelf

        var document = JsonElement.Parse((await _app.Get("/openapi/v1.json", Alice)).Body);
        var responses = document.GetProperty("paths").GetProperty("/shelves/{shelf}/books").GetProperty("put").GetProperty("responses");
        responses.TryGetProperty("201", out _).ShouldBeTrue();
        responses.TryGetProperty("204", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_view_built_for_its_reader_differs_per_reader_with_an_ETag_by_content()
    {
        // TN-43 (VA-1, Tatalu FB-E07-3): From(aggregate, ViewContext); ETag "<version>-<hash>"
        (await _app.Post("/house", """{"name":"Alice's"}""", Alice)).Status.ShouldBe(HttpStatusCode.Created);

        var alice = await _app.Get("/house/greeting", Alice);
        var bob = await _app.Get("/house/greeting", Bob);

        alice.Status.ShouldBe(HttpStatusCode.OK, alice.Body);
        JsonElement.Parse(alice.Body).GetProperty("reader").GetString().ShouldBe("alice");
        JsonElement.Parse(bob.Body).GetProperty("reader").GetString().ShouldBe("bob");
        alice.ETag.ShouldStartWith("\"1-");
        alice.ETag.ShouldNotBe(bob.ETag);
        (await _app.Get("/house/greeting", Alice, h => h.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(alice.ETag!)))).Status
            .ShouldBe(HttpStatusCode.NotModified);
        (await _app.Get("/house/greeting", Bob, h => h.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(alice.ETag!)))).Status
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_restricted_view_needs_its_own_grant_and_that_grant_reads_no_other_view()
    {
        // TN-43 (VA-2a/b, Tatalu FB-F01.1-6): Read<T> granted every view of T; now a restricted one needs MayRead<T, TView>
        (await _app.Post("/house", """{"name":"Alice's"}""", Alice)).Status.ShouldBe(HttpStatusCode.Created);
        var bookkeeper = new TestApp.Caller("berta", TestApp.TenantA, "bookkeeper");

        (await _app.Get("/house/ledger", Alice)).Status.ShouldBe(HttpStatusCode.Forbidden);          // Read<House> is not enough
        var ledger = await _app.Get("/house/ledger", bookkeeper);
        ledger.Status.ShouldBe(HttpStatusCode.OK, ledger.Body);
        (await _app.Get("/house", bookkeeper)).Status.ShouldBe(HttpStatusCode.Forbidden);           // MayRead<House, HouseLedger> only
        (await _app.Get("/house", Alice)).Status.ShouldBe(HttpStatusCode.OK);                        // unrestricted views: as before
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
