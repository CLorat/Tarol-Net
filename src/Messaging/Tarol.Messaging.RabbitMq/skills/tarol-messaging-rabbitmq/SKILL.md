---
name: tarol-messaging-rabbitmq
description: >-
  Use when Tarol systems exchange messages over RabbitMQ: UseRabbitMq(uri) or UseRabbitMq(RabbitMqTransportOptions: Uri,
  Prefix, StampUserId, ClientName), RabbitMqTransport (ExchangeOf, QueueOf, Unroutable), the exchange-per-source and
  quorum-queue topology, retry queues, user_id and per-user permissions. Do not write your own AMQP publisher, consumer or
  delayed-retry plugin setup; do not decompile or web-search.
---
# Tarol.Messaging.RabbitMq

## Rules

1. **Register** `AddMessaging("<app>", m => m.….UseRabbitMq("amqp://<user>:<password>@<host>:5672/"))`. Publications
   still need `AddReactionWorker()`.
2. **Topology is declared for you**: a topic exchange per source, a quorum queue per subscription with six delay queues
   (`.retry.1s`, `.5s`, `.30s`, `.2m`, `.10m`, `.1h`; a backoff goes to the nearest), the bindings; `Prepare` runs when the
   host starts and before the first publish.
3. **One user per system, named like its prefix** (`billing`): configure and write on `billing` / `billing\..*` (and
   `amq.default` for retries), read on the exchanges it consumes. With the default `StampUserId = true` the broker checks
   `user_id`, and consumers see the user as `Incoming.AuthenticatedAs`: a user named otherwise is buried
   `Messaging.SourceMismatch`. If all systems share one user, set `StampUserId = false`.
4. **Retries are delayed by a TTL queue**; the attempt counts across them (`x-tarol-attempt`).
5. **A contract nobody subscribes** is returned by the broker, logged and counted as delivered (`Unroutable`).
6. **Duplicates are delivered**: the consumer's key replays them (no broker dedup).

## Example

```csharp
var services = new ServiceCollection();
services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
    .UseInMemoryStore()
    .AddReactionWorker()
    .AddMessaging("billing", m => m
        .Consume(GeneratedConsumers.Set)
        .Publish(GeneratedPublications.Set)
        .Trust("artflow", t => t.AnyTenant())
        .UseRabbitMq(new RabbitMqTransportOptions { Uri = new Uri(uri), StampUserId = true })
        .UseInMemoryDeadLetters());
```

## Common mistakes

| Mistake | What you get |
|---|---|
| publishing to another system's exchange | `ACCESS_REFUSED` (403), the row retries until `outbox_dead` |
| one shared user with `StampUserId = true` | every message buried `Messaging.SourceMismatch` (authenticated as that user) |
| forging `user_id` in your own publisher | `PRECONDITION_FAILED` (406) |
| a consumer before the other system ever declared its exchange, without configure permission | 404 at `Prepare`; the host retries after its delay |
| a prefix with dots | `ArgumentException`: letters, digits, `-`, `_` |

## AOT

RabbitMQ.Client 7.2.2 is AOT-clean as used here; a native binary grows by about 2 MB.
