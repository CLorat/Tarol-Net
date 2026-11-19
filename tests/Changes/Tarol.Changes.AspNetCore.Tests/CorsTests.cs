using System.Net;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>BC-1: a browser on another origin may send Tarol's request headers and read its response headers.</summary>
public sealed class CorsTests : IAsyncLifetime
{
    private const string Origin = "http://localhost:5173";
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start(corsOrigin: Origin);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_preflight_allows_the_tarol_request_headers()
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/auctions");
        preflight.Headers.Add("Origin", Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key,if-match,if-none-match,tarol-read-after,traceparent");

        var response = await _app.Client.SendAsync(preflight, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([Origin]);
        var allowed = response.Headers.GetValues("Access-Control-Allow-Headers").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TarolHeaders.Allowed.ShouldAllBe(h => allowed.Contains(h));
        response.Headers.GetValues("Access-Control-Allow-Methods").Single().ShouldContain("POST");
    }

    [Fact]
    public async Task An_answer_exposes_etag_location_and_the_position()
    {
        var created = await _app.Post("/auctions", Auction(), Alice, h => h.Add("Origin", Origin));

        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        created.Response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([Origin]);
        var exposed = created.Response.Headers.GetValues("Access-Control-Expose-Headers").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        exposed.ShouldBe(TarolHeaders.Exposed.ToHashSet(StringComparer.OrdinalIgnoreCase), ignoreOrder: true);
        created.Response.Headers.Contains("ETag").ShouldBeTrue();
        created.Response.Headers.Contains(Positions.PositionHeader).ShouldBeTrue();
    }

    [Fact]
    public async Task Another_origin_gets_no_cors_headers()
    {
        var created = await _app.Post("/auctions", Auction(), Alice, h => h.Add("Origin", "https://evil.example"));

        created.Response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        created.Response.Headers.Contains("Access-Control-Expose-Headers").ShouldBeFalse();
    }
}
