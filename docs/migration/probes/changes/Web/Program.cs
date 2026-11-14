using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArtFlow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tarol.Changes;
using Tarol.Changes.Generated;
using Tarol.Changes.Web;
using Tarol.Kernel;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5077");
var json = ValueObjectJson.Options(AppJson.Default);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.TypeInfoResolverChain.Insert(0, ValueObjectJson.Resolver); o.SerializerOptions.TypeInfoResolverChain.Add(AppJson.Default); });
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddSingleton(new ChangeJson(json));
builder.Services.AddSingleton<InMemoryAggregateStore>();
builder.Services.AddSingleton<IAggregateStore>(s => s.GetRequiredService<InMemoryAggregateStore>());
builder.Services.AddSingleton(s => new ChangePipeline(s.GetRequiredService<IAggregateStore>(), time));
builder.Services.AddSingleton(new WebhookSecrets(new Dictionary<string, byte[]> { ["payments"] = "whsec_probe"u8.ToArray() }));
builder.Services.AddAuthentication("header").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("header", null);
builder.Services.AddAuthorizationBuilder().AddPolicy("auctions:write", p => p.RequireAuthenticatedUser());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapChanges();                                              // generated (candidate A)

// baseline: a hand-written lambda in user code, which the Request Delegate Generator intercepts
app.MapPost("/baseline/auctions/{auction}/close", async (Id<Auction> auction, HttpContext http, ChangePipeline pipeline, ChangeJson cj) =>
{
    var (context, options) = ChangeHttp.Context(http);
    await ChangeHttp.Write(http, await pipeline.Run(new CloseAuction(auction), context, options, http.RequestAborted), cj);
}).RequireAuthorization("auctions:write");

// candidate C: a central map in library code, generic over the input (RequestDelegate: AOT-safe; binding by a lambda)
CentralMap.MapChange(app, "/c/auctions/{auction}/close", http => new CloseAuction(Id<Auction>.Parse((string)http.Request.RouteValues["auction"]!, null)));
#if NAIVE
CentralMap.MapChangeNaive<CloseAuction>(app, "/naive/auctions/close");   // generic MapPost(Delegate): RDG cannot intercept
#endif

if (args is ["--selftest"])
{
    await app.StartAsync();
    var code = await SelfTest.Run(app.Services, time);
    await app.StopAsync();
    return code;
}
await app.RunAsync();
return 0;

[JsonSerializable(typeof(CreateAuction)), JsonSerializable(typeof(PlaceBid)), JsonSerializable(typeof(CloseAuction)), JsonSerializable(typeof(PaymentSucceededV2))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class AppJson : JsonSerializerContext;

internal static class CentralMap
{
    public static void MapChange<TChange>(IEndpointRouteBuilder app, string pattern, Func<HttpContext, TChange> bind)
        where TChange : IChange<Auction>
        => app.MapPost(pattern, async http =>
        {
            var (context, options) = ChangeHttp.Context(http);
            var pipeline = http.RequestServices.GetRequiredService<ChangePipeline>();
            await ChangeHttp.Write(http, await pipeline.Run(bind(http), context, options, http.RequestAborted), http.RequestServices.GetRequiredService<ChangeJson>());
        }).RequireAuthorization("auctions:write");

#if NAIVE
    public static void MapChangeNaive<TChange>(IEndpointRouteBuilder app, string pattern)
        where TChange : IChange<Auction>
        => app.MapPost(pattern, async (TChange input, HttpContext http, ChangePipeline pipeline) =>
        {
            var (context, options) = ChangeHttp.Context(http);
            return (await pipeline.Run(input, context, options, http.RequestAborted)).IsSuccess ? Results.NoContent() : Results.Conflict();
        });
#endif
}

internal sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-User"].ToString() is not { Length: > 0 } user)
            return Task.FromResult(AuthenticateResult.NoResult());
        List<Claim> claims = [new("sub", user)];
        if (Request.Headers["X-Tenant"].ToString() is { Length: > 0 } tenant) claims.Add(new("tenant", tenant));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "header")), "header")));
    }
}

