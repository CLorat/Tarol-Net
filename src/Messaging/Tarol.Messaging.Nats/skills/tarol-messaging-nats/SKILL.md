---
name: tarol-messaging-nats
description: >-
  Use when Tarol systems exchange messages over NATS JetStream: UseNats(url) or UseNats(NatsTransportOptions: Url,
  Username, Password, Prefix, DuplicateWindow, PublishTimeout, ClientName), NatsTransport (StreamOf, Duplicates), the
  stream-per-source topology and subject permissions per user. Do not write your own JetStream publisher or consumer
  loop, call EnsureSuccess on a publish ack, or decompile or web-search.
---
# Tarol.Messaging.Nats

## Rules

1. **Register** `AddMessaging("<app>", m => m.….UseNats(url))`; with a user `UseNats(new NatsTransportOptions { Url = …,
   Username = …, Password = … })`. Publications still need `AddReactionWorker()`.
2. **Topology is declared for you**: one stream per source system (`ARTFLOW` for `artflow.>`), one durable consumer per
   subscription; `Prepare` runs when the host starts and before the first publish.
3. **A subscription reads one stream**: a consumer's contracts must share one source (else `ArgumentException`).
4. **Duplicates are success**: `Nats-Msg-Id` is (event, contract, version), so v1 and v2 of one event are two messages
   and a republish within `DuplicateWindow` is dropped.
5. **Who may publish**: one NATS user per system, publish allowed on `<prefix>.>` plus `$JS.API.>`, `$JS.ACK.>`,
   `_INBOX.>`. A forbidden publish is a `TimeoutException` after `PublishTimeout`, and the server's permission error is
   logged (event 11201).
6. **Order**: `Concurrency = 1` keeps one stream's order per subscription; higher concurrency does not.

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
        .UseNats(new NatsTransportOptions { Url = url, Username = "billing", Password = password })
        .UseInMemoryDeadLetters());
```

## Common mistakes

| Mistake | What you get |
|---|---|
| a user without publish permission on its prefix | `TimeoutException` "may this user publish on it?", log 11201, the row ends in `outbox_dead` |
| `Nats-Msg-Id` = the event id in your own publisher | v2 of a dual publish dropped as a duplicate |
| `ack.EnsureSuccess()` in your own code | a duplicate throws and the row retries until it is dead |
| one consumer reading `artflow.*` and `billing.*` | `ArgumentException` at `Prepare`: make two consumers |
| a prefix with dots | `ArgumentException`: letters, digits, `-`, `_` |

## AOT

NATS.Net 3.3.2 is AOT-clean as used here (raw `byte[]` payloads, no serializer registry); a native binary grows by about
5.7 MB.
