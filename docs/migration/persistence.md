# Persistence: `Tarol.Persistence` (step 7, PR 7A)

The store port and the in-memory store, as decided in the step 7 design ([changes.md](changes.md) C-6, C-8, C-15,
C-16; concept [docs/architecture/changes.md](../architecture/changes.md) §7). Step 8 adds `Tarol.Persistence.Npgsql`; its
design evaluation continues this note from §8. Labels as in the [conventions](../architecture/conventions.md).


> **Changed in step 10A** ([rest.md](rest.md) §14.2): idempotency keys carry an optional fingerprint (column
> `idempotency_keys.fingerprint`, 422 `Idempotency.KeyReused` on reuse); `TarolBuilder`, `UseInMemoryStore`, `UseNpgsqlStore`,
> `IIdempotencyKeyPurge`.

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

Designed in §8–§19 (owner answers §15, implementation §16); the paragraph below is the brief from step 7.


Implement `IAggregateStore` in `Tarol.Persistence.Npgsql` against the contract in the README (tenant in every key and
filter, one transaction for events + state + lookups + key + outbox rows, `Stream.VersionConflict` from the expected
version), decide how lookups are declared for the database (a column, or an expression index on the state JSON), the
key retention, and run this note's behaviour tests against both stores (a shared contract test suite).

---

# Step 8 design: `Tarol.Persistence.Npgsql` (evaluation)

Evaluation of [docs/architecture/persistence.md](../architecture/persistence.md): inventory of the old persistence code,
candidates for the schema, the codec and lookups, probes and measurements against a real PostgreSQL 17, the owner's
decisions, and what the three implementation PRs must do. **Design only**: this PR changes no package code. Probes and
output: [`probes/persistence/`](probes/persistence/), [`output.txt`](probes/persistence/output.txt). Source of the old
code: `CLorat/Tarol@6a62f81`.

## 8. Summary

* **Nothing of the old persistence is ported** **[verified by reading]**: the old aggregate path never wrote events. It
  stored the aggregate object as a Marten document, while the repository read a different snapshot type (old review
  findings 1–3 still hold). The only real event-stream append with an expected version is in the saga store.
* **Decided shape** (P-5, P-6): shared tables `streams` / `events` / `lookups` / `idempotency_keys` / `outbox` with the
  tenant first in every key; one commit is one guarded statement plus guarded follow-ups in one `NpgsqlBatch`; a lost
  race is a row count, not an exception.
* **Codec** (P-2, P-7, P-13): a library `EventCodec` in `Tarol.Persistence` over a generated alias table (`T.Events`,
  `T.StreamType`) and the user's `JsonSerializerContext` with `ValueObjectJson.Resolver` first; upcasters on `JsonObject`.
* **Correct under contention** **[verified]**: 1, 4, 16 and 64 writers on one stream, every guard: versions gapless,
  conflicts mapped to `Stream.VersionConflict`, 0 exceptions leaking.
* **Native AOT** **[verified]**: 0 IL warnings, all 55 functional checks pass natively against PostgreSQL.
* **One contract suite** **[verified]**: 10 tests × 2 stores = 20/20, Testcontainers `postgres:17`, ≈ 4 s.
* **Found while probing**: an untargeted `ON CONFLICT` hides lookup violations; a lost race must not run the rest of
  the batch unguarded; a plain sequence checkpoint loses late commits, and the xmin filter alone is not enough;
  `InMemoryAggregateStore.Load` copies the whole history before checking the state.

## 9. Inventory of the old code **[verified by reading]**

### 9.1 Public API (`src/Persistence/*`, Marten 9.45.0)

| Type | Members | Ref |
|---|---|---|
| `IAggregateRootRepository<T>` | `ValueTask<Result<T>> GetById(Id<T>, ct)`; an empty id returns a fresh tracked instance; no `Add`/`Save` | `Persistence/IAggregateRootRepository.cs:14-21` |
| `IUnitOfWork : IAsyncDisposable` | `IsCommitted`, `IsPoisoned`, `Track<T>`, `MarkForRollback`, `CommitAsync` | `Persistence/IUnitOfWork.cs:36-72` |
| `IUnitOfWorkScope` | `ExecuteAsync(Func<IServiceProvider, CT, Task<Result>>)` (+ generic) | `Persistence/IUnitOfWorkScope.cs:30-39` |
| `IRepositoryInterceptor` | staged/committed hooks per aggregate and event; no transaction passed | `Persistence/IRepositoryInterceptor.cs:40-69` |
| `PersistenceErrors` | `AggregateNotFound`, `UnitOfWorkPoisoned`, `UnitOfWorkDisposed`, `CascadeLimitExceeded`, `PersistenceFailed`, `PostCommitHookFailed` | `Persistence/PersistenceErrors.cs:5-49` |
| `MartenUnitOfWork` (namespace `YourApp.Persistence.Marten`) | the `IUnitOfWork`, cascade loop ≤ 32, one `SaveChangesAsync` | `Persistence.Marten/MartenUnitOfWork.cs:10-232` |
| `MartenSnapshotRepository<T, TSnapshot>` | `GetById` (loads `TSnapshot` document), `RebuildSnapshotFromEvents` | `Persistence.Marten/MartenSnapshotRepository.cs:9-90` |
| `UnitOfWorkScope`, `AddSnapshotAggregate<T, TSnapshot>` | DI scope, the only registration | `MartenUnitOfWorkScope.cs`, `MartenSnapshotRepository.cs:92-99` |
| Sagas: `ISagaStore`, `ISagaOutbox`, `MartenSagaStore` | `CommitAsync(SagaCommit)` with expected version and outbox documents; `ClaimDueAsync/Complete/Retry/DeadLetter` | `src/Sagas/Sagas/Abstractions/Abstractions.cs:17-108`, `Sagas.Persistence.Marten/MartenSagaStore.cs` |

### 9.2 What Marten did that the new store needs

