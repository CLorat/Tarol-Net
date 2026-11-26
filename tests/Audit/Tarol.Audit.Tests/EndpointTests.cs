using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Security;
using Tarol.StronglyTypedId;

namespace Tarol.Audit.Tests;

/// <summary>MapAudit (TN-64, AU-4): the caller's tenant's log over HTTP, filters, cursor, field errors, the policy.</summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("0196f000-0000-7000-8000-0000000000aa");
    private static readonly Guid TenantB = Guid.Parse("0196f000-0000-7000-8000-0000000000bb");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private sealed class Scheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-User"].ToString() is not { Length: > 0 } user)
                return Task.FromResult(AuthenticateResult.NoResult());
            List<Claim> claims = [new("sub", user), new("tenant", Request.Headers["X-Tenant"].ToString())];
            claims.AddRange(Request.Headers["X-Roles"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role")), "test")));
        }
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddTarol(AuditJson.Default, codec => codec.Add<Child>()).UseInMemoryStore().AddSecurity(_ => { }).AddEndpoints()
            .AddAudit(a => a.Add<Child>()).UseInMemoryAudit();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Scheme>("test", null);
        builder.Services.AddAuthorizationBuilder().AddPolicy("audit:read", p => p.RequireRole("dpo"));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapAudit("/protokoll", "audit:read");
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();

        var store = _app.Services.GetRequiredService<IAggregateStore>();
        foreach (var (tenant, name) in new[] { (TenantA, "Mia"), (TenantA, "Leo"), (TenantB, "Ida") })
        {
            var context = OperationContext.Start(Actor.User("anna"), Id<ITenant>.From(tenant), TimeProvider.System);
            (await store.Append(Child.Register(Id<Child>.New(), name, "Bären"), 0, context, Option.None, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        }
        await _app.Services.GetRequiredService<AuditHost>().CatchUp(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> Get(string path, string roles = "dpo", Guid? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-User", "dora");
        request.Headers.Add("X-Roles", roles);
        request.Headers.Add("X-Tenant", (tenant ?? TenantA).ToString());
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_log_lists_the_callers_tenant_newest_first_with_filters_and_a_cursor()
    {
        var all = await Get("/protokoll");
        var names = await Get("/protokoll?field=firstName&limit=1");
        var next = JsonElement.Parse(names.Body).GetProperty("next").GetString();
        var second = await Get($"/protokoll?field=firstName&limit=1&after={next}");
        var b = await Get("/protokoll", tenant: TenantB);

        all.Status.ShouldBe(HttpStatusCode.OK, all.Body);
        var items = JsonElement.Parse(all.Body).GetProperty("items");
        items.GetArrayLength().ShouldBe(4);
        items[0].GetProperty("kind").GetString().ShouldBe("Created");
        items[0].GetProperty("actor").GetProperty("id").GetString().ShouldBe("anna");
        JsonElement.Parse(names.Body).GetProperty("items")[0].GetProperty("after").GetString().ShouldBe("Leo");   // JSON value, not text
        JsonElement.Parse(second.Body).GetProperty("items")[0].GetProperty("after").GetString().ShouldBe("Mia");
        JsonElement.Parse(second.Body).GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
        JsonElement.Parse(b.Body).GetProperty("items").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Bad_values_are_field_errors_and_the_policy_guards_the_log()
    {
        var bad = await Get("/protokoll?target=x&kind=Moved&from=yesterday&limit=many");
        var cursor = await Get("/protokoll?after=nope");
        var denied = await Get("/protokoll", roles: "caregiver");

        bad.Status.ShouldBe(HttpStatusCode.BadRequest);
        foreach (var field in new[] { "target", "kind", "from", "limit" })
            bad.Body.ShouldContain($"\"{field}\"");
        cursor.Status.ShouldBe(HttpStatusCode.BadRequest);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
    }
}
