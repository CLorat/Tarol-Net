using System.Net;
using System.Text.Json;
using Tarol.Security;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-59 (VA-4) over HTTP: a Sensitive view reports every granted read, a 304 included; denials are reported everywhere.</summary>
public sealed class AccessObserverEndpointTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task A_sensitive_view_reports_each_read_and_its_304_a_plain_view_only_its_denials()
    {
        var created = await _app.Post("/auctions", Auction("Vase"), Alice);
        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        var id = JsonElement.Parse(created.Body).GetProperty("id").GetString()!;

        var first = await _app.Get($"/auctions/{id}/dossier", Rita);
        var again = await _app.Get($"/auctions/{id}/dossier", Rita, h => h.TryAddWithoutValidation("If-None-Match", first.Response.Headers.ETag!.ToString()));
        var plain = await _app.Get($"/auctions/{id}", Rita);
        var denied = await _app.Get($"/auctions/{id}/dossier", Ada with { Roles = "" });

        (first.Status, again.Status, plain.Status, denied.Status).ShouldBe((HttpStatusCode.OK, HttpStatusCode.NotModified, HttpStatusCode.OK, HttpStatusCode.Forbidden));
        _app.Access.Seen.Select(a => (a.Decision, a.View, a.Actor.Id, a.Code)).ShouldBe([
            (AccessDecision.Allowed, "AuctionDossier", "rita", null),
            (AccessDecision.Allowed, "AuctionDossier", "rita", null),
            (AccessDecision.Denied, "AuctionDossier", "ada", "Auth.Forbidden"),
        ]);
        _app.Access.Seen.ShouldAllBe(a => a.Id == Guid.Parse(id) && a.Aggregate == "Auction" && a.Intent == "Read<Auction>");
    }
}
