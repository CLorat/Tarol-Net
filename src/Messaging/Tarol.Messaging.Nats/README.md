# Tarol.Messaging.Nats

The NATS JetStream transport of [Tarol.Messaging](https://github.com/CLorat/Tarol-Net): publications leave a system's
outbox onto a JetStream stream, and consumers pull from durable consumers. Native-AOT ready (NATS.Net 3.3.2; it adds about
5.7 MB to a native binary).

## Install

```bash
dotnet add package Tarol.Messaging.Nats      # brings Tarol.Messaging and NATS.Net
```

## 30 seconds

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Invoice>())
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddMessaging("billing", m => m
        .Consume(GeneratedConsumers.Set)
        .Publish(GeneratedPublications.Set)
        .Trust("artflow", t => t.AnyTenant())
        .UseNats(builder.Configuration.GetConnectionString("nats")!)    // or UseNats(new NatsTransportOptions { … })
        .UseNpgsqlDeadLetters());
```

## Topology

| Tarol | NATS JetStream |
|---|---|
| a source system (`artflow`) | stream `ARTFLOW`, subjects `artflow.>`, file storage, duplicate window (2 min) |
| a publish | `PublishAsync` on the contract's subject with `Nats-Msg-Id` = `Message.DedupId` (event, contract, version); returns after the stream's acknowledgement |
| a subscription (`billing.IssueInvoice`) | durable pull consumer `billing_IssueInvoice`, explicit ack, `AckWait` = `Subscription.Lease`, `MaxAckPending` = `Subscription.Concurrency` |
| retry after a delay | `Nak(delay)` |
| attempt | the server's delivery count |

* **Duplicates are success**: a republish within the window comes back as `Duplicate` and counts as published
  (`NatsTransport.Duplicates`). The transport never calls `EnsureSuccess()`, which throws for a duplicate.
* **Who may publish under a prefix**: subject permissions per user, e.g. user `billing` may publish `billing.>`,
  `$JS.API.>`, `$JS.ACK.>` and `_INBOX.>`. NATS stamps no identity (`Incoming.AuthenticatedAs` is `null`): the subject's
  prefix is the authenticated source.
* **A publish to a forbidden subject is a timeout**, not an error: the server drops it with a permission violation on
  the connection, which the transport logs (event 11201); the row retries until `outbox_dead`.
* **`Prefix`** puts a namespace in front of subjects, streams and consumers for environments that share a server.

Design: docs/architecture/messaging.md §6. Licence: proprietary, see LICENSE.

## Retention (step 16)

`NatsTransportOptions.MaxAge` is the streams' JetStream `MaxAge`, 7 days by default (the bus retention). Before step 16 the streams kept
messages for ever; `TimeSpan.Zero` still does.
