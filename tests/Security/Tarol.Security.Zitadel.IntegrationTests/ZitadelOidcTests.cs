using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Changes.AspNetCore.Tests;
using Tarol.Kernel;
using Tarol.Persistence;

namespace Tarol.Security.Zitadel.IntegrationTests;

/// <summary>The HTTP entries against a real Zitadel (R-17): discovery + JWKS, project roles into the AZ-D role map, the
/// organisation as the tenant, machine users with the service role as services, a foreign audience and a tampered token.</summary>
[Trait("Category", "Integration")]
public sealed class ZitadelOidcTests(Zitadel zitadel) : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>())
            .WithReactions(GeneratedReactions.Set)
            .UseInMemoryStore()
            .AddSecurity(roles =>
            {
                roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
                roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
                roles.Authenticated().May<WatchAuction>();
            })
            .AddEndpoints()
            .AddZitadel(o => (o.Authority, o.ProjectId, o.RequireHttpsMetadata) = (zitadel.Authority, zitadel.ProjectId, false));
        builder.Services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
        builder.Services.AddAuthorizationBuilder()                                   // with an IdP the role map decides (no scope claims)
            .AddPolicy("auctions:read", p => p.RequireAuthenticatedUser())
            .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser())
            .AddPolicy("auctions:bid", p => p.RequireAuthenticatedUser());
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapChanges();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _http = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> Post(string path, string? body, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string Auction() => $$"""{"title":"Zitadel","reserve":100,"endsAt":"{{TimeProvider.System.GetUtcNow().AddDays(7):O}}"}""";

    [Fact]
    public async Task Zitadel_tokens_reach_the_role_map_the_tenant_and_the_actor()
    {
        var alice = await zitadel.Token("alice");
        var created = await Post("/auctions", Auction(), alice);
        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        var id = System.Text.Json.JsonElement.Parse(created.Body).GetProperty("id").GetString()!;
        var store = _app.Services.GetRequiredService<InMemoryAggregateStore>();

        store.Recorded[^1].Context.Actor.ShouldBe(Actor.User(zitadel.Clients["alice"].UserId));
        store.Recorded[^1].Context.Tenant.IsSome.ShouldBeTrue();                          // the organisation, as an Id<ITenant>
        (await Post($"/auctions/{id}/bids", """{"amount":150}""", await zitadel.Token("bob"))).Status.ShouldBe(HttpStatusCode.NoContent);
        var refused = await Post($"/auctions/{id}/close", null, await zitadel.Token("bob"));
        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.Body.ShouldContain("Auction.NotSeller");
        (await Post("/auctions", Auction(), await zitadel.Token("nora"))).Status.ShouldBe(HttpStatusCode.Forbidden);   // no project role
        (await Post($"/auctions/{id}/watch", """{"note":"billing"}""", await zitadel.Token("billing"))).Status.ShouldBe(HttpStatusCode.NoContent);
        store.Recorded[^1].Context.Actor.ShouldBe(Actor.Service("billing"));
        (await Post($"/auctions/{id}/close", null, alice)).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_token_for_another_audience_and_a_tampered_token_are_401()
    {
        (await Post("/auctions", Auction(), await zitadel.Token("alice", audience: false))).Status.ShouldBe(HttpStatusCode.Unauthorized);
        var token = await zitadel.Token("alice");
        (await Post("/auctions", Auction(), token[..^4] + "AAAA")).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
