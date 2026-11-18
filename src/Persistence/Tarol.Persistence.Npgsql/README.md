# Tarol.Persistence.Npgsql

The **PostgreSQL store** of Tarol, on plain Npgsql (no ORM): it implements `IAggregateStore` and `IEventFeed` of
`Tarol.Persistence`. Every commit writes, in one transaction and one round trip, the events with their metadata, the
aggregate's current state as queryable JSON, the unique lookups and the idempotency key. The tenant of the operation is
the first column of every key. Native-AOT ready. Depends on `Tarol.Persistence` and `Npgsql`.

## Install

```bash
dotnet add package Tarol.Persistence.Npgsql
```

## 30 seconds

```csharp
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Validations;

// [JsonSerializable(typeof(Auction.Opened))] … for every event, in your context
var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();     // AOT: the slim builder, arrays on
var codec = new EventCodec(ValueObjectJson.Options(AppJson.Default)).Add<Auction>();
var lookups = new StoreLookups().Define<Auction>("lot", a => a.Lot);                   // share with your in-memory tests
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Lookups = lookups });
await store.EnsureSchema(ct);                                                          // or NpgsqlSchema.UpgradeScript(schema, 0) + SchemaMode.VerifyOnly

// then exactly as with any IAggregateStore: Load → decide → Prepare → Append(expected, key) → Committed
```

## What it stores

