using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Changes.AspNetCore.Tests;
using Tarol.Messaging;
using Tarol.Messaging.SignalR;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Search;
using Tarol.Security;

var failures = 0;
void Check(string name, bool ok, object? detail = null)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(ok || detail is null ? "" : $": {detail}")}");
    if (!ok)
        failures++;
}

var pg = Environment.GetEnvironmentVariable("TAROL_PG");
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var tarol = builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>()).WithReactions(GeneratedReactions.Set);
if (pg is { Length: > 0 })
    tarol.UseNpgsqlStore(pg, new NpgsqlStoreOptions { Schema = "aot_http" });
else
    tarol.UseInMemoryStore();
if (pg is { Length: > 0 })
    tarol.UseNpgsqlReadModels();
else
    tarol.UseInMemoryReadModels();
tarol.AddProjections(p => p.Add<AuctionRow>(o => o.Poll = TimeSpan.FromMilliseconds(20)));
tarol.AddSearch(s => { s.Add<Auction>(); s.Projection.Poll = TimeSpan.FromMilliseconds(20); }).UseInMemorySearch();   // step 18D
tarol.AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
    })
    .AddEndpoints();
// BC-25: the UI push's topics in the document (x-tarol-topics), the publication rows only (as the sample's API)
tarol.WithOutboxRows(GeneratedPublications.Set)
    .AddMessaging("artflow", m => m.UseInMemoryTransport().PushToUi(push =>
    {
        push.Forward("artflow.bid-placed");
        push.Rules.Subscribe("artflow.bid-placed");
    }));
builder.Services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
builder.Services.AddSingleton<IAuthorizationHandler, ReadOwnAuction>();
builder.Services.AddProblemDetails();
builder.Services.Configure<WebhookOptions>(o => o.Senders["payments"] = new WebhookSender { Secrets = { "whsec" } });
builder.Services.AddAuthentication("smoke").AddScheme<AuthenticationSchemeOptions, SmokeScheme>("smoke", null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("auctions:read", p => p.RequireAuthenticatedUser())
    .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser())
    .AddPolicy("auctions:bid", p => p.RequireAuthenticatedUser());
builder.Services.AddOpenApi(o => o.AddTarolEndpoints());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapChanges();
app.MapTarolMe();
app.MapTarolSearch("/search", "auctions:read");
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

async Task<(HttpStatusCode Status, string Body, HttpResponseMessage R)> Send(HttpMethod method, string path, string? body, string? user, Action<HttpRequestHeaders>? headers = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null)
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
    if (user is not null)
    {
        request.Headers.Add("X-User", user);
        request.Headers.Add("X-Tenant", user == "carol" ? "0193b000-0000-7000-8000-00000000000b" : "0193a000-0000-7000-8000-00000000000a");
    }
    headers?.Invoke(request.Headers);
    var response = await http.SendAsync(request);
    return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
}

var ends = TimeProvider.System.GetUtcNow().AddDays(7).ToString("O");
// a word of its own per run: TAROL_PG may be a database earlier runs wrote to, whose auctions would push this one off the page
var word = string.Concat(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 10));
var auction = $$"""{"title":"Sunflowers {{word}}","reserve":100,"endsAt":"{{ends}}"}""";
var key = "k-" + Guid.CreateVersion7().ToString("N");                               // per run: the database may be reused
var created = await Send(HttpMethod.Post, "/auctions", auction, "alice", h => h.Add("Idempotency-Key", key));
Check("creation: 201 with Location and ETag", created.Status == HttpStatusCode.Created && created.R.Headers.ETag?.Tag == "\"1\"" && created.R.Headers.Location is not null, created.Body);
var id = JsonElement.Parse(created.Body).GetProperty("id").GetString()!;
var replay = await Send(HttpMethod.Post, "/auctions", auction, "alice", h => h.Add("Idempotency-Key", key));
var reused = await Send(HttpMethod.Post, "/auctions", auction.Replace("Sunflowers", "Irises", StringComparison.Ordinal), "alice", h => h.Add("Idempotency-Key", key));
Check("key replay 200, reuse with another body 422", replay.Status == HttpStatusCode.OK && replay.Body == created.Body && reused.Status == HttpStatusCode.UnprocessableEntity, $"{replay.Status}/{reused.Status}");
var missing = await Send(HttpMethod.Post, "/auctions", """{"title":"x"}""", "alice");
Check("binder: every field, JSON-cased (Length, Required)", missing.Status == HttpStatusCode.BadRequest && missing.Body.Contains("\"reserve\":[\"Required\"]") && missing.Body.Contains("\"endsAt\":[\"Required\"]"), missing.Body);
Check("errorDetails through IProblemDetailsService natively (BC-5)", missing.Body.Contains("\"errorDetails\":{", StringComparison.Ordinal)
    && missing.Body.Contains("{\"code\":\"Required\"", StringComparison.Ordinal), missing.Body);
