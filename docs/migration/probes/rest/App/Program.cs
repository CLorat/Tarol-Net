using System.Security.Claims;
using System.Text;
using ArtFlow;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Probe.Rest.App;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Security;
using Tarol.StronglyTypedId;

// modes: selftest (default) | bench | serve (the API) | worker (the reaction worker) | startup (serve, print the time to the first 200)
// PROBE_PG = connection string -> Npgsql store, else in-memory; PROBE_PROBLEMS=writer -> no AddProblemDetails (hand-written bodies)
var started = System.Diagnostics.Stopwatch.GetTimestamp();
var mode = args.FirstOrDefault() ?? "selftest";
var builder = WebApplication.CreateSlimBuilder(args);
if (Environment.GetEnvironmentVariable("PROBE_LOG") is null)
    builder.Logging.ClearProviders();
var url = Environment.GetEnvironmentVariable("PROBE_URL") ?? "http://127.0.0.1:5088";
builder.WebHost.UseUrls(url);
var pg = Environment.GetEnvironmentVariable("PROBE_PG") is { Length: > 0 } e ? e : builder.Configuration.GetConnectionString("tarol");

var oidc = Environment.GetEnvironmentVariable("PROBE_OIDC");                 // e.g. http://localhost:8080 (Zitadel)
var audience = Environment.GetEnvironmentVariable("PROBE_OIDC_AUDIENCE") is { Length: > 0 } aud ? aud
    : Environment.GetEnvironmentVariable("PROBE_OIDC_AUDIENCE_FILE") is { } file ? File.ReadAllText(file).Trim() : null;
// ── one registration (question 7) ──────────────────────────────────────────────────────────────────────────────────
var tarol = builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>())
    .WithReactions(GeneratedReactions.Set);
if (pg is null) tarol.UseInMemoryStore(); else tarol.UseNpgsqlStore(pg);
if (mode == "worker") tarol.AddReactionWorker();
tarol.AddEndpoints(o => { if (oidc is not null) o.Actor = ZitadelClaims.Actor; });
builder.Services.AddTarolSecurity(roles =>                                   // AZ-D: the app's roles mapped onto changes and reads
{
    roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
    roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
    roles.Role("reader").May<Read<Auction>>();
    roles.Role("admin").May<CloseAuction>().May<Read<Auction>>();
    roles.Authenticated().May<WatchAuction>();                                // TouchAuction: granted to nobody → 403
});
foreach (var handler in new IAuthorizationHandler[] { new CloseAuctionRule(), new CreateAuctionRule(), new ReadOwnAuction() })
    builder.Services.AddSingleton(handler);
if (Environment.GetEnvironmentVariable("PROBE_PROBLEMS") != "writer")
    builder.Services.AddProblemDetails();
builder.Services.Configure<WebhookOptions>(o =>
{
    o.Senders["payments"] = new() { Secrets = ["whsec_new", "whsec_old"] };                         // rotation: both accepted
    o.Senders[$"payments/{Tokens.TenantB}"] = new() { Secrets = ["whsec_b"], Tenant = Tokens.TenantB };   // a per-tenant URL
});

// ── authentication and coarse policies stay the app's (R-3) ─────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    if (oidc is not null)
    {
        o.Authority = oidc;                                                    // discovery + JWKS (RS256, kid)
        o.Audience = audience;                                                 // the Zitadel project id
        o.RequireHttpsMetadata = false;                                        // test container without TLS
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters { NameClaimType = "sub", RoleClaimType = "role" };
        return;
    }
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = Tokens.Issuer, ValidAudience = Tokens.Audience, IssuerSigningKey = Tokens.Key,
        NameClaimType = "sub", RoleClaimType = "role",
    };
});
if (oidc is not null)
    builder.Services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, ZitadelClaims>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("auctions:read", p => p.RequireAuthenticatedUser())
    // Zitadel's access tokens carry no "scope" claim: with an IdP the role map (AZ-D) decides, the endpoint only authenticates
    .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser().RequireAssertion(c => oidc is not null || Tokens.HasScope(c.User, "auctions:write")))
    .AddPolicy("auctions:bid", p => p.RequireAuthenticatedUser().RequireAssertion(c => oidc is not null || Tokens.HasScope(c.User, "auctions:bid")));
builder.Services.AddOpenApi();
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)              // under the Aspire AppHost
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService(mode == "worker" ? "worker" : "api"))
        .WithTracing(t => t.AddSource("Microsoft.AspNetCore", Tarol.Sagas.ReactionWorker.ActivitySourceName).AddOtlpExporter());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => "ok").AllowAnonymous();
app.MapOpenApi().AllowAnonymous();
app.MapChanges();                                                            // generated (candidate A of question 2)
app.MapGroup("/v1").MapChanges(names: "V1");                                 // the same entries in a route group (versioning)

// the hand-written minimal-API baseline: user code, so the Request Delegate Generator intercepts it
app.MapPost("/hand/auctions/{auction}/watch", async (Id<Auction> auction, [FromBody] WatchBody body, HttpContext http, [FromServices] ChangeEndpoints web) =>
{
    var (context, options, _) = web.Edge(http);
    var result = await web.Pipeline.Run(new WatchAuction(auction, body.Note), context, options, http.RequestAborted);
    if (!result.TryGetValue(out ChangeOutcome outcome))
        return Results.Conflict();
    http.Response.Headers.ETag = $"\"{outcome.Version}\"";
    return Results.NoContent();
}).RequireAuthorization();

switch (mode)
{
    case "oidc-selftest":
        await app.StartAsync();
        var oidcFails = await OidcSelfTest.Run(app, url, oidc!, audience!, Environment.GetEnvironmentVariable("PROBE_OIDC_CLIENTS")!);
        await app.StopAsync();
        return oidcFails;
    case "selftest":
        await app.StartAsync();
        var fails = await SelfTest.Run(app, url);
        await app.StopAsync();
        return fails;
    case "bench":
        await app.StartAsync();
        await Bench.Run(app, url, args.Skip(1).ToArray());
        await app.StopAsync();
        return 0;
    case "startup":
        await app.StartAsync();
        using (var probe = new HttpClient())
        {
            var addr = app.Urls.First();
            while (!(await probe.GetAsync(addr + "/health")).IsSuccessStatusCode) await Task.Delay(1);
        }
        Console.WriteLine($"startup to first 200: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms ({Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT"}, {(pg is null ? "in-memory" : "Npgsql")})");
        await app.StopAsync();
        return 0;
    default:
        await app.RunAsync();
        return 0;
}

/// <summary>Test tokens (a symmetric key: the app's JwtBearer validates them like an identity provider's).</summary>
internal static class Tokens
{
    public const string Issuer = "probe", Audience = "artflow";
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("probe-signing-key-0123456789abcdef-0123456789abcdef"));
    public static readonly Guid TenantA = Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0193b000-0000-7000-8000-00000000000b");

    public static string For(string sub, Guid? tenant, string scope = "auctions:write auctions:bid", string? role = null, string? client = null, string[]? roles = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = sub, ["scope"] = scope };
        if (tenant is { } t) claims["tenant"] = t.ToString();
        if (role is not null) claims["role"] = role;
        if (roles is not null) claims["role"] = roles;
        if (client is not null) claims["client_id"] = client;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    public static bool HasScope(ClaimsPrincipal user, string scope)
        => user.FindAll("scope").Any(c => c.Value.Split(' ').Contains(scope));
}
