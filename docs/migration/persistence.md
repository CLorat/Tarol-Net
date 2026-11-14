# Persistence: `Tarol.Persistence` (step 7, PR 7A)

The store port and the in-memory store, as decided in the step 7 design ([changes.md](changes.md) C-6, C-8, C-15,
C-16; concept [docs/architecture/changes.md](../architecture/changes.md) §7). Step 8 adds `Tarol.Persistence.Npgsql` and
continues this note. Labels as in the [conventions](../architecture/conventions.md).

## 1. Origin

A new package, not a port: the old `CLorat/Tarol@6a62f81` has `src/Persistence/Persistence.Marten` (not migrated,
ROADMAP D-1) and no store abstraction the change pipeline could use **[verified by reading]**. The shape comes from the
step 7 probe (`probes/changes/Core/Ports.cs`, `InMemoryAggregateStore.cs`), hardened here.

## 2. Public API (all new)

| Type | Members |
|---|---|
| `IAggregateReader` | `Load<T>(Id<T>, OperationContext, ct) : ValueTask<Option<T>>`; `Find<T>(lookup, key, OperationContext, ct) : ValueTask<Option<Id<T>>>` |
| `IAggregateStore : IAggregateReader` | `Append<T>(T, long expectedVersion, OperationContext, Option<IdempotencyKey>, ct) : ValueTask<Result>`; `Seen(IdempotencyKey, ct) : ValueTask<Option<StoredOutcome>>` |
| `IdempotencyKey` | `(Option<Id<ITenant>> Tenant, string Scope, string Key)`, non-empty scope and key |
| `StoredOutcome` | `(Guid StreamId, long Version, int Events)` |
| `StoreErrors` | `VersionConflict(expected, actual)`, `DuplicateKey(key)`, `LookupTaken(lookup, key)` (all `Error.Conflict`), their codes, `IsVersionConflict(result)` |
| `InMemoryAggregateStore(TimeProvider, JsonSerializerOptions? stateOptions = null)` | the port, plus `DefineLookup<T>(name, keyOf)`, `Recorded`, `History<T>(id, tenant)`, `Loads` |

Differences from the probe and from changes.md §6 (deliberate):
* `Seen` returns `StoredOutcome` (a store type), not `ChangeOutcome`; `Tarol.Changes` maps it.
* The probe's test-only `Index` and `BeforeNextAppend` are gone: lookups are **defined** (`DefineLookup`) and maintained
  on every commit, as the Npgsql store will derive them from the state; races are tested with two loads.
* Lookups are unique: a key that belongs to another aggregate refuses the commit (`Lookup.Taken`); an empty or `null`
  key is not indexed; `Find` of an undefined lookup throws (a programming error, named in the message).
* Uncommitted changes that do not start at `expectedVersion + 1` throw `ArgumentException` (the caller passed the
  version after the behaviour, the most likely mistake), instead of being appended at the wrong version.
* An append with no uncommitted changes succeeds and stores nothing, not even the key.

## 3. Behaviour **[verified]** (`tests/Persistence/Tarol.Persistence.Tests`, 25 tests)

Missing stream → `None`; append + load with state and version; expected 0 on an existing stream → conflict; a stale
expected version → conflict and nothing written; wrong expected version → `ArgumentException`; the aggregate keeps its
uncommitted changes; empty append stores no key; tenant isolation for streams and lookups (and `None` tenant ≠ any
tenant); key stored with the commit, refused when stored meanwhile, not stored on failure, scoped by tenant and scope;
recorded events carry stream id, version, UUIDv7 event id, one `RecordedAt` per commit from the fake clock, and the
operation context; loads read the stored state (with options), replay without options and for `[LoadByReplay]`; state
equals replay; lookups find, move on a changed key, refuse a taken key, are tenant-scoped; undefined lookup throws;
cancellation; 16 concurrent appends to one stream → exactly one commits. Plus the skill example, verbatim.

## 4. Suppressions and analyzer configuration

None.

## 5. Verification (SDK 11.0.100, 2026-11-14)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | 684 total, 0 failed (659 before + 25 `Tarol.Persistence.Tests`) |
| `dotnet build Tarol.slnx -c Release` then `eng/verify-packages.sh` | OK: `Tarol.Persistence.0.0.0-alpha.0.14.nupkg` holds `lib/net11.0/Tarol.Persistence.dll`, `.xml`, `README.md`, `LICENSE`, `skills/tarol-persistence/SKILL.md`; the consumer passes 7/7 and its `project.assets.json` lists `tarol-persistence` and the dependencies' skills; the Primitives generator and Results analyzer reach it |
| `eng/aot-smoke.sh` | 7 apps, each 0 IL warnings; `Tarol.Persistence.AotSmoke` 7/7 natively |

## 6. Skill evaluation checklist (`tarol-persistence`)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| "load an account and deposit" | `Load` → remember `Version` → behaviour → `Prepare` → `Append(…, expected, …)` → `Committed` | expected version taken after the behaviour (`ArgumentException`); `Committed` before `Append` |
| "make a webhook redelivery safe" | `Seen(key)` first; `Append(…, key, …)` | storing the key in a second step; keys without the tenant |
| "find an auction by lot number in a test" | `DefineLookup<Auction>("lot", a => a.Lot)`, `Find<Auction>("lot", …)` | scanning `Recorded`; `Find` without a definition |
| "assert who caused an event" | `store.Recorded[^1].Context.Actor` | putting actor data into the event |
| "handle a concurrent writer" | `StoreErrors.IsVersionConflict(result)` → return 409 / retry later with a fresh load | reusing the stale instance |

## 7. What step 8 must do

Implement `IAggregateStore` in `Tarol.Persistence.Npgsql` against the contract in the README (tenant in every key and
filter, one transaction for events + state + lookups + key + outbox rows, `Stream.VersionConflict` from the expected
version), decide how lookups are declared for the database (a column, or an expression index on the state JSON), the
key retention, and run this note's behaviour tests against both stores (a shared contract test suite).