var bid = await Send(HttpMethod.Post, $"/auctions/{id}/bids", """{"amount":150}""", "bob");
var low = await Send(HttpMethod.Post, $"/auctions/{id}/bids", """{"amount":120}""", "bob");
Check("change 204 with ETag; guard 409 with its code", bid.Status == HttpStatusCode.NoContent && bid.R.Headers.ETag?.Tag == "\"2\"" && low.Status == HttpStatusCode.Conflict && low.Body.Contains("Bid.TooLow"), low.Body);
var stale = await Send(HttpMethod.Post, $"/auctions/{id}/bids", """{"amount":300}""", "bob", h => h.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));
Check("If-Match stale: 409", stale.Status == HttpStatusCode.Conflict, stale.Body);
var refused = await Send(HttpMethod.Post, $"/auctions/{id}/close", null, "bob");
Check("row rule: 403 with its code", refused.Status == HttpStatusCode.Forbidden && refused.Body.Contains("Auction.NotSeller"), refused.Body);
var foreign = await Send(HttpMethod.Post, $"/auctions/{id}/close", null, "carol");
Check("another tenant: 404", foreign.Status == HttpStatusCode.NotFound, foreign.Status);
var view = await Send(HttpMethod.Get, $"/auctions/{id}", null, "alice");
var notModified = await Send(HttpMethod.Get, $"/auctions/{id}", null, "alice", h => h.IfNoneMatch.Add(new EntityTagHeaderValue("\"2\"")));
Check("view 200 with ETag, 304 for If-None-Match", view.Status == HttpStatusCode.OK && view.Body.Contains("\"highestBid\":150") && notModified.Status == HttpStatusCode.NotModified, view.Body);
var position = bid.R.Headers.TryGetValues(Positions.PositionHeader, out var values) ? values.Single() : "";
var list = await Send(HttpMethod.Get, "/auctions?seller=alice&status=Open&count=true", null, "alice", h => h.Add(Positions.ReadAfterHeader, position));
var listed = list.Status == HttpStatusCode.OK ? JsonElement.Parse(list.Body) : default;
Check("list after the bid's Tarol-Position: the row, its ETag and the position", list.Status == HttpStatusCode.OK && position.Length > 0
    && listed.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetString() == id && i.GetProperty("bids").GetInt32() == 1)
    && list.R.Headers.ETag is { IsWeak: true } && list.R.Headers.Contains(Positions.PositionHeader) && !list.R.Headers.Contains(Positions.StaleHeader), list.Body);
var search = await Send(HttpMethod.Get, $"/search?q=sunflowers%20{word}&counts=true&snippets=true", null, "alice", h => h.Add(Positions.ReadAfterHeader, position));
var searched = search.Status == HttpStatusCode.OK ? JsonElement.Parse(search.Body) : default;
Check("search after the bid's Tarol-Position: the auction, counts, a weak ETag (step 18D)", search.Status == HttpStatusCode.OK
    && searched.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetString() == id)
    && searched.GetProperty("counts").EnumerateObject().Any() && search.R.Headers.ETag is { IsWeak: true }, search.Body);
var me = await Send(HttpMethod.Get, "/me", null, "alice");
Check("GET /me natively (BC-13)", me.Status == HttpStatusCode.OK && me.Body.Contains("\"subject\":\"alice\"", StringComparison.Ordinal), me.Body);
var page = await Send(HttpMethod.Get, "/auctions?limit=1&sort=Id", null, "alice");
var badCursor = await Send(HttpMethod.Get, "/auctions?after=x", null, "alice");
Check("list: cursor page, bad cursor 400", page.Status == HttpStatusCode.OK && badCursor.Status == HttpStatusCode.BadRequest && badCursor.Body.Contains("\"after\""), badCursor.Body);
var close = await Send(HttpMethod.Post, $"/auctions/{id}/close", null, "alice");
Check("close by the seller: 204", close.Status == HttpStatusCode.NoContent, close.Body);
var anonymous = await Send(HttpMethod.Post, "/auctions", auction, null);
Check("anonymous: 401", anonymous.Status == HttpStatusCode.Unauthorized, anonymous.Status);

