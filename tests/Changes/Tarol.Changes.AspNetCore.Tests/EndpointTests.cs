using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tarol.Kernel;
using Tarol.Persistence;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>Every row of changes.md §6 over HTTP, plus authorization (AZ-D), keys, If-Match and the edge context.</summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    // ── creations ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_creation_answers_201_with_location_etag_and_id()
    {
        var created = await _app.Post("/auctions", Auction(), Alice);

        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        var id = JsonElement.Parse(created.Body).GetProperty("id").GetString();
        created.Response.Headers.Location!.ToString().ShouldBe($"/auctions/{id}");
        created.ETag.ShouldBe("\"1\"");
        created.Body.ShouldContain("\"version\":1");
    }

    [Fact]
    public async Task Anonymous_is_401_with_the_schemes_challenge_and_a_missing_scope_is_403_without_a_body()
    {
        var anonymous = await _app.Post("/auctions", Auction(), Nobody);
        var noScope = await _app.Post("/auctions", Auction(), Rita);

        anonymous.Status.ShouldBe(HttpStatusCode.Unauthorized);
        noScope.Status.ShouldBe(HttpStatusCode.Forbidden);
        noScope.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_values_are_400_with_every_field_by_json_path()
    {
        var invalid = await _app.Post("/auctions", """{"title":"x","reserve":0,"endsAt":"2030-01-01T00:00:00Z"}""", Alice);

        invalid.Status.ShouldBe(HttpStatusCode.BadRequest);
        invalid.Response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        invalid.Body.ShouldContain("\"title\":[\"Length\"]");
        invalid.Body.ShouldContain("\"reserve\":[\"Range\"]");
    }

    [Fact]
    public async Task A_factory_guard_on_an_input_field_is_400_on_that_field()
        => (await _app.Post("/auctions", $$"""{"title":"Irises","reserve":10,"endsAt":"{{TimeProvider.System.GetUtcNow().AddHours(1):O}}"}""", Alice))
            .Body.ShouldContain("\"endsAt\":[\"TooSoon\"]");

    [Fact]
    public async Task The_member_binder_reports_every_missing_and_every_mistyped_member()
    {
        var missing = await _app.Post("/auctions", """{"title":"Sunflowers"}""", Alice);
        var mistyped = await _app.Post("/auctions", """{"title":"Sunflowers","reserve":"lots","endsAt":7}""", Alice);
        var broken = await _app.Post("/auctions", "{", Alice);
        var notAnObject = await _app.Post("/auctions", "[1]", Alice);

        missing.Status.ShouldBe(HttpStatusCode.BadRequest);
        missing.Body.ShouldContain("\"reserve\":[\"Required\"]");
        missing.Body.ShouldContain("\"endsAt\":[\"Required\"]");
        mistyped.Body.ShouldContain("\"reserve\":[\"Json\"]");
        mistyped.Body.ShouldContain("\"endsAt\":[\"Json\"]");
        broken.Status.ShouldBe(HttpStatusCode.BadRequest);
        notAnObject.Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_members_and_route_members_in_the_body_are_ignored()
    {
        var id = await _app.Created(Alice);

        var bid = await _app.Post($"/auctions/{id}/bids", $$$"""{"auction":"{{{Guid.CreateVersion7()}}}","amount":150,"extra":{"deep":[1,2]}}""", Bob);

        bid.Status.ShouldBe(HttpStatusCode.NoContent, bid.Body);                        // the route's auction, not the body's
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_415_from_routing_and_a_large_one_is_413()
    {
        var text = await _app.Post("/auctions", "title=x", Alice, contentType: "text/plain");
        var large = await _app.Post("/auctions", $$"""{"title":"{{new string('x', 70_000)}}"}""", Alice);

        text.Status.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        large.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_json_property_name_and_an_optional_member_with_a_default_bind()
    {
        var id = await _app.Created(Alice);

        var renamed = await _app.Send(HttpMethod.Put, $"/auctions/{id}/title", """{"new_title":"Irises"}""", Alice);
        var wrongName = await _app.Send(HttpMethod.Put, $"/auctions/{id}/title", """{"title":"Irises"}""", Alice);

        renamed.Status.ShouldBe(HttpStatusCode.NoContent, renamed.Body);
        _app.Store.Recorded[^1].Event.Event.ShouldBe(new Auction.Watched("none"));          // Reason = its default
        wrongName.Body.ShouldContain("\"new_title\":[\"Required\"]");
    }

    // ── keys ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_idempotency_key_replays_the_first_outcome_and_refuses_another_body()
    {
        var tulips = Auction("Tulips");                                                  // one body (its endsAt is computed)
        var first = await _app.Post("/auctions", tulips, Alice, h => h.Add("Idempotency-Key", "k-1"));
        var again = await _app.Post("/auctions", tulips, Alice, h => h.Add("Idempotency-Key", "k-1"));
        var other = await _app.Post("/auctions", Auction("Roses"), Alice, h => h.Add("Idempotency-Key", "k-1"));
        var otherCaller = await _app.Post("/auctions", tulips, Bob, h => h.Add("Idempotency-Key", "k-1"));

        first.Status.ShouldBe(HttpStatusCode.Created);
        again.Status.ShouldBe(HttpStatusCode.OK);
        again.Body.ShouldBe(first.Body);
        again.Response.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        other.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        other.Body.ShouldContain(StoreErrors.KeyReusedCode);
        otherCaller.Status.ShouldBe(HttpStatusCode.Created);
        otherCaller.Body.ShouldNotBe(first.Body);
    }

    [Fact]
    public async Task A_change_retried_with_its_key_answers_204_again_and_appends_nothing()
    {
        var id = await _app.Created(Alice);
        var first = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.Add("Idempotency-Key", "bid-1"));
        var events = _app.Store.Recorded.Count;
        var again = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.Add("Idempotency-Key", "bid-1"));

        again.Status.ShouldBe(HttpStatusCode.NoContent);
        again.ETag.ShouldBe(first.ETag);
        _app.Store.Recorded.Count.ShouldBe(events);
    }

    [Fact]
    public async Task An_overlong_idempotency_key_is_400()
        => (await _app.Post("/auctions", Auction(), Alice, h => h.Add("Idempotency-Key", new string('k', 300)))).Body.ShouldContain("Idempotency-Key");

    // ── changes, versions ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_change_answers_204_with_the_new_version_and_a_guard_409_with_its_code()
    {
        var id = await _app.Created(Alice);

        var bid = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob);
        var low = await _app.Post($"/auctions/{id}/bids", """{"amount":120}""", Bob);
        var own = await _app.Post($"/auctions/{id}/bids", """{"amount":500}""", Alice);

        bid.Status.ShouldBe(HttpStatusCode.NoContent);
        bid.ETag.ShouldBe("\"2\"");
        low.Status.ShouldBe(HttpStatusCode.Conflict);
        low.Body.ShouldContain("\"code\":\"Bid.TooLow\"");
        own.Body.ShouldContain("Bid.OwnAuction");
    }

    [Fact]
    public async Task If_match_stale_is_409_malformed_400_current_204()
    {
        var id = await _app.Created(Alice);

        var stale = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"7\"")));
        var malformed = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.TryAddWithoutValidation("If-Match", "W/\"x\""));
        var current = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));

        stale.Status.ShouldBe(HttpStatusCode.Conflict);
        stale.Body.ShouldContain(StoreErrors.VersionConflictCode);
        malformed.Status.ShouldBe(HttpStatusCode.BadRequest);
        malformed.Body.ShouldContain("\"If-Match\":[\"Format\"]");
        current.Status.ShouldBe(HttpStatusCode.NoContent);
        current.ETag.ShouldBe("\"2\"");
    }

    [Fact]
    public async Task A_lifecycle_step_twice_is_409_and_a_change_without_a_body_binds_from_the_route()
    {
        var id = await _app.Created(Alice);

        var close = await _app.Post($"/auctions/{id}/close", null, Alice);
        var twice = await _app.Post($"/auctions/{id}/close", null, Alice);

        close.Status.ShouldBe(HttpStatusCode.NoContent);
        twice.Status.ShouldBe(HttpStatusCode.Conflict);
        twice.Body.ShouldContain("\"code\":\"AuctionStatus.NotAllowed\"");
    }

    [Fact]
    public async Task A_bad_route_id_is_400_and_an_unknown_one_404()
    {
        var bad = await _app.Post("/auctions/not-a-guid/close", null, Alice);
        var unknown = await _app.Post($"/auctions/{Guid.CreateVersion7()}/close", null, Alice);

        bad.Status.ShouldBe(HttpStatusCode.BadRequest);
        bad.Body.ShouldContain("\"auction\":[\"Format\"]");
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Body.ShouldContain("Auction.NotFound");
    }

    // ── authorization ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_row_rule_refuses_another_seller_with_its_code_and_before_the_version_is_compared()
    {
        var id = await _app.Created(Alice);

        var refused = await _app.Post($"/auctions/{id}/close", null, Bob);
        var stale = await _app.Post($"/auctions/{id}/close", null, Bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"9\"")));

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.Body.ShouldContain("\"code\":\"Auction.NotSeller\"");
        stale.Status.ShouldBe(HttpStatusCode.Forbidden);                               // R-2: no 409 "found 1" for a refused caller
    }

    [Fact]
    public async Task No_granting_role_is_403_and_an_admin_of_the_tenant_passes_the_row_rule()
    {
        var id = await _app.Created(Alice);

        var noRole = await _app.Post("/auctions", Auction(), new Caller("nora", TenantA));
        var touch = await _app.Post($"/auctions/{id}/touch", null, Alice);
        var admin = await _app.Post($"/auctions/{id}/close", null, Ada);

        noRole.Status.ShouldBe(HttpStatusCode.Forbidden);
        noRole.Body.ShouldContain("Auth.Forbidden");
        touch.Status.ShouldBe(HttpStatusCode.Forbidden);                               // nothing grants TouchAuction: fail closed
        admin.Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Another_tenants_aggregate_is_404_and_a_route_tenant_must_be_the_callers()
    {
        var id = await _app.Created(Alice);

        var close = await _app.Post($"/auctions/{id}/close", null, Carol);
        var view = await _app.Get($"/auctions/{id}", Carol);

        close.Status.ShouldBe(HttpStatusCode.NotFound);
        view.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── context ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_context_comes_from_the_principal_with_a_new_correlation_per_request()
    {
        var id = await _app.Created(Alice);
        _ = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob);
        _ = await _app.Post($"/auctions/{id}/watch", """{"note":"svc"}""", new Caller("billing", TenantA, Client: "billing"));

        var mine = _app.Store.Recorded.Where(e => e.StreamId == Guid.Parse(id)).Select(e => e.Context).ToArray();
        mine[0].Actor.ShouldBe(Actor.User("alice"));
        mine[1].Actor.ShouldBe(Actor.User("bob"));
        mine[2].Actor.ShouldBe(Actor.Service("billing"));                                // sub == client_id
        mine.Select(c => c.CorrelationId).Distinct().Count().ShouldBe(3);
        mine.ShouldAllBe(c => c.Tenant.ToString().Contains(TenantA.ToString()));
    }

    [Fact]
    public async Task A_route_group_maps_the_same_entries_under_its_prefix()
    {
        var created = await _app.Post("/v1/auctions", Auction(), Alice);

        created.Status.ShouldBe(HttpStatusCode.Created);
        created.Response.Headers.Location!.ToString().ShouldStartWith("/v1/auctions/");
    }

    // ── views ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_view_answers_200_with_etag_and_304_for_if_none_match()
    {
        var id = await _app.Created(Alice);
        _ = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob);

        var view = await _app.Get($"/auctions/{id}", Rita);
        var same = await _app.Get($"/auctions/{id}", Rita, h => h.IfNoneMatch.Add(new EntityTagHeaderValue("\"2\"")));

        view.Status.ShouldBe(HttpStatusCode.OK);
        view.ETag.ShouldBe("\"2\"");
        view.Body.ShouldContain("\"highestBid\":150");
        view.Body.ShouldNotContain("reserve");
        same.Status.ShouldBe(HttpStatusCode.NotModified);
    }

    [Theory]                                                               // BC-7: RFC 9110 §13.1.2, the weak comparison
    [InlineData("W/\"2\"", HttpStatusCode.NotModified)]
    [InlineData("\"1\", \"2\"", HttpStatusCode.NotModified)]
    [InlineData("\"1\",W/\"2\"", HttpStatusCode.NotModified)]
    [InlineData("*", HttpStatusCode.NotModified)]
    [InlineData("\"1\", W/\"3\"", HttpStatusCode.OK)]
    [InlineData("\"20\"", HttpStatusCode.OK)]
    public async Task A_view_matches_if_none_match_weakly_and_in_lists(string ifNoneMatch, HttpStatusCode expected)
    {
        var id = await _app.Created(Alice);
        _ = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob);

        var view = await _app.Get($"/auctions/{id}", Rita, h => h.TryAddWithoutValidation("If-None-Match", ifNoneMatch));

        view.Status.ShouldBe(expected);
        view.ETag.ShouldBe("\"2\"");
    }

    [Fact]
    public async Task A_view_is_authorized_by_role_or_ownership()
    {
        var id = await _app.Created(Alice);

        var owner = await _app.Get($"/auctions/{id}", new Caller("alice", TenantA, Scope: ""));
        var stranger = await _app.Get($"/auctions/{id}", new Caller("nora", TenantA));
        var anonymous = await _app.Get($"/auctions/{id}", Nobody);

        owner.Status.ShouldBe(HttpStatusCode.OK);
        stranger.Status.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.Status.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
