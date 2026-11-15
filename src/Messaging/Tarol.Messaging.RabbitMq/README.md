# Tarol.Messaging.RabbitMq

The RabbitMQ transport of [Tarol.Messaging](https://github.com/CLorat/Tarol-Net): publications leave a system's outbox
onto a topic exchange with publisher confirms, consumers read quorum queues. Native-AOT ready (RabbitMQ.Client 7.2.2; it
adds about 2 MB to a native binary).

## Install

```bash
dotnet add package Tarol.Messaging.RabbitMq      # brings Tarol.Messaging and RabbitMQ.Client
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
        .UseRabbitMq("amqp://billing:secret@rabbit:5672/")      // or UseRabbitMq(new RabbitMqTransportOptions { … })
        .UseNpgsqlDeadLetters());
```

## Topology

| Tarol | RabbitMQ |
|---|---|
| a source system (`artflow`) | durable topic exchange `artflow`, routing key = contract |
| a publish | `mandatory`, persistent, `message-id` = `Message.DedupId`, `user_id` = the connection's user; returns after the publisher confirm |
| a subscription (`billing.IssueInvoice`) | quorum queue `billing.IssueInvoice` bound to its contracts, prefetch = concurrency |
| retry after a delay | ack + a copy into `billing.IssueInvoice.retry` whose per-message TTL dead-letters it back; the attempt in `x-tarol-attempt` |

* **Unroutable** (no queue bound): the broker returns the message; it is logged (11301) and counts as delivered
  (`RabbitMqTransport.Unroutable`), since a publisher does not know its consumers.
* **Duplicates** are delivered again; the consumer's key replays them.
* **Who may publish under a prefix**: one user per system, named like its prefix, with write permission on its own
  exchange only; the broker validates `user_id`, so `Incoming.AuthenticatedAs` is the publishing user. A consumer's user
  needs read on the other system's exchange (it is declared passively when the user may not configure it).
  `StampUserId = false` when all systems share one user (then `AuthenticatedAs` is `null`).
* **`Prefix`** puts a namespace in front of exchanges and queues for environments that share a virtual host.

Design: docs/architecture/messaging.md §6. Licence: proprietary, see LICENSE.
