using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Caching;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Results;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>
/// The HTTP rows of migration/caching.md §11 (13C): the cache headers, and the list entries over <c>Tarol.Caching</c>
/// (a counting store under the cache tells a hit from a query).
/// </summary>
public sealed class CacheTests : IAsyncLifetime
{
    private static readonly Caller Sam = new("sam", TenantA, "seller", "auctions:read auctions:write");   // may sell, may not read

    private readonly SearchCount _searches = new();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync()
        => _app = await Start(endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromSeconds(10), store: b =>
        {
            b.UseInMemoryStore();
            b.Services.AddSingleton<InMemoryReadModels>();
            b.Services.AddSingleton<IReadModels>(provider => new CountingReadModels(provider.GetRequiredService<InMemoryReadModels>(), _searches));
            b.AddProjectionPositions().AddCaching();
        });

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<(string Id, string Position)> Create(Caller caller, string title)
    {
        var created = await _app.Post("/auctions", Auction(title), caller);
        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        return (JsonElement.Parse(created.Body).GetProperty("id").GetString()!, created.Response.Headers.GetValues(Positions.PositionHeader).Single());
    }

    private Task<Answer> Get(string path, Caller caller, string? readAfter = null, string? ifNoneMatch = null)
        => _app.Get(path, caller, h =>
        {
            if (readAfter is not null)
                h.Add(Positions.ReadAfterHeader, readAfter);
            if (ifNoneMatch is not null)
                h.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        });

    private static string[] Titles(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    private static void ShouldCarryTheCacheHeaders(Answer answer)
    {
        answer.Response.Headers.CacheControl.ShouldNotBeNull($"{answer.Status} {answer.Body}");
        answer.Response.Headers.CacheControl!.Private.ShouldBeTrue();
        answer.Response.Headers.CacheControl.NoCache.ShouldBeTrue();
        answer.Response.Headers.CacheControl.Public.ShouldBeFalse();
        answer.Response.Headers.Vary.ShouldBe([CacheHeaders.Vary]);
    }

    [Fact]
    public async Task List_and_view_answers_carry_private_no_cache_and_vary_by_authorization()
    {
        var (id, position) = await Create(Alice, "Irises");

        var list = await Get("/auctions", Alice, readAfter: position);
        var notModified = await Get("/auctions", Alice, ifNoneMatch: list.Response.Headers.ETag!.ToString());
        var badFilter = await Get("/auctions?status=Gone", Alice);
        var denied = await Get("/auctions", Sam);
        var view = await Get($"/auctions/{id}", Alice);
        var viewNotModified = await Get($"/auctions/{id}", Alice, ifNoneMatch: view.Response.Headers.ETag!.ToString());

        (list.Status, notModified.Status, badFilter.Status, denied.Status, view.Status, viewNotModified.Status)
            .ShouldBe((HttpStatusCode.OK, HttpStatusCode.NotModified, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.NotModified));
        foreach (var answer in new[] { list, notModified, badFilter, denied, view, viewNotModified })
            ShouldCarryTheCacheHeaders(answer);
    }

    [Fact]
    public async Task A_list_304_from_a_warm_cache_runs_no_query()
    {
        var (_, position) = await Create(Alice, "Irises");
        var first = await Get("/auctions", Alice, readAfter: position);
        first.Status.ShouldBe(HttpStatusCode.OK);
        var searches = _searches.Value;

        var notModified = await Get("/auctions", Alice, ifNoneMatch: first.Response.Headers.ETag!.ToString());
        var again = await Get("/auctions", Alice);

        notModified.Status.ShouldBe(HttpStatusCode.NotModified);
        notModified.Response.Headers.GetValues(Positions.PositionHeader).Single().ShouldBe(first.Response.Headers.GetValues(Positions.PositionHeader).Single());
        again.Body.ShouldBe(first.Body);
        _searches.Value.ShouldBe(searches);                                     // both from the cache
    }

    [Fact]
    public async Task Read_after_waits_for_the_checkpoint_then_the_cache_misses_and_shows_the_change()
    {
        var (_, first) = await Create(Alice, "Alpha");
        Titles(await Get("/auctions", Alice, readAfter: first)).ShouldBe(["Alpha"]);
        Titles(await Get("/auctions", Alice)).ShouldBe(["Alpha"]);             // warm

        var (_, second) = await Create(Alice, "Beta");
        var fresh = await Get("/auctions", Alice, readAfter: second);

        Titles(fresh).ShouldBe(["Alpha", "Beta"]);
        fresh.Response.Headers.Contains(Positions.StaleHeader).ShouldBeFalse();

        await using var slow = await Start(endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromMilliseconds(30), store: b =>
        {
            b.UseInMemoryStore();
            b.UseInMemoryReadModels().AddCaching();
        });
        var future = Positions.Format(new GlobalPosition(ulong.MaxValue - 1, 1));
        var stale = await slow.Get("/auctions", Alice, h => h.Add(Positions.ReadAfterHeader, future));
        stale.Status.ShouldBe(HttpStatusCode.OK);
        stale.Response.Headers.GetValues(Positions.StaleHeader).Single().ShouldBe("true");
    }

    [Fact]
    public async Task CheckList_runs_on_every_hit_so_a_denied_caller_gets_403_after_another_warmed_the_entry()
    {
        var (_, position) = await Create(Alice, "Irises");
        (await Get("/auctions", Alice, readAfter: position)).Status.ShouldBe(HttpStatusCode.OK);
        (await Get("/auctions", Rita)).Status.ShouldBe(HttpStatusCode.OK);    // the same tenant and query: a hit
        var searches = _searches.Value;

        var denied = await Get("/auctions", Sam);

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.Body.ShouldNotContain("Irises");
        _searches.Value.ShouldBe(searches);
        (await Get("/auctions", Carol)).Body.ShouldNotContain("Irises");       // another tenant: its own entry
    }

    [Fact]
    public async Task A_FromClaim_list_caches_per_claim_value()
    {
        await Create(Alice, "Mine");
        var (_, last) = await Create(Bob, "Bobs");
        Titles(await Get("/my/auctions", Alice, readAfter: last)).ShouldBe(["Mine"]);
        var afterAlice = _searches.Value;

        Titles(await Get("/my/auctions", Bob)).ShouldBe(["Bobs"]);              // another claim value: a miss
        _searches.Value.ShouldBe(afterAlice + 1);
        Titles(await Get("/my/auctions", Alice)).ShouldBe(["Mine"]);            // a hit
        Titles(await Get("/my/auctions", Bob)).ShouldBe(["Bobs"]);              // a hit
        _searches.Value.ShouldBe(afterAlice + 1);
    }

    internal sealed class SearchCount
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Add() => Interlocked.Increment(ref _value);
    }