string Signed(string body, out Action<HttpRequestHeaders> sign, string secret = "whsec")
{
    var ts = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "." + body)));
    sign = h => { h.Add("Webhook-Timestamp", ts); h.Add("Webhook-Signature", signature); };
    return body;
}
var payment = Signed($$$"""{"id":"evt-{{{key}}}","type":"payment.succeeded","version":"2","data":{"paymentId":"p1","reference":"{{{id}}}","amount":150,"currency":"EUR"}}""", out var sign);
var hook = await Send(HttpMethod.Post, "/webhooks/payments", payment, null, sign);
var forged = await Send(HttpMethod.Post, "/webhooks/payments", Signed(payment, out var wrong, "nope"), null, wrong);
var unknown = Signed("""{"id":"evt_2","type":"payment.refunded","data":{}}""", out var signUnknown);
var unknownType = await Send(HttpMethod.Post, "/webhooks/payments", unknown, null, signUnknown);
Check("webhook: signed (the shared URL has no tenant, the auction is tenant A's: 503), forged 401, unknown type 202",
    hook.Status == HttpStatusCode.ServiceUnavailable && forged.Status == HttpStatusCode.Unauthorized && unknownType.Status == HttpStatusCode.Accepted, $"{hook.Status}/{forged.Status}/{unknownType.Status}");

var openapi = await Send(HttpMethod.Get, "/openapi/v1.json", null, null);
var bids = JsonElement.Parse(openapi.Body).GetProperty("paths").GetProperty("/auctions/{auction}/bids").GetProperty("post");
Check("OpenAPI natively: the generated entry with its path parameter and 204",
    bids.GetProperty("parameters")[0].GetProperty("name").GetString() == "auction" && bids.GetProperty("responses").TryGetProperty("204", out _), openapi.Body.Length);
Check("OpenAPI natively: x-tarol-kind, x-tarol-errors, x-tarol-id and the TarolProblem schema (BC-3)",
    bids.GetProperty("x-tarol-kind").GetString() == "change" && bids.GetProperty("x-tarol-errors").GetArrayLength() > 0
    && bids.GetProperty("parameters")[0].GetProperty("x-tarol-id").GetString() == "Auction"
    && JsonElement.Parse(openapi.Body).GetProperty("components").GetProperty("schemas").TryGetProperty("TarolProblem", out _), openapi.Body.Length);
var ruled = JsonElement.Parse(openapi.Body).GetProperty("components").GetProperty("schemas");
Check("OpenAPI natively: x-tarol-rules on a value object, an input member and a multi-field value object (BC-4)",
    ruled.GetProperty("Title").GetProperty("x-tarol-rules")[0].GetProperty("max").GetInt32() == 60
    && ruled.GetProperty("MakeOffer").GetProperty("properties").GetProperty("contacts").GetProperty("x-tarol-rules")[0].GetProperty("rule").GetString() == "Count"
    && ruled.GetProperty("Price").GetProperty("properties").GetProperty("currency").GetProperty("x-tarol-rules")[0].GetProperty("flags").GetString() == "i",
    openapi.Body.Length);
var topic = JsonElement.Parse(openapi.Body).GetProperty("x-tarol-topics").GetProperty("artflow.bid-placed");
Check("OpenAPI natively: x-tarol-topics with the contract's version, stream and schema (BC-25)",
    topic.GetProperty("version").GetInt32() == 1 && topic.GetProperty("stream").GetString() == "Auction" && topic.GetProperty("subscribe").GetBoolean()
    && topic.GetProperty("schema").GetProperty("$ref").GetString() == "#/components/schemas/BidPlacedV1"
    && ruled.GetProperty("BidPlacedV1").GetProperty("properties").GetProperty("auction").GetProperty("format").GetString() == "uuid",
    topic.ToString());

await app.StopAsync();
Console.WriteLine(failures == 0 ? $"Tarol.Changes.AspNetCore AOT smoke ({(pg is { Length: > 0 } ? "Npgsql" : "in-memory")}): all checks passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

internal sealed class SmokeScheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-User"].ToString() is not { Length: > 0 } user)
            return Task.FromResult(AuthenticateResult.NoResult());
        Claim[] claims = [new("sub", user), new("tenant", Request.Headers["X-Tenant"].ToString()), new("role", "seller"), new("role", "bidder")];
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "smoke", "sub", "role")), "smoke")));
    }
}

/// <summary>BC-25: the publication behind the topic artflow.bid-placed.</summary>
internal static class Publications
{
    [Publishes<Bid.Placed>("artflow.bid-placed", 1)]
    public static BidPlacedV1 BidPlacedV1(Bid.Placed e, RecordedEvent r) => new(r.StreamId, e.Amount, e.At);
}
