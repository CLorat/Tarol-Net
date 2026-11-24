using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-6 over HTTP: a list scope restricts the rows to the caller's, binds the cursor and is in the OpenAPI document.</summary>
public sealed class ListScopeEndpointTests : IAsyncLifetime
{
    private static readonly Caller Paula = new("paula", TenantA, "reader", "auctions:read");      // a parent: two sellers
    private static readonly Caller Vera = new("vera", TenantA, "reader", "auctions:read");        // a substitute for two weeks: three
    private static readonly Caller Vince = new("vince", TenantA, "reader", "auctions:read");      // a substitute whose period ended
    private static readonly Caller Sam = new("sam", TenantA, "reader", "auctions:read");          // no family at all
    private static readonly Caller Carl = new("carl", TenantA, "seller");

    private TestApp _app = null!;
    private string _last = "";

    public async ValueTask InitializeAsync()
    {
        _app = await Start(endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromSeconds(10));
        foreach (var (caller, title) in new[] { (Alice, "Alpha"), (Bob, "Beta"), (Carl, "Gamma"), (Alice, "Delta"), (Bob, "Epsilon") })
        {
            var created = await _app.Post("/auctions", Auction(title), caller);
            created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
            _last = created.Response.Headers.GetValues(Positions.PositionHeader).Single();
        }
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private Task<Answer> List(string path, Caller caller) => _app.Get(path, caller, h => h.Add(Positions.ReadAfterHeader, _last));

    private static string[] Titles(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_parent_with_two_children_lists_the_rows_of_both_and_no_other()
    {
        var list = await List("/family/auctions", Paula);

        list.Status.ShouldBe(HttpStatusCode.OK, list.Body);
        Titles(list).ShouldBe(["Alpha", "Beta", "Delta", "Epsilon"]);
        Titles(await List("/auctions", Paula)).Length.ShouldBe(5);         // the unscoped list is another entry
    }

    [Fact]
    public async Task A_substitute_sees_the_assigned_rows_only_within_the_period()
    {
        Titles(await List("/family/auctions", Vera)).ShouldBe(["Alpha", "Beta", "Delta", "Epsilon", "Gamma"]);

        var ended = await List("/family/auctions", Vince);

        ended.Status.ShouldBe(HttpStatusCode.OK, ended.Body);
        Titles(ended).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_scope_answers_an_empty_page_never_an_unfiltered_one()
    {
        var list = await List("/family/auctions?count=true", Sam);

        list.Status.ShouldBe(HttpStatusCode.OK, list.Body);
        var page = JsonElement.Parse(list.Body);
        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
        page.GetProperty("total").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task The_scope_works_with_the_request_filters_and_binds_the_cursor_and_the_etag()
    {
        Titles(await List("/family/auctions?status=Closed", Paula)).ShouldBeEmpty();
        var first = await List("/family/auctions?limit=2", Paula);
        var next = JsonElement.Parse(first.Body).GetProperty("next").GetString().ShouldNotBeNull();

        var own = await List($"/family/auctions?limit=2&after={next}", Paula);
        var foreign = await List($"/family/auctions?limit=2&after={next}", Vera);

        own.Status.ShouldBe(HttpStatusCode.OK, own.Body);
        foreign.Status.ShouldBe(HttpStatusCode.BadRequest, foreign.Body);  // a cursor of another scope never pages another set
        (await List("/family/auctions", Vera)).ETag.ShouldNotBe((await List("/family/auctions", Paula)).ETag);
        _app.App.Services.GetRequiredService<FamilyScope>().Calls.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task The_document_marks_the_scoped_list()
    {
        var paths = JsonElement.Parse(await _app.Client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).GetProperty("paths");

        var family = paths.GetProperty("/family/auctions").GetProperty("get");
        family.GetProperty("x-tarol-scoped").GetBoolean().ShouldBeTrue();
        family.GetProperty("description").GetString()!.ShouldContain("only the rows the caller may see");
        family.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(
            ["status", "after", "limit", "offset", "count", "Tarol-Read-After"]);
        paths.GetProperty("/auctions").GetProperty("get").TryGetProperty("x-tarol-scoped", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_list_that_requires_a_scope_fails_at_startup_without_one()
    {
        var failed = await Should.ThrowAsync<InvalidOperationException>(async () => await (await Start(scopes: false)).DisposeAsync());

        failed.Message.ShouldContain("IListScope<FamilyAuctions>");
    }
}
