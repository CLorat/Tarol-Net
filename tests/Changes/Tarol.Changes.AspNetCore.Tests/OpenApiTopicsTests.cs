using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Messaging;
using Tarol.Messaging.SignalR;
using Tarol.Primitives;
using Tarol.Security;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>BC-25: the UI push's topics are in the OpenAPI document as x-tarol-topics, with the contract record's schema.</summary>
public sealed class OpenApiTopicsTests
{
    private static async Task<JsonElement> Document(Action<UiPushOptions> push, bool ownAuthorizer = false, bool onlyAnAssemblyWithoutSignalR = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        if (ownAuthorizer)
            builder.Services.AddSingleton<ITopicAuthorizer, Nobody>();
        builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Memo>())
            .WithOutboxRows(GeneratedPublications.Set)                 // as the sample's API: the rows only, the worker publishes
            .UseInMemoryStore()
            .AddSecurity(_ => { })
            .AddEndpoints()
            .AddMessaging("shop", m => m.UseInMemoryTransport().Trust("billing").PushToUi(push));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestApp.HeaderScheme>("test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IListScope<FamilyAuctions>, FamilyScope>();
        builder.Services.AddOpenApi(o =>
        {
            if (onlyAnAssemblyWithoutSignalR)
                Garden.GeneratedOpenApi.AddTarolEndpoints(o);          // the garden fixture does not reference Tarol.Messaging.SignalR
            else
                o.AddTarolEndpoints();
        });
        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapChanges();
        app.MapMessageHub();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var text = await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
        return JsonElement.Parse(text);
    }

    [Fact]
    public async Task Every_topic_is_in_the_document_with_its_contract()
    {
        var document = await Document(push =>
        {
            push.Forward("shop.bid-placed", "billing.invoice-paid");
            push.Rules.Subscribe("shop.*").Subscribe("billing.invoice-paid", role: "accounting").Publish("shop.chat").Subscribe("shop.chat");
        });

        var topics = document.GetProperty("x-tarol-topics");
        topics.EnumerateObject().Select(t => t.Name).ShouldBe(["billing.invoice-paid", "shop.bid-placed", "shop.chat"]);
        Compact(topics.GetProperty("shop.bid-placed")).ShouldBe(
            """{"version":1,"stream":"Auction","schema":{"$ref":"#/components/schemas/BidPlacedV1"},"subscribe":true,"publish":false}""");
        Compact(topics.GetProperty("billing.invoice-paid")).ShouldBe("""{"subscribe":true,"publish":false}""");   // trusted, no local record
        Compact(topics.GetProperty("shop.chat")).ShouldBe("""{"subscribe":true,"publish":true}""");               // client-only

        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("BidPlacedV1");
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldBe(["auction", "amount", "at"]);
        schema.GetProperty("properties").GetProperty("auction").GetProperty("format").GetString().ShouldBe("uuid");
        schema.GetProperty("properties").GetProperty("at").GetProperty("format").GetString().ShouldBe("date-time");
    }

    [Fact]
    public async Task With_its_own_authorizer_the_rules_are_not_stated()
    {
        var document = await Document(push => push.Forward("shop.bid-placed").Rules.Subscribe("shop.bid-placed"), ownAuthorizer: true);

        Compact(document.GetProperty("x-tarol-topics").GetProperty("shop.bid-placed"))
            .ShouldBe("""{"version":1,"stream":"Auction","schema":{"$ref":"#/components/schemas/BidPlacedV1"}}""");
    }

    [Fact]
    public async Task The_topics_are_written_when_only_the_host_references_SignalR()
    {
        // TN-24 (Tatalu FB-F17.3-2): the transformer was generated only into an assembly that references SignalR itself, so the
        // topics were missing when the contexts did not and only the host did
        var document = await Document(push => push.Forward("shop.bid-placed").Rules.Subscribe("shop.bid-placed"), onlyAnAssemblyWithoutSignalR: true);

        Compact(document.GetProperty("x-tarol-topics").GetProperty("shop.bid-placed")).ShouldContain("\"stream\":\"Auction\"");
    }

    [Fact]
    public async Task Without_topics_the_document_has_no_extension()
        => (await Document(_ => { })).TryGetProperty("x-tarol-topics", out _).ShouldBeFalse();

    private static string Compact(JsonElement element)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            element.WriteTo(writer);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class Nobody : ITopicAuthorizer
    {
        public ValueTask<bool> Authorize(ClaimsPrincipal principal, TopicOperation operation, string topic, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }
}

/// <summary>The publication of the topic tests (in this project only: Domain.cs is shared with projects without messaging).</summary>
public static class Publications
{
    [Publishes<Bid.Placed>("shop.bid-placed", 1)]
    public static BidPlacedV1 BidPlacedV1(Bid.Placed e, RecordedEvent r) => new(r.StreamId, e.Amount, e.At);
}
