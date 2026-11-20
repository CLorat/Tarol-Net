using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Changes.AspNetCore.Tests;
using Tarol.Messaging;
using Tarol.Messaging.SignalR;

namespace Tarol.Security.Zitadel.IntegrationTests;

/// <summary>BC-2: a browser reaches the message hub with a real Zitadel JWT in the query string over a real WebSocket (it
/// cannot set headers on one); outside the hub's path a query token is ignored.</summary>
[Trait("Category", "Integration")]
public sealed class ZitadelHubTests(Zitadel zitadel) : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _base = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>())
            .UseInMemoryStore()
            .AddEndpoints()
            .AddZitadel(o => (o.Authority, o.ProjectId, o.RequireHttpsMetadata) = (zitadel.Authority, zitadel.ProjectId, false))
            .AddMessaging("artflow", m => m.UseInMemoryTransport().PushToUi(o => o.Rules.Subscribe("artflow.*")));
        builder.Services.AddAuthorization();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseWebSockets();
        _app.MapMessageHub();
        _app.MapGet("/whoami", (System.Security.Claims.ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true ? user.Identity.Name! : "anonymous");
        await _app.StartAsync(Ct);
        _base = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private Uri Ws(string pathAndQuery) => new($"ws://{_base.Authority}{pathAndQuery}");

    /// <summary>The SignalR JSON protocol's handshake.</summary>
    private static async Task Handshake(ClientWebSocket socket)
    {
        await Send(socket, """{"protocol":"json","version":1}""");
        (await Receive(socket)).ShouldBe("{}");
    }

    /// <summary>One invocation; returns the completion's error (null: success).</summary>
    private static async Task<string?> Invoke(ClientWebSocket socket, string id, string target, string argument)
    {
        await Send(socket, $$"""{"type":1,"invocationId":"{{id}}","target":"{{target}}","arguments":["{{argument}}"]}""");
        while (true)
        {
            var message = JsonElement.Parse(await Receive(socket));
            if (message.GetProperty("type").GetInt32() == 3)
                return message.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
    }

    private static Task Send(ClientWebSocket socket, string json)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json + "\u001e"), WebSocketMessageType.Text, true, Ct);

    private static async Task<string> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[16384];
        var text = new StringBuilder();
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, Ct);
            text.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
            if (read.EndOfMessage)
                return text.ToString().TrimEnd('\u001e').Split('\u001e')[0];
        }
    }

    [Fact]
    public async Task A_websocket_with_the_token_in_the_query_reaches_the_hub_as_the_user()
    {
        var alice = await zitadel.Token("alice");
        using var socket = new ClientWebSocket();

        await socket.ConnectAsync(Ws($"/tarol/messages?access_token={Uri.EscapeDataString(alice)}"), Ct);

        await Handshake(socket);
        (await Invoke(socket, "1", "Subscribe", "artflow.bids")).ShouldBeNull();     // authenticated, tenant from the organisation
        (await Invoke(socket, "2", "Subscribe", "billing.secret")).ShouldNotBeNull().ShouldContain(Tarol.Messaging.SignalR.HubErrors.Forbidden + ": ");
    }

    [Fact]
    public async Task Without_a_token_or_with_a_tampered_one_the_upgrade_is_401()
    {
        using var anonymous = new ClientWebSocket();
        (await Should.ThrowAsync<WebSocketException>(() => anonymous.ConnectAsync(Ws("/tarol/messages"), Ct))).Message.ShouldContain("401");
        var tampered = (await zitadel.Token("alice"))[..^4] + "AAAA";
        using var forged = new ClientWebSocket();
        (await Should.ThrowAsync<WebSocketException>(() => forged.ConnectAsync(Ws($"/tarol/messages?access_token={tampered}"), Ct))).Message.ShouldContain("401");
    }

    [Fact]
    public async Task A_query_token_outside_the_hub_is_ignored()
    {
        var alice = await zitadel.Token("alice");
        using var http = new HttpClient { BaseAddress = _base };

        (await http.GetStringAsync($"/whoami?access_token={Uri.EscapeDataString(alice)}", Ct)).ShouldBe("anonymous");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice);
        (await (await http.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct)).ShouldBe(zitadel.Clients["alice"].UserId);
    }
}
