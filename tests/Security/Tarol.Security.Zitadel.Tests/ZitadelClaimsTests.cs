using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Kernel;

namespace Tarol.Security.Zitadel.Tests;

/// <summary>Zitadel's token shape, as Zitadel v4.20.3 issued it (docs/migration/probes/rest/output.txt §11).</summary>
public sealed class ZitadelClaimsTests
{
    private const string Project = "399724483650256899";
    private const string Organisation = "399724476671000579";

    private static ClaimsPrincipal Token(params (string Type, string Value)[] extra)
    {
        List<Claim> claims =
        [
            new("sub", "399724483734142979"), new("client_id", "alice"), new("aud", Project), new("iss", "http://localhost:8080"),
            new($"urn:zitadel:iam:org:project:{Project}:roles",
                $$$"""{"bidder":{"{{{Organisation}}}":"zitadel.localhost"},"seller":{"{{{Organisation}}}":"zitadel.localhost"}}""", "JSON"),
            new("urn:zitadel:iam:user:resourceowner:id", Organisation),
        ];
        claims.AddRange(extra.Select(e => new Claim(e.Type, e.Value)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "AuthenticationTypes.Federation", "sub", "role"));
    }

    private static Task<ClaimsPrincipal> Transform(ClaimsPrincipal principal)
        => new ZitadelClaimsTransformation(new ZitadelOptions { Authority = "http://localhost:8080", ProjectId = Project }).TransformAsync(principal);

    [Fact]
    public async Task The_projects_roles_become_roles_and_the_organisation_the_tenant()
    {
        var user = await Transform(Token());

        user.IsInRole("seller").ShouldBeTrue();
        user.IsInRole("bidder").ShouldBeTrue();
        user.IsInRole("admin").ShouldBeFalse();
        user.FindFirst("tenant")!.Value.ShouldBe(ZitadelTenants.Of(Organisation).ToString());
    }