internal static class SelfTest
{
    public static async Task<int> Run(IServiceProvider services, FakeTimeProvider time)
    {
        var fails = 0;
        void Check(bool ok, string what, object? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail is null ? "" : $": {detail}")}");
            if (!ok) fails++;
        }
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5077") };
        async Task<(HttpStatusCode Status, string Body, HttpResponseMessage R)> Post(string path, string? body, string? user = "alice", Action<HttpRequestHeaders>? headers = null, string contentType = "application/json")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
            if (user is not null) request.Headers.Add("X-User", user);
            headers?.Invoke(request.Headers);
            var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
        }
        var ends = time.GetUtcNow().AddDays(7).ToString("O");

        var anon = await Post("/auctions", $$"""{"title":"Sunflowers","reserve":100,"endsAt":"{{ends}}"}""", user: null);
        Check(anon.Status == HttpStatusCode.Unauthorized, "anonymous create: 401 from the endpoint policy", anon.Status);
        var created = await Post("/auctions", $$"""{"title":"Sunflowers","reserve":100,"endsAt":"{{ends}}"}""");
        Check(created.Status == HttpStatusCode.Created && created.Body.Contains("\"id\""), "create: 201 with the server-assigned id", $"{(int)created.Status} {created.Body}");
        var id = JsonDocument.Parse(created.Body).RootElement.GetProperty("id").GetString()!;
        var invalid = await Post("/auctions", $$"""{"title":"x","reserve":0,"endsAt":"{{ends}}"}""");
        Check(invalid.Status == HttpStatusCode.BadRequest && invalid.Body.Contains("\"title\":[\"Length\"]") && invalid.Body.Contains("\"reserve\":[\"Range\"]"),
            "invalid body: 400 problem details, all fields, JSON-cased paths", invalid.Body);
        var tooSoon = await Post("/auctions", $$"""{"title":"Irises","reserve":10,"endsAt":"{{time.GetUtcNow().AddHours(1):O}}"}""");
        Check(tooSoon.Status == HttpStatusCode.BadRequest && tooSoon.Body.Contains("\"endsAt\":[\"TooSoon\"]"), "factory guard on an input field: 400 endsAt", tooSoon.Body);
        var malformed = await Post("/auctions", """{"title":"Sunflowers","reserve":"lots"}""");
        Check(malformed.Status == HttpStatusCode.BadRequest, "malformed JSON: 400", malformed.Body);
        var text = await Post("/auctions", "title=x", contentType: "text/plain");
        Check(text.Status == HttpStatusCode.UnsupportedMediaType, "not JSON: 415 from routing (generated AcceptsMetadata)", text.Status);
        var k1 = await Post("/auctions", $$"""{"title":"Tulips","reserve":10,"endsAt":"{{ends}}"}""", headers: h => h.Add("Idempotency-Key", "k-1"));
        var k2 = await Post("/auctions", $$"""{"title":"Tulips","reserve":10,"endsAt":"{{ends}}"}""", headers: h => h.Add("Idempotency-Key", "k-1"));
        Check(k1.Status == HttpStatusCode.Created && k2.Status == HttpStatusCode.OK && k1.Body == k2.Body, "Idempotency-Key: the retry returns the first outcome (200, same id)", $"{(int)k1.Status} {k1.Body} / {(int)k2.Status} {k2.Body}");

        var bid = await Post($"/auctions/{id}/bids", """{"amount":150}""", user: "bob");
        Check(bid.Status == HttpStatusCode.NoContent && bid.R.Headers.ETag?.Tag == "\"2\"", "bid: 204 with ETag = version", $"{(int)bid.Status} {bid.R.Headers.ETag}");
        var low = await Post($"/auctions/{id}/bids", """{"amount":120}""", user: "carol");
        Check(low.Status == HttpStatusCode.Conflict && low.Body.Contains("\"code\":\"Bid.TooLow\""), "low bid: 409 with the guard's code", low.Body);
        var stale = await Post($"/auctions/{id}/bids", """{"amount":500}""", user: "carol", headers: h => h.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));
        Check(stale.Status == HttpStatusCode.Conflict && stale.Body.Contains("Stream.VersionConflict"), "If-Match stale: 409", stale.Body);
        var close = await Post($"/auctions/{id}/close", null);
        Check(close.Status == HttpStatusCode.NoContent, "close: 204 (no body needed: all members from the route)", close.Status);
        var twice = await Post($"/auctions/{id}/close", null);
        Check(twice.Status == HttpStatusCode.Conflict && twice.Body.Contains("AuctionStatus.NotAllowed"), "close twice: 409", twice.Body);
        var badId = await Post("/auctions/not-a-guid/close", null);
        Check(badId.Status == HttpStatusCode.BadRequest && badId.Body.Contains("\"auction\":[\"Format\"]"), "bad route id: 400 auction", badId.Body);
        var missing = await Post($"/auctions/{Guid.CreateVersion7()}/close", null);
        Check(missing.Status == HttpStatusCode.NotFound, "missing: 404", missing.Status);
        var otherTenant = await Post($"/auctions/{id}/close", null, headers: h => h.Add("X-Tenant", Guid.CreateVersion7().ToString()));
        Check(otherTenant.Status == HttpStatusCode.NotFound, "another tenant: 404", otherTenant.Status);
        var baseline = await Post($"/baseline/auctions/{id}/close", null);
        var central = await Post($"/c/auctions/{id}/close", null);
        Check(baseline.Status == HttpStatusCode.Conflict && central.Status == HttpStatusCode.Conflict, "baseline (RDG) and candidate C endpoints answer the same", $"{baseline.Status}/{central.Status}");

        // webhook: settle first (job), then the provider reports the payment
        time.Advance(TimeSpan.FromDays(8));
        var pipeline = services.GetRequiredService<ChangePipeline>();
        var settled = await pipeline.Run(new SettleAuction(Id<Auction>.Parse(id, null)), OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), Tarol.Options.Option.None, time));
        Check(settled.IsSuccess, "settled by the job", settled);
        string Envelope(string delivery, string type) => $$$"""{"id":"{{{delivery}}}","type":"{{{type}}}","data":{"paymentId":"pay_1","reference":"{{{id}}}","amount":150,"currency":"EUR"}}""";
        Action<HttpRequestHeaders> Sign(string body, string secret = "whsec_probe", long? at = null) => h =>
        {
            var ts = (at ?? time.GetUtcNow().ToUnixTimeSeconds()).ToString();
            h.Add("Webhook-Timestamp", ts);
            h.Add("Webhook-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "." + body))));
        };
        var store = services.GetRequiredService<InMemoryAggregateStore>();
        var body1 = Envelope("evt_1", "payment.succeeded");
        var w1 = await Post("/webhooks/payments", body1, user: null, headers: Sign(body1));
        var events = store.Log.Count;
        var w2 = await Post("/webhooks/payments", body1, user: null, headers: Sign(body1));
        Check(w1.Status == HttpStatusCode.OK && w2.Status == HttpStatusCode.OK && store.Log.Count == events, "signed webhook: 200; redelivery of evt_1: 200, nothing written", $"{w1.Status}/{w2.Status}");
        Check(store.Log[^1].Context.Actor is { Kind: ActorKind.Service, Id: "webhook:payments" }, "webhook acts as Service(webhook:payments)", store.Log[^1].Context.Actor);
        var forged = await Post("/webhooks/payments", body1, user: null, headers: Sign(body1, "wrong"));
        Check(forged.Status == HttpStatusCode.Unauthorized, "wrong signature: 401", forged.Status);
        var replayed = await Post("/webhooks/payments", body1, user: null, headers: Sign(body1, at: time.GetUtcNow().AddMinutes(-10).ToUnixTimeSeconds()));
        Check(replayed.Status == HttpStatusCode.Unauthorized, "timestamp outside 5 minutes: 401", replayed.Status);
        var tampered = await Post("/webhooks/payments", body1.Replace("150", "1"), user: null, headers: Sign(body1));
        Check(tampered.Status == HttpStatusCode.Unauthorized, "body changed after signing: 401", tampered.Status);
        var other = Envelope("evt_2", "payment.refunded");
        var unknown = await Post("/webhooks/payments", other, user: null, headers: Sign(other));
        Check(unknown.Status == HttpStatusCode.Accepted, "unhandled type: 202 (ack, no retry storm)", unknown.Status);
        var late = Envelope("evt_3", "payment.succeeded");
        var w3 = await Post("/webhooks/payments", late, user: null, headers: Sign(late));
        Check(w3.Status == HttpStatusCode.OK, "a new delivery the guard rejects (already paid): 200 ack", w3.Status);

        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails;
    }
}
