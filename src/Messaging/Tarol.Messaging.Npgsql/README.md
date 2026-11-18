# Tarol.Messaging.Npgsql

The in-database message bus of [Tarol.Messaging](https://github.com/CLorat/Tarol-Net) on plain Npgsql: systems that share a
PostgreSQL server exchange messages through a `bus` schema, with no broker to run. Publishing is one statement
(deduplicated by event, contract and version, one delivery per subscription); consumers claim with `FOR UPDATE SKIP LOCKED`
in publish order; the database stamps the publisher's role, and row-level security binds a role to its contract prefix.
Also the consumer's dead letters in `messages_dead`, for any transport. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.Messaging.Npgsql      # brings Tarol.Messaging and Npgsql
```

## 30 seconds

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Invoice>())
    .UseNpgsqlStore(connectionString)                           // registers the NpgsqlDataSource the dead letters use
    .AddReactionWorker()                                        // publishes the outbox's publication rows
    .AddMessaging("billing", m => m
        .Consume(GeneratedConsumers.Set)
        .Publish(GeneratedPublications.Set)
        .Trust("artflow", t => t.AnyTenant())
        .UseNpgsqlBus(busConnectionString, new NpgsqlBusOptions { Notify = true })
        .UseNpgsqlDeadLetters());                               // tarol.messages_dead, created at start
```

## The bus

| Table | Content |
|---|---|
| `bus.messages` | one row per (event id, contract, version): headers (`jsonb`), data, `publisher` = `session_user` |
| `bus.subscriptions` | (contract, subscription) rows, added by `Prepare` |
| `bus.deliveries` | one row per (subscription, message) until it is acknowledged; `due_at` is the next delivery (a lease or a retry) |

* **Publish**: `INSERT … ON CONFLICT (id, type, version) DO NOTHING`, fanned out to one delivery per subscription in the
  same statement; a republish after a crash is dropped. A contract without subscribers is stored and delivered to nobody.
* **Consume**: `Subscription.Concurrency` loops claim up to `Batch` due deliveries; the claim sets a lease of
  `Subscription.Lease` and is ordered **after** `UPDATE … RETURNING`, so one subscription with concurrency 1 receives its
  messages in publish order. An acknowledgement deletes the delivery, a retry makes it due later; both present the claim,
  so a late answer after a lost lease changes nothing.
* **NOTIFY** (`Notify = true`, opt-in): a `NOTIFY` in the publish's statement batch wakes the consumers; else they poll
  every `PollInterval` (100 ms).
* **Retention**: `NpgsqlBus.PurgeMessages(olderThan, batch, ct)` deletes delivered messages. With a job host (`AddJobs` of
  `Tarol.Jobs`) `UseNpgsqlBus` schedules it as the system job `tarol.purge-messages` (hourly, once per cluster) with
  `NpgsqlBusOptions.Retention` (7 days) and `PurgeBatch` (1000).

## Schema versions (step 15)

`NpgsqlBus.EnsureSchema` and `NpgsqlMessageDeadLetters.EnsureSchema` use the store's upgrader (`NpgsqlSchemaUpgrader` of
`Tarol.Persistence.Npgsql`): a polled session advisory lock per PostgreSQL schema, the version 1 script only on an empty
schema, steps with `lock_timeout` and retries, and `compatible_from` so old and new code share a schema. The bus stays at
version 1 (it gains `compatible_from`); `messages_dead` gets its own version table `messages_dead_version` and version 2
(`operations_audit`, a `dead_at` index built `CONCURRENTLY`). With a migration tool: `NpgsqlBusSchema.UpgradeScript(schema,
from)`, `DeadLettersUpgradeScript(schema, from)` and `NpgsqlBusOptions.SchemaMode = SchemaMode.VerifyOnly`.

## Who may publish under a prefix

Give each system a login role named like its prefix, and let the schema's owner run:

```csharp
NpgsqlBusSchema.Script("bus")                       // the tables
NpgsqlBusSchema.RowLevelSecurityScript("bus")       // policy publish_own_prefix: split_part(type, '.', 1) = session_user
NpgsqlBusSchema.GrantScript("billing", "bus")       // per system
```

Then `billing` inserting an `artflow.*` message fails with SQLSTATE 42501. Connect each system as its role with
`EnsureSchema = false` and map the roles to sources, so consumers check the stamped role too:

```csharp
new NpgsqlBusOptions { EnsureSchema = false, Roles = new Dictionary<string, string> { ["artflow"] = "artflow", ["billing"] = "billing" } }
```

With the map, a message whose role is not its contract's prefix is buried (`Messaging.SourceMismatch`), with or without
the policy; an unknown role is never a source. Without the map (all systems as one role) the stamp proves nothing.

Design: docs/architecture/messaging.md §6. Licence: AGPL-3.0-only, see LICENSE; a commercial licence is available from the owner (NOTICE).