| Table (`tarol.` by default) | Key | Holds |
|---|---|---|
| `streams` | (tenant, type, id) | version (the concurrency guard), `schema`, `state jsonb` (the aggregate's current state) |
| `events` | (tenant, type, stream_id, version) | alias, alias version, entity and parent id, `payload jsonb`, `recorded_at` (one per commit), actor, correlation, causation, `traceparent`, `position` + `tx_id` (the feed) |
| `lookups` | (tenant, type, lookup, key) | the stream owning a unique key |
| `idempotency_keys` | (tenant, scope, key) | the first outcome of a keyed commit |
| `outbox` | id; unique (reaction, event_id) | one row per (event, reaction) from `NpgsqlStoreOptions.Reactions`, written in the commit; `due_at`, `head` (partial index), lease, claim id, attempts, last error |
| `outbox_dead` | id (the row's) | rows given up by the worker, with the last error; re-driven by a human |
| `jobs` | (tenant, name) | one row per (tenant, job): the stored schedule text, `enabled`, the cursor `next_at`, claim id, lease, attempts, last error (step 14) |
| `jobs_dead` | id | occurrences given up by the job host, with the last error and a pending `redrive` flag |

`type` is the aggregate's generated `StreamType`; the tenant is `00000000-0000-0000-0000-000000000000` without one.
Query the state with SQL (`state->>'lot'`); add expression or GIN indexes for your queries.

## Behaviour

* **Conflicts are results**: the stream row is updated only at the expected version and the events go in only if it was;
  a lost race returns `Stream.VersionConflict(expected, actual)`, a taken key `Lookup.Taken`, a stored key
  `Idempotency.DuplicateKey`. Nothing of a refused commit is written. Other database errors are exceptions.
* **Loads** read the stored state; another schema (a changed model), no state, `[LoadByReplay]`, or a state that does
  not parse → the events are replayed, and the next commit rewrites the state. `RebuildStates<T>()` rewrites stale
  states ahead of time.
* **Events are the truth**: an alias or version without an event or upcaster throws on load.
* **The feed** (`ReadAll(GlobalPosition after, limit)`) returns events in (transaction id, position) order and only from
  transactions older than the oldest running one, so it never skips a late commit. Consequence: **a long-running or
  idle-in-transaction session anywhere in the cluster holds the feed back** until it ends; set
  `idle_in_transaction_session_timeout`.
* **Keys** are kept until you purge them: `PurgeKeys(now - TimeSpan.FromDays(7), 1000)` in a periodic job; a redelivery
  after its key was purged commits again.
* **Outbox** (`IOutbox`): with `Reactions` (the generated `GeneratedReactions.Set`, or `OutboxReactions.From(table)`),
  every commit writes one row per (event, reaction), due at the commit or at a scheduled reaction's `DueAt`.
  `store.Outbox.Claim(max, lease)` hands due **heads** to a worker with `FOR UPDATE SKIP LOCKED`, each with its
  `RecordedEvent` and `Traceparent`: a `head` column marks the oldest row of a (stream, reaction), so one stream's events
  reach a reaction in order, one at a time, and a claim costs the same however long the backlog; rows of a scheduled
  reaction are all heads and run by due time. `Complete` and `Bury` (to `outbox_dead`) promote the next row under a
  share lock on the stream row; `Retry(row, at, reason)` delays it; all return `false` after a lost lease.
  `DeadLetters`/`Redrive(id)` are for the human. `Notify = true` wakes `WaitForRows` at each commit (≈ 7 ms), at a cost to
  writers (≈ 30 % commits/s with 16 writers); the default is polling. The worker is `Tarol.Sagas`.
* **Job rows** (`IJobRows`, `store.Jobs`): the rows of the job host (`Tarol.Jobs`). `Claim(lease)` takes **one** enabled,
  due row (or a row with a pending re-drive, first) with `FOR UPDATE SKIP LOCKED` (~1 ms at any table size, partial index
  `jobs_due`); `Move`, `Advance`, `Extend`, `Retry` and `Bury` are one statement each, guarded by the claim id, and return
  `false` after a lost lease. `Sync` runs in one transaction under an advisory lock. Times are whole seconds; the cursor
  reads back unchanged from `timestamptz`.
* **Schema** (operations.md §4): versioned. `NpgsqlSchema.Script(schema)` is the version 1 baseline; `NpgsqlSchema.Steps`
  follow it (version 2: `operations_audit`, `dead_at` indexes built `CONCURRENTLY`). `EnsureSchema` takes a session advisory
  lock per schema by polling `pg_try_advisory_lock`, runs the baseline only on an empty (or pre-step-15) schema, then the
  missing steps with `lock_timeout` (`SchemaLockTimeout`, 2 s) and up to 5 attempts, and stores the version in the step's
  transaction. `schema_version.compatible_from` lets old and new code share a schema: a newer schema is accepted while it is
  compatible (a rolling deploy), otherwise refused with both versions. With a migration tool run
  `NpgsqlSchema.UpgradeScript(schema, from)` and set `SchemaMode = SchemaMode.VerifyOnly`. Tarol's steps stay additive
  within a major version: no volatile defaults, no plain `CREATE INDEX`, no drops or renames.
* **The feed's wake-up**: `NotifyFeed = true` sends `NOTIFY <schema>_feed` (`FeedChannel`) with every commit, so projection
  hosts (`Tarol.Queries.Npgsql`) wake at once instead of polling; off by default like `Notify`. `Append` returns the
  commit's `GlobalPosition` (its transaction and the position of its last event).

## Operations (step 15)

* The data source `UseNpgsqlStore(connectionString)` builds traces Npgsql commands only inside an activity: an idle
  worker's polls made 51 root spans per second. `TraceRootCommands = true` opts out; `TraceOnlyInActivities()` puts the
  filter on a data source you build.
* Snapshot signals (`tarol.reactions.backlog`, `.oldest_due`, `.dead`, `tarol.jobs.overdue`, `.dead`): index lookups and
  small counts, read once per poll.
* Dead letters: paged queries, `Discard`, and the audited `IDeadLetterSource` acts (`operations_audit` in the same
  statement as the re-drive or discard).

## Personal data (step 16)

Schema version 3 adds `tenant_keys`, `subject_keys`, nullable `subjects` columns and a GIN index (built concurrently).
`NpgsqlKeyStore` caches unwrapped keys per process. An erasure writes the tombstone and a `NOTIFY` in one statement, and
every process evicts the key. With `NpgsqlStoreOptions.Keys`, `[PersonalData]` is sealed on `Append` and opened on every
read, personal lookups keep blind indexes, and `EnsureSchema` raises `compatible_from` to 3. `SealHistory<T>` seals
plaintext written before. `StreamsOf`, `EventsOf`, `StateOf`, `DeletePersonalLookups` and `DeleteStreams` serve
`Tarol.DataProtection`. Logs 8006–8008 (Debug).

## AOT

No reflection: SQL is plain text, JSON goes through your source-generated context and the generated state code. Use
`NpgsqlSlimDataSourceBuilder` with `EnableArrays()`; jsonb is sent and read as text, so `EnableJsonTypes()` is not
needed. The smoke app publishes with 0 IL warnings and runs against PostgreSQL natively.
