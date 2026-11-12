using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Tarol.StronglyTypedId.Tests;

/// <summary>Minimal APIs bind Id&lt;T&gt; from route and query through ISpanParsable, and write it as JSON.</summary>
public class MinimalApiBindingTests
{
    [Fact]
    public async Task Route_And_Query_Bind_And_Response_Serializes()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/users/{id}", (Id<UserMarker> id, Id<OrderMarker>? order) => Results.Ok(new Echo(id, order)));
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>()
            .Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var user = Id<UserMarker>.New();
        var order = Id<OrderMarker>.New();

        var echo = await http.GetFromJsonAsync<Echo>($"/users/{user}?order={order}", ct);
        var badRequest = await http.GetAsync(new Uri("/users/not-a-guid", UriKind.Relative), ct);

        echo.ShouldBe(new Echo(user, order));
        badRequest.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        await app.StopAsync(ct);
    }

    public sealed record Echo(Id<UserMarker> User, Id<OrderMarker>? Order);
}
