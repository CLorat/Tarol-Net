using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;

namespace Tarol.Messaging.SignalR.Tests;

/// <summary>BC-15: the hub closes a connection when its authentication expires, unless the application opts out.</summary>
public sealed class HubMappingTests
{
    private static HttpConnectionDispatcherOptions Mapped(Action<UiPushOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddTarol(PushTestJson.Default, _ => { })
            .UseInMemoryStore()
            .AddMessaging("artflow", m => m.UseInMemoryTransport().PushToUi(o =>
            {
                o.Rules.Subscribe("artflow.*");
                configure?.Invoke(o);
            }));
        var app = builder.Build();
        app.MapMessageHub();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static d => d.Endpoints);
        return endpoints.Select(static e => e.Metadata.GetMetadata<HttpConnectionDispatcherOptions>()).First(static o => o is not null)!;
    }

    [Fact]
    public void The_hub_closes_on_authentication_expiration_by_default() => Mapped().CloseOnAuthenticationExpiration.ShouldBeTrue();

    [Fact]
    public void An_application_may_keep_connections_past_the_tokens_expiry()
        => Mapped(o => o.CloseOnAuthenticationExpiration = false).CloseOnAuthenticationExpiration.ShouldBeFalse();
}
