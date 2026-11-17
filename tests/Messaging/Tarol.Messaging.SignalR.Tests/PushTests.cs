using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Options;

namespace Tarol.Messaging.SignalR.Tests;

/// <summary>
/// The UI push end to end (docs/migration/messaging.md §11, PR 11E): the scenarios of the old
/// plan-messagingIntegrationTests.prompt.md, the forwarding consumer, authorization, and the client's fixes F7–F11, F17–F19.
/// </summary>
public sealed class PushTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(MessagePushClient Client, Inbox Inbox, IAsyncDisposable Handle)> Subscribed(PushHost host, string topic, string user = "alice",
        string? roles = "member", Tarol.StronglyTypedId.Id<Kernel.ITenant>? tenant = null)
    {
        var client = host.Client(user, tenant ?? PushHost.TenantA, roles);
        var inbox = new Inbox();
        await client.Start(Ct);
        var handle = await client.Subscribe(topic, inbox.Add, Ct);
        return (client, inbox, handle);
    }

    /// <summary>
    /// Waits until the server knows the client's subscriptions again: publishes a marker every 200 ms until one comes back
    /// (bounded by the event, 15 s cap). A fixed delay raced the replay under load (docs/migration/operations.md §5 item 14).
    /// </summary>
    private static async Task Replayed(MessagePushClient client, Inbox inbox, string topic)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!inbox.Messages.Any(IsMarker))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException($"no marker came back on {topic} after the reconnect; received {inbox.Messages.Count}");
            await client.Publish(topic, Json.Of("{\"marker\":true}"), Ct);
            await Task.Delay(200, Ct);
        }
    }

    private static bool IsMarker(PushedMessage message) => message.Data.TryGetProperty("marker", out _);

    private static bool IsAfter(PushedMessage message) => message.Data.TryGetProperty("after", out _);

    [Fact]
    public async Task A_client_receives_its_own_publish_stamped_by_the_server()
    {
        await using var host = await PushHost.Start();
        var (client, inbox, _) = await Subscribed(host, "artflow.chat");
        await using var _client = client;

        await client.Publish("artflow.chat", Json.Of("{\"text\":\"hello\"}"), Ct);

        await inbox.Until(m => m.Count == 1, "the message");
        var message = inbox.Messages[0];
        message.Sender.ShouldBe("alice");                                                          // the server's, not the client's word
        message.Topic.ShouldBe("artflow.chat");
        message.Data.GetProperty("text").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task Other_clients_of_the_tenant_receive_it_and_another_tenant_does_not()
    {
        await using var host = await PushHost.Start();
        var (a, inboxA, _) = await Subscribed(host, "artflow.chat", "alice");
        var (b, inboxB, _) = await Subscribed(host, "artflow.chat", "bob");
        var (c, inboxC, _) = await Subscribed(host, "artflow.chat", "carol", tenant: PushHost.TenantB);
        await using var _a = a;
        await using var _b = b;
        await using var _c = c;

        await a.Publish("artflow.chat", Json.Of("{}"), Ct);

        await inboxA.Until(m => m.Count == 1, "alice's copy");
        await inboxB.Until(m => m.Count == 1, "bob's copy");
        await inboxC.Quiet(0);
    }

    [Fact]
    public async Task Topics_are_isolated()
    {
        await using var host = await PushHost.Start();
        var (a, inboxA, _) = await Subscribed(host, "artflow.chat");
        var (b, inboxB, _) = await Subscribed(host, "artflow.auction-settled", "bob");
        await using var _a = a;
        await using var _b = b;

        await a.Publish("artflow.chat", Json.Of("{}"), Ct);

        await inboxA.Until(m => m.Count == 1, "the chat message");
        await inboxB.Quiet(0);
    }

    [Fact]
    public async Task Disposing_a_handle_removes_that_handler_only_and_the_last_one_unsubscribes()
    {
        await using var host = await PushHost.Start();
        var (client, first, handle) = await Subscribed(host, "artflow.chat");
        await using var _client = client;
        var second = new Inbox();
        var other = await client.Subscribe("artflow.chat", second.Add, Ct);

        await handle.DisposeAsync();                                                                 // F11: not every handler of the topic
        await client.Publish("artflow.chat", Json.Of("{}"), Ct);
        await second.Until(m => m.Count == 1, "the other handler's copy");
        await first.Quiet(0);

        await other.DisposeAsync();
        client.Topics.ShouldBeEmpty();
        await client.Publish("artflow.chat", Json.Of("{}"), Ct);
        await second.Quiet(1);
    }

    [Fact]
    public async Task After_a_reconnect_the_subscriptions_are_replayed()
    {
        await using var host = await PushHost.Start();
        var (client, inbox, _) = await Subscribed(host, "artflow.chat");
        await using var _client = client;

        await host.Stop();                                                                           // the server goes away …
        await host.Restart();                                                                        // … and comes back on the same port
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (client.State != HubConnectionState.Connected && waited.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(50, Ct);
        client.State.ShouldBe(HubConnectionState.Connected);
        await Replayed(client, inbox, "artflow.chat");                                              // the replay runs on Reconnected

        await client.Publish("artflow.chat", Json.Of("{\"after\":true}"), Ct);

        await inbox.Until(m => m.Any(IsAfter), "the message after the reconnect (F7)");
    }

    [Fact]
    public async Task A_connection_that_closed_for_good_is_started_again_by_the_client()
    {
        await using var host = await PushHost.Start();
        var connection = new HubConnectionBuilder()                                                   // no automatic reconnect: Closed is final
            .WithUrl(host.Hub, o =>
            {
                o.Headers["X-User"] = "alice";
                o.Headers["X-Roles"] = "member";
            })
            .AddJsonProtocol(static o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, SignalRJson.Default))
            .Build();
        await using var client = new MessagePushClient(connection, ownsConnection: true);
        var inbox = new Inbox();
        await using var handle = await client.Subscribe("artflow.chat", inbox.Add, Ct);
        await client.Start(Ct);

        await host.Stop();
        await host.Restart();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (client.State != HubConnectionState.Connected && waited.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(50, Ct);
        client.State.ShouldBe(HubConnectionState.Connected);
        await Replayed(client, inbox, "artflow.chat");                                              // the replay follows the start

        await client.Publish("artflow.chat", Json.Of("{\"after\":true}"), Ct);
        await inbox.Until(m => m.Any(IsAfter), "the message after the client's own restart");
    }

    [Fact]
    public async Task A_subscription_made_before_start_reaches_the_server_at_start()
    {
        await using var host = await PushHost.Start();
        await using var client = host.Client("alice", PushHost.TenantA, "member");
        var inbox = new Inbox();
        await using var handle = await client.Subscribe("artflow.chat", inbox.Add, Ct);           // not connected yet (F8)

        await client.Start(Ct);
        await client.Publish("artflow.chat", Json.Of("{}"), Ct);

        await inbox.Until(m => m.Count == 1, "the message");
    }

    [Fact]
    public async Task The_payload_survives_and_many_messages_all_arrive()
    {
        await using var host = await PushHost.Start();
        var (client, inbox, _) = await Subscribed(host, "artflow.chat");
        await using var _client = client;
        const string payload = "{\"text\":\"Grüße \\u2603\",\"n\":[1,2.5,null,true],\"nested\":{\"a\":\"b\"}}";

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => client.Publish("artflow.chat", Json.Of(payload.Replace("\"a\":\"b\"", $"\"a\":{i}", StringComparison.Ordinal)), Ct)));

        await inbox.Until(m => m.Count == 50, "50 messages");
        inbox.Messages.Select(m => m.Data.GetProperty("nested").GetProperty("a").GetInt32()).Order().ShouldBe(Enumerable.Range(0, 50));
        inbox.Messages[0].Data.GetProperty("text").GetString().ShouldBe("Grüße ☃");
        inbox.Messages[0].Data.GetProperty("n").GetRawText().ShouldBe("[1,2.5,null,true]");
    }

    [Fact]
    public async Task A_failing_handler_does_not_stop_the_others()
    {
        await using var host = await PushHost.Start();
        var (client, inbox, _) = await Subscribed(host, "artflow.chat");
        await using var _client = client;
        await using var failing = await client.Subscribe("artflow.chat", static (_, _) => throw new InvalidOperationException("boom"), Ct);
        var after = new Inbox();
        await using var third = await client.Subscribe("artflow.chat", after.Add, Ct);

        await client.Publish("artflow.chat", Json.Of("{}"), Ct);

        await inbox.Until(m => m.Count == 1, "the first handler");
        await after.Until(m => m.Count == 1, "the handler after the failing one (F18)");
    }

    [Fact]
    public async Task Messages_from_the_transport_reach_their_tenants_subscribers()
    {
        await using var host = await PushHost.Start();
        var (a, inboxA, _) = await Subscribed(host, "artflow.auction-settled", "alice");
        var (b, inboxB, _) = await Subscribed(host, "artflow.auction-settled", "bob", tenant: PushHost.TenantB);
        await using var _a = a;
        await using var _b = b;
        var settled = PushHost.Envelope("artflow.auction-settled", PushHost.TenantA);

        await host.Transport.Publish(settled, Ct);

        await inboxA.Until(m => m.Count == 1, "the forwarded message");
        var pushed = inboxA.Messages[0];
        pushed.Id.ShouldBe(settled.Id);
        pushed.Sender.ShouldBe("artflow");
        pushed.Subject.ShouldBe(settled.Subject);
        pushed.Data.GetProperty("price").GetInt32().ShouldBe(150);
        await inboxB.Quiet(0);
    }

    [Fact]
    public async Task Untrusted_tenants_sources_and_non_JSON_data_are_not_forwarded()
    {
        await using var host = await PushHost.Start();
        var (client, inbox, _) = await Subscribed(host, "billing.invoice-paid", tenant: PushHost.TenantB);
        await using var _client = client;

        await host.Transport.Publish(PushHost.Envelope("billing.invoice-paid", PushHost.TenantB), Ct);           // billing may name tenant A only
        await host.Transport.Publish(PushHost.Envelope("billing.invoice-paid", PushHost.TenantB, source: "artflow"), Ct);
        await host.Transport.Publish(PushHost.Envelope("billing.invoice-paid", PushHost.TenantB, data: "not json"), Ct);

        await inbox.Quiet(0, 800);
    }

    [Fact]
    public async Task A_refused_subscribe_or_publish_is_a_hub_error_and_keeps_no_handler()
    {
        await using var host = await PushHost.Start();
        await using var client = host.Client("alice", PushHost.TenantA, roles: null);
        await client.Start(Ct);

        (await Should.ThrowAsync<HubException>(() => client.Subscribe("billing.secret", static (_, _) => ValueTask.CompletedTask, Ct))).Message.ShouldContain("Forbidden");
        client.Topics.ShouldBeEmpty();                                                              // F9
        (await Should.ThrowAsync<HubException>(() => client.Publish("artflow.chat", Json.Of("{}"), Ct))).Message.ShouldContain("Forbidden");   // no member role
        (await Should.ThrowAsync<HubException>(() => client.Subscribe("no-system", static (_, _) => ValueTask.CompletedTask, Ct))).Message.ShouldContain("Topic");
    }

    [Fact]
    public async Task An_unauthenticated_client_cannot_connect()
    {
        await using var host = await PushHost.Start();
        await using var client = host.Client(user: null);

        var refused = await Should.ThrowAsync<HttpRequestException>(() => client.Start(Ct));
        refused.Message.ShouldContain("401");
    }

    [Fact]
    public async Task A_tenant_claim_that_is_not_a_tenant_is_refused()
    {
        await using var host = await PushHost.Start();
        await using var client = MessagePushClient.Connect(host.Hub, configure: o =>
        {
            o.Headers["X-User"] = "mallory";
            o.Headers["X-Tenant"] = "everyone";
        });
        await client.Start(Ct);

        (await Should.ThrowAsync<HubException>(() => client.Subscribe("artflow.chat", static (_, _) => ValueTask.CompletedTask, Ct))).Message.ShouldContain("Auth.Tenant");
    }

    [Fact]
    public async Task Shutting_down_with_live_clients_does_not_hang()
    {
        var host = await PushHost.Start();
        var clients = new List<MessagePushClient>();
        for (var i = 0; i < 5; i++)
            clients.Add((await Subscribed(host, "artflow.chat", "user" + i)).Client);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => clients[0].Publish("artflow.chat", Json.Of("{}"), cancelled.Token));
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);                     // live connections are aborted, not waited for
        await Task.WhenAll(clients.Select(c => c.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    [Fact]
    public async Task Disposing_a_handle_after_the_server_went_away_does_not_throw()
    {
        var host = await PushHost.Start();
        var (client, _, handle) = await Subscribed(host, "artflow.chat");
        await using var _client = client;
        await host.DisposeAsync();

        await handle.DisposeAsync();                                                                 // the unsubscribe is best effort

        client.Topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Groups_are_tenant_and_topic_and_rules_match_prefixes_and_roles()
    {
        MessageHub.Group(PushHost.TenantA, "artflow.chat").ShouldBe("0193a000-0000-7000-8000-00000000000a/artflow.chat");
        MessageHub.Group(Option.None, "artflow.chat").ShouldBe("-/artflow.chat");
        var rules = new TopicRules().Subscribe("artflow.*").Publish("artflow.chat", role: "member");
        var member = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Role, "member")], "Test"));
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        (await rules.Authorize(member, TopicOperation.Subscribe, "artflow.auction-settled", Ct)).ShouldBeTrue();
        (await rules.Authorize(member, TopicOperation.Subscribe, "billing.invoice-paid", Ct)).ShouldBeFalse();
        (await rules.Authorize(member, TopicOperation.Publish, "artflow.chat", Ct)).ShouldBeTrue();
        (await rules.Authorize(anonymous, TopicOperation.Subscribe, "artflow.chat", Ct)).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new UiPushOptions().Forward("nosystem"));
    }
}
