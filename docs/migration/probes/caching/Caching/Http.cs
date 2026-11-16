using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.StronglyTypedId;

namespace Probe.Caching;

/// <summary>Caches authenticated GETs (what the default policy refuses); optionally varies by the tenant claim.</summary>
internal sealed class AuthenticatedPolicy(bool varyByTenant) : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        context.EnableOutputCaching = true; context.AllowCacheLookup = true; context.AllowCacheStorage = true; context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = TimeSpan.FromMinutes(1);
        var tenant = context.HttpContext.User.FindFirst("tenant")?.Value ?? "-";
        context.Tags.Add("tenant:" + tenant);
        if (varyByTenant) context.CacheVaryByRules.VaryByValues["tenant"] = tenant;
        return ValueTask.CompletedTask;
    }
    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;
    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;
}

internal static class Http
{
    public static int Calls;

    public static async Task<(WebApplication App, string Url)> Start(Action<WebApplication>? map = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOutputCache(o =>
        {
            o.AddPolicy("auth-novary", new AuthenticatedPolicy(false));
            o.AddPolicy("auth-vary", new AuthenticatedPolicy(true));
        });
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use(async (http, next) =>                                  // stand-in authentication: "Bearer <tenant>-<user>"
        {
            if (http.Request.Headers.Authorization.ToString() is { Length: > 7 } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                var parts = auth[7..].Split('-');
                http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", parts[0]), new Claim("sub", parts[1])], "probe"));
            }
            await next(http);
        });
        app.UseOutputCache();
        Task Body(HttpContext http)
        {
            Interlocked.Increment(ref Calls);
            return http.Response.WriteAsync($"tenant={http.User.FindFirst("tenant")?.Value ?? "-"} call={Calls}");
        }
        app.MapGet("/default", Body).CacheOutput();
        app.MapGet("/auth-novary", Body).CacheOutput("auth-novary");
        app.MapGet("/auth-vary", Body).CacheOutput("auth-vary");
        map?.Invoke(app);
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, url);
    }

    public static async Task<int> SelfTest()
    {
        Console.WriteLine($"# selftest http ({Lab.Runtime})");
        var c = new Checks("selftest http");
        var (app, url) = await Start();
        using var client = new HttpClient { BaseAddress = new Uri(url) };
        async Task<string> Get(string path, string? token = null, string? readAfter = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (readAfter is not null) req.Headers.Add("Tarol-Read-After", readAfter);
            using var res = await client.SendAsync(req);
            return await res.Content.ReadAsStringAsync();
        }

        Calls = 0;
        await Get("/default"); await Get("/default");
        c.Check("output cache, default policy: an anonymous GET is cached (handler once)", Calls == 1);
        Calls = 0;
        await Get("/default", "a-alice"); await Get("/default", "a-alice");
        c.Check("output cache, default policy: a GET with an Authorization header is never cached (handler twice)", Calls == 2);

        Calls = 0;
        var aBody = await Get("/auth-novary", "a-alice");
        var bBody = await Get("/auth-novary", "b-bob");
        c.Check("found: a policy that caches authenticated GETs without a vary rule answers tenant B with tenant A's body",
            Calls == 1 && bBody == aBody && aBody.Contains("tenant=a", StringComparison.Ordinal), bBody);

        Calls = 0;
        var a1 = await Get("/auth-vary", "a-alice");
        var b1 = await Get("/auth-vary", "b-bob");
        var a2 = await Get("/auth-vary", "a-carl");
        c.Check("varied by the tenant claim: tenants get their own entries; users of one tenant share one (no per-user rules may exist: QR-6)",
            Calls == 2 && b1.Contains("tenant=b", StringComparison.Ordinal) && a2 == a1);
        var before = Calls;
        var ra = await Get("/auth-vary", "a-alice", readAfter: "999999.1");
        c.Check("found: a cached answer ignores Tarol-Read-After (served from the cache without waiting or Tarol-Stale)", Calls == before && ra == a1);

        await app.Services.GetRequiredService<IOutputCacheStore>().EvictByTagAsync("tenant:a", default);
        await Get("/auth-vary", "a-alice"); await Get("/auth-vary", "b-bob");
        c.Check("EvictByTagAsync(tenant:a) drops tenant A's entry only (in this process; the default store is in memory)", Calls == before + 1);
        await app.StopAsync();
        return c.Done();
    }

    /// <summary>The list and view endpoints over the seeded bench schema, plain and cached.</summary>
    public static async Task Bench(string schema, int seconds)
    {
        var store = await Lab.Store(schema, fresh: false);
        var codec = Lab.Codec();
        var rows = Lab.ReadModels(schema).Store<AuctionRow>();
        await Lab.CheckpointTrigger(schema);
        await using var watch = new CheckpointWatch(Lab.Db, schema, rows, TimeSpan.FromSeconds(1));
        var pushed = new PageCache(rows, Validation.Pushed, Lab.Time, TimeSpan.FromMinutes(5), watch);
        var caching = new CachingStore(store, codec, Lab.Time, TimeSpan.FromMinutes(5));
        var ids = await Probe.Caching.Bench.Ids(schema, Lab.TenantA, 1000);
        var q = new ListQuery(Lab.TenantA) { Sort = "ends_at", Limit = 50 };
        var ctx = Lab.Context(Lab.TenantA);

        static bool NotModified(HttpContext http, string etag)
        {
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.ToString() == etag) { http.Response.StatusCode = 304; return true; }
            return false;
        }
        async Task WriteBody(HttpContext http, byte[] body)
        {
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = body.Length;
            await http.Response.Body.WriteAsync(body);
        }
        var (app, url) = await Start(map: a =>
        {
            a.MapGet("/lists/plain", async (HttpContext http) =>                      // QueryEndpoints.List: checkpoint, search, ETag/304, serialize
            {
                var cp = await rows.Checkpoint(http.RequestAborted);
                var page = (await rows.Search(q, http.RequestAborted)).GetValueOrThrow();
                http.Response.Headers["Tarol-Position"] = cp.Position.ToString();
                if (NotModified(http, page.ETag)) return;
                await WriteBody(http, PageCache.Serialize(page));
            });
            a.MapGet("/lists/cached", async (HttpContext http) =>                     // the page cache, checkpoint pushed
            {
                var ans = await pushed.Get(q, http.RequestAborted);
                if (NotModified(http, ans.ETag)) return;
                await WriteBody(http, ans.Body);
            });
            a.MapGet("/lists/output", async (HttpContext http) =>                     // output cache in front of the plain list (anonymous: upper bound)
            {
                var page = (await rows.Search(q, http.RequestAborted)).GetValueOrThrow();
                await WriteBody(http, PageCache.Serialize(page));
            }).CacheOutput(p => p.Expire(TimeSpan.FromMinutes(5)));
            a.MapGet("/views/plain/{n:int}", async (HttpContext http, int n) =>
            {
                var au = (await store.Load((Id<Auction>)ids[n % ids.Count], ctx, http.RequestAborted)).GetValueOrThrow();
                if (NotModified(http, $"\"{au.Version}\"")) return;
                await WriteBody(http, JsonSerializer.SerializeToUtf8Bytes(AuctionView.From(au), ProbeJson.Default.AuctionView));
            });
            a.MapGet("/views/cached/{n:int}", async (HttpContext http, int n) =>
            {
                var au = (await caching.Load((Id<Auction>)ids[n % ids.Count], ctx, http.RequestAborted)).GetValueOrThrow();
                if (NotModified(http, $"\"{au.Version}\"")) return;
                await WriteBody(http, JsonSerializer.SerializeToUtf8Bytes(AuctionView.From(au), ProbeJson.Default.AuctionView));
            });
        });
        using var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 256 }) { BaseAddress = new Uri(url) };
        string? listTag;
        using (var first = await client.GetAsync("/lists/plain")) listTag = first.Headers.ETag?.ToString();
        var viewTags = new Dictionary<int, string>();
        for (var n = 0; n < 100; n++) { using var r = await client.GetAsync($"/views/plain/{n}"); viewTags[n] = r.Headers.ETag!.ToString(); }
        var counter = 0;
        async Task Hit(string path, string? etag, HttpStatusCode expect)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var res = await client.SendAsync(req);
            await res.Content.ReadAsByteArrayAsync();
            if (res.StatusCode != expect) throw new InvalidOperationException($"{path}: {res.StatusCode}");
        }
        var cases = new (string Name, Func<Task> Op)[]
        {
            ("list, plain 200", () => Hit("/lists/plain", null, HttpStatusCode.OK)),
            ("list, plain 304", () => Hit("/lists/plain", listTag, HttpStatusCode.NotModified)),
            ("list, page cache 200", () => Hit("/lists/cached", null, HttpStatusCode.OK)),
            ("list, page cache 304", () => Hit("/lists/cached", listTag, HttpStatusCode.NotModified)),
            ("list, output cache 200 (anonymous)", () => Hit("/lists/output", null, HttpStatusCode.OK)),
            ("view, plain 200", () => { var n = Interlocked.Increment(ref counter) % 100; return Hit($"/views/plain/{n}", null, HttpStatusCode.OK); }),
            ("view, plain 304", () => { var n = Interlocked.Increment(ref counter) % 100; return Hit($"/views/plain/{n}", viewTags[n], HttpStatusCode.NotModified); }),
            ("view, aggregate cache 200", () => { var n = Interlocked.Increment(ref counter) % 100; return Hit($"/views/cached/{n}", null, HttpStatusCode.OK); }),
            ("view, aggregate cache 304", () => { var n = Interlocked.Increment(ref counter) % 100; return Hit($"/views/cached/{n}", viewTags[n], HttpStatusCode.NotModified); }),
        };
        Console.WriteLine("| HTTP case | clients | latency | throughput |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var (name, op) in cases)
            foreach (var n in new[] { 1, 16 })
            {
                var (lat, ops) = await Hybrid.Loop(n, seconds, op);
                Console.WriteLine($"| {name} | {n} | {Lab.Pct(lat.Select(x => x / 1000).ToList())} | {Lab.F(ops, "F0")} req/s |");
            }
        await app.StopAsync();
    }
}
