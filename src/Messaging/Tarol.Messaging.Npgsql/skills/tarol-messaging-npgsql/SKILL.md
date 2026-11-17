---
name: tarol-messaging-npgsql
description: >-
  Use when Tarol systems exchange messages through PostgreSQL (no broker), or keep consumer dead letters in the
  database: UseNpgsqlBus, NpgsqlBusOptions (Schema, EnsureSchema, Notify, PollInterval, Batch, Roles), NpgsqlBus
  (EnsureSchema, PurgeMessages, Pending), NpgsqlBusSchema (Script, RowLevelSecurityScript, GrantScript, DeadLettersScript),
  UseNpgsqlDeadLetters and NpgsqlMessageDeadLetters. Do not write your own queue table, polling loop or LISTEN code; do
  not decompile or web-search.
---
# Tarol.Messaging.Npgsql

## Rules

1. **Register the bus as the transport**: `AddMessaging("<app>", m => m.….UseNpgsqlBus(connectionString, options))` or
   `UseNpgsqlBus(provider => dataSource, options)`. Every system points at the same database and `Schema` (default `bus`).
2. **Dead letters** go to the consumer's database: `UseNpgsqlDeadLetters()` uses the `NpgsqlDataSource` registered by
   `UseNpgsqlStore(connectionString)` and schema `tarol`; pass `schema`/`dataSource` otherwise. It works with any transport.
3. **Publications still need the reaction worker** (`AddReactionWorker()`): the bus is only where they land.
4. **Order**: one subscription with `Concurrency = 1` (the default) receives one stream's messages in publish order.
   Higher concurrency trades order for throughput.
5. **Who may publish**: one login role per system, named like its contract prefix; the owner runs
   `NpgsqlBusSchema.Script`, `RowLevelSecurityScript` and `GrantScript(role)`; systems connect with
   `EnsureSchema = false` and `Roles = { [role] = source }`. A foreign prefix fails with SQLSTATE 42501; a stamped role
   that disagrees with the prefix is buried by the consumer.
6. **`Notify = true`** makes deliveries arrive in milliseconds instead of at the next poll; it serializes publishers'
   commits (opt-in). `PollInterval` stays the fallback.
7. **Retention**: with a job host (`AddJobs`, skill `tarol-jobs`) the system job `tarol.purge-messages` deletes messages
   nobody still has to receive, older than `NpgsqlBusOptions.Retention` (7 days), hourly, once per cluster. Without one,
   call `bus.PurgeMessages(olderThan, batch, ct)` yourself.

## Example

```csharp
var services = new ServiceCollection();
services.AddTarol(ContractJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>().Add<NoteLog>())
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddMessaging("billing", m => m
        .Consume(GeneratedConsumers.Set)
        .Publish(GeneratedPublications.Set)
        .Trust("artflow", t => t.AnyTenant())
        .UseNpgsqlBus(connectionString, new NpgsqlBusOptions
        {
            Notify = true,
            Roles = new Dictionary<string, string> { ["artflow"] = "artflow", ["billing"] = "billing" },
        })
        .UseNpgsqlDeadLetters());

// once, as the schema's owner, for row-level security
var sql = NpgsqlBusSchema.Script() + NpgsqlBusSchema.RowLevelSecurityScript() + NpgsqlBusSchema.GrantScript("billing");
```

## Common mistakes

| Mistake | What you get |
|---|---|
| `UseNpgsqlDeadLetters()` without `UseNpgsqlStore(connectionString)` | `InvalidOperationException` at resolve: no `NpgsqlDataSource`; pass `dataSource:` |
| consumers without any dead-letter store | `AddMessaging` throws "Choose where dead letters go" |
| restricted roles with `EnsureSchema = true` | permission denied on `CREATE SCHEMA`: let the owner run the script |
| `Roles` map missing a publishing role | its messages arrive as `role:<name>` and are buried `Messaging.SourceMismatch` |
| a schema name with capitals or dashes | `ArgumentException`: plain identifiers only |
| expecting order with `Concurrency > 1` | inversions within a stream |

## AOT

No reflection: Npgsql's slim data source, the headers through a source-generated JSON context. Native AOT publishes
with 0 IL warnings.
