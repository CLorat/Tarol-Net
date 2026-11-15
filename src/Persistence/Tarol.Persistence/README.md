# Tarol.Persistence

The **aggregate store port** of Tarol, and an **in-memory store** that keeps the same contract for tests and samples.
The change pipeline (`Tarol.Changes`) loads and commits through it; `Tarol.Persistence.Npgsql` implements it for
production. Native-AOT ready. Depends on `Tarol.Primitives` (and through it Kernel, Results, Options, StronglyTypedId).

## Install

```bash
dotnet add package Tarol.Persistence
```

## 30 seconds

```csharp
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;

var store = new InMemoryAggregateStore(time, stateOptions)          // stateOptions: your JsonSerializerContext's options
    .DefineLookup<Auction>("lot", a => a.Lot);                      // a unique key besides the id

// load (tenant-scoped): None when missing or in another tenant
if (!(await store.Load(id, context, ct)).TryGetValue(out Auction? auction)) return Error.NotFound("Auction.NotFound");
var expected = auction.Version;

// decide, check, append with the expected version (and a key for redelivery), then mark committed
var decided = auction.Close(time.GetUtcNow());
if (!decided.IsSuccess || !AggregateChanges.Prepare(auction).IsSuccess) return …;           // discard the instance
var appended = await store.Append(auction, expected, context, new IdempotencyKey(context.Tenant, "payments", "evt_1"), ct);
if (StoreErrors.IsVersionConflict(appended)) return appended;                                // 409: the caller retries
AggregateChanges.Committed(auction);

// redelivery of evt_1: the first outcome, nothing runs
(await store.Seen(new IdempotencyKey(context.Tenant, "payments", "evt_1"), ct)).TryGetValue(out StoredOutcome first);
```

## The contract

| Rule | What it means |
|---|---|
| Tenant isolation | a stream is (tenant from the `OperationContext`, aggregate type, id); another tenant's stream does not exist (`None`), and its lookups do not match |
| Atomic append | events, current state, lookups and the idempotency key are committed together or not at all |
| Expected version | the stream's version at load; `0` = must not exist. Mismatch → `Error.Conflict("Stream.VersionConflict")`, never an exception |
| Idempotency | a key is stored only with a successful commit; `Seen(key)` returns `StoredOutcome(StreamId, Version, Events)`; a key stored meanwhile → `Idempotency.DuplicateKey` |
| Lookups | unique per (tenant, type, lookup); a key that belongs to another aggregate → `Lookup.Taken`; `Find` of an undefined lookup throws |
| The caller commits | `Append` never changes the aggregate; call `AggregateChanges.Committed` after success |
| Programming errors throw | uncommitted changes that do not start at `expected + 1` → `ArgumentException` |

## In memory

`InMemoryAggregateStore(TimeProvider, JsonSerializerOptions? stateOptions)` (or `(TimeProvider, InMemoryStoreOptions)`
with state options, codec and lookups) records every event as a `RecordedEvent`
(event id UUIDv7, `RecordedAt` = one time per commit from the clock, the `OperationContext`), exposes `Recorded`,
`History<T>(id, tenant)` and `Loads`. With `stateOptions` it writes the stored state on every commit and loads from it
(`T.ReadState`), falling back to a replay when the state is missing, of another schema or unreadable; `[LoadByReplay]` aggregates
always replay. It is thread-safe; concurrent appends to one stream commit exactly one.

## Events in storage (step 8)

A database store keeps events as (stream type, alias, version, JSON payload). The names come from the Primitives
generator: `T.StreamType` (`[StreamType]` or the snake-cased aggregate name) and `T.EventTypes` (alias, version and
type of every event; `[EventType]` pins an alias). `EventCodec` turns events into payloads and back through your
`JsonSerializerContext`, and upcasts old payloads:

```csharp
var codec = new EventCodec(ValueObjectJson.Options(AppJson.Default))   // every event listed with [JsonSerializable]
    .Add<Auction>()                                                     // throws now if an event has no metadata
    .Upcast<Auction>(new OpenedV1ToV2());                               // IEventUpcaster: (alias, v1) → (alias, v2)
var lookups = new StoreLookups().Define<Auction>("lot", a => a.Lot);   // share with the production store
var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = codec, Lookups = lookups });
```

With a codec the in-memory store sends every event through JSON, as the database does. An unknown alias or version
that no upcaster moves throws on load (events are the truth); a stored state that does not parse falls back to a
replay. Both stores implement `IEventFeed`: `ReadAll(GlobalPosition after, int limit, ct)` returns every committed
event in commit order with its `GlobalPosition` (transaction, position), the checkpoint of a projection.

## Reactions and the outbox

`IOutboxReactions` tells a store which reactions an event has, when a scheduled one is due and which reactions are
scheduled; the generated `GeneratedReactions.Set` of `Tarol.Changes` implements it, `OutboxReactions.From(ReactionTable)`
wraps a hand-written table. With `InMemoryStoreOptions.Reactions` (or `NpgsqlStoreOptions.Reactions`) every commit writes
one outbox row per (event, reaction), and `store.Outbox` (`IOutbox`) serves them to a worker:

| Call | Effect |
|---|---|
| `Claim(max, lease, ct)` | due **heads** with their event and `Traceparent`: the oldest row of a (tenant, type, stream, reaction), or any row of a scheduled reaction (by due time) |
| `Complete(row, ct)` | removes it; the next row of its stream becomes the head |
| `Retry(row, at, reason, ct)` | due again at `at`, stays the head (later rows wait) |
| `Bury(row, reason, ct)` | moves it to the dead letters; the next row runs |
| `DeadLetters(max, ct)`, `Redrive(id, ct)` | read them; re-drive one behind the rows that waited |
| `Pending(ct)`, `WaitForRows(timeout, ct)` | live rows; wait for a commit or the poll interval |

Each returns `false` after a lost lease. The in-memory outbox follows the same rules, so tests see event → reaction →
change → its reactions with a fake clock; the worker is `Tarol.Sagas`.

## AOT

No reflection: loads call the aggregate's generated static `Rehydrate`/`ReadState`, the state is written by generated
code, and JSON goes through the options you pass. The smoke app publishes with 0 IL warnings.
