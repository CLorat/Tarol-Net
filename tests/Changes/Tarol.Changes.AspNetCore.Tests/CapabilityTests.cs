using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-50 (owner decision S-1): capability links, anonymous, tenant and token from the route, checked by ICapabilityCheck.</summary>
public sealed class CapabilityTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private LinkCheck Check => (LinkCheck)_app.App.Services.GetRequiredService<ICapabilityCheck>();

    [Fact]
    public async Task An_open_link_runs_the_change_anonymously_in_the_routes_tenant_as_the_checks_actor()
    {
        var auction = await _app.Created(Alice);

        var closed = await _app.Post($"/links/{TenantA}/open-sesame/close", $$"""{"auction":"{{auction}}"}""", Nobody);

        closed.Status.ShouldBe(HttpStatusCode.NoContent, closed.Body);
        var recorded = _app.Store.Recorded.Single(e => e.Event.Event is Auction.Closed);
        recorded.Context.Actor.ShouldBe(Actor.System("Links"));
        recorded.Context.Tenant.ShouldBe(Tarol.Options.Option.Some((Tarol.StronglyTypedId.Id<ITenant>)TenantA));
        var asked = Check.Asked.Single();
        (asked.Entry, asked.Input, asked.Tenant.Value, asked.Token).ShouldBe(("CloseAuctionByLink", typeof(CloseAuctionByLink), TenantA, "open-sesame"));
    }

    [Fact]
    public async Task The_checks_refusal_is_the_answer_and_nothing_runs()
    {
        var auction = await _app.Created(Alice);
        var body = $$"""{"auction":"{{auction}}"}""";

        var unknown = await _app.Post($"/links/{TenantA}/used/close", body, Nobody);
        var late = await _app.Post($"/links/{TenantA}/late/close", body, Nobody);
        var malformed = await _app.Post("/links/not-a-tenant/open-sesame/close", body, Nobody);

        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Body.ShouldContain("Link.NotFound");
        late.Status.ShouldBe(HttpStatusCode.Gone);
        malformed.Status.ShouldBe(HttpStatusCode.NotFound);
        malformed.Body.ShouldContain(ChangeEndpoints.CapabilityInvalidCode);
        _app.Store.Recorded.ShouldNotContain(e => e.Event.Event is Auction.Closed);
    }

    [Fact]
    public async Task A_link_of_another_tenant_finds_nothing_and_a_signed_in_caller_changes_nothing()
    {
        var auction = await _app.Created(Alice);
        var body = $$"""{"auction":"{{auction}}"}""";

        (await _app.Post($"/links/{TenantB}/open-sesame/close", body, Nobody)).Status.ShouldBe(HttpStatusCode.NotFound);   // tenant A's auction
        var signedIn = await _app.Post($"/links/{TenantA}/open-sesame/close", body, Carol);                                 // tenant B's user

        signedIn.Status.ShouldBe(HttpStatusCode.NoContent, signedIn.Body);                                                  // the link's tenant, the check's actor
        _app.Store.Recorded.Single(e => e.Event.Event is Auction.Closed).Context.Actor.ShouldBe(Actor.System("Links"));
    }

    [Fact]
    public async Task The_link_is_in_the_document_without_a_policy_and_an_idempotent_retry_replays()
    {
        var auction = await _app.Created(Alice);
        var body = $$"""{"auction":"{{auction}}"}""";
        void Key(System.Net.Http.Headers.HttpRequestHeaders h) => h.Add(TarolHeaders.IdempotencyKey, "k-1");

        (await _app.Post($"/links/{TenantA}/open-sesame/close", body, Nobody, Key)).Status.ShouldBe(HttpStatusCode.NoContent);
        var again = await _app.Post($"/links/{TenantA}/open-sesame/close", body, Nobody, Key);

        again.Status.ShouldBe(HttpStatusCode.NoContent, again.Body);
        again.Response.Headers.GetValues(TarolHeaders.IdempotentReplayed).Single().ShouldBe("true");
        var document = await _app.Get("/openapi/v1.json", Nobody);
        document.Body.ShouldContain("/links/{tenant}/{token}/close");
    }
}
