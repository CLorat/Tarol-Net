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
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.SignalR.Tests;

[JsonSerializable(typeof(int))]
internal sealed partial class PushTestJson : JsonSerializerContext;

/// <summary>A Kestrel server with the UI push on a port of its own; it can be stopped and started again on the same port.</summary>
public sealed class PushHost : IAsyncDisposable
{
    public static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    public static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000b");

    private WebApplication? _app;

    private PushHost(int port) => Port = port;

    public int Port { get; }

    public Uri Hub => new($"http://127.0.0.1:{Port}/tarol/messages");

    public IMessageTransport Transport => _app!.Services.GetRequiredService<IMessageTransport>();

    public int Connections => _app!.Services.GetRequiredService<HubConnections>().Count;

    public static async Task<PushHost> Start()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var host = new PushHost(port);
        await host.Restart();
        return host;
    }

    /// <summary>Starts the server (again) on the same port.</summary>
    public async Task Restart()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.Configure<HubOptions>(o => o.AddFilter(new AbortFilter(this)));
        builder.Services.AddTarol(PushTestJson.Default, _ => { })
            .UseInMemoryStore()
            .AddMessaging("artflow", m => m
                .Trust("billing", t => t.Tenants(TenantA))
                .UseInMemoryTransport()
                .PushToUi(o =>
                {
                    o.Forward("artflow.auction-settled", "billing.invoice-paid");
                    o.Rules.Subscribe("artflow.*").Subscribe("billing.invoice-paid").Publish("artflow.chat", role: "member");
                }));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMessageHub();
        await _app.StartAsync();
    }

    private int _abortNext;

    /// <summary>How many of the next connections the server closes right after their handshake (the client's start returned).</summary>
    public int AbortNext
    {
        get => Volatile.Read(ref _abortNext);
        set => Volatile.Write(ref _abortNext, value);
    }

    /// <summary>Closes a connection right after it connected while <see cref="AbortNext"/> is above 0.</summary>
    private sealed class AbortFilter(PushHost host) : IHubFilter
    {
        public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            if (Interlocked.Decrement(ref host._abortNext) >= 0)
                context.Context.Abort();
            return next(context);
        }
    }

    public async Task Stop()
    {
        if (_app is { } app)
        {
            await app.StopAsync();
            await app.DisposeAsync();
            _app = null;
        }
    }

    /// <summary>A client authenticated as <paramref name="user"/> of <paramref name="tenant"/> (headers read by the test scheme).</summary>
    public MessagePushClient Client(string? user, Id<ITenant>? tenant = null, string? roles = null)
        => MessagePushClient.Connect(Hub, configure: o =>
        {
            if (user is not null)
                o.Headers["X-User"] = user;
            if (tenant is { } t)
                o.Headers["X-Tenant"] = t.Value.ToString("D");
            if (roles is not null)
                o.Headers["X-Roles"] = roles;
        });

    public static Message Envelope(string contract, Option<Id<ITenant>> tenant, string data = "{\"price\":150}", string? source = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = source ?? Message.SystemOf(contract),
        Type = contract,
        Version = 1,
        Time = TimeProvider.System.GetUtcNow(),
        Subject = Guid.NewGuid().ToString("D"),
        Tenant = tenant,
        Correlation = Id<ICorrelation>.New(),
        Data = System.Text.Encoding.UTF8.GetBytes(data),
    };

    public ValueTask DisposeAsync() => new(Stop());
}

/// <summary>Collects what a client receives.</summary>
public sealed class Inbox
{
    private readonly ConcurrentQueue<PushedMessage> _messages = new();

    public IReadOnlyList<PushedMessage> Messages => [.. _messages];

    public ValueTask Add(PushedMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    public async Task Until(Func<IReadOnlyList<PushedMessage>, bool> condition, string what, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!condition(Messages))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(seconds))
                throw new TimeoutException($"{what}; received {Messages.Count}");
            await Task.Delay(20);
        }
    }

    /// <summary>Waits a little and asserts nothing more arrived.</summary>
    public async Task Quiet(int count, int milliseconds = 500)
    {
        await Task.Delay(milliseconds);
        Messages.Count.ShouldBe(count);
    }
}

/// <summary>Authenticates by headers: X-User (sub; or a bearer token as the user), X-Tenant (tenant), X-Roles (comma-separated). Neither: not authenticated.</summary>
public sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var bearer = Request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..]
            : Request.Query["access_token"].ToString();
        if ((Request.Headers["X-User"].ToString() is { Length: > 0 } named ? named : bearer) is not { Length: > 0 } user)
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user), new(ClaimTypes.NameIdentifier, user) };
        if (Request.Headers["X-Tenant"].ToString() is { Length: > 0 } tenant)
            claims.Add(new("tenant", tenant));
        foreach (var role in Request.Headers["X-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new(ClaimTypes.Role, role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

internal static class Json
{
    public static JsonElement Of(string json) => JsonElement.Parse(json);
}