    private sealed class CountingReadModels(IReadModels inner, SearchCount searches) : IReadModels
    {
        public IReadModelStore<TRow> Store<TRow>()
            where TRow : class, IProjection<TRow>
            => new Counting<TRow>(inner.Store<TRow>(), searches);

        private sealed class Counting<TRow>(IReadModelStore<TRow> inner, SearchCount searches) : IReadModelStore<TRow>
            where TRow : class, IProjection<TRow>
        {
            public ValueTask Replace(IReadOnlyList<Versioned<TRow>> rows, CancellationToken cancellationToken) => inner.Replace(rows, cancellationToken);

            public ValueTask<int> Remove(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken) => inner.Remove(keys, cancellationToken);

            public ValueTask Prepare(CancellationToken cancellationToken) => inner.Prepare(cancellationToken);

            public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => inner.Checkpoint(cancellationToken);

            public ValueTask<IReadOnlyDictionary<RowKey, Versioned<TRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
                => inner.Load(keys, cancellationToken);

            public ValueTask<bool> Write(ProjectionBatch<TRow> batch, CancellationToken cancellationToken) => inner.Write(batch, cancellationToken);

            public ValueTask<Versioned<TRow>?> Find(RowKey key, CancellationToken cancellationToken) => inner.Find(key, cancellationToken);

            public ValueTask<Result<Page<TRow>>> Search(ListQuery query, CancellationToken cancellationToken)
            {
                searches.Add();
                return inner.Search(query, cancellationToken);
            }
        }
    }
}
