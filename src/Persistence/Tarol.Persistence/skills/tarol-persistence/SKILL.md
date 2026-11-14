---
name: tarol-persistence
description: >-
  Use when loading or committing Tarol aggregates through the store port, writing tests with InMemoryAggregateStore, or
  implementing IAggregateStore for a database. Covers IAggregateReader (Load, Find), IAggregateStore (Append with
  expected version and IdempotencyKey, Seen), StoredOutcome, StoreErrors, DefineLookup, RecordedEvent recording and
  stored-state loads. Do not decompile or web-search, do not write your own in-memory event store, do not retry inside
  Append or call Committed before Append succeeded.
---
# Tarol.Persistence

## Rules

1. **Tenant from the context.** Every call takes the `OperationContext`; its `Tenant` scopes the stream, the lookups and
   (by convention) the `IdempotencyKey`. Never pass a tenant from user input.
2. **Load → decide → Prepare → Append → Committed.** Remember `expected = aggregate.Version` right after the load.
   `Append(aggregate, expected, context, key, ct)` returns a `Result`; on success call
   `AggregateChanges.Committed(aggregate)`. On any failure discard the instance. Creating: expected version `0`.
3. **Conflicts are results.** `StoreErrors.IsVersionConflict(result)` → someone committed first; return it (409) or,
   in a worker, retry later with a fresh load. `Idempotency.DuplicateKey` and `Lookup.Taken` are also `Error.Conflict`.
4. **Keys only for redeliverable work** (webhooks, reactions, consumers, REST `Idempotency-Key`): check `Seen(key)`
   first and return the stored outcome; pass the key to `Append` so it is stored in the same commit. Failures store no key.
5. **Lookups** are unique secondary keys: `DefineLookup<T>(name, a => a.Key)` on the in-memory store, then
   `Find<T>(name, key, context, ct)` → `Option<Id<T>>`. Empty or `null` key = not indexed.
6. **Tests**: `new InMemoryAggregateStore(fakeTime, MyJson.Default.Options)` exercises the stored state like production;
   assert on `Recorded` (`RecordedEvent.Context`, `RecordedAt`, `Event.Version`) and `History<T>(id, tenant)`.

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

## AOT

No reflection: the store calls the aggregate's generated static members and writes JSON through the options you pass.
Put the state's member types in your `JsonSerializerContext` (with `ValueObjectJson.Options(context)` for value objects).
