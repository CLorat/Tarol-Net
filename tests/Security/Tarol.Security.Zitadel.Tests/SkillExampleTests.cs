using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Changes.AspNetCore.Tests;

namespace Tarol.Security.Zitadel.Tests.Skill;

/// <summary>The example of skills/tarol-zitadel/SKILL.md, verbatim (code identical, comments included).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_wires_Zitadel_and_maps_the_entries()
    {
        // ── verbatim ──
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
            .UseInMemoryStore()                                    // .UseNpgsqlStore(connectionString) in production
            .AddSecurity(roles => roles.Role("seller").May<CreateAuction>().May<CloseAuction>())   // Zitadel project roles
            .AddEndpoints()
            .AddZitadel(zitadel =>
            {
                zitadel.Authority = "https://auth.example.com";
                zitadel.ProjectId = "399724483650256899";         // the project: the audience and the roles
            });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapChanges();
        // ── end ──

        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ShouldNotBeEmpty();
        app.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IClaimsTransformation>().ShouldBeOfType<ZitadelClaimsTransformation>();
        await app.DisposeAsync();
    }
}
