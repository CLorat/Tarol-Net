---
name: tarol-persistence
description: >-
  Use when loading or committing Tarol aggregates through the store port, writing tests with InMemoryAggregateStore, or
  implementing IAggregateStore for a database. Covers IAggregateReader (Load, Find), IAggregateStore (Append with
  expected version and IdempotencyKey, Seen), StoredOutcome, StoreErrors, DefineLookup and StoreLookups, RecordedEvent
  recording, stored-state loads, the EventCodec (aliases, IEventUpcaster), StreamType and the IEventFeed (ReadAll,
  GlobalPosition). Do not decompile or web-search, do not write your own in-memory event store, do not retry inside
  Append or call Committed before Append succeeded.
---
# Tarol.Persistence

## Rules

1. **Tenant from the context.** Every call takes the `OperationContext`; its `Tenant` scopes the stream, the lookups and
   (by convention) the `IdempotencyKey`. Never pass a tenant from user input.
2. **Load → decide → Prepare → Append → Committed.** Remember `expected = aggregate.Version` right after the load.
   `Append(aggregate, expected, context, key, ct)` returns a `Result<GlobalPosition>` (the commit's position in the feed,
   for read-your-writes; `GlobalPosition.Start` when nothing was appended); on success call
   `AggregateChanges.Committed(aggregate)`. On any failure discard the instance. Creating: expected version `0`.
3. **Conflicts are results.** `StoreErrors.IsVersionConflict(result)` → someone committed first; return it (409) or,
   in a worker, retry later with a fresh load. `Idempotency.DuplicateKey` and `Lookup.Taken` are also `Error.Conflict`.
4. **Keys only for redeliverable work** (webhooks, reactions, consumers, REST `Idempotency-Key`): check `Seen(key)`
   first and return the stored outcome; pass the key to `Append` so it is stored in the same commit. Failures store no key.
   A key may carry a `Fingerprint` (`new IdempotencyKey(…) { Fingerprint = hash }`): stored with the outcome; the change
   pipeline refuses the same key with another fingerprint (`StoreErrors.KeyReused`, 422).
5. **Lookups** are unique secondary keys: `DefineLookup<T>(name, a => a.Key)` on the in-memory store, then
   `Find<T>(name, key, context, ct)` → `Option<Id<T>>`. Empty or `null` key = not indexed.
6. **Tests**: `new InMemoryAggregateStore(fakeTime, MyJson.Default.Options)` exercises the stored state like production;
   assert on `Recorded` (`RecordedEvent.Context`, `RecordedAt`, `Event.Version`) and `History<T>(id, tenant)`.
7. **Outbox** (`IOutbox`, both stores): `InMemoryStoreOptions { Reactions = GeneratedReactions.Set }` writes one row per
   (event, reaction) in the commit; `store.Outbox.Claim` returns heads (oldest per stream and reaction; scheduled ones by
   due time), then `Complete`, `Retry(row, at, reason)` or `Bury(row, reason)`; all `false` after a lost lease.
8. **Job rows** (`IJobRows`, `store.Jobs`): written for the job host of `Tarol.Jobs` (skill `tarol-jobs`); application code
   only reads them (`Rows`, `DeadJobs`, `Overdue`) and re-drives (`Redrive(id)`). Times are whole seconds.

## Example

```csharp
var time = new FakeTimeProvider(start);
var store = new InMemoryAggregateStore(time, TestJson.Default.Options).DefineLookup<Account>("number", a => a.Number);
var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(time), time);

var account = Account.Open("A-1");                                    // static factory raised Opened
(await store.Append(account, 0, context, Option.None, ct)).IsSuccess.ShouldBeTrue();
AggregateChanges.Committed(account);

var loaded = (await store.Load(account.Id, context, ct)).GetValueOrThrow();
var expected = loaded.Version;                                        // 1
loaded.Deposit(10).IsSuccess.ShouldBeTrue();
AggregateChanges.Prepare(loaded).IsSuccess.ShouldBeTrue();
var key = new IdempotencyKey(context.Tenant, "payments", "evt_1");
(await store.Append(loaded, expected, context, key, ct)).IsSuccess.ShouldBeTrue();
AggregateChanges.Committed(loaded);

(await store.Seen(key, ct)).GetValueOrThrow().ShouldBe(new StoredOutcome(account.Id.Value, 2, 1));
(await store.Find<Account>("number", "A-1", context, ct)).GetValueOrThrow().ShouldBe(account.Id);
store.Recorded[^1].Context.ShouldBe(context);
```