    [Fact]
    public async Task Transforming_twice_adds_nothing_twice()
    {
        var user = await Transform(await Transform(Token()));

        user.FindAll("role").Count().ShouldBe(2);
        user.FindAll("tenant").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Another_projects_roles_and_a_malformed_claim_are_ignored()
    {
        var user = await Transform(Token(("urn:zitadel:iam:org:project:999:roles", """{"admin":{}}"""), ("urn:zitadel:iam:org:project:roles", "not json")));

        user.IsInRole("admin").ShouldBeFalse();
        user.FindAll("role").Count().ShouldBe(2);
    }

    [Fact]
    public async Task The_generic_roles_claim_of_the_clients_own_project_is_not_read()
    {
        // a client of another project that asked for this project's audience: its own project's roles come in the generic claim
        var user = await Transform(Token(("urn:zitadel:iam:org:project:roles", $$$"""{"admin":{"{{{Organisation}}}":"zitadel.localhost"}}""")));

        user.IsInRole("admin").ShouldBeFalse();
        user.FindAll("role").Select(r => r.Value).Order().ShouldBe(["bidder", "seller"]);
    }

    [Fact]
    public async Task A_role_granted_in_another_organisation_is_not_a_role_in_the_users_tenant()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "1"),
            new Claim($"urn:zitadel:iam:org:project:{Project}:roles",
                $$$"""{"admin":{"111":"other.localhost"},"bidder":{"111":"other.localhost","{{{Organisation}}}":"zitadel.localhost"}}"""),
            new Claim("urn:zitadel:iam:user:resourceowner:id", Organisation),
        ], "AuthenticationTypes.Federation", "sub", "role"));

        var user = await Transform(principal);

        user.IsInRole("admin").ShouldBeFalse();
        user.IsInRole("bidder").ShouldBeTrue();
    }

    [Fact]
    public async Task The_users_organisation_replaces_a_tenant_claim_in_the_token()
    {
        var user = await Transform(Token(("tenant", Guid.NewGuid().ToString())));

        user.FindAll("tenant").ShouldHaveSingleItem().Value.ShouldBe(ZitadelTenants.Of(Organisation).ToString());
    }

    [Fact]
    public async Task An_anonymous_principal_is_left_alone()
        => (await Transform(new ClaimsPrincipal(new ClaimsIdentity()))).Claims.ShouldBeEmpty();

    [Fact]
    public void An_organisation_maps_to_the_same_tenant_everywhere()
    {
        ZitadelTenants.Of(Organisation).ShouldBe(ZitadelTenants.Of(Organisation));
        ZitadelTenants.Of(Organisation).ShouldNotBe(ZitadelTenants.Of("399724476671000580"));
        ZitadelTenants.Of(Organisation).Value.Version.ShouldBe(8);
    }

    [Fact]
    public async Task A_machine_user_with_the_service_role_is_a_service_any_other_a_user()
    {
        var rule = ZitadelTenants.Actors("service");

        rule(await Transform(Token())).ShouldBe(Actor.User("399724483734142979"));
        var machine = Token();
        ((ClaimsIdentity)machine.Identity!).AddClaim(new Claim("role", "service"));
        rule(machine).ShouldBe(Actor.Service("alice"));
        ZitadelTenants.Actors(null)(machine).ShouldBe(Actor.User("399724483734142979"));
    }

    [Fact]
    public void AddZitadel_configures_JwtBearer_the_transformation_and_the_actor_rule()
    {
        var services = new ServiceCollection();
        services.AddTarol(TestJson.Default, _ => { }).UseInMemoryStore().AddSecurity(_ => { }).AddEndpoints()
            .AddZitadel(o => (o.Authority, o.ProjectId, o.RequireHttpsMetadata) = ("http://localhost:8080", Project, false));
        using var provider = services.BuildServiceProvider();

        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        jwt.Authority.ShouldBe("http://localhost:8080");
        jwt.Audience.ShouldBe(Project);
        jwt.MapInboundClaims.ShouldBeFalse();
        jwt.TokenValidationParameters.RoleClaimType.ShouldBe("role");
        provider.GetRequiredService<IClaimsTransformation>().ShouldBeOfType<ZitadelClaimsTransformation>();
        provider.GetRequiredService<ChangeEndpointOptions>().Actor.ShouldNotBeNull();
    }

    [Fact]
    public void The_clock_skew_and_the_hub_path_are_options()                        // BC-9, BC-2
    {
        JwtBearerOptions Jwt(Action<ZitadelOptions> more)
        {
            var services = new ServiceCollection();
            services.AddTarol(TestJson.Default, _ => { }).UseInMemoryStore().AddSecurity(_ => { }).AddEndpoints()
                .AddZitadel(o => { (o.Authority, o.ProjectId, o.RequireHttpsMetadata) = ("http://localhost:8080", Project, false); more(o); });
            return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        }

        Jwt(_ => { }).TokenValidationParameters.ClockSkew.ShouldBe(TimeSpan.FromMinutes(5));      // JwtBearer's default
        Jwt(o => o.ClockSkew = TimeSpan.Zero).TokenValidationParameters.ClockSkew.ShouldBe(TimeSpan.Zero);
        var tarol = new ServiceCollection().AddTarol(TestJson.Default, _ => { }).UseInMemoryStore().AddEndpoints();
        Should.Throw<InvalidOperationException>(() => tarol.AddZitadel(o => (o.Authority, o.ProjectId, o.HubPath) = ("http://localhost:8080", Project, "tarol")))
            .Message.ShouldContain("HubPath");
        Should.Throw<InvalidOperationException>(() => tarol.AddZitadel(o => (o.Authority, o.ProjectId, o.ClockSkew) = ("http://localhost:8080", Project, TimeSpan.FromSeconds(-1))))
            .Message.ShouldContain("ClockSkew");
    }

    [Fact]
    public void AddZitadel_needs_AddEndpoints_first_and_complete_options()
    {
        var tarol = new ServiceCollection().AddTarol(TestJson.Default, _ => { }).UseInMemoryStore();

        Should.Throw<InvalidOperationException>(() => tarol.AddZitadel(o => (o.Authority, o.ProjectId) = ("http://localhost:8080", Project)))
            .Message.ShouldContain("AddEndpoints");
        Should.Throw<InvalidOperationException>(() => tarol.AddEndpoints().AddZitadel(o => o.ProjectId = Project)).Message.ShouldContain("Authority");
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
internal sealed partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;
