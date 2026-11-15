using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace ArtFlow;

/// <summary>Requests/s and p50/p99: the generated endpoint vs the hand-written minimal-API endpoint, same change, same
/// authentication (JWT), same pipeline. Client and server share the machine's cores (loopback, keep-alive).</summary>
internal static class Bench
{
    public static async Task Run(WebApplication app, string url, string[] args)
    {
        var seconds = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 5;
        var clients = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 16;
        var pipeline = app.Services.GetRequiredService<ChangePipeline>();
        var context = OperationContext.Start(Actor.User("alice"), (Id<ITenant>)Tokens.TenantA, TimeProvider.System);
        var ids = new string[clients];
        for (var i = 0; i < clients; i++)
        {
            var created = await pipeline.Run(new CreateAuction(new Title("Bench"), new Amount(10), DateTimeOffset.UtcNow.AddDays(7)), context);
            ids[i] = created.GetValueOrThrow().StreamId.ToString();
        }
        var token = Tokens.For("bob", Tokens.TenantA);
        var store = Environment.GetEnvironmentVariable("PROBE_PG") is null ? "in-memory" : "Npgsql";
        var runtime = Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";
        Console.WriteLine($"| endpoint | runtime | store | clients | requests/s | p50 ms | p99 ms | errors |");
        Console.WriteLine($"|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var round in new[] { "warmup", "warmup", "measure" })   // JIT: the first long run still tiers up (verified: order decided the winner)
            foreach (var (name, path) in Environment.GetEnvironmentVariable("PROBE_BENCH_ORDER") == "hand-first"
                         ? new[] { ("hand-written (RDG)", "/hand/auctions/{0}/watch"), ("generated", "/auctions/{0}/watch") }
                         : new[] { ("generated", "/auctions/{0}/watch"), ("hand-written (RDG)", "/hand/auctions/{0}/watch") })
            {
                var (rps, p50, p99, errors) = await Load(url, path, ids, token, round == "warmup" ? 3 : seconds);
                if (round == "measure")
                    Console.WriteLine($"| {name} | {runtime} | {store} | {clients} | {rps:F0} | {p50:F2} | {p99:F2} | {errors} |");
            }
    }

    private static async Task<(double Rps, double P50, double P99, int Errors)> Load(string url, string path, string[] ids, string token, int seconds)
    {
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = ids.Length, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(url) };
        var stop = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        var latencies = new List<double>[ids.Length];
        var errors = 0;
        var body = Encoding.UTF8.GetBytes("""{"note":"bench"}""");
        var start = Stopwatch.GetTimestamp();
        await Task.WhenAll(ids.Select((id, i) => Task.Run(async () =>
        {
            var mine = latencies[i] = new List<double>(100_000);
            var target = string.Format(System.Globalization.CultureInfo.InvariantCulture, path, id);
            while (Stopwatch.GetTimestamp() < stop)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var t = Stopwatch.GetTimestamp();
                using var response = await http.SendAsync(request);
                mine.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                if (response.StatusCode != HttpStatusCode.NoContent) Interlocked.Increment(ref errors);
            }
        })));
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var all = latencies.SelectMany(l => l).Order().ToArray();
        return (all.Length / elapsed, all[all.Length / 2], all[(int)(all.Length * 0.99)], errors);
    }
}
