using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.SignalR;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var tenant = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
using var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start();
var port = ((IPEndPoint)probe.LocalEndpoint).Port;
probe.Stop();

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
builder.Services.AddAuthentication("Smoke").AddScheme<AuthenticationSchemeOptions, SmokeAuthentication>("Smoke", null);
builder.Services.AddAuthorization();
builder.Services.AddTarol(SmokeJson.Default, _ => { })
    .UseInMemoryStore()
    .AddMessaging("smoke", m => m
        .UseInMemoryTransport()
        .PushToUi(o =>
        {
            o.Forward("smoke.updated");
            o.Rules.Subscribe("smoke.*").Publish("smoke.chat");
        }));
await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapMessageHub();
await app.StartAsync();

await using var client = MessagePushClient.Connect(new Uri($"http://127.0.0.1:{port}/tarol/messages"), configure: o =>
{
    o.Headers["X-User"] = "alice";
    o.Headers["X-Tenant"] = tenant.Value.ToString("D");
});
var received = new ConcurrentQueue<PushedMessage>();
await using var chat = await client.Subscribe("smoke.chat", (m, _) => { received.Enqueue(m); return ValueTask.CompletedTask; });
await using var updated = await client.Subscribe("smoke.updated", (m, _) => { received.Enqueue(m); return ValueTask.CompletedTask; });
await client.Start();
Check("connected and subscribed", client.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected && client.Topics.Count == 2);

await client.Publish("smoke.chat", JsonElement.Parse("{\"text\":\"hi\"}"));
await app.Services.GetRequiredService<IMessageTransport>().Publish(new Message
{
    Id = Guid.CreateVersion7(),
    Source = "smoke",
    Type = "smoke.updated",
    Version = 1,
    Time = TimeProvider.System.GetUtcNow(),
    Tenant = tenant,
    Correlation = Id<ICorrelation>.New(),
    Data = "{\"n\":1}"u8.ToArray(),
}, default);
var waited = Stopwatch.StartNew();
while (received.Count < 2 && waited.Elapsed < TimeSpan.FromSeconds(15))
    await Task.Delay(20);
Check("a publish through the hub, stamped by the server", received.Any(m => m.Topic == "smoke.chat" && m.Sender == "alice" && m.Data.GetProperty("text").GetString() == "hi"));
Check("a message forwarded from the transport", received.Any(m => m.Topic == "smoke.updated" && m.Sender == "smoke" && m.Data.GetProperty("n").GetInt32() == 1));
var refused = false;
try
{
    await using var _ = await client.Subscribe("other.secret", static (_, _) => ValueTask.CompletedTask);
}
catch (HubException e) when (e.Message.Contains("Forbidden", StringComparison.Ordinal))
{
    refused = true;
}
Check("a refused subscribe", refused);
var stop = Stopwatch.StartNew();
await app.StopAsync();
Check("the server stops with a live client", stop.Elapsed < TimeSpan.FromSeconds(5));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

[JsonSerializable(typeof(int))]
internal sealed partial class SmokeJson : JsonSerializerContext;

internal sealed class SmokeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-User"].ToString() is not { Length: > 0 } user)
            return Task.FromResult(AuthenticateResult.NoResult());
        Claim[] claims = [new("sub", user), new("tenant", Request.Headers["X-Tenant"].ToString())];
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Smoke")), "Smoke")));
    }
}