## Events in storage: codec, upcasters, feed

7. **Stored names are generated**: `T.StreamType` (snake case of the aggregate name, or `[StreamType("…")]`) and
   `T.EventTypes` (alias + version + type per event). Pin `[EventType("alias", version)]` before renaming an event and
   `[StreamType]` before renaming an aggregate; two aggregates with one stream type are TPRM028.
8. **`EventCodec`** writes payloads through your `JsonSerializerContext`: list every event with `[JsonSerializable]`,
   use `ValueObjectJson.Options(context)` when events hold value objects. `Add<T>()` throws at once for a missing event.
9. **Change a stored event** by raising its version and registering an `IEventUpcaster` from the old (alias, version);
   upcasters chain, also across a renamed alias. An unknown alias or version throws on load: never catch it.
10. **Tests catch serialization bugs** with `new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = codec,
    Lookups = lookups })`: every event goes through JSON, the state uses the codec's options. Share `StoreLookups`
    with the production store.
11. **Projections** read `IEventFeed.ReadAll(checkpoint, limit, ct)` and store `PositionedEvent.Position`
    (a `GlobalPosition`); never page by a sequence number of your own.

```csharp
public sealed class CancelledV1ToV2 : IEventUpcaster             // v1 had no reason
{
    public EventType From => new("subscription.cancelled", 1);
    public EventType Into => new("subscription.cancelled", 2);
    public void Upcast(JsonObject payload) => payload["reason"] = "unknown";
}

var codec = new EventCodec(new JsonSerializerOptions(CodecJson.Default.Options))
    .Add<Subscription>()
    .Upcast<Subscription>(new CancelledV1ToV2());
var lookups = new StoreLookups().Define<Subscription>("plan", s => s.Plan);
var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = codec, Lookups = lookups });

var subscription = Subscription.Start("pro");
subscription.Cancel("price");
(await store.Append(subscription, 0, context, Option.None, ct)).IsSuccess.ShouldBeTrue();

codec.Decode(Subscription.StreamType, new EventType("subscription.cancelled", 1), "{}"u8)
    .ShouldBe(new Subscription.Cancelled("unknown"));
await foreach (var e in store.ReadAll(GlobalPosition.Start, 100, ct))
    e.StreamType.ShouldBe("subscription");
```

## Common mistakes

| Mistake | Result |
|---|---|
| `Append(aggregate, aggregate.Version, …)` after the behaviour ran | `ArgumentException` (`expectedVersion`): use the version from before the behaviour |
| ignoring the `Result` of `Append` | TRES002 (Tarol.Results analyzer) |
| `AggregateChanges.Committed` before `Append` succeeded | the events are lost from `UncommittedChanges`; the store never saw them |
| reusing an instance after a version conflict | stale state: load again |
| loading with another tenant's context | `None`, by design (a 404, never a 403) |
| `Find<T>("iban", …)` without `DefineLookup<T>("iban", …)` | `InvalidOperationException` naming the missing definition |
| a member type of the state missing from your JSON context | `NotSupportedException` from `WriteState` during `Append` |
| an event missing from the context given to `EventCodec` | `InvalidOperationException` from `Add<T>()` listing the events |
| a renamed event without `[EventType]` pin or upcaster | `InvalidOperationException` on load: "no upcaster moves it" |
| `AppJson.Default.Options` with value objects in events | TVAL009; use `ValueObjectJson.Options(AppJson.Default)` |

## Operations contracts (step 15)

* `IOutbox.DeadLetters(DeadLetterQuery)` / `IJobRows.DeadJobs(DeadLetterQuery)`: keyset pages (`After` = the last id,
  `Limit` ≤ 200, `Tenant`, `Name`); `Discard(id)` deletes one for good; `IOutbox.OldestDue()` is the backlog's age (alert
  on it, not on `Pending`, a count of every row).
* A package registers `OperationsSignal` singletons (one number of the snapshot, read once per poll by
  `Tarol.Operations`, never per scrape) and `IDeadLetterSource`s (kinds `reactions`, `jobs`, `messages`); `UseStore`
  registers the store's. Names are `tarol.<area>.<what>` (`OperationsSignals`), tags never the tenant.

## AOT

No reflection: the store calls the aggregate's generated static members and writes JSON through the options you pass;
the codec maps aliases with the generated `T.EventTypes` and source-generated metadata.
Put the state's member types in your `JsonSerializerContext` (with `ValueObjectJson.Options(context)` for value objects).
