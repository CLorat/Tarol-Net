# Tarol.Messaging.SignalR

The UI push of [Tarol.Messaging](https://github.com/CLorat/Tarol-Net): messages between systems reach browsers and apps
over SignalR, per tenant and topic, with an authenticated hub that authorizes every subscribe and publish. Also a .NET
client that survives reconnects. Native-AOT ready (server and client).

## Install

```bash
dotnet add package Tarol.Messaging.SignalR      # brings Tarol.Messaging and the SignalR .NET client; the server is ASP.NET Core
```

## 30 seconds

```csharp
builder.Services.AddAuthentication().AddJwtBearer();             // or AddZitadel (Tarol.Security.Zitadel)
builder.Services.AddAuthorization();
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddMessaging("artflow", m => m
        .Trust("billing", t => t.AnyTenant())
        .UseNats(builder.Configuration.GetConnectionString("nats")!)
        .PushToUi(o =>
        {
            o.Forward("artflow.auction-settled", "billing.invoice-paid");      // contracts forwarded to the UI
            o.Rules.Subscribe("artflow.*").Subscribe("billing.invoice-paid").Publish("artflow.chat", role: "member");
        }));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapMessageHub();                                               // /tarol/messages
```

A browser: `connection.invoke("Subscribe", "artflow.auction-settled")`, then `connection.on("message", m => …)` with
`m = { topic, id, version, subject, time, sender, data, revision, position }`. `version` is the contract version;
`revision` is the stream's version after the event (the number of a view's strong `ETag` of `subject`), so a client skips a
message it already has (its own change, a newer read); `position` is the event's feed position in the text of
`Tarol-Position`, so a client re-reads a list with `Tarol-Read-After: <position>` and gets the projection that has the
event. Both are `null` for a client's publish and for a message of a publisher without them.

## What it does

* **Forwarding**: a consumer subscription (`<app>.UiPush`) receives `Forward(…)`'s contracts on the configured transport
  and sends each to the SignalR group `<tenant>/<topic>` of its envelope's tenant. Only this application's messages and
  those of a trusted source for a tenant it may name are forwarded (as for consumers, M-13). A client that is not connected
  misses the message: the push is a notification, the store is the truth.
* **The hub** (`MessageHub`, a plain `Hub` with `[Authorize]`): `Subscribe(topic)`, `Unsubscribe(topic)`,
  `Publish(topic, data)`. The tenant comes from the caller's `tenant` claim (or `UiPushOptions.Tenant` maps it), never from
  the client; `ITopicAuthorizer` decides every subscribe and publish (`UiPushOptions.Rules`, or register your own); a
  client's publish is stamped with the caller's `sub` as `Sender`. Refusals are `HubException`s whose message is
  `<code>: <detail>` with the codes of `HubErrors`: `Hub.Forbidden`, `Hub.Topic`, `Auth.Tenant` (a contract like a problem's
  `code`; `HubErrors.CodeOf(message)` reads it).
* **Token expiry**: `UiPushOptions.CloseOnAuthenticationExpiration` (default `true`) closes a connection when its bearer
  token expires, so a revoked user stops receiving pushes; the client reconnects with a fresh token. Browsers send the token
  as `?access_token=` (they cannot set WebSocket headers): `Tarol.Security.Zitadel` reads it under `ZitadelOptions.HubPath`;
  with another identity provider, read it in `JwtBearerEvents.OnMessageReceived` for the hub's path only.
* **Shutdown**: when the application stops, the hub aborts its connections, and any client that reconnects meanwhile, so a
  graceful stop does not wait for long-lived connections.
* **The .NET client** (`MessagePushClient.Connect(hubUrl, accessToken)`): `Subscribe(topic, handler)` returns a handle
  that removes that handler only; subscriptions made before `Start` and all of them after every reconnect are sent to the
  server again; it reconnects forever (0, 1, 2, 5 s, then every 10 s with jitter) and starts again by itself if a connection
  still closes for good, until `Stop` or disposal; a failing handler is logged and the others still run; a refused subscribe keeps no handler.

## Native AOT

The JSON of the hub is source-generated (`SignalRJson`, added by `PushToUi` and `Connect`). SignalR's server needs one
feature switch in the application's project:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="Microsoft.AspNetCore.SignalR.Hub.IsCustomAwaitableSupported" Value="false" Trim="true" />
</ItemGroup>
```

## Scale-out

Every node of a web tier must receive every message for its own connections: give each node its own forwarder name
(`o.Name = "UiPush-" + Environment.MachineName`) so each has its own subscription.

Design: docs/architecture/messaging.md §10. Licence: AGPL-3.0-only, see LICENSE; a commercial licence is available from the owner (NOTICE).
