---
name: tarol-persistence-npgsql
description: >-
  Use when storing Tarol aggregates in PostgreSQL, wiring NpgsqlAggregateStore, creating its schema, reading the global
  event feed for a projection, rebuilding stored states after a model change, purging idempotency keys, or testing
  against a real database. Covers NpgsqlAggregateStore, NpgsqlStoreOptions (SchemaMode, SchemaLockTimeout), NpgsqlSchema
  (Script, Steps, Definition, UpgradeScript), EnsureSchema and schema upgrades (SchemaMode, SchemaState, NpgsqlSchemaUpgrader), ReadAll,
  RebuildStates, PurgeKeys, the outbox (Reactions, IOutboxReactions, Outbox.Claim/Complete/Retry/Bury/DeadLetters/Redrive,
  Notify, OutboxRow), the job rows (Jobs, jobs, jobs_dead), sealed personal data (NpgsqlKeyStore, NpgsqlStoreOptions.Keys,
  UseNpgsqlKeyStore, SealHistory, StreamsOf/EventsOf, DeleteStreams, personal lookups) and the tables. Do not add an ORM, do not write your own SQL for appends or loads, do not
  page the events table by position, do not run the version 1 script by hand on a live schema, do not decompile or web-search.
---
# Tarol.Persistence.Npgsql

## Rules

1. **Data source**: `new NpgsqlSlimDataSourceBuilder(cs).EnableArrays().Build()` (AOT-safe; arrays are required).
2. **Codec first**: `new EventCodec(ValueObjectJson.Options(AppJson.Default)).Add<T>()` for every aggregate; every event
   needs `[JsonSerializable]` in the context. The stored state uses the codec's options unless
   `NpgsqlStoreOptions.StateOptions` says otherwise.
3. **One `StoreLookups`** for production and the in-memory store of your tests (`InMemoryStoreOptions.Lookups`).
4. **Schema** (versioned, `NpgsqlSchema.Version` = 2): `await store.EnsureSchema(ct)` at startup (`UseNpgsqlStore` does it).
   One process upgrades under a polled advisory lock, the others wait; each step has a `lock_timeout` (`SchemaLockTimeout`,
   2 s) and retries. A newer schema is accepted while its `compatible_from` ≤ 2 (rolling deploys). With a migration tool:
   run `NpgsqlSchema.UpgradeScript(schema, from)` (0 = empty database; the `CONCURRENTLY` statements outside a transaction)
   and set `SchemaMode = SchemaMode.VerifyOnly`. `NpgsqlStoreOptions.Schema` is a plain lowercase identifier (default `tarol`).
5. **Use it as `IAggregateStore`**: Load → decide → `Prepare` → `Append(aggregate, expected, context, key, ct)` →
   `Committed`. Conflicts are results (`StoreErrors.IsVersionConflict`), never exceptions; the tenant comes from the
   `OperationContext`.
6. **Projections** read `ReadAll(checkpoint, limit, ct)` and persist `PositionedEvent.Position` (a `GlobalPosition`).
   The feed waits for every older transaction: keep transactions short, set `idle_in_transaction_session_timeout`.
7. **Model changed?** Loads replay old states automatically; `RebuildStates<T>(ct)` rewrites them ahead of time.
8. **Keys**: call `PurgeKeys(now - window, batch, ct)` periodically (default window 7 days) until it returns less than `batch`.
9. **Outbox**: set `NpgsqlStoreOptions.Reactions = GeneratedReactions.Set` (or `OutboxReactions.From(table)` over static
   arrays) and run the `Tarol.Sagas` worker; by hand: `Outbox.Claim(max, lease, ct)` → run the reaction for `row.Event`
   → `Complete(row, ct)`, `Retry(row, at, reason, ct)` or `Bury(row, reason, ct)`. Claims are exclusive and per (stream,
   reaction) in version order; scheduled reactions by due time. Make the reaction idempotent with the key (reaction,
   event id): a lease can expire and the row run twice. Dead letters: `DeadLetters(max, ct)`, `Redrive(id, ct)`.
10. **Job rows**: `store.Jobs` (`IJobRows`) is used by the `Tarol.Jobs` host (skill `tarol-jobs`); read `Rows`, `DeadJobs`,
   `Overdue` and call `Redrive(id)` from admin code; never update `tarol.jobs` by hand (pause a job with configuration).
11. **Operations** (step 15): the data source `UseNpgsqlStore(connectionString)` builds traces Npgsql only inside an
   activity (`TraceRootCommands = true` opts out; `builder.TraceOnlyInActivities()` on a data source you build). The store
   registers the snapshot signals (`tarol.reactions.oldest_due`, `.backlog`, `.dead`, `tarol.jobs.overdue`, `.dead`) and
   its dead-letter sources; their `Redrive`/`Discard(id, actor)` write `operations_audit` in the same statement.
   `UseNpgsqlStore(Func)` registers the data source as `NpgsqlDataSource` unless one is registered.
12. **Query** the state with SQL on `tarol.streams` (`state->>'lot'`), always with `tenant = …` and `type = '<StreamType>'`.

## Example

