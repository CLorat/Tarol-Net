---
name: tarol-persistence-npgsql
description: >-
  Use when storing Tarol aggregates in PostgreSQL, wiring NpgsqlAggregateStore, creating its schema, reading the global
  event feed for a projection, rebuilding stored states after a model change, purging idempotency keys, or testing
  against a real database. Covers NpgsqlAggregateStore, NpgsqlStoreOptions, NpgsqlSchema, EnsureSchema, ReadAll,
  RebuildStates, PurgeKeys, the outbox (Reactions, IOutboxReactions, Outbox.Claim/Complete/Retry/Bury/DeadLetters/Redrive,
  Notify, OutboxRow), the job rows (Jobs, jobs, jobs_dead) and the tables. Do not add an ORM, do not write your own SQL for appends or loads, do not
  page the events table by position, do not decompile or web-search.
---
# Tarol.Persistence.Npgsql

## Rules

1. **Data source**: `new NpgsqlSlimDataSourceBuilder(cs).EnableArrays().Build()` (AOT-safe; arrays are required).
2. **Codec first**: `new EventCodec(ValueObjectJson.Options(AppJson.Default)).Add<T>()` for every aggregate; every event
   needs `[JsonSerializable]` in the context. The stored state uses the codec's options unless
   `NpgsqlStoreOptions.StateOptions` says otherwise.
3. **One `StoreLookups`** for production and the in-memory store of your tests (`InMemoryStoreOptions.Lookups`).
4. **Schema**: `await store.EnsureSchema(ct)` at startup (dev, tests, Aspire) or run `NpgsqlSchema.Script(schema)` with
   your migration tool. `NpgsqlStoreOptions.Schema` is a plain lowercase identifier (default `tarol`).
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
11. **Query** the state with SQL on `tarol.streams` (`state->>'lot'`), always with `tenant = …` and `type = '<StreamType>'`.

## Example

```csharp
var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
var codec = new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();
var lookups = new StoreLookups().Define<Member>("email", m => m.Email);
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = "skill", Lookups = lookups });
await store.EnsureSchema(ct);

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

## AOT

Plain SQL text, source-generated JSON, generated state code: 0 IL warnings with `NpgsqlSlimDataSourceBuilder`.