| Concern | Old code | New store |
|---|---|---|
| append + expected version | aggregates: **never** (`session.Store(aggregate)`, `Events.Append` only in a comment, `MartenUnitOfWork.cs:118,292-295`); sagas: `StartStream`/`Append(id, expected + n, …)`, Marten's version is the one *after* the append (`MartenSagaStore.cs:116-119`) | expected = version at load (step 7 contract); guarded statement (P-6) |
| conflicts | sagas match exception **type names** in the chain (`MartenSagaStore.cs:270-295`); aggregates map every exception to `commit.failed` (`MartenUnitOfWork.cs:154-161`) | a row count → `Stream.VersionConflict`; `23505` mapped by constraint name; other errors stay exceptions |
| stream identity | `Guid` only, no stream type, no tenant | `(tenant, type, id)` |
| serialization | Marten STJ with reflection; Marten's default alias (short CLR name) for aggregates | `EventCodec`, source-generated metadata, aliases from Primitives |
| metadata | none for aggregate events; the rebuild overwrites the domain timestamp with Marten's (`MartenSnapshotRepository.cs:81`) | columns per event (P-9), one `RecordedAt` per commit |
| snapshots | "the snapshot is the document"; written only by `RebuildSnapshotFromEvents`; schema hash throws on mismatch, not caught (`MartenSnapshotRepository.cs:44-89`) | stored state on every commit, another schema → replay + rewrite (primitives.md §16) |
| tenancy | none | the tenant first in every key |
| outbox | sagas only, documents, claimed one by one with an optimistic version (no `SKIP LOCKED`) | table rows in the commit, `FOR UPDATE SKIP LOCKED`, head per (stream, reaction) |
| upcasting | the Primitives codec's `IEventUpcaster.TryUpcast(ref alias, ref version, ref payload)` (`src/Primitives/Primitives/EventCodec.cs:39-43`), one instance, not wired into any store | per (alias, version) upcasters on `JsonObject`, chained by the codec |

