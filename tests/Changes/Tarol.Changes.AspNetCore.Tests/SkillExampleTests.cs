using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Security;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>The example of skills/tarol-endpoints/SKILL.md, verbatim (code identical, comments included).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_maps_every_entry()
    {
        // ── verbatim ──
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
            .UseInMemoryStore()                                    // .UseNpgsqlStore(connectionString) in production
            .AddSecurity(roles =>
            {
                roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
                roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
            })
            .AddEndpoints();
        builder.Services.AddAuthentication().AddJwtBearer();       // authentication stays the app's
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser())
            .AddPolicy("auctions:bid", p => p.RequireAuthenticatedUser())
            .AddPolicy("auctions:read", p => p.RequireAuthenticatedUser());
        builder.Services.AddOpenApi(o => o.AddTarolEndpoints());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        app.MapChanges();                                          // every [Endpoint] and [Webhook] of this assembly
        // ── end ──

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToArray();
        routes.ShouldContain("/auctions/{auction}/bids");
        routes.ShouldContain("/auctions/{auction}");
        routes.ShouldContain("/webhooks/payments");
        await app.DisposeAsync();
    }
}