```csharp
var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
var codec = new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();
var lookups = new StoreLookups().Define<Member>("email", m => m.Email);
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = "skill", Lookups = lookups });
await store.EnsureSchema(ct);                                         // creates or upgrades; another process may hold the lock
(await store.EnsureSchema(SchemaMode.VerifyOnly, ct)).Version.ShouldBe(NpgsqlSchema.Version);

var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
var member = Member.Join(Id<Member>.New(), "a@x");
(await store.Append(member, 0, context, Option.None, ct)).IsSuccess.ShouldBeTrue();
AggregateChanges.Committed(member);

var loaded = (await store.Load(member.Id, context, ct)).GetValueOrThrow();
var expected = loaded.Version;
loaded.Earn(5).IsSuccess.ShouldBeTrue();
(await store.Append(loaded, expected, context, Option.None, ct)).IsSuccess.ShouldBeTrue();
(await store.Find<Member>("email", "a@x", context, ct)).GetValueOrThrow().ShouldBe(member.Id);

var checkpoint = GlobalPosition.Start;
await foreach (var e in store.ReadAll(checkpoint, 500, ct))
    checkpoint = e.Position;                                       // persist it with the projection
(await store.RebuildStates<Member>(ct)).ShouldBe(0);
_ = await store.PurgeKeys(TimeProvider.System.GetUtcNow() - TimeSpan.FromDays(7), 1000, ct);
```

## Personal data (step 16)

* `NpgsqlKeyStore` keeps the keys in `tenant_keys` and `subject_keys` (schema version 3) and caches them per process. With
  `NpgsqlStoreOptions.Keys` (or `UseNpgsqlKeyStore()` plus an `IKeyEncryptionKey` in DI), the store seals
  `[PersonalData]` on `Append` and opens it in `Load`, `ReadAll`, the outbox and `ReadStream`. It records the subjects in
  `events.subjects` / `streams.subjects`, and a personal lookup keeps a blind index.
* A sealing store raises `compatible_from` to `NpgsqlSchema.SealingVersion` (3) on `EnsureSchema`, so pods from before
  sealing refuse the schema instead of reading envelopes as values. A store without keys writes exactly what it wrote
  before and still runs on older schemas.
* Events written before a member was marked stay plaintext until `store.SealHistory<T>(500, ct)`: an explicit, batched
  rewrite that is safe to rerun.
* For `Tarol.DataProtection`: `StreamsOf` / `EventsOf(tenant, subject)` (opened with that subject's key only),
  `StateOf`, `DeletePersonalLookups`, and `DeleteStreams(type, olderThan, 500)`, which skips streams with pending reactions.
* Erase through `IDataSubjects`. `keys.EraseSubject` alone does not make projections or caches forget.

```csharp
await using var keys = new NpgsqlKeyStore(db, master, TimeProvider.System, new NpgsqlKeyStoreOptions { Schema = "tarol" });
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Keys = keys, Lookups = lookups });
await store.EnsureSchema(ct);                       // version 4, compatible_from raised to 3
```

## Common mistakes

| Mistake | Result |
|---|---|
| `NpgsqlDataSource.Create(cs)` in a Native AOT app | trimming/AOT warnings from Npgsql's full type mapping; use the slim builder |
| slim builder without `EnableArrays()` | an Npgsql exception on the first `Append` (the events are sent as arrays) |
| an event missing from the context | `InvalidOperationException` from `codec.Add<T>()` listing the events |
| `Load<T>` of an aggregate not added to the codec | `InvalidOperationException`: "not registered with the event codec" |
| `Schema = "MyApp"` or with spaces | `ArgumentException`: plain lowercase identifiers only |
| `SELECT … FROM tarol.events WHERE position > @last` in a projection | skips late commits; use `ReadAll` and `GlobalPosition` |
| a projection that never sees new events | an idle transaction holds the feed back (`pg_stat_activity`, `state = 'idle in transaction'`) |
| catching the exception of an unknown alias | data is hidden; pin the alias with `[EventType]` or add an `IEventUpcaster` |
| `e => e is Bid.Placed ? ["Notify"] : []` as the `ReactionTable` | CS9203: return spans over `static readonly string[]` |
| one reaction listed twice for an event | `InvalidOperationException` (unique (reaction, event_id)); nothing is committed |
| completing a row after its lease expired | `Complete` returns `false`: another worker owns it now |
| `UPDATE tarol.jobs SET next_at = …` to skip or pause a job | a held claim's `Advance` overwrites it; use `Tarol:Jobs:<name>:Enabled` |
| deleting rows of `tarol.events` | the stream no longer replays; events are append-only |
| `NpgsqlSchema.Script()` as the whole schema for a migration tool | version 1 only; `VerifyOnly` fails "has version 1; this code needs version 2": use `UpgradeScript(schema, 0)` |
| `UpgradeScript` in one Npgsql command or transaction | `25001`: `CREATE INDEX CONCURRENTLY` cannot run in a transaction block |
| old pods crash with "compatible from version N; … knows version M" | a contract step of a newer major version; deploy the newer package everywhere |
| a migration tool and `SchemaMode.Upgrade` both upgrading | two owners of one schema; pick one (`VerifyOnly` with the tool) |
| `UPDATE tarol.subject_keys SET wrapped = NULL` to erase | no `NOTIFY`: other processes keep the key cached; projections, caches and lookups keep the data; no audit |
| marking a member and expecting old events to be sealed | they stay plaintext: run `SealHistory<T>` once |
| a key store on another schema than the store's | its keys are not where `EnsureSchema` created the tables; use the store's schema |

## AOT

Plain SQL text, source-generated JSON, generated state code: 0 IL warnings with `NpgsqlSlimDataSourceBuilder`. The upgrader
is plain SQL too (`UpgradeScript` and `EnsureSchema` run in the AOT smoke app).

Logs: 8001 step applied, 8002 step retried after a lock timeout, 8003 invalid index dropped, 8004 newer compatible schema,
8005 refused.
