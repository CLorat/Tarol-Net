---
name: tarol-messaging-signalr
description: >-
  Use when a Tarol app pushes messages to browsers or .NET clients over SignalR, or a client listens: PushToUi
  (UiPushOptions: Forward, Rules, Name, HubPath, TenantClaim, Tenant, SubjectClaim), MapMessageHub, MessageHub
  (Subscribe, Unsubscribe, Publish, Group), ITopicAuthorizer, TopicRules, PushedMessage, SignalRJson, HubConnections and
  MessagePushClient (Connect, Start, Subscribe, Publish, Stop). Do not write your own hub, a typed hub, a backplane
  relay, or a client reconnect loop; do not decompile or web-search.
---
# Tarol.Messaging.SignalR

## Rules

1. **Register on the messaging builder**: `AddMessaging("<app>", m => m.….PushToUi(o => { o.Forward(…); o.Rules.…; }))`,
   then `app.UseAuthentication(); app.UseAuthorization(); app.MapMessageHub();`. Authentication is the app's own.
2. **Forward contracts, not domain events**: `o.Forward("artflow.auction-settled")` forwards what the transport carries;
   publish it first (`[Publishes<T>]`). Other systems' contracts need `.Trust(source, …)`.
3. **Nothing is allowed by default**: `o.Rules.Subscribe(topicOrPrefix*, role?)`, `o.Rules.Publish(…)`, or register an
   `ITopicAuthorizer` in DI before `PushToUi`.
4. **Tenants come from the principal** (`tenant` claim, or `o.Tenant = value => …` to map an organisation id): a client
   reaches only `<its tenant>/<topic>`. A claim that does not map is `HubException("Auth.Tenant…")`.
5. **The server stamps the sender**: `PushedMessage.Sender` is the caller's `sub` for a client publish, the source system
   for a forwarded message.
6. **The .NET client**: `MessagePushClient.Connect(hubUri, accessToken)`, `Subscribe` (any time, before or after `Start`),
   dispose the returned handle to stop one handler, `Start`. It reconnects forever (0, 1, 2, 5 s, then every 10 s) and
   replays the subscriptions; only `Stop` or disposing ends it.
7. **Native AOT** needs `Microsoft.AspNetCore.SignalR.Hub.IsCustomAwaitableSupported=false` in the app
   (`RuntimeHostConfigurationOption`).

## Example

```csharp
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
```

A .NET client:

```csharp
await using var push = MessagePushClient.Connect(new Uri(baseUrl + "/tarol/messages"), () => Task.FromResult<string?>(token));
await using var settled = await push.Subscribe("artflow.auction-settled", (m, ct) => { Show(m.Data); return ValueTask.CompletedTask; }, cancellationToken);
await push.Start(cancellationToken);
```

## Common mistakes

| Mistake | What you get |
|---|---|
| no rule for a topic | `HubException("Forbidden: Subscribe …")`; the client keeps no handler |
| a topic without a system prefix | `HubException("Topic: …")` |
| `MapMessageHub()` without `PushToUi` | `InvalidOperationException` at startup |
| an unauthenticated connection | 401 at `Start` |
| a typed hub or `Hub<T>` of your own under AOT | Reflection.Emit: not AOT-compatible |
| forgetting the AOT feature switch | IL2026/IL3050 at publish (`DefaultHubDispatcher`) |
| one forwarder name on several web nodes | each message reaches the clients of one node only |

## AOT

Hub arguments and messages use the source-generated `SignalRJson`; server and client publish with 0 IL warnings (with the
feature switch above).
