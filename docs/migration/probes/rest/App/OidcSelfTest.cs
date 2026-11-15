using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace ArtFlow;

/// <summary>The same app against a real identity provider (Zitadel in a container): discovery + JWKS, project roles mapped to
/// the AZ-D role map, the organisation as the tenant, machine users as services, foreign and tampered tokens.</summary>
internal static class OidcSelfTest
{
    public static async Task<int> Run(WebApplication app, string url, string issuer, string audience, string clientsFile)
    {
        var fails = 0;
        void Check(bool ok, string what, object? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail is null ? "" : $": {detail}")}");
            if (!ok) fails++;
        }
        Console.WriteLine($"# OIDC {Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT"}, store {(Environment.GetEnvironmentVariable("PROBE_PG") is null ? "in-memory" : "Npgsql")}, issuer {issuer}");
        var clients = File.ReadAllLines(clientsFile).Where(l => l.StartsWith("client ", StringComparison.Ordinal))
            .Select(l => l.Split(' ')).ToDictionary(p => p[1], p => (Id: p[2], Secret: p[3], User: p[4]));
        using var idp = new HttpClient { BaseAddress = new Uri(issuer) };
        async Task<string> Token(string name, bool withAudience = true)
        {
            var scope = "openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner" + (withAudience ? $" urn:zitadel:iam:org:project:id:{audience}:aud" : "");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/v2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = scope }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clients[name].Id}:{clients[name].Secret}")));
            using var response = await idp.SendAsync(request);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
        }
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        async Task<(HttpStatusCode Status, string Body, HttpResponseMessage R)> Send(HttpMethod method, string path, string? body, string token)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
        }
        var alice = await Token("alice");
        var bob = await Token("bob");
        var auction = $$"""{"title":"Zitadel","reserve":100,"endsAt":"{{DateTimeOffset.UtcNow.AddDays(7):O}}"}""";

        var created = await Send(HttpMethod.Post, "/auctions", auction, alice);
        Check(created.Status == HttpStatusCode.Created, "alice (Zitadel roles seller, bidder): create 201; keys from discovery + JWKS (RS256)", $"{(int)created.Status} {created.Body}");
        var id = JsonDocument.Parse(created.Body).RootElement.GetProperty("id").GetString()!;
        var feed = (IEventFeed)app.Services.GetRequiredService<IAggregateStore>();
        Tarol.Primitives.RecordedEvent? first = null;
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, 100_000, default))
            if (e.Event.StreamId == Guid.Parse(id)) { first = e.Event; break; }
        Check(first?.Context.Actor is { Kind: ActorKind.User } a && a.Id == clients["alice"].User, "actor = User(<Zitadel user id>) by the app's actor rule", first?.Context.Actor);
        Check(first?.Context.Tenant.TryGetValue(out Id<ITenant> t) == true, "tenant = the user's organisation (resourceowner claim), mapped to an Id<ITenant> by the claims transformation", first?.Context.Tenant);
        var bid = await Send(HttpMethod.Post, $"/auctions/{id}/bids", """{"amount":150}""", bob);
        Check(bid.Status == HttpStatusCode.NoContent, "bob (bidder): bid 204", (int)bid.Status);
        var close = await Send(HttpMethod.Post, $"/auctions/{id}/close", null, bob);
        Check(close.Status == HttpStatusCode.Forbidden && close.Body.Contains("Auction.NotSeller"), "bob (seller of other auctions): close alice's 403 Auction.NotSeller (row rule)", $"{(int)close.Status} {close.Body}");
        var nora = await Token("nora");
        var noRole = await Send(HttpMethod.Post, "/auctions", auction, nora);
        Check(noRole.Status == HttpStatusCode.Forbidden, "nora (no project role): create 403", (int)noRole.Status);
        var read = await Send(HttpMethod.Get, $"/auctions/{id}/summary", null, await Token("rita"));
        var noRead = await Send(HttpMethod.Get, $"/auctions/{id}/summary", null, nora);
        Check(read.Status == HttpStatusCode.OK && noRead.Status == HttpStatusCode.Forbidden,
            "rita (reader): GET 200; nora (same organisation, no role): 403", $"{(int)read.Status}/{(int)noRead.Status}");
        var billing = await Send(HttpMethod.Post, $"/auctions/{id}/watch", """{"note":"billing"}""", await Token("billing"));
        Tarol.Primitives.RecordedEvent? last = null;
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, 100_000, default)) last = e.Event;
        Check(billing.Status == HttpStatusCode.NoContent && last?.Context.Actor is { Kind: ActorKind.Service, Id: "billing" }, "machine user with role service: Actor.Service(client_id)", $"{(int)billing.Status} {last?.Context.Actor}");
        var foreign = await Send(HttpMethod.Post, "/auctions", auction, await Token("alice", withAudience: false));
        Check(foreign.Status == HttpStatusCode.Unauthorized, "token without this project in aud: 401", (int)foreign.Status);
        var parts = alice.Split('.');
        var tampered = await Send(HttpMethod.Post, "/auctions", auction, $"{parts[0]}.{parts[1]}.{parts[2][..^4]}AAAA");
        Check(tampered.Status == HttpStatusCode.Unauthorized, "tampered signature: 401", (int)tampered.Status);
        var own = await Send(HttpMethod.Post, $"/auctions/{id}/close", null, alice);
        Check(own.Status == HttpStatusCode.NoContent, "alice closes her own auction: 204", (int)own.Status);
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails;
    }
}
