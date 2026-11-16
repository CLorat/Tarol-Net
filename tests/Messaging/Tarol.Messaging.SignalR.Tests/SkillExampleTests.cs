using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;

namespace Tarol.Messaging.SignalR.Tests;

/// <summary>The example of skills/tarol-messaging-signalr/SKILL.md, verbatim (both blocks).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_pushes_a_forwarded_message_to_the_client()
    {
        var tenantA = PushHost.TenantA;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
        probe.Stop();
        var token = "alice";
        var cancellationToken = TestContext.Current.CancellationToken;
        var shown = new ConcurrentQueue<System.Text.Json.JsonElement>();
        void Show(System.Text.Json.JsonElement data) => shown.Enqueue(data);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddTarol(PushTestJson.Default, _ => { })
            .UseInMemoryStore()
            .AddMessaging("artflow", m => m
                .Trust("billing", t => t.Tenants(tenantA))
                .UseInMemoryTransport()
                .PushToUi(o =>
                {
                    o.Forward("artflow.auction-settled");
                    o.Rules.Subscribe("artflow.*").Publish("artflow.chat", role: "member");
                }));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMessageHub();

        app.Urls.Add(baseUrl);
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var running = app;

        await using var push = MessagePushClient.Connect(new Uri(baseUrl + "/tarol/messages"), () => Task.FromResult<string?>(token));
        await using var settled = await push.Subscribe("artflow.auction-settled", (m, ct) => { Show(m.Data); return ValueTask.CompletedTask; }, cancellationToken);
        await push.Start(cancellationToken);

        await app.Services.GetRequiredService<IMessageTransport>().Publish(PushHost.Envelope("artflow.auction-settled", Tarol.Options.Option.None), TestContext.Current.CancellationToken);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (shown.IsEmpty && waited.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        shown.ShouldHaveSingleItem().GetProperty("price").GetInt32().ShouldBe(150);
    }
}
