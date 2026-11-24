using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tarol.Persistence;
using Tarol.Security;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>ArtFlow over HTTP on a TestServer: a header test scheme (X-User, X-Roles, X-Tenant, X-Client), the role map and
/// rules, webhooks, OpenAPI; the entries mapped at the root and in a /v1 group.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public static readonly Guid TenantA = Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0193b000-0000-7000-8000-00000000000b");

    private TestApp(WebApplication app, HttpClient client) => (App, Client) = (app, client);

    public WebApplication App { get; }
    public HttpClient Client { get; }
    public InMemoryAggregateStore Store => App.Services.GetRequiredService<InMemoryAggregateStore>();

    public static async Task<TestApp> Start(bool problemDetailsService = true, Action<ChangeEndpointOptions>? endpoints = null,
        Action<TarolBuilder>? store = null, bool projections = true, bool search = false, Tarol.Search.ISearchIndex? searchIndex = null,
        string? corsOrigin = null, bool scopes = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        if (searchIndex is not null)
            builder.Services.AddSingleton(searchIndex);
        var tarol = builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Memo>())
            .WithReactions(GeneratedReactions.Set)
            .Store(store)
            .AddSecurity(roles =>
            {
                roles.Role("seller").May<CreateAuction>().May<CloseAuction>().May<RenameAuction>();
                roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>().May<Read<Memo>>();
                roles.Role("reader").May<Read<Auction>>();
                roles.Role("admin").May<CloseAuction>().May<Read<Auction>>();
                roles.Authenticated().May<WatchAuction>();
            })
            .AddEndpoints(endpoints)
            .Projections(projections);
        if (search)
            Tarol.Search.SearchTarolBuilderExtensions.UseInMemorySearch(Tarol.Search.SearchTarolBuilderExtensions.AddSearch(tarol, s =>
            {
                s.Add<Auction>().Add<Payout>().Add<Memo>();
                s.Projection.Poll = TimeSpan.FromMilliseconds(5);
            }));
        foreach (var rule in new IAuthorizationHandler[] { new CloseAuctionRule(), new CreateAuctionRule(), new ReadOwnAuction() })
            builder.Services.AddSingleton(rule);
        if (scopes)
            builder.Services.AddSingleton<FamilyScope>().AddSingleton<IListScope<FamilyAuctions>>(s => s.GetRequiredService<FamilyScope>());
        if (problemDetailsService)
            builder.Services.AddProblemDetails();
        builder.Services.Configure<WebhookOptions>(o =>
        {
            o.Senders["payments"] = new WebhookSender { Secrets = { "whsec_new", "whsec_old" } };
            o.Senders[$"payments/{TenantB}"] = new WebhookSender { Secrets = { "whsec_b" }, Tenant = TenantB };
        });
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderScheme>("test", null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("auctions:read", p => p.RequireAuthenticatedUser())
            .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser().RequireClaim("scope", "auctions:write"))
            .AddPolicy("auctions:bid", p => p.RequireAuthenticatedUser().RequireClaim("scope", "auctions:bid"));
        builder.Services.AddOpenApi(o => o.AddTarolEndpoints());

        if (corsOrigin is not null)                                            // the README's recipe (BC-1)
            builder.Services.AddCors(o => o.AddPolicy("browser", p => p.WithOrigins(corsOrigin).AllowAnyMethod().WithTarolHeaders()));
        var app = builder.Build();
        if (corsOrigin is not null)
            app.UseCors("browser");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        app.MapChanges();
        app.MapGroup("/v1").MapChanges(names: "V1");
        if (search)
            app.MapTarolSearch("/search", "auctions:read");
        app.MapTarolMe();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TestApp(app, app.GetTestClient());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    public sealed record Caller(string? User, Guid? Tenant, string Roles = "", string Scope = "auctions:write auctions:bid", string? Client = null);

    public static readonly Caller Alice = new("alice", TenantA, "seller bidder");
    public static readonly Caller Bob = new("bob", TenantA, "bidder seller");
    public static readonly Caller Rita = new("rita", TenantA, "reader", "auctions:read");
    public static readonly Caller Carol = new("carol", TenantB, "seller bidder reader");
    public static readonly Caller Ada = new("ada", TenantA, "admin");
    public static readonly Caller Nobody = new(null, null);

    public async Task<Answer> Send(HttpMethod method, string path, string? body, Caller caller, Action<HttpRequestHeaders>? headers = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = contentType.Length == 0
                ? new ByteArrayContent(Encoding.UTF8.GetBytes(body))                  // no Content-Type at all
                : new StringContent(body, Encoding.UTF8, contentType);
        if (caller.User is not null)
        {
            request.Headers.Add("X-User", caller.User);
            request.Headers.Add("X-Roles", caller.Roles);
            request.Headers.Add("X-Scope", caller.Scope);
            if (caller.Tenant is { } t)
                request.Headers.Add("X-Tenant", t.ToString());
            if (caller.Client is not null)
                request.Headers.Add("X-Client", caller.Client);
        }
        headers?.Invoke(request.Headers);
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return new Answer(response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public Task<Answer> Post(string path, string? body, Caller caller, Action<HttpRequestHeaders>? headers = null, string contentType = "application/json")
        => Send(HttpMethod.Post, path, body, caller, headers, contentType);

    public Task<Answer> Get(string path, Caller caller, Action<HttpRequestHeaders>? headers = null) => Send(HttpMethod.Get, path, null, caller, headers);

    public static string Auction(string title = "Sunflowers", int reserve = 100)
        => $$"""{"title":"{{title}}","reserve":{{reserve}},"endsAt":"{{TimeProvider.System.GetUtcNow().AddDays(7):O}}"}""";

    public async Task<string> Created(Caller caller, string title = "Sunflowers")
    {
        var created = await Post("/auctions", Auction(title), caller);
        created.Status.ShouldBe(System.Net.HttpStatusCode.Created, created.Body);
        return System.Text.Json.JsonElement.Parse(created.Body).GetProperty("id").GetString()!;
    }

    /// <summary>Signs a webhook body like the provider: HMAC-SHA256(secret, timestamp + "." + body).</summary>
    public static Action<HttpRequestHeaders> Sign(string body, string secret = "whsec_new", long? at = null) => h =>
    {
        var ts = (at ?? TimeProvider.System.GetUtcNow().ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Add("Webhook-Timestamp", ts);
        h.Add("Webhook-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "." + body))));
    };

    internal sealed class HeaderScheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-User"].ToString() is not { Length: > 0 } user)
                return Task.FromResult(AuthenticateResult.NoResult());
            List<Claim> claims = [new("sub", user)];
            claims.AddRange(Request.Headers["X-Roles"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));
            claims.AddRange(Request.Headers["X-Scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => new Claim("scope", s)));
            if (Request.Headers["X-Tenant"].ToString() is { Length: > 0 } tenant)
                claims.Add(new Claim("tenant", tenant));
            if (Request.Headers["X-Client"].ToString() is { Length: > 0 } client)
                claims.Add(new Claim("client_id", client));
            var identity = new ClaimsIdentity(claims, "test", "sub", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}

internal static class TestStores
{
    /// <summary>The auction list: read models always (the API reads them), the host only when <paramref name="hosted"/> (else it stays building).</summary>
    public static TarolBuilder Projections(this TarolBuilder builder, bool hosted)
    {
        if (!builder.Services.Any(static d => d.ServiceType == typeof(Tarol.Queries.IReadModels)))
            Tarol.Queries.QueriesTarolBuilderExtensions.UseInMemoryReadModels(builder);
        return hosted ? Tarol.Queries.QueriesTarolBuilderExtensions.AddProjections(builder, p => p.Add<AuctionRow>(o => o.Poll = TimeSpan.FromMilliseconds(5))) : builder;
    }

    public static TarolBuilder Store(this TarolBuilder builder, Action<TarolBuilder>? store)
    {
        if (store is null)
            return builder.UseInMemoryStore();
        store(builder);
        return builder;
    }
}

internal sealed record Answer(HttpResponseMessage Response, string Body)
{
    public System.Net.HttpStatusCode Status => Response.StatusCode;
    public string? ETag => Response.Headers.ETag?.Tag;
    public override string ToString() => $"{(int)Status} {Body}";
}
