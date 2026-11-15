using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace ArtFlow;

/// <summary>Every row of changes.md §6 end to end over HTTP, authorization, keys, If-Match, reads and webhooks.</summary>
internal static class SelfTest
{
    public static async Task<int> Run(WebApplication app, string url)
    {
        var fails = 0;
        void Check(bool ok, string what, object? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail is null ? "" : $": {detail}")}");
            if (!ok) fails++;
        }
        Console.WriteLine($"# {Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT"}, store {(Environment.GetEnvironmentVariable("PROBE_PG") is null ? "in-memory" : "Npgsql")}, "
                          + $"binding {(typeof(SelfTest).Assembly.GetType("Probe.Rest.App.GeneratedEndpoints")?.GetMethod("Bind_PlaceBid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) is null ? "B1 record" : "B2 members")}, "
                          + $"problems {(Environment.GetEnvironmentVariable("PROBE_PROBLEMS") == "writer" ? "hand-written" : "IProblemDetailsService")}");
        var denied = 0L;
        using var meters = new MeterListener();
        meters.InstrumentPublished = (i, l) => { if (i.Meter.Name == Tarol.Security.ChangeAuthorization.MeterName) l.EnableMeasurementEvents(i); };
        meters.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref denied, v));
        meters.Start();

        using var http = new HttpClient { BaseAddress = new Uri(url) };
        var alice = Tokens.For("alice", Tokens.TenantA, roles: ["seller", "bidder"]);
        var bob = Tokens.For("bob", Tokens.TenantA, roles: ["bidder", "seller"]);
        var reader = Tokens.For("rita", Tokens.TenantA, scope: "auctions:read", role: "reader");
        var carol = Tokens.For("carol", Tokens.TenantB, roles: ["seller", "bidder", "reader"]);
        var admin = Tokens.For("ada", Tokens.TenantA, role: "admin");

        async Task<(HttpStatusCode Status, string Body, HttpResponseMessage R)> Send(HttpMethod method, string path, string? body, string? token,
            Action<HttpRequestHeaders>? headers = null, string contentType = "application/json")
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            headers?.Invoke(request.Headers);
            var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
        }
        Task<(HttpStatusCode Status, string Body, HttpResponseMessage R)> Post(string path, string? body, string? token, Action<HttpRequestHeaders>? headers = null, string contentType = "application/json")
            => Send(HttpMethod.Post, path, body, token, headers, contentType);
        Task<(HttpStatusCode Status, string Body, HttpResponseMessage R)> Get(string path, string? token, Action<HttpRequestHeaders>? headers = null)
            => Send(HttpMethod.Get, path, null, token, headers);
        string S((HttpStatusCode Status, string Body, HttpResponseMessage R) r) => $"{(int)r.Status} {r.Body}";
        var ends = DateTimeOffset.UtcNow.AddDays(7).ToString("O");
        string Auction(string title = "Sunflowers", int reserve = 100) => $$"""{"title":"{{title}}","reserve":{{reserve}},"endsAt":"{{ends}}"}""";

        // ── creation, validation, content type, keys ─────────────────────────────────────────────────────────────
        var anon = await Post("/auctions", Auction(), null);
        Check(anon.Status == HttpStatusCode.Unauthorized && anon.R.Headers.WwwAuthenticate.ToString().StartsWith("Bearer"), "anonymous create: 401 + WWW-Authenticate: Bearer (endpoint policy)", $"{(int)anon.Status} {anon.R.Headers.WwwAuthenticate}");
        var noScope = await Post("/auctions", Auction(), reader);
        Check(noScope.Status == HttpStatusCode.Forbidden && noScope.Body.Length == 0, "create without the auctions:write scope: 403 from the endpoint policy, no body", S(noScope));
        var created = await Post("/auctions", Auction(), alice);
        Check(created.Status == HttpStatusCode.Created && created.R.Headers.ETag?.Tag == "\"1\"" && created.R.Headers.Location is not null && created.Body.Contains("\"version\":1"),
            "create: 201, Location, ETag \"1\", {id, version}", $"{S(created)} Location={created.R.Headers.Location}");
        var id = JsonDocument.Parse(created.Body).RootElement.GetProperty("id").GetString()!;
        Check(created.R.Headers.Location?.ToString() == $"/auctions/{id}", "Location = the GET route of the new auction", created.R.Headers.Location);
        var invalid = await Post("/auctions", """{"title":"x","reserve":0,"endsAt":"2030-01-01T00:00:00Z"}""", alice);
        Check(invalid.Status == HttpStatusCode.BadRequest && invalid.Body.Contains("\"title\":[\"Length\"]") && invalid.Body.Contains("\"reserve\":[\"Range\"]"),
            "invalid body: 400 problem details, all fields, JSON-cased paths", S(invalid));
        var tooSoon = await Post("/auctions", $$"""{"title":"Irises","reserve":10,"endsAt":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""", alice);
        Check(tooSoon.Status == HttpStatusCode.BadRequest && tooSoon.Body.Contains("\"endsAt\":[\"TooSoon\"]"), "factory guard on an input field: 400 endsAt", S(tooSoon));
        var malformed = await Post("/auctions", """{"title":"Sunflowers","reserve":"lots","endsAt":"2030-01-01T00:00:00Z"}""", alice);
        Check(malformed.Status == HttpStatusCode.BadRequest && malformed.Body.Contains("reserve"), "wrong JSON type: 400, the member named", S(malformed));
        var missing = await Post("/auctions", """{"title":"Sunflowers"}""", alice);
        Check(missing.Status == HttpStatusCode.BadRequest, "missing members: 400", S(missing));
        var notJson = await Post("/auctions", "{", alice);
        Check(notJson.Status == HttpStatusCode.BadRequest, "broken JSON: 400", S(notJson));
        var text = await Post("/auctions", "title=x", alice, contentType: "text/plain");
        Check(text.Status == HttpStatusCode.UnsupportedMediaType, "text/plain: 415 from routing (generated AcceptsMetadata)", S(text));
        var run = Guid.CreateVersion7().ToString("N");                     // keys are per run: the Npgsql database is shared by the runs
        var k1 = await Post("/auctions", Auction("Tulips"), alice, h => h.Add("Idempotency-Key", "k-1-" + run));
        var k2 = await Post("/auctions", Auction("Tulips"), alice, h => h.Add("Idempotency-Key", "k-1-" + run));
        Check(k1.Status == HttpStatusCode.Created && k2.Status == HttpStatusCode.OK && k1.Body == k2.Body && k2.R.Headers.Contains("Idempotent-Replayed"),
            "Idempotency-Key retry: 200 with the first outcome, Idempotent-Replayed", $"{S(k1)} / {S(k2)}");
        var k3 = await Post("/auctions", Auction("Roses", 999), alice, h => h.Add("Idempotency-Key", "k-1-" + run));
        Check(k3.Status == HttpStatusCode.OK && k3.Body == k1.Body, "the same key with ANOTHER body: replays the first outcome (no fingerprint; R-question)", S(k3));
        var k4 = await Post("/auctions", Auction("Tulips"), bob, h => h.Add("Idempotency-Key", "k-1-" + run));
        Check(k4.Status == HttpStatusCode.Created && k4.Body != k1.Body, "the same key from another caller: its own creation (scope = tenant, caller, route)", S(k4));
        var noRole = await Post("/auctions", Auction("Daisies"), Tokens.For("nora", Tokens.TenantA));
        Check(noRole.Status == HttpStatusCode.Forbidden && noRole.Body.Contains("Auth.Forbidden"), "AZ-D: scope ok, but no app role grants CreateAuction: 403", S(noRole));
        var limit = await Post("/auctions", Auction("Rembrandt", 500_000), alice);
        var dealer = await Post("/auctions", Auction("Rembrandt", 500_000), Tokens.For("dora", Tokens.TenantA, roles: ["seller", "dealer"]));
        Check(limit.Status == HttpStatusCode.Forbidden && limit.Body.Contains("Auction.ReserveLimit") && dealer.Status == HttpStatusCode.Created,
            "AZ-D: role grants the creation, a row rule on the NEW aggregate denies it (403 Auction.ReserveLimit); the dealer role passes", $"{S(limit)} / {(int)dealer.Status}");
        var v1 = await Post("/v1/auctions", Auction("Grouped"), alice);
        Check(v1.Status == HttpStatusCode.Created && v1.R.Headers.Location?.ToString().StartsWith("/v1/auctions/") == true, "route group /v1: the same entry, Location under the group", $"{S(v1)} {v1.R.Headers.Location}");

        // ── changes, guards, versions, authorization ─────────────────────────────────────────────────────────────
        var bid = await Post($"/auctions/{id}/bids", """{"amount":150}""", bob);
        Check(bid.Status == HttpStatusCode.NoContent && bid.R.Headers.ETag?.Tag == "\"2\"", "bid: 204, ETag \"2\"", $"{(int)bid.Status} {bid.R.Headers.ETag}");
        var low = await Post($"/auctions/{id}/bids", """{"amount":120}""", bob);
        Check(low.Status == HttpStatusCode.Conflict && low.Body.Contains("\"code\":\"Bid.TooLow\"") && low.R.Content.Headers.ContentType?.MediaType == "application/problem+json",
            "low bid: 409 problem details, code = the guard's code", S(low));
        var own = await Post($"/auctions/{id}/bids", """{"amount":500}""", alice);
        Check(own.Status == HttpStatusCode.Conflict && own.Body.Contains("Bid.OwnAuction"), "a domain rule about the caller stays a guard: 409 Bid.OwnAuction", S(own));
        var stale = await Post($"/auctions/{id}/bids", """{"amount":500}""", bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));
        Check(stale.Status == HttpStatusCode.Conflict && stale.Body.Contains("Stream.VersionConflict"), "If-Match stale: 409 Stream.VersionConflict", S(stale));
        var badMatch = await Post($"/auctions/{id}/bids", """{"amount":500}""", bob, h => h.TryAddWithoutValidation("If-Match", "W/\"x\""));
        Check(badMatch.Status == HttpStatusCode.BadRequest && badMatch.Body.Contains("If-Match"), "If-Match not a version: 400", S(badMatch));
        var current = await Post($"/auctions/{id}/bids", """{"amount":200}""", bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"2\"")));
        Check(current.Status == HttpStatusCode.NoContent && current.R.Headers.ETag?.Tag == "\"3\"", "If-Match current: 204, ETag \"3\"", $"{(int)current.Status} {current.R.Headers.ETag}");
        var bKey1 = await Post($"/auctions/{id}/bids", """{"amount":210}""", bob, h => h.Add("Idempotency-Key", "bid-1-" + run));
        var bKey2 = await Post($"/auctions/{id}/bids", """{"amount":210}""", bob, h => h.Add("Idempotency-Key", "bid-1-" + run));
        Check(bKey1.Status == HttpStatusCode.NoContent && bKey2.Status == HttpStatusCode.NoContent && bKey2.R.Headers.ETag?.Tag == "\"4\"" && bKey2.R.Headers.Contains("Idempotent-Replayed"),
            "change retried with its key: 204 again, same ETag, nothing appended", $"{bKey1.R.Headers.ETag} {bKey2.R.Headers.ETag}");
        var forbidden = await Post($"/auctions/{id}/close", null, bob);
        Check(forbidden.Status == HttpStatusCode.Forbidden && forbidden.Body.Contains("Auction.NotSeller"), "close by another user: 403 problem details, code from the handler", S(forbidden));
        var leak = await Post($"/auctions/{id}/close", null, bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));
        Check(leak.Status == HttpStatusCode.Conflict && leak.Body.Contains("found 4"), "TODAY: forbidden caller + stale If-Match gets 409 with the current version (pipeline order, R-2)", S(leak));
        var touch = await Post($"/auctions/{id}/touch", null, alice);
        Check(touch.Status == HttpStatusCode.Forbidden, "endpoint whose change no role grants and no rule allows: 403 (fail closed)", S(touch));
        var otherTenant = await Post($"/auctions/{id}/close", null, carol);
        Check(otherTenant.Status == HttpStatusCode.NotFound, "another tenant: 404 (never loaded, never authorized)", S(otherTenant));
        var badId = await Post("/auctions/not-a-guid/close", null, alice);
        Check(badId.Status == HttpStatusCode.BadRequest && badId.Body.Contains("\"auction\":[\"Format\"]"), "bad route id: 400 errors.auction", S(badId));
        var none = await Post($"/auctions/{Guid.CreateVersion7()}/close", null, alice);
        Check(none.Status == HttpStatusCode.NotFound && none.Body.Contains("Auction.NotFound"), "unknown id: 404 problem details", S(none));
        var adminClose = await Post($"/v1/auctions/{JsonDocument.Parse(v1.Body).RootElement.GetProperty("id").GetString()}/close", null, admin);
        Check(adminClose.Status == HttpStatusCode.NoContent, "admin of the tenant closes another seller's auction: 204 (role + row rule)", S(adminClose));
        var close = await Post($"/auctions/{id}/close", null, alice);
        Check(close.Status == HttpStatusCode.NoContent, "close by the seller: 204 (no body: all members from the route)", S(close));
        var twice = await Post($"/auctions/{id}/close", null, alice);
        Check(twice.Status == HttpStatusCode.Conflict && twice.Body.Contains("\"code\":\"AuctionStatus."), "close twice: 409 lifecycle code", S(twice));
        Check(Interlocked.Read(ref denied) >= 4, "denials counted on the Tarol.Security meter (tarol.authorization.denied)", Interlocked.Read(ref denied));

        // ── reads (R-1: GET by id) ────────────────────────────────────────────────────────────────────────────────
        var state = await Get($"/auctions/{id}", reader);
        Check(state.Status == HttpStatusCode.OK && state.R.Headers.ETag?.Tag == close.R.Headers.ETag?.Tag && state.Body.Contains("\"state\""), "GET state (G1): 200, ETag = version, the stored state JSON", $"{(int)state.Status} {state.R.Headers.ETag} {state.Body}");
        var notModified = await Get($"/auctions/{id}", reader, h => h.IfNoneMatch.Add(close.R.Headers.ETag!));
        Check(notModified.Status == HttpStatusCode.NotModified && notModified.Body.Length == 0, "GET If-None-Match current: 304", (int)notModified.Status);
        var summary = await Get($"/auctions/{id}/summary", reader);
        Check(summary.Status == HttpStatusCode.OK && summary.Body.Contains("\"highestBid\":210") && !summary.Body.Contains("reserve"), "GET view (G2): 200, the view's own shape (no reserve)", summary.Body);
        var getOther = await Get($"/auctions/{id}", carol);
        Check(getOther.Status == HttpStatusCode.NotFound, "GET another tenant's auction: 404", S(getOther));
        var ownRead = await Get($"/auctions/{id}/summary", alice);
        var noRead = await Get($"/auctions/{id}/summary", Tokens.For("nora", Tokens.TenantA));
        Check(ownRead.Status == HttpStatusCode.OK && noRead.Status == HttpStatusCode.Forbidden, "AZ-D: no reader role, but the seller reads their own auction (row grant): 200; a stranger: 403", $"{(int)ownRead.Status}/{(int)noRead.Status}");
        var getAnon = await Get($"/auctions/{id}", null);
        Check(getAnon.Status == HttpStatusCode.Unauthorized, "GET anonymous: 401", (int)getAnon.Status);

        // ── context: actor, correlation, trace ──────────────────────────────────────────────────────────────────────
        var feed = (IEventFeed)app.Services.GetRequiredService<IAggregateStore>();
        var recorded = new List<PositionedEvent>();
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, 100_000, default)) recorded.Add(e);
        var mine = recorded.Where(e => e.Event.StreamId == Guid.Parse(id)).ToArray();
        Check(mine[0].Event.Context.Actor is { Kind: ActorKind.User, Id: "alice" } && mine[1].Event.Context.Actor.Id == "bob", "actor from the token's sub (User alice, User bob)", $"{mine[0].Event.Context.Actor} / {mine[1].Event.Context.Actor}");
        Check(mine.Select(e => e.Event.Context.CorrelationId).Distinct().Count() == mine.Length, "a new correlation per request (no caller-provided id)", mine.Length);
        Check(mine.All(e => e.Event.Context.Tenant.TryGetValue(out Id<ITenant> t) && t.Value == Tokens.TenantA), "tenant from the token's tenant claim", mine[0].Event.Context.Tenant);
        var service = Tokens.For("svc-billing", Tokens.TenantA, client: "svc-billing");
        var bySvc = await Post($"/auctions/{id}/watch", """{"note":"svc"}""", service);
        recorded.Clear();
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, 100_000, default)) recorded.Add(e);
        Check(bySvc.Status == HttpStatusCode.NoContent && recorded[^1].Event.Context.Actor is { Kind: ActorKind.Service, Id: "svc-billing" }, "client credentials token (sub == client_id): Actor.Service", recorded[^1].Event.Context.Actor);
        if (app.Services.GetService<NpgsqlDataSource>() is { } db)
        {
            async Task<string?> Traced(ActivityTraceId trace)
            {
                _ = await Post($"/auctions/{id}/watch", """{"note":"traced"}""", bob, h => h.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01"));
                await using var cmd = db.CreateCommand("SELECT traceparent FROM tarol.events ORDER BY position DESC LIMIT 1");
                return await cmd.ExecuteScalarAsync() as string;
            }
            var unheard = await Traced(ActivityTraceId.CreateRandom());
            Check(unheard is null, "incoming traceparent, nobody listening to Microsoft.AspNetCore: no Activity, so no traceparent stored", unheard ?? "NULL");
            using var listener = new ActivityListener { ShouldListenTo = a => a.Name == "Microsoft.AspNetCore", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
            ActivitySource.AddActivityListener(listener);
            var trace = ActivityTraceId.CreateRandom();
            var stored = await Traced(trace);
            Check(stored?.Contains(trace.ToString()) == true, "with a listener (as OpenTelemetry adds): the caller's trace id continues into the event's traceparent column", stored);
        }

        // ── webhooks ────────────────────────────────────────────────────────────────────────────────────────────────
        string Envelope(string delivery, string version, string data, string type = "payment.succeeded") => $$"""{"id":"{{delivery}}","type":"{{type}}","version":"{{version}}","data":{{data}}}""";
        string V2(string auction, decimal amount = 150, string payment = "pay_1") => $$"""{"paymentId":"{{payment}}","reference":"{{auction}}","amount":{{amount}},"currency":"EUR"}""";
        Action<HttpRequestHeaders> Sign(string body, string secret = "whsec_new", long? at = null) => h =>
        {
            var ts = (at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
            h.Add("Webhook-Timestamp", ts);
            h.Add("Webhook-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "." + body))));
        };
        async Task<int> Events() { var n = 0; await foreach (var _ in feed.ReadAll(GlobalPosition.Start, 100_000, default)) n++; return n; }
        // the webhook route has no tenant: an auction of "no tenant" (a single-tenant app's), created with a token without a tenant claim
        var single = await Post("/auctions", Auction("Lilies"), Tokens.For("sam", null, role: "seller"));
        var sid = JsonDocument.Parse(single.Body).RootElement.GetProperty("id").GetString()!;
        var early = Envelope(run + "evt_0", "2", V2(sid));
        var w0 = await Post("/webhooks/payments", early, null, Sign(early));
        Check(w0.Status == HttpStatusCode.ServiceUnavailable && w0.R.Headers.GetValues("Webhook-Error").First() == "Auction.NotClosed", "payment for an open auction: Conflict → 503 (the sender retries, C-9)", $"{(int)w0.Status} {string.Join(",", w0.R.Headers.GetValues("Webhook-Error"))}");
        _ = await Post($"/auctions/{sid}/close", null, Tokens.For("sam", null, role: "seller"));
        var b1 = Envelope(run + "evt_1", "2", V2(sid));
        var w1 = await Post("/webhooks/payments", b1, null, Sign(b1));
        var before = await Events();
        var w2 = await Post("/webhooks/payments", b1, null, Sign(b1));
        Check(w1.Status == HttpStatusCode.OK && w2.Status == HttpStatusCode.OK && await Events() == before, "signed webhook: 200; redelivery of evt_1: 200, nothing written (delivery-id key)", $"{(int)w1.Status}/{(int)w2.Status}");
        recorded.Clear();
        await foreach (var e in feed.ReadAll(GlobalPosition.Start, 100_000, default)) recorded.Add(e);
        var paid = recorded.Last(e => e.Event.StreamId == Guid.Parse(sid));
        Check(paid.Event.Context.Actor is { Kind: ActorKind.Service, Id: "webhook:payments" }, "the webhook acts as Service(webhook:payments)", paid.Event.Context.Actor);
        var rotated = Envelope(run + "evt_1b", "2", V2(sid));
        var wOld = await Post("/webhooks/payments", rotated, null, Sign(rotated, "whsec_old"));
        Check(wOld.Status == HttpStatusCode.OK, "signed with the previous secret (rotation): 200; same payment id again = success, no event", (int)wOld.Status);
        var forged = await Post("/webhooks/payments", b1, null, Sign(b1, "wrong"));
        Check(forged.Status == HttpStatusCode.Unauthorized, "forged signature: 401", (int)forged.Status);
        var tampered = await Post("/webhooks/payments", b1.Replace("150", "1"), null, Sign(b1));
        Check(tampered.Status == HttpStatusCode.Unauthorized, "body changed after signing: 401", (int)tampered.Status);
        var staleHook = await Post("/webhooks/payments", b1, null, Sign(b1, at: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()));
        Check(staleHook.Status == HttpStatusCode.Unauthorized, "timestamp outside ±5 minutes: 401", (int)staleHook.Status);
        var unsigned = await Post("/webhooks/payments", b1, null);
        Check(unsigned.Status == HttpStatusCode.Unauthorized, "no signature: 401", (int)unsigned.Status);
        var refund = Envelope(run + "evt_2", "2", V2(sid), "payment.refunded");
        var unknownType = await Post("/webhooks/payments", refund, null, Sign(refund));
        Check(unknownType.Status == HttpStatusCode.Accepted, "unhandled type: 202 (acknowledged, no retry storm)", (int)unknownType.Status);
        var v3 = Envelope(run + "evt_3", "3", V2(sid));
        var unknownVersion = await Post("/webhooks/payments", v3, null, Sign(v3));
        Check(unknownVersion.Status == HttpStatusCode.UnprocessableEntity, "known type, unknown version 3: 422 (dead-letter at the sender; option)", (int)unknownVersion.Status);
        var second = await Post("/auctions", Auction("Poppies"), Tokens.For("sam", null, role: "seller"));
        var sid2 = JsonDocument.Parse(second.Body).RootElement.GetProperty("id").GetString()!;
        _ = await Post($"/auctions/{sid2}/close", null, Tokens.For("sam", null, role: "seller"));
        var old = Envelope(run + "evt_4", "1", $$"""{"id":"pay_9","order":"{{sid2}}","cents":12500}""");
        var wV1 = await Post("/webhooks/payments", old, null, Sign(old));
        Check(wV1.Status == HttpStatusCode.OK, "version 1 payload through its own From factory: 200", (int)wV1.Status);
        var other = Envelope(run + "evt_5", "2", V2(sid2, payment: "pay_10"));
        var wPaid = await Post("/webhooks/payments", other, null, Sign(other));
        Check(wPaid.Status == HttpStatusCode.ServiceUnavailable, "a second, different payment: guard Conflict → 503 (C-9: retried, then dead-lettered at the sender)", (int)wPaid.Status);
        var ghost = Envelope(run + "evt_6", "2", V2(Guid.CreateVersion7().ToString()));
        var wGhost = await Post("/webhooks/payments", ghost, null, Sign(ghost));
        Check(wGhost.Status == HttpStatusCode.ServiceUnavailable, "unknown reference: NotFound → 503 (S-7: may arrive before the auction)", (int)wGhost.Status);
        var zero = Envelope(run + "evt_7", "2", V2(sid2, 0, "pay_11"));
        var wZero = await Post("/webhooks/payments", zero, null, Sign(zero));
        Check(wZero.Status == HttpStatusCode.UnprocessableEntity, "payload mapped to an invalid input (amount 0): 422", (int)wZero.Status);
        var shape = Envelope(run + "evt_8", "2", """{"paymentId":7}""");
        var wShape = await Post("/webhooks/payments", shape, null, Sign(shape));
        Check(wShape.Status == HttpStatusCode.UnprocessableEntity, "data that does not match the contract record: 422", (int)wShape.Status);
        var big = Envelope(run + "evt_9", "2", $$"""{"pad":"{{new string('x', 70_000)}}"}""");
        var wBig = await Post("/webhooks/payments", big, null, Sign(big));
        Check(wBig.Status == HttpStatusCode.RequestEntityTooLarge, "body above 64 KiB: 413 before the signature is computed", (int)wBig.Status);
        var tb = await Post("/auctions", Auction("Orchids"), carol);
        var tbId = JsonDocument.Parse(tb.Body).RootElement.GetProperty("id").GetString()!;
        _ = await Post($"/auctions/{tbId}/close", null, carol);
        var forB = Envelope(run + "evt_10", "2", V2(tbId, payment: "pay_b"));
        var wB = await Post($"/webhooks/payments/{Tokens.TenantB}", forB, null, Sign(forB, "whsec_b"));
        var wBwrong = await Post($"/webhooks/payments/{Tokens.TenantB}", forB, null, Sign(forB));
        var wBnoTenant = await Post("/webhooks/payments", Envelope(run + "evt_11", "2", V2(tbId, payment: "pay_b2")), null, Sign(Envelope(run + "evt_11", "2", V2(tbId, payment: "pay_b2"))));
        Check(wB.Status == HttpStatusCode.OK && wBwrong.Status == HttpStatusCode.Unauthorized && wBnoTenant.Status == HttpStatusCode.ServiceUnavailable,
            "per-tenant URL: tenant B's secret loads tenant B's auction (200); the shared secret there: 401; the shared URL cannot see it (404 → 503)", $"{(int)wB.Status}/{(int)wBwrong.Status}/{(int)wBnoTenant.Status}");

        // ── OpenAPI ─────────────────────────────────────────────────────────────────────────────────────────────────
        var openapi = await Get("/openapi/v1.json", null);
        var paths = openapi.Status == HttpStatusCode.OK ? JsonDocument.Parse(openapi.Body).RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray() : [];
        Console.WriteLine($"INFO OpenAPI: {(int)openapi.Status}, {paths.Length} paths: {string.Join(" ", paths)}");
        Console.WriteLine($"INFO OpenAPI schemas: {(openapi.Status == HttpStatusCode.OK && JsonDocument.Parse(openapi.Body).RootElement.TryGetProperty("components", out var c) && c.TryGetProperty("schemas", out var s) ? string.Join(" ", s.EnumerateObject().Select(p => p.Name)) : "none")}");
        if (Environment.GetEnvironmentVariable("PROBE_OPENAPI_DUMP") is { } dump) await File.WriteAllTextAsync(dump, openapi.Body);

        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails;
    }
}
