using System.Net;
using System.Text.Json;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Queries;
using Tarol.Results;
using Tarol.Search;
using Tarol.StronglyTypedId;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>The HTTP rows of search.md §11 (18D): MapTarolSearch over the in-memory index.</summary>
public sealed class SearchEndpointTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start(search: true, endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromSeconds(10));

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<(string Id, string Position)> Create(Caller caller, string title)
    {
        var created = await _app.Post("/auctions", Auction(title), caller);
        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        return (JsonElement.Parse(created.Body).GetProperty("id").GetString()!, created.Response.Headers.GetValues(Positions.PositionHeader).Single());
    }

    private Task<Answer> Search(string query, Caller caller, string? readAfter = null, string? ifNoneMatch = null)
        => _app.Get("/search?" + query, caller, h =>
        {
            if (readAfter is not null)
                h.Add(Positions.ReadAfterHeader, readAfter);
            if (ifNoneMatch is not null)
                h.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        });

    private static string[] Titles(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    private async Task<string> Commit<T>(T aggregate, Guid tenant)
        where T : Tarol.Primitives.AggregateRoot<T>, Tarol.Primitives.IAggregateRoot<T>
    {
        var context = OperationContext.Start(Actor.User("test"), (Id<ITenant>)tenant, TimeProvider.System);
        var position = (await _app.Store.Append(aggregate, 0, context, Option.None, TestContext.Current.CancellationToken)).GetValueOrThrow();
        Tarol.Primitives.AggregateChanges.Committed(aggregate);
        return Positions.Format(position);
    }

    [Fact]
    public async Task A_search_after_the_callers_change_finds_it_with_position_etag_and_cache_headers()
    {
        var (id, position) = await Create(Alice, "Sunflowers at noon");

        var found = await Search("q=sunflowers", Alice, readAfter: position);

        found.Status.ShouldBe(HttpStatusCode.OK, found.Body);
        var item = JsonElement.Parse(found.Body).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("id").GetString().ShouldBe(id);
        item.GetProperty("type").GetString().ShouldBe(Tarol.Search.SearchDocuments.StreamTypeOf<Auction>());
        item.GetProperty("aggregate").GetString().ShouldBe(id);
        found.Response.Headers.GetValues(Positions.PositionHeader).ShouldHaveSingleItem();
        found.Response.Headers.CacheControl!.Private.ShouldBeTrue();
        found.Response.Headers.CacheControl.NoCache.ShouldBeTrue();
        found.Response.Headers.Vary.ShouldContain(CacheHeaders.Vary);
        var etag = found.Response.Headers.ETag!.ToString();
        etag.ShouldStartWith("W/\"");

        var again = await Search("q=sunflowers", Alice, ifNoneMatch: etag);
        again.Status.ShouldBe(HttpStatusCode.NotModified);
        // BC-7: the weak comparison, a list of tags and * (RFC 9110 §13.1.2)
        (await Search("q=sunflowers", Alice, ifNoneMatch: etag[2..])).Status.ShouldBe(HttpStatusCode.NotModified);
        (await Search("q=sunflowers", Alice, ifNoneMatch: "W/\"0\", " + etag)).Status.ShouldBe(HttpStatusCode.NotModified);
        (await Search("q=sunflowers", Alice, ifNoneMatch: "*")).Status.ShouldBe(HttpStatusCode.NotModified);
        (await Search("q=sunflowers", Alice, ifNoneMatch: "W/\"0\"")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_tenant_never_sees_the_document()
    {
        var (_, position) = await Create(Alice, "Irises in the garden");

        (await Search("q=irises", Alice, readAfter: position)).Body.ShouldContain("Irises");
        Titles(await Search("q=irises", Carol, readAfter: position)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_requested_type_the_caller_may_not_read_is_forbidden_and_the_default_leaves_it_out()
    {
        var payout = Tarol.Search.SearchDocuments.StreamTypeOf<Payout>();
        (await Search($"q=x&types={payout}", Alice)).Status.ShouldBe(HttpStatusCode.Forbidden);
        var (_, position) = await Create(Alice, "Water lilies");

        var counts = JsonElement.Parse((await Search("q=lilies&counts=true", Alice, readAfter: position)).Body).GetProperty("counts");
        counts.EnumerateObject().Select(p => p.Name).ShouldBe([Tarol.Search.SearchDocuments.StreamTypeOf<Auction>()]);
    }

    [Fact]
    public async Task A_caller_without_any_readable_type_is_forbidden()
        => (await Search("q=x", new Caller("sam", TenantA, "seller", "auctions:read"))).Status.ShouldBe(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Bad_requests_are_400_and_an_anonymous_caller_401()
    {
        (await Search("q=x&types=nope", Alice)).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Search("q=x&limit=51", Alice)).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Search("q=x&prefix=maybe", Alice)).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Search("limit=5", Alice)).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Search("q=x&after=not-a-cursor", Alice)).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Search("q=x", Alice, readAfter: "nope")).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await Search("q=x", Nobody)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_audience_is_the_callers_subject_and_roles()
    {
        await Commit(Memo.Write("harbour memo of alice", "alice"), TenantA);
        await Commit(Memo.Write("harbour memo for bidders", "bidder"), TenantA);
        var position = await Commit(Memo.Write("harbour memo for everyone", null), TenantA);

        Titles(await Search("q=harbour", Alice, readAfter: position)).Order().ShouldBe(["harbour memo for bidders", "harbour memo for everyone", "harbour memo of alice"]);
        Titles(await Search("q=harbour", new Caller("bea", TenantA, "bidder"), readAfter: position)).Order().ShouldBe(["harbour memo for bidders", "harbour memo for everyone"]);
    }

    [Fact]
    public async Task Pages_follow_the_cursor_and_a_prefix_finds_while_typing()
    {
        for (var i = 0; i < 3; i++)
            await Create(Alice, $"Poppies {i}");
        var (_, position) = await Create(Alice, "Poppies 3");

        var first = JsonElement.Parse((await Search("q=poppies&limit=2", Alice, readAfter: position)).Body);
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        var next = first.GetProperty("next").GetString()!;
        var second = JsonElement.Parse((await Search($"q=poppies&limit=2&after={Uri.EscapeDataString(next)}", Alice)).Body);
        second.GetProperty("items").GetArrayLength().ShouldBe(2);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
        Titles(await Search("q=popp&prefix=true&limit=10", Alice)).Length.ShouldBe(4);
    }

    [Fact]
    public async Task While_no_index_version_is_current_the_answer_is_503()
    {
        await using var app = await Start(search: true, searchIndex: new Building());

        var answer = await app.Get("/search?q=x", Alice);

        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, answer.Body);
        answer.Response.Headers.RetryAfter.ShouldNotBeNull();
    }

    /// <summary>An index that never becomes current.</summary>
    private sealed class Building : ISearchIndex
    {
        private readonly InMemorySearchIndex _inner = new();

        public ValueTask Prepare(SearchIndexDefinition definition, CancellationToken cancellationToken) => _inner.Prepare(definition, cancellationToken);
        public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => _inner.Checkpoint(cancellationToken);
        public ValueTask<bool> Write(SearchBatch batch, CancellationToken cancellationToken) => _inner.Write(batch with { State = ProjectionState.Building }, cancellationToken);
        public ValueTask Replace(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken) => _inner.Replace(streams, cancellationToken);
        public ValueTask<Result<SearchPage>> Search(SearchRequest request, CancellationToken cancellationToken) => _inner.Search(request, cancellationToken);
        public ValueTask<IReadOnlyList<SearchHit>> Documents(Guid tenant, IReadOnlyCollection<Guid> aggregates, CancellationToken cancellationToken)
            => _inner.Documents(tenant, aggregates, cancellationToken);
    }
}
