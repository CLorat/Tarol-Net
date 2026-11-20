using System.Net;
using System.Text.Json;
using Tarol.Persistence;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>The HTTP rows of queries.md §11 (12C): list entries over the auction-list projection.</summary>
public sealed class QueryEndpointTests : IAsyncLifetime
{
    private static readonly Caller Sam = new("sam", TenantA, "seller", "auctions:read auctions:write");   // may sell, may not read

    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start(endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromSeconds(10));

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<(string Id, string Position)> Create(Caller caller, string title)
    {
        var created = await _app.Post("/auctions", Auction(title), caller);
        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        return (JsonElement.Parse(created.Body).GetProperty("id").GetString()!, created.Response.Headers.GetValues(Positions.PositionHeader).Single());
    }

    private Task<Answer> List(string path, Caller caller, string? readAfter = null, string? ifNoneMatch = null)
        => _app.Get(path, caller, h =>
        {
            if (readAfter is not null)
                h.Add(Positions.ReadAfterHeader, readAfter);
            if (ifNoneMatch is not null)
                h.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        });

    private static string[] Titles(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    private static string? Next(Answer answer) => JsonElement.Parse(answer.Body).GetProperty("next").GetString();

    private static string[] Fields(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_change_answers_its_position_and_a_list_read_after_it_contains_the_change()
    {
        var (_, position) = await Create(Alice, "Irises");
        Positions.TryParse(position, out var at).ShouldBeTrue(position);

        var list = await List("/auctions", Alice, readAfter: position);

        list.Status.ShouldBe(HttpStatusCode.OK, list.Body);
        Titles(list).ShouldBe(["Irises"]);
        list.Response.Headers.Contains(Positions.StaleHeader).ShouldBeFalse();
        Positions.TryParse(list.Response.Headers.GetValues(Positions.PositionHeader).Single(), out var listed).ShouldBeTrue();
        listed.ShouldBeGreaterThanOrEqualTo(at);
        list.Response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task A_read_after_that_times_out_answers_200_with_the_stale_flag()
    {
        await using var app = await Start(endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromMilliseconds(30));
        var future = Positions.Format(new GlobalPosition(ulong.MaxValue - 1, 1));

        var list = await app.Get("/auctions", Alice, h => h.Add(Positions.ReadAfterHeader, future));

        list.Status.ShouldBe(HttpStatusCode.OK);
        list.Response.Headers.GetValues(Positions.StaleHeader).Single().ShouldBe("true");
        (await List("/auctions", Alice, readAfter: "not-a-position")).Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filters_and_sorts_come_from_the_query_string_with_the_id_breaking_ties()
    {
        await Create(Alice, "Alpha");
        await Create(Bob, "Beta");
        var (_, last) = await Create(Alice, "Gamma");

        var alice = await List("/auctions?seller=alice&status=Open", Alice, readAfter: last);
        var byId = await List("/auctions?sort=id", Alice, readAfter: last);

        Titles(alice).ShouldBe(["Alpha", "Gamma"]);                           // ends_at: created in this order, seven days on
        Titles(byId).Length.ShouldBe(3);
        Titles(await List("/auctions?status=closed", Alice)).ShouldBeEmpty();   // enum names are case-insensitive
        Fields(await List("/auctions?status=Gone", Alice)).ShouldBe(["status"]);
        Fields(await List("/auctions?status=0", Alice)).ShouldBe(["status"]);  // numbers are not names
        Fields(await List("/auctions?sort=title", Alice)).ShouldBe(["sort"]);
        Fields(await List("/auctions?seller=a&seller=b", Alice)).ShouldBe(["seller"]);
    }

    [Fact]
    public async Task Pages_follow_the_cursor_and_bad_paging_is_400()
    {
        var last = "";
        foreach (var title in new[] { "One", "Two", "Three", "Four", "Five" })
            (_, last) = await Create(Alice, title);

        var page1 = await List("/auctions?limit=2", Alice, readAfter: last);
        var page2 = await List($"/auctions?limit=2&after={Uri.EscapeDataString(Next(page1)!)}", Alice);
        var page3 = await List($"/auctions?limit=2&after={Uri.EscapeDataString(Next(page2)!)}", Alice);

        Titles(page1).Concat(Titles(page2)).Concat(Titles(page3)).ShouldBe(["One", "Two", "Three", "Four", "Five"]);
        Next(page3).ShouldBeNull();
        Titles(await List("/auctions?limit=2&offset=2", Alice)).ShouldBe(Titles(page2));
        Fields(await List($"/auctions?sort=highestBid&after={Uri.EscapeDataString(Next(page1)!)}", Alice)).ShouldBe(["after"]);
        Fields(await List("/auctions?after=garbage", Alice)).ShouldBe(["after"]);
        Fields(await List("/auctions?limit=201", Alice)).ShouldBe(["limit"]);
        Fields(await List("/auctions?limit=0", Alice)).ShouldBe(["limit"]);
        Fields(await List("/auctions?offset=1001", Alice)).ShouldBe(["offset"]);
        Fields(await List($"/auctions?offset=1&after={Uri.EscapeDataString(Next(page1)!)}", Alice)).ShouldBe(["offset"]);
        Fields(await List("/auctions?count=maybe&limit=x", Alice)).ShouldBe(["count", "limit"]);   // every error at once
    }

    [Fact]
    public async Task The_total_is_counted_only_when_asked()
    {
        var (_, last) = await Create(Alice, "Counted");
        await Create(Alice, "Also counted");

        JsonElement.Parse((await List("/auctions?limit=1", Alice, readAfter: last)).Body).TryGetProperty("total", out _).ShouldBeFalse();
        var counted = await List("/auctions?limit=1&count=true", Alice);
        JsonElement.Parse(counted.Body).GetProperty("total").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task An_unknown_query_key_is_400_instead_of_an_unfiltered_list()     // BC-14
    {
        var (_, position) = await Create(Alice, "Known");

        var typo = await List("/auctions?selller=alice&limit=5", Alice, readAfter: position);
        var known = await List("/auctions?Seller=alice&SORT=EndsAt&limit=5&count=true", Alice, readAfter: position);

        typo.Status.ShouldBe(HttpStatusCode.BadRequest, typo.Body);
        Fields(typo).ShouldBe(["selller"]);
        JsonElement.Parse(typo.Body).GetProperty("errors").GetProperty("selller")[0].GetString().ShouldBe(ChangeEndpoints.UnknownKeyCode);
        known.Status.ShouldBe(HttpStatusCode.OK, known.Body);                  // keys compare without case, like the query string
    }

    [Fact]
    public async Task The_page_ETag_answers_304_until_a_row_on_the_page_changes()
    {
        var (id, position) = await Create(Alice, "Tagged");
        var first = await List("/auctions", Alice, readAfter: position);
        first.ETag.ShouldNotBeNull();
        first.Response.Headers.ETag!.IsWeak.ShouldBeTrue();

        var again = await List("/auctions", Alice, ifNoneMatch: first.Response.Headers.ETag!.ToString());
        again.Status.ShouldBe(HttpStatusCode.NotModified);
        again.Response.Headers.Contains(Positions.PositionHeader).ShouldBeTrue();

        var bid = await _app.Post($"/auctions/{id}/bids", """{"amount":500}""", Bob);
        bid.Status.ShouldBe(HttpStatusCode.NoContent, bid.Body);
        var changed = await List("/auctions", Alice, readAfter: bid.Response.Headers.GetValues(Positions.PositionHeader).Single(),
            ifNoneMatch: first.Response.Headers.ETag!.ToString());
        changed.Status.ShouldBe(HttpStatusCode.OK);
        changed.ETag.ShouldNotBe(first.ETag);
        changed.Body.ShouldContain("\"bids\":1");
    }

    [Fact]
    public async Task A_row_appended_behind_a_full_last_page_answers_200_with_a_next_cursor()   // BC-6
    {
        var (_, position) = await Create(Alice, "First");
        var full = await List("/auctions?sort=id&limit=1", Alice, readAfter: position);
        full.Status.ShouldBe(HttpStatusCode.OK, full.Body);
        Next(full).ShouldBeNull();

        var (_, later) = await Create(Alice, "Second");                    // a version 7 id: sorts behind the page
        var grown = await List("/auctions?sort=id&limit=1", Alice, readAfter: later, ifNoneMatch: full.Response.Headers.ETag!.ToString());

        grown.Status.ShouldBe(HttpStatusCode.OK);
        Titles(grown).ShouldBe(["First"]);
        Next(grown).ShouldNotBeNull();
        grown.ETag.ShouldNotBe(full.ETag);
    }

    [Fact]
    public async Task A_list_needs_the_Read_grant_is_tenant_scoped_and_runs_no_row_rules()
    {
        var (_, position) = await Create(Alice, "Visible");

        (await List("/auctions", Nobody)).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await List("/auctions", Sam)).Status.ShouldBe(HttpStatusCode.Forbidden);             // the role map grants no Read<Auction>
        var rita = await List("/auctions", Rita, readAfter: position);                     // a reader: ReadOwnAuction would deny GET by id
        Titles(rita).ShouldBe(["Visible"]);
        Titles(await List("/auctions", Carol)).ShouldBeEmpty();                            // tenant B sees nothing of tenant A
    }

    [Fact]
    public async Task A_claim_bound_member_comes_from_the_caller_never_from_the_request()
    {
        await Create(Alice, "Mine");
        var (_, last) = await Create(Bob, "Bobs");

        Titles(await List("/my/auctions", Alice, readAfter: last)).ShouldBe(["Mine"]);
        var overridden = await List("/my/auctions?seller=bob", Alice);                      // not a query parameter: refused (BC-14)
        overridden.Status.ShouldBe(HttpStatusCode.BadRequest);
        Fields(overridden).ShouldBe(["seller"]);
        Titles(await List("/my/auctions", Bob)).ShouldBe(["Bobs"]);
    }

    [Fact]
    public async Task A_route_value_filters_and_the_default_sort_descends()
    {
        var (low, _) = await Create(Alice, "Low");
        var (high, _) = await Create(Alice, "High");
        (await _app.Post($"/auctions/{low}/bids", """{"amount":150}""", Bob)).Status.ShouldBe(HttpStatusCode.NoContent);
        var bid = await _app.Post($"/auctions/{high}/bids", """{"amount":900}""", Bob);

        var list = await List("/sellers/alice/auctions", Alice, readAfter: bid.Response.Headers.GetValues(Positions.PositionHeader).Single());

        Titles(list).ShouldBe(["High", "Low"]);
        Titles(await List("/sellers/bob/auctions", Alice)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_projection_that_is_still_building_answers_503_with_Retry_After()
    {
        await using var app = await Start(projections: false);

        var list = await app.Get("/auctions", Alice);

        list.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, list.Body);
        list.Response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(5));
        list.Body.ShouldContain(ChangeEndpoints.BuildingCode);
    }

    [Fact]
    public async Task A_replayed_change_has_no_position()
    {
        var first = await _app.Post("/auctions", Auction("Keyed"), Alice, h => h.Add("Idempotency-Key", "k-1"));
        var replay = await _app.Post("/auctions", Auction("Keyed"), Alice, h => h.Add("Idempotency-Key", "k-1"));

        first.Response.Headers.Contains(Positions.PositionHeader).ShouldBeTrue();
        replay.Response.Headers.Contains(Positions.PositionHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task The_OpenAPI_document_describes_the_list()
    {
        var document = await _app.Get("/openapi/v1.json", Nobody);
        document.Status.ShouldBe(HttpStatusCode.OK);
        var get = JsonElement.Parse(document.Body).GetProperty("paths").GetProperty("/auctions").GetProperty("get");

        var parameters = get.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        parameters.ShouldBe(["status", "seller", "sort", "after", "limit", "offset", "count", "Tarol-Read-After"], ignoreOrder: true);
        var sort = get.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "sort");
        sort.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ShouldBe(["EndsAt", "HighestBid", "Id"]);
        var responses = get.GetProperty("responses");
        responses.TryGetProperty("304", out _).ShouldBeTrue();
        responses.TryGetProperty("503", out _).ShouldBeTrue();
        var ok = responses.GetProperty("200");
        ok.GetProperty("headers").TryGetProperty("Tarol-Position", out _).ShouldBeTrue();
        var page = ok.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        page.GetProperty("properties").GetProperty("items").GetProperty("type").GetString().ShouldBe("array");
        page.GetProperty("properties").GetProperty("items").GetProperty("items").ToString().ShouldContain("AuctionRow");
        var post = JsonElement.Parse(document.Body).GetProperty("paths").GetProperty("/my/auctions").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        post.ShouldNotContain("seller");                                                     // a claim member is not a parameter
    }

    [Fact]
    public async Task Positions_round_trip_as_text()
    {
        Positions.Format(new GlobalPosition(7341, 1000412)).ShouldBe("7341.1000412");
        Positions.TryParse("7341.1000412", out var p).ShouldBeTrue();
        p.ShouldBe(new GlobalPosition(7341, 1000412));
        foreach (var bad in new[] { "", "7341", ".5", "7341.", "-1.2", "1.-2", "a.b", "1.2.3" })
            Positions.TryParse(bad, out _).ShouldBeFalse(bad);
        await Task.CompletedTask;
    }
}