The old Primitives codec (moved to step 8 by R-2) chose the alias from `[EventType]` or snake case of
`<aggregate>.<containers>.<event>` (`CodecEmitter.cs:48,55-84`), threw `UnknownEventType` / `UnknownEventVersion` for an
unknown alias or version without an upcaster (`CodecEmitter.cs:139-189`), and restored the envelope through
`[UnsafeAccessor]` setters. The new design keeps the alias rule (already in Tarol-Net's Primitives) and the fail-loud
behaviour; the envelope is no longer on the event (domain-core.md §3.3 c), so nothing is restored.

### 9.3 Old review findings (`docs/review/persistence.md` §3) re-verified

| # | Finding | Status at `6a62f81` | In the new design |
|---|---|---|---|
| 1 | events never persisted | holds (`MartenUnitOfWork.cs:118,292-295`) | events are the primary write |
| 2 | UoW writes `T`, repository reads `TSnapshot` | holds | one stream row, written and read by generated `WriteState`/`ReadState` |
| 3 | no optimistic concurrency | holds | P-6 |
| 4 | envelope not deserializable by a user STJ context | no longer applies (envelope `[JsonIgnore]`, columns) | envelope is not on the event |
| 5 | commit not exception-safe, changes cleared before flush | holds (`:279-285` vs `:156`) | `Append` never changes the aggregate; the caller calls `Committed` |
| 6 | `OperationCanceledException` swallowed | holds (`:154-161`) | cancellation propagates |
| 7 | post-commit hook failure reported as failure | holds | no hooks; outbox rows in the commit |
| 8 | second `CommitAsync` drops changes | holds (`:95`) | no unit of work |
| 9, 10 | hooks and dual-write per cascade batch | hold | no cascade |
| 11 | replay overwrites the domain timestamp | holds | business time is payload (time.md); `RecordedAt` is a column |
| 12 | no `Add`/`Save` | holds | `Append` with expected 0 |
| 13 | JasperFx generator runs in consumers | holds for the csproj | no Marten |
| 14 | no DI | holds | constructor over `NpgsqlDataSource`; DI with step 9 |
| 15 | no alias strategy | holds for Marten | Primitives aliases + `T.StreamType` |
| 16–22 | error codes, namespaces, dead references, typos, rebuild in memory | hold | new names; `RebuildStates<T>` streams rows one by one |
| 23 | saga conflict detection by exception type name | holds | step 9 uses this store |

Old tests: 4 NSubstitute + 4 Testcontainers `postgres:16` tests of the snapshot repository only; nothing tests the
unit of work, concurrency or a load-commit-reload round trip (`tests/Persistence/Persistence.Marten.IntegrationTests`).

## 10. Candidates, as ArtFlow user code plus SQL plus library or generated code

### 10.1 Schema (question 1)

**S-A, shared tables (decided, P-5)**: [architecture §2](../architecture/persistence.md#2-schema-p-5-shared-tables-s-a),
full DDL in [`Core/Schema.cs`](probes/persistence/Core/Schema.cs). User code: none (`await store.EnsureSchema(ct)` or the
shipped script). Queries a user can write: `SELECT id FROM tarol.streams WHERE tenant = $1 AND type = 'auction' AND
state->>'lot' = $2`.

**S-B, tables per aggregate type** (not probed):

```sql
CREATE TABLE tarol.auction_streams (tenant uuid, id uuid, version bigint, schema int, state jsonb, PRIMARY KEY (tenant, id));
CREATE TABLE tarol.auction_events  (tenant uuid, stream_id uuid, version bigint, …, PRIMARY KEY (tenant, stream_id, version));
-- the generator (or EnsureSchema<T>()) emits DDL per aggregate; ReadAll = UNION ALL over every *_events table
```

| | S-A | S-B |
|---|---|---|
| DDL | one script, fixed | per aggregate; changes with every new aggregate |
| feed and outbox | one table each | a `UNION` over all event tables, or a shared outbox anyway |
| index size | one index over all streams (type in the key) | smaller per type **[unverified]** benefit |
| tenant in keys | yes | yes |

**Global position** (P-3, P-14): `position bigint identity` + `tx_id xid8 DEFAULT pg_current_xact_id()`; the feed orders
by `(tx_id, position)` ([architecture §9](../architecture/persistence.md#9-the-global-feed-p-3-p-14)).

### 10.2 Codec (question 2)

**K-A (decided, P-7)**: generated table + library codec + user context (architecture §4). What the Primitives generator
adds next to `EventTypeOf` (probe: [`App/Domain.cs`](probes/persistence/App/Domain.cs) `Catalogs`, by hand):

```csharp
// generated into partial class Auction
public static string StreamType => "auction";                    // [StreamType("…")] pins it (P-13)
public static EventCatalog Events { get; } = new("auction", [
    new(new("auction.opened", 2), typeof(Auction.Opened)),       // [EventType("auction.opened", 2)]
    new(new("auction.relotted", 1), typeof(Auction.Relotted)),
    new(new("auction.closed", 1), typeof(Auction.Closed)),
    new(new("auction.bid.placed", 1), typeof(Bid.Placed)),       // default alias of a child event
]);
```

**K-B, the generator writes the JSON metadata** (PR #24's way): no `[JsonSerializable]` per event in user code, but the
generator emits `JsonMetadataServices` code per event (a second serializer next to System.Text.Json's own generator, to
keep in step with every STJ feature: required members, converters on properties, polymorphism); value objects still
need `ValueObjectJson.Resolver`.

```csharp
var codec = new EventCodec().Add<Auction>();   // generated Auction.EventJson : IJsonTypeInfoResolver
```

**K-C, hand registration**: no generator change; aliases are written twice and drift at run time.

```csharp
codec.Map<Auction.Opened>("auction.opened", 2, AppJson.Default.AuctionOpened)
     .Map<Auction.Closed>("auction.closed", 1, AppJson.Default.AuctionClosed);
```

### 10.3 Lookups (question 6)

**L-A, key function (decided, P-8)**:

```csharp
var lookups = new StoreLookups().Define<Auction>("lot", a => a.Lot);   // name for 8A; one definition, both stores
```

```sql
DELETE FROM tarol.lookups WHERE tenant=$1 AND type=$2 AND lookup=$3 AND stream_id=$4 AND key IS DISTINCT FROM $5 AND <ours>;
INSERT INTO tarol.lookups (tenant, type, lookup, key, stream_id) SELECT $1,$2,$3,$4,$5 WHERE <ours>
  ON CONFLICT (tenant, type, lookup, key) DO UPDATE SET stream_id = EXCLUDED.stream_id WHERE lookups.stream_id = EXCLUDED.stream_id;
-- 0 rows → another stream owns the key → Lookup.Taken, ROLLBACK
```

**L-B, expression index on the state** (probed, works, `Find` uses the index):

```sql
CREATE UNIQUE INDEX lookup_auction_lot ON tarol.streams (tenant, (state->>'lot')) WHERE type = 'auction';
-- 23505 on lookup_auction_lot → Lookup.Taken
```

**L-C, an attribute generating L-A's function** (not probed): `[Lookup("lot")] public string Lot { get; private set; }`.

## 11. Measurements **[verified]** ([`output.txt`](probes/persistence/output.txt); 4 cores, PostgreSQL 17.12 in local Docker)

### 11.1 Load and change cost (BenchmarkDotNet 0.16.0-preview.2, `--job short --inProcess`)

| Method | Events | Mean | Allocated |
|---|---|---|---|
| Load, in memory, state | 10 | 4.0 µs | 4.5 KB |
| Load, in memory, replay | 10 | 1.3 µs | 4.0 KB |
| Load, Npgsql, state | 10 | 277 µs | 7.4 KB |
| Load, Npgsql, replay | 10 | 285 µs | 11.5 KB |
| Load, in memory, state | 100 | 34 µs | 42.7 KB |
| Load, in memory, replay | 100 | 9.7 µs | 39.4 KB |
| Load, Npgsql, state | 100 | 407 µs | 48.3 KB |
| Load, Npgsql, replay | 100 | 565 µs | 82.1 KB |
| load + `Relot` + append, in memory | (growing stream) | 354–390 µs | **1.64 MB** |
| load + `Relot` + append, Npgsql | (growing stream) | 2.2–2.3 ms | 22.4 KB |

Reading: against the database the stored state is as fast as a replay at 10 events and ≈ 30 % faster at 100 with
≈ 60 % of the allocations. The in-memory change is O(stream length): `InMemoryAggregateStore.Load` copies the whole
history (`history = [.. stream.Events]`) before it checks the state, and the benchmark's stream grows with every
iteration (P-15 fixes it in 8A).

### 11.2 Contention (Release, JIT; N writers × attempts on one stream, each load → `Relot` → append)

| Guard | Writers | Attempts | ok | conflict | other / thrown | gapless | attempts/s | p50 ms | p99 ms |
|---|---|---|---|---|---|---|---|---|---|
| G-1 guarded CTE | 1 | 200 | 200 | 0 | 0 / 0 | yes | 371 | 2.5 | 6.5 |
| G-1 | 16 | 400 | 37 | 363 | 0 / 0 | yes | 677 | 22.8 | 59.0 |
| G-1 | 64 | 1600 | 92 | 1508 | 0 / 0 | yes | 752 | 84.6 | 250.2 |
| G-2 events PK (`23505`) | 64 | 1600 | 68 | 1532 | 0 / 0 | yes | 1430 | 34.2 | 203.8 |
| G-3 `FOR UPDATE` | 64 | 1600 | 136 | 1464 | 0 / 0 | yes | 1451 | 39.1 | 111.1 |
| G-4 fail fast | 64 | 1600 | 39 | 1561 | 0 / 0 | yes | 1591 | 31.0 | 146.7 |

All 16 rows (4 guards × 1/4/16/64 writers) are in `output.txt`; every row is gapless with 0 other results and 0
exceptions. Sampling `pg_stat_activity` during a 64-writer run shows both G-1 and G-3 waiting on `Lock:tuple` of the
stream row (G-1: 2076 samples in the `WITH g AS (UPDATE …)` statement; G-3: 1395 in the `SELECT … FOR UPDATE`): the
row lock serializes every guard; G-1 losers hold their batch longer. Round 2 showed the owner the Debug-build numbers
(G-1 80 ms / 812 att/s, G-3 31 ms / 1.6 k); the Release run above has the same ranking.

### 11.3 Native AOT

`dotnet publish -c Release -r linux-x64` of `App/` (`PublishAot`, `JsonSerializerIsReflectionEnabledByDefault=false`,
`NpgsqlSlimDataSourceBuilder` + `EnableArrays`): **0 IL warnings**, 7.9 MB, the native run passes the same 55 checks as
JIT, including upcasters, the feed and outbox claims.

### 11.4 Upcaster and schema change

* A v1 `auction.opened` row without `currency`, inserted by SQL, loads through `OpenedV1ToV2` as v2 (`Currency == "EUR"`,
  the `Title` value object read from a plain string); without the upcaster the load throws naming alias and version; a
  renamed alias throws the same way.
* `UPDATE streams SET schema = 42` → the load replays (counter), the next commit rewrites the row with
  `Auction.StateSchema`; `RebuildStates<Auction>()` rewrites the other stale rows (2) guarded by `version = $read`, so a
  concurrent commit is not overwritten.

### 11.5 Feed, outbox, keys, contract

* Feed: the three readers of [architecture §9](../architecture/persistence.md#9-the-global-feed-p-3-p-14), each with
  two raw-SQL transactions interleaved on two connections.
* Outbox: 10 streams × 5 events; O-1 with 4 workers claims 50 rows exactly once; O-2 processes 50 rows, per stream in
  version order, max 1 in flight per stream; the claim plan is a nested-loop anti-join on `outbox_stream`.
* Keys: stored with the commit, `Seen` returns `StoredOutcome(stream, version, events)`, a duplicate → `DuplicateKey` and
  nothing written, tenant-scoped, a failed commit stores none, `PurgeKeys` after 8 days (fake clock) deletes 1.
* Contract suite: `Probe.Persistence.Contract Total: 20, Errors: 0, Failed: 0` (10 tests per store).

## 12. Found while probing

1. **`ON CONFLICT DO NOTHING` without a conflict target** on the stream insert swallowed the L-B index violation; a
   taken lot returned `Stream.VersionConflict` **[verified]**. Rule: every `ON CONFLICT` names its key.
2. **With a row-count guard the batch runs on after a lost race**; an unguarded lookup insert hit `lookups_stream` and
   threw (10 exceptions in the first contention run) **[verified]**. Rule: follow-ups are guarded by "this commit's
   first event id exists".
3. **The conflict message needs the actual version** without an extra round trip: a final `SELECT version` in the same
   batch reads the winner's committed row under read committed **[verified]** (`expected version 3, found 4`).
4. **A G-1 conflict surfaced as `23505`** until the events insert was tied to the guard in one statement (data-modifying
   CTE) **[verified]**.
5. **`xmin` alone is not a safe checkpoint** (architecture §9) **[verified]**.
6. **`ReadOnlySpan<string>` from a lambda with a collection expression is CS9203** **[verified]**: the generated reaction
   table must return static arrays.
7. **jsonb as text**: parameters typed `NpgsqlDbType.Jsonb` with a string, read with `GetFieldValue<byte[]>` (UTF-8 JSON,
   no version byte); no `EnableJsonTypes` needed under the slim builder **[verified]**.
8. **`InMemoryAggregateStore.Load` copies the history first** (11.1) **[verified]**.
9. **TVAL009 fires on `AppJson.Default.Options`** in user code that holds value objects (the probe's "missing metadata"
   test had to build empty options): the analyzer already steers users to `ValueObjectJson.Options` **[verified]**.

## 13. Decided API (sketch for 8A–8C; names final in the PRs)

| Package | Type | Members |
|---|---|---|
| `Tarol.Primitives` (generator) | per aggregate | `static string StreamType`, `static EventCatalog Events` (on `IAggregateRoot<T>`); `[StreamType("…")]`; a diagnostic for two aggregates with one stream type in a compilation |
| `Tarol.Persistence` | `EventCodec` | `(JsonSerializerOptions)`, `Add<T>()`, `Upcast<T>(IEventUpcaster)`, `Encode<T>`, `Decode(streamType, alias, version, payload)` |
| | `IEventUpcaster` | `EventType From`, `EventType To`, `void Upcast(JsonObject payload)` |
| | `IEventFeed` | `IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, ct)`; `GlobalPosition(ulong Transaction, long Position)`, `PositionedEvent(GlobalPosition, string StreamType, RecordedEvent)` |
| | `StoreLookups` | `Define<T>(name, Func<T, string?>)`; `InMemoryAggregateStore.DefineLookup` stays as a shortcut |
| | `InMemoryAggregateStore` | + optional `EventCodec` (round trip, P-15), + `IEventFeed` |
| `Tarol.Persistence.Npgsql` | `NpgsqlAggregateStore : IAggregateStore, IEventFeed` | `(NpgsqlDataSource, EventCodec, TimeProvider, NpgsqlStoreOptions)`, `EnsureSchema`, `RebuildStates<T>`, `PurgeKeys(olderThan, batch)` |
| | `NpgsqlStoreOptions` | `Schema` (default `tarol`), `StateOptions`, `Lookups`, `Reactions` (`ReactionTable`, 8C) |
| | `NpgsqlSchema` | `Script(schema)`, `Version` |
| | `OutboxClaims` (8C) | `Claim(max, lease, now)`, `Complete(id)`, `Fail(id, retryAt, error)`; `OutboxRow` |

## 14. Decisions asked

Round 1 (scope, before probing): P-1 … P-4. Round 2 (after the measurements): P-5 … P-8. Round 3: P-9 … P-12. Round 4:
P-13 … P-16. Not asked because existing decisions answer them: no ORM (D-1, D-14), Testcontainers (D-15), the tenant in
every key and cross-tenant 404 (security.md), one `RecordedAt` per commit from `TimeProvider` (time.md), UUIDv7 event ids
(domain-core.md), keys for successes only in the commit (C-8), no retry of conflicts (C-7), stored-state shape and lazy
rewrite (primitives.md §16), `ValueObjectJson.Resolver` first (V-9).

## 15. Owner answers (2026-11-14)

| # | Question | Answer |
|---|---|---|
| P-1 | Outbox in step 8 | **Table + commit write + claims**: rows from a pluggable `ReactionTable` in the append transaction; `Claim`/`Complete`/`Fail` with `FOR UPDATE SKIP LOCKED` tested; step 9 builds the worker loop, retry and dead-letter |
| P-2 | Packages | **Codec in `Tarol.Persistence`** (store-agnostic; the in-memory store can round-trip); `Tarol.Persistence.Npgsql` holds only SQL + Npgsql |
| P-3 | Global feed | **Position column + safe `ReadAll`**; projections later |
| P-4 | DDL | **Shipped versioned idempotent SQL + `EnsureSchema()`**; schema name configurable |
| P-5 | Table layout | **S-A shared tables**, stream type as a column, tenant first in every key |
| P-6 | Expected-version check | **G-1**: guarded `UPDATE`/`INSERT` in a data-modifying CTE, events only if it passed, follow-ups guarded, actual version in the same batch |
| P-7 | Codec | **K-A**: generated `T.Events` alias table + library `EventCodec` + the user's context (resolver first); registration fails on missing metadata; `IEventUpcaster` on `JsonObject` |
| P-8 | Lookups | **L-A**: key function evaluated at commit, `lookups` table, one definition for both stores |
| P-9 | Metadata | **Columns + `traceparent` as text** (`NULL` without an activity) |
| P-10 | Outbox ordering | **O-2**: head per (tenant, type, stream, reaction) |
| P-11 | Key retention | **`PurgeKeys(olderThan, batch)`, default window 7 days**, called by step 9's host; the store never purges on its own |
| P-12 | Bad rows | **A state that fails to parse → replay** (logged; rewritten on the next commit); **unknown alias/version without an upcaster throws** |
| P-13 | Stream type | **Generated `T.StreamType`** = snake case of the aggregate name, pinned by `[StreamType("…")]` |
| P-14 | Feed checkpoint | **`GlobalPosition(ulong Transaction, long Position)`**, ordered by `(tx_id, position)` below `pg_snapshot_xmin` |
| P-15 | In-memory parity | **Optional codec round trip** in `InMemoryAggregateStore` + fix the history copy, both in 8A |
| P-16 | PR split | **Three PRs**: 8A codec and port, 8B Npgsql store, 8C outbox |

All answers follow the recommendation. Consequences worth stating:

* **P-6**: under heavy contention on one stream G-1 is the slowest guard (11.2); the cost lands on writers that lose
  anyway and must retry (C-7). If a hot stream appears in practice, G-3 is a local change in the store.
* **P-10**: a reaction whose row keeps failing holds back that reaction's later rows *of the same stream* until step 9
  dead-letters it; other streams and other reactions are not affected.
* **P-11**: a webhook redelivered after 7 days commits again; senders retry within hours, so the window is a default,
  not a guarantee.
* **P-12**: a corrupt state never blocks a load; corrupt or unknown *events* do, by design.

## 16. What the implementation must do (in the decided order)

### PR 8A `persistence: event codec, stream types, feed port and in-memory parity`

1. **Primitives generator**: `static string StreamType` and `static EventCatalog Events` on `IAggregateRoot<T>`
   (`EventCatalog`, `EventCatalogEntry` in `Tarol.Primitives`), `[StreamType("…")]`, the default rule (snake case of the
   aggregate name; equal to the first segment of the default event alias), a duplicate-stream-type diagnostic (TPRM028),
   snapshots and incrementality tests, `AnalyzerReleases`.
2. **`Tarol.Persistence`**: `EventCodec`, `IEventUpcaster`, `EncodedEvent`; `IEventFeed`, `GlobalPosition`,
   `PositionedEvent`; `StoreLookups`. Freeze the codec's tables after registration (the probe locks per call).
3. **`InMemoryAggregateStore`**: optional `EventCodec` (encode on append, decode on replay and on `ReadAll`), `IEventFeed`
   (in-memory positions), shared `StoreLookups`, and the history copy only when replaying (P-15, §12 item 8).
4. **Contract suite as shared test source**: `tests/Persistence/Shared/StoreContract.cs` (abstract, fresh tenant per test),
   compiled into `Tarol.Persistence.Tests` now and into the Npgsql tests in 8B; the 25 existing tests move into it where
   they are contract (not in-memory specifics).
5. README, skill `tarol-persistence` (codec and upcaster section), AOT smoke (codec round trip), package verification.

### PR 8B `persistence: Tarol.Persistence.Npgsql`

1. `src/Persistence/Tarol.Persistence.Npgsql` (Npgsql only; `Directory.Packages.props`: `Npgsql`, tests:
   `Testcontainers.PostgreSql`): `NpgsqlSchema` (the S-A script of `Core/Schema.cs`, versioned, idempotent; every
   `ON CONFLICT` with a target), `NpgsqlAggregateStore` (P-6 statement as probed, follow-ups guarded, actual version in the
   batch, `23505` mapping by constraint name, other errors propagate, cancellation propagates), `Load` (state, replay,
   P-12 fallback), `Find`, `Seen`, `PurgeKeys`, `RebuildStates<T>` (guarded by version, batched), `ReadAll` (P-14),
   metadata columns (P-9, `traceparent` from `Activity.Current`).
2. `NpgsqlSlimDataSourceBuilder` in docs and samples; `IsAotCompatible`; no reflection.
3. `tests/Persistence/Tarol.Persistence.Npgsql.IntegrationTests`: the contract suite on Testcontainers (assembly
   fixture), plus store tests: schema change → replay + rewrite, `RebuildStates`, upcaster, unknown alias, corrupt state
   → replay, metadata round trip, feed gap cases (two connections), 16 and 64 concurrent writers gapless, key purge,
   tenant isolation of every table.
4. CI: an `integration` job (Docker), required once green (plan.md "CI and release"); the `build` job excludes
   `*.IntegrationTests`.
5. `Tarol.Persistence.Npgsql.AotSmoke` against a PostgreSQL service in the AOT CI job, or a documented local run if the
   job cannot start one (decide in the PR; the probe proves the native run).
6. README, skill `tarol-persistence-npgsql` (§17), package verification, docs, CHANGELOG, plan status.

### PR 8C `persistence: outbox rows and claims`

1. `NpgsqlStoreOptions.Reactions` (`ReactionTable`, returns `ReadOnlySpan<string>` over static arrays), rows in the
   commit, guarded; `OutboxClaims` with O-2, lease, `Complete`, `Fail(retryAt, error)`; `UNIQUE (reaction, event_id)`.
2. Tests: rows only for successful commits, one per (event, reaction), 4 concurrent claimers exactly once, per-stream
   order with one in flight, lease expiry re-claims, `Fail` delays.
3. Docs: what step 9 adds (worker, C-9 policy, dead-letter table or column, `ActivityLink` from `traceparent`).

### Step 9 (designed here)

The generated `ReactionsTo` as the `ReactionTable`; the worker on `OutboxClaims`; dead-letter that unblocks O-2; the host
calls `PurgeKeys`; saga persistence on this store (old finding 23 disappears); DI registration. **Designed** in
[sagas.md](sagas.md) §11–§12: PR 9B changes the 8C claim (head flag, S-8), adds scheduled rows (S-9), `outbox_dead` (S-5)
and `traceparent` in claims; schema version stays 1 (nothing released).

## 17. Skill evaluation checklist (`tarol-persistence-npgsql`, for PR 8B)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| "store auctions in PostgreSQL" | `NpgsqlSlimDataSourceBuilder(cs).EnableArrays().Build()`, `new EventCodec(ValueObjectJson.Options(AppJson.Default)).Add<Auction>()`, `new NpgsqlAggregateStore(db, codec, time, new() { StateOptions = codec.Options })`, `EnsureSchema` | `NpgsqlDataSource.Create` under AOT; options without the resolver (TVAL009); forgetting `[JsonSerializable]` per event (throws at `Add`) |
| "rename an event that is already stored" | pin the old alias with `[EventType("auction.opened", 2)]` | renaming the class without a pin (unknown alias throws on load) |
| "add a field to a stored event" | bump the version, `IEventUpcaster` from the old version | changing the payload in place; catching the load exception |
| "find an auction by lot" | `StoreLookups.Define<Auction>("lot", a => a.Lot)` shared by both stores; `Find<Auction>("lot", …)` | a hand-written `SELECT` without the tenant; an index on `state` for uniqueness |
| "rebuild states after a model change" | `RebuildStates<Auction>()` (optional; loads replay meanwhile) | `UPDATE streams SET state = NULL` |
| "build a projection" | `ReadAll(checkpoint, 500)`, persist `GlobalPosition` | `WHERE position > @last` on the table |
| "test against a real database" | the contract suite + Testcontainers, fresh tenant per test | truncating tables between tests |

## 18. Verification of this PR (SDK 11.0.100, 2026-11-14) **[verified]**

Docs and probes only; no project in `Tarol.slnx` changed. `dotnet build Tarol.slnx`: 0 warnings, 0 errors.
`dotnet test --solution Tarol.slnx`: 743 total, 0 failed. `dotnet format Tarol.slnx --verify-no-changes`: exit 0.
Probes: `probes/persistence/all.sh` → [`output.txt`](probes/persistence/output.txt) (55 checks JIT + 55 native, 0 FAIL;
16 contention rows gapless; contract 20/20; benchmark table).

## 19. Open items

* S-B (tables per type) and partitioning of `events` by tenant or time: not measured; revisit with real volumes.
* Row-level security as defense in depth for the tenant filter: not designed (the store filters in every statement).
* Whether the outbox should reference `events.position` for a feed-ordered dispatch (column present, unused).
* The attribution of the ≈ 2 ms per change to the durable commit is not profiled.

## 20. Implementation: PR 8A (2026-11-14)

`persistence: event codec, stream types, feed port and in-memory parity`, stacked on the design PR. As decided in §15
and §16; differences below.

### 20.1 Public API (new)

| Package | Type | Members |
|---|---|---|
| `Tarol.Primitives` | `StreamTypeAttribute(string name)` | pins the stream type |
| | `EventCatalog(string streamType, params EventCatalogEntry[] events)` | `StreamType`, `Events` |
| | `EventCatalogEntry(EventType Type, Type ClrType)` | readonly record struct |
| | `IAggregateRoot<TSelf>` | + `static abstract string StreamType`, `static abstract EventCatalog EventTypes` (generated) |
| `Tarol.Persistence` | `EventCodec(JsonSerializerOptions)` | `Options`, `Add<T>()`, `Upcast<T>(IEventUpcaster)`, `IsRegistered<T>()`, `StreamTypeOf<T>()`, `Encode<T>(event, IBufferWriter<byte>) : EventType`, `Decode(streamType, EventType, ReadOnlySpan<byte>) : DomainEvent` |
| | `IEventUpcaster` | `EventType From`, `EventType Into`, `void Upcast(JsonObject)` |
| | `IEventFeed` | `ReadAll(GlobalPosition after, int limit, ct) : IAsyncEnumerable<PositionedEvent>` |
| | `GlobalPosition(ulong Transaction, long Position)` | `Start`, `IComparable`, `<`, `>`, `<=`, `>=` |
| | `PositionedEvent(GlobalPosition Position, string StreamType, RecordedEvent Event)` | |
| | `StoreLookups` | `Define<T>(name, key)`, `IsDefined<T>(name)`, `KeysOf<T>(aggregate)` |
| | `InMemoryStoreOptions` | `StateOptions`, `Codec`, `Lookups` |
| | `InMemoryAggregateStore` | + `(TimeProvider, InMemoryStoreOptions)`, implements `IEventFeed` |

Generator diagnostics: **TPRM028** two aggregates of one compilation with one stream type (Error), **TPRM029** an empty
`[StreamType]` (Error).

### 20.2 Behaviour changes and deliberate differences from §13/§16

* **`IEventUpcaster.Into`**, not `To`: CA1716 refuses `To` (a reserved word in Visual Basic).
* **`T.EventTypes`**, not `T.Events`: a static `Events` would collide with a user member of that common name.
* **`EventCodec` takes the options and makes them read-only** (`MakeReadOnly()`; the overload that fills in a missing
  resolver is `[RequiresUnreferencedCode]`), so pass a copy you do not mutate later: `new JsonSerializerOptions(AppJson.Default.Options)`
  or `ValueObjectJson.Options(AppJson.Default)`.
* **`InMemoryAggregateStore` keys streams by `T.StreamType`** instead of `typeof(T).FullName`, as the database will.
* **A stored state that does not parse falls back to a replay** (P-12); before it threw `JsonException` from `Load`.
* **The history is copied only to replay** (P-15): a state load of a 5,000-event stream allocates < 40 KB (tested).
* `DefineLookup` stays; it defines on the store's `StoreLookups` (shared when passed in the options).
* The contract suite (`tests/Persistence/Shared/`) is **new**; the 25 in-memory tests stay where they are (they also
  check in-memory specifics: `StateLoads`, `Recorded`, `History`), so some cases are covered twice.
* TPRM028 found two `Order` aggregates in `Tarol.Primitives.Tests`; one is now pinned `[StreamType("test_order")]`.

### 20.3 Tests **[verified]**

| Project | New | What |
|---|---|---|
| `Tarol.Primitives.Generators.Tests` | +3 (131) | TPRM028 fires (two same-named aggregates and a pin onto a taken name), a unique pin does not, TPRM029; four snapshots gained `StreamType`/`EventTypes` (reviewed diff: only those members) |
| `Tarol.Primitives.Tests` | +4 (113) | default and pinned stream type, the catalog lists every concrete event, agrees with `EventTypeOf`, rejects an empty stream type |
| `Tarol.Persistence.Tests` | +58 (83) | the store contract (19 tests) twice: objects and through the codec; codec (12: round trip, upcast, chain across a rename, unknown alias message, missing upcaster, unregistered stream type, malformed payload, missing metadata message, idempotent `Add`, unique upcasters, unregistered encode, frozen options); in-memory (7: feed order and paging, position order, codec copies and state with codec options, codec without the aggregate, replay fallback, state load allocations, shared lookups); the skill's codec example verbatim |
| `Tarol.Persistence.AotSmoke` | +5 checks | codec append/load, catalog, upcast, feed; natively |
| `Tarol.Persistence.PackageTests` | +2 checks | generated catalog and codec round trip from the packages |

### 20.4 Suppressions and analyzer configuration

None.

### 20.5 Verification (SDK 11.0.100, 2026-11-14)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | 808 total, 0 failed (743 before) |
| `rm -rf artifacts`, `dotnet build Tarol.slnx -c Release`, `eng/verify-packages.sh` | OK; `Tarol.Persistence` consumer 9/9 (2 new), skills listed |
| `eng/aot-smoke.sh` | 8 apps, each 0 IL warnings, all checks passed natively |

## 21. Implementation: PR 8B (2026-11-14)

`persistence: Tarol.Persistence.Npgsql`, stacked on PR 8A. As decided in §15 and §16; differences below.

### 21.1 Public API (new package `Tarol.Persistence.Npgsql`, depends on `Tarol.Persistence` and `Npgsql` 10.0.3)

| Type | Members |
|---|---|
| `NpgsqlAggregateStore : IAggregateStore, IEventFeed` | `(NpgsqlDataSource, EventCodec, TimeProvider, NpgsqlStoreOptions?)`, `Schema`, `EnsureSchema(ct)`, `Load`, `Find`, `Seen`, `Append`, `ReadAll`, `RebuildStates<T>(ct) : Task<int>`, `PurgeKeys(olderThan, batch, ct) : Task<int>` |
| `NpgsqlStoreOptions` | `Schema` (validated identifier, default `tarol`), `StateOptions` (default: the codec's options), `Lookups` (`StoreLookups`) |
| `NpgsqlSchema` | `Version` (1), `DefaultSchema`, `Script(schema)` |

### 21.2 Deliberate differences from §13/§16

* **No outbox table yet**: the script has streams, events, lookups, idempotency keys and `schema_version`; 8C adds the
  outbox table (still schema version 1: nothing is released).
* **No per-load logging** of the state fallback (P-12 said "logged"): the package has no logging dependency; a counter
  or `ILogger` hook can come with step 9's host.
* The feed index is `(tx_id, position)` (`events_feed`); the probe had one on `position` only.
* `RebuildStates<T>` takes no `OperationContext` (it writes no events) and pages by `(tenant, id)`, 500 streams a page.
* `EnsureSchema` refuses a database whose `schema_version` is newer than the package's.
* The shared contract suite now creates its store on first use (a derived class's fixture is set by then) and waits up
  to 30 s for its events in a database feed (see 21.3, item 2).

### 21.3 Found while building

1. **The feed sorted by the text output column** (`SELECT tx_id::text … ORDER BY tx_id`): a checkpoint skipped every
   transaction id with fewer digits; 259 of 1,416 events missing in the new paging test with 8 concurrent writers
   **[verified]**. Fixed (`AS tx_text`, `ORDER BY events.tx_id, events.position`), in the probe too; the probe's
   `output.txt` was not affected (all its ids had three digits).
2. **Any open transaction in the cluster holds the feed back** (cluster-wide `pg_snapshot_xmin`): the contract's feed
   test saw nothing while another test held a transaction open **[verified]**. Documented in README and skill.
3. A span (`UncommittedChanges`, the `Rehydrate` argument) cannot live across an `await` (CS4007): the store copies the
   changes to an array and rehydrates in a non-async helper.

### 21.4 Tests **[verified]**

`Tarol.Persistence.Npgsql.IntegrationTests` (36, `[Trait("Category", "Integration")]`, one Testcontainers `postgres:17`
assembly fixture, fresh tenant per test, own schema for cross-tenant tests): the shared contract (19); queryable state and
event columns; tenant in lookups and keys; `[LoadByReplay]` stores no state; another schema → replay → rewrite; a state
that does not parse → replay; `RebuildStates`; a v1 payload upcast and an unknown alias; every `RecordedEvent` column from
the feed including `traceparent` under an `ActivityListener`; the two late-commit cases on two connections; paging the
feed while 8 writers commit (no loss, no duplicate, ordered); 64 writers × 5 attempts gapless; 16 concurrent creations
with one lookup key → one commits; `PurgeKeys` in batches; `EnsureSchema` idempotent and refusing a newer schema; schema
name validation; an aggregate missing from the codec; the skill example verbatim. Stable in 8 consecutive runs.

`Tarol.Persistence.Npgsql.AotSmoke`: offline check always; with `TAROL_PG` 10 database checks natively (append, state
load, key, lookup, conflict, feed with context, rebuild, purge; own schema, dropped afterwards). The CI `aot-smoke` job gets
a `postgres:17` service and `TAROL_PG`. `Tarol.Persistence.Npgsql.PackageTests`: schema script, stream type and schema
validation from the packed package, without a database.

### 21.5 CI

`build` runs the unit tests with `-- --filter-not-trait "Category=Integration" --ignore-exit-code 8` (exit code 8: a
project with no test left); the new `integration` job runs the integration tests on the hosted runner's Docker; to make
required on `main` (plan.md "CI and release").

### 21.6 Suppressions and analyzer configuration

None.

### 21.7 Verification (SDK 11.0.100, 2026-11-14)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` (Docker running) | 844 total, 0 failed (808 + 36 integration) |
| `dotnet test --solution Tarol.slnx -- --filter-not-trait "Category=Integration" --ignore-exit-code 8` (Docker stopped) | 808 total, 0 failed; the integration project runs no test and starts no container |
| `rm -rf artifacts`, `dotnet build -c Release`, `eng/verify-packages.sh` | OK; `Tarol.Persistence.Npgsql` holds `lib/net11.0` (dll, xml), `README.md`, `LICENSE`, `skills/tarol-persistence-npgsql/SKILL.md`; the consumer passes 3/3 and lists the skills of the package and its dependencies |
| `TAROL_PG=… eng/aot-smoke.sh` (local `postgres:17`) | 9 apps, each 0 IL warnings; `Tarol.Persistence.Npgsql.AotSmoke` 11/11 natively |

## 22. Implementation: PR 8C (2026-11-14)

`persistence: outbox rows and claims`, stacked on PR 8B. As decided in §15 (P-1, P-10) and §16; differences below.

### 22.1 Public API (new)

| Package | Type | Members |
|---|---|---|
| `Tarol.Persistence` | `delegate ReadOnlySpan<string> ReactionTable(DomainEvent)` | the reactions to an event (step 9 generates it) |
| `Tarol.Persistence.Npgsql` | `NpgsqlStoreOptions.Reactions` | `ReactionTable?`; `null` = no outbox rows |
| | `NpgsqlAggregateStore.Outbox` | `NpgsqlOutbox` |
| | `NpgsqlOutbox` | `Claim(max, lease, ct) : Task<IReadOnlyList<OutboxRow>>`, `Complete(row, ct) : Task<bool>`, `Fail(row, retryAt, error, ct) : Task<bool>`, `Pending(ct) : Task<long>` |
| | `OutboxRow(long Id, Guid ClaimId, string Reaction, Option<Id<ITenant>> Tenant, int Attempts, Option<string> LastError, PositionedEvent Event)` | |

Schema: the `outbox` table (`UNIQUE (reaction, event_id)`, `claim_id`, `lease_until`, indexes `outbox_due`, `outbox_stream`)
joins the script; still schema version 1 (nothing released).

### 22.2 Deliberate differences from §8 and §16

* **`ReactionTable` lives in `Tarol.Persistence`**, not in the Npgsql package: step 9's generated `ReactionsTo` can fill it
  without a reference to Npgsql.
* **Claims carry the event** (`OutboxRow.Event`, read in the claim statement), so the worker needs no second query.
* **A claim id per claim**: `Complete` and `Fail` present it and return `false` when the lease expired and another
  worker claimed the row again (the probe's `Complete(id)` could delete a row someone else owned).
* **Claims read the store's `TimeProvider`** (time.md), instead of taking `now` as a parameter.
* **The in-memory store writes no outbox rows** yet; step 9 adds an in-memory outbox with its worker.
* **No dead-letter** in 8C: a row that keeps failing stays the head of its (stream, reaction); step 9 moves it out (C-9).

### 22.3 Found while building

1. The feed tests were flaky again (1 failure in about 20 runs) for the same reason as §21.3 item 2: another test held a
   transaction open and the cluster-wide xmin held this test's feed back. Every feed test that expects rows now waits
   until they are final (`Eventually`); 25 consecutive runs are green.
2. A collection expression in a span-returning lambda is CS9203 (design §12 item 6) — it bit the first test again; the
   `ReactionTable` docs and the skill show static arrays.

### 22.4 Tests **[verified]**

`NpgsqlOutboxTests` (8): one row per (event, reaction) with the full `RecordedEvent` and tenant, heads first; refused
commits (version conflict, taken lookup) write no rows; no table → no rows; a reaction listed twice → exception and
nothing committed; 4 workers claim 50 rows of 10 streams exactly once, per stream in version order, never two of one
stream at once; the next row of a stream waits for its head; an expired lease is claimed again and the first worker's
`Complete`/`Fail` return `false`; `Fail` delays the row and keeps the error. AOT smoke: claim with the event and
complete, natively.

### 22.5 Verification (SDK 11.0.100, 2026-11-14)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` (Docker running) | 852 total, 0 failed (808 + 44 integration) |
| `rm -rf artifacts`, `dotnet build -c Release`, `eng/verify-packages.sh` | OK |
| `TAROL_PG=… eng/aot-smoke.sh` | 9 apps, each 0 IL warnings; `Tarol.Persistence.Npgsql.AotSmoke` 12/12 natively |

### 22.6 Suppressions and analyzer configuration

None.

## 23. Fix: an undecodable outbox row no longer stalls its batch (2026-11-18)

**Behaviour change.** `NpgsqlOutbox.Claim` commits the lease and `attempts + 1` for up to `Batch` rows, then decodes their
events together. When one payload could not be decoded (an alias of a newer version during a rolling deploy, a key store that is
down), the whole call threw: the reaction worker logged it and claimed the same batch again after the lease, over and over.
`Handle` never ran, so no row of the batch progressed, and every row's `attempts` grew past `MaxAttempts`. Once a worker could
decode them, the healthy rows were buried at their first ordinary failure.

Now a failed batch decode falls back to decoding row by row. A row that still fails is released for one lease with
`last_error = 'Outbox.Undecodable: …'` and its claim not counted (`attempts - 1`); the other rows are returned and run. The
released row comes back once the lease has passed and runs when the event can be decoded. A cancellation still throws, as before.

Test: `NpgsqlOutboxTests.A_row_whose_event_cannot_be_decoded_is_released_alone_and_does_not_hold_back_its_batch` (fails without the
fix). The in-memory outbox does not decode, so the unit tests cannot show it.

## 24. Fix: a commit's outcome no longer depends on a late cancellation (2026-11-18)

**Behaviour change.** Every Npgsql transaction (`Append`, the outbox re-drive and bury, `Sync` of the job rows, the schema steps,
the read-model batches) committed with the caller's `CancellationToken`. A cancellation arriving during `COMMIT` threw
`OperationCanceledException` although the transaction may have committed, so the caller could not tell whether to retry; without
an idempotency key a retry appended twice. `CommitAsync` now gets `CancellationToken.None`, as the rollbacks already did; the
cancellation still stops the statements before it. No test: the window is the COMMIT round trip itself.

## 25. Performance: statements are built once and prepared (2026-11-18)

**Behaviour change, public API addition.**
* The SQL texts of the store, the outbox and the job rows were expression-bodied interpolated strings, so each access built the
  string again (several KB per commit and per claim), although the remarks said "built once per schema name". Each is now built on
  first use and kept (`=> field ??= …`).
* No statement was prepared: the data sources Tarol builds did not set `Max Auto Prepare`, so every commit sent its batch of up to
  seven statements to be parsed and planned again, and so did every claim and load. `NpgsqlTracing.AutoPrepareUnlessSet(builder,
  statements = 64)` (new) sets it unless the connection string does; `UseNpgsqlStore(connectionString)` and
  `UseNpgsqlBus(connectionString)` call it. Behind PgBouncer in transaction mode without `max_prepared_statements`, set
  `Max Auto Prepare=0` in the connection string. A data source the application builds is unchanged.

Test: `AutoPrepareTests.Auto_prepare_is_set_unless_the_connection_string_sets_it`; the whole suite with Docker (1883 tests) passes
with both changes. Not measured here: the gain per commit.

## 26. Performance: schema version 4 and the rebuild's paging (2026-11-18)

**Schema change, public API change.**
* Retention (`DeleteStreams`) selects `type = $1 AND updated_at < $2 ORDER BY updated_at LIMIT n`, and no index served it (the
  primary key is `(tenant, type, id)`): every batch of 500 was a sequential scan and a sort. Schema **version 4** (compatible from
  1, so code of version 1–3 still runs on it) adds `streams_retention ON streams (type, updated_at)`, built `CONCURRENTLY` like the
  earlier indexes. `NpgsqlSchema.Version` is now 4; it is a `const`, so code compiled against 3 keeps 3 until it is rebuilt
  (the shipped API baseline lists the change). `SealingVersion` stays 3. It is the one store query that does not filter by tenant,
  by design: retention runs across tenants.
* `RebuildStates` paged with `type = $1 AND (tenant, id) > ($3, $4) ORDER BY tenant, id`, an order no index serves, so each page
  scanned and sorted every stream of the type (quadratic over a large table). It now pages on the primary key's order,
  `(tenant, type, id) > ($3, $1, $4) ORDER BY tenant, type, id`, as `UnsealedStates` already did; the rows and their order are the
  same because `type` is fixed.

Tests: the schema state assertions move to version 4 (`DataProtectionTests`: 9 and 6 steps run); the suite with Docker passes
(1883). `A_Cached_Key_Costs_No_Key_Query` now waits for the key store's `LISTEN` first: the cache clear that follows it could land
while the test counted (it failed once in a full run).
