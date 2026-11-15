# Persistence

Concept note for step 8 (evaluation: [migration/persistence.md](../migration/persistence.md) §8–§17): the production
implementation of the store port that step 7 created. The store is **thin, explicit and AOT-ready**: plain Npgsql, no
ORM (D-1: Marten not migrated, D-14: no EF Core), one transaction per commit, the tenant in every key.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probes and output in
[`probes/persistence/`](../migration/probes/persistence/), [`output.txt`](../migration/probes/persistence/output.txt));
**[verified by reading]** means read in the named code, not run; **[unverified]** means reasoned, not run here.

**Status: implemented** (owner decisions P-1 … P-16, [migration/persistence.md](../migration/persistence.md) §15) in three
PRs: 8A codec and port (§20), 8B `Tarol.Persistence.Npgsql` (§21), 8C outbox (§22).

## 1. What one commit writes

```text
Append(aggregate, expected, context, key)
  BEGIN
  ├─ streams        UPDATE … WHERE version = expected  (INSERT … ON CONFLICT (pk) DO NOTHING for expected 0)
  │                 sets version, schema, state jsonb  ─┐ one statement (data-modifying CTE): events go in only
  ├─ events         INSERT … SELECT FROM unnest(…)     ─┘ if the guard passed (P-6, G-1)
  ├─ outbox         one row per (event, reaction)       ┐
  ├─ idempotency    the key (successes only, C-8)        ├ each guarded by "this transaction's first event id exists"
  ├─ lookups        move / insert the unique keys        ┘
  └─ SELECT version (the actual version for a conflict message)
  COMMIT  (or ROLLBACK and return Stream.VersionConflict / Idempotency.DuplicateKey / Lookup.Taken)
```

One `NpgsqlBatch` = one round trip plus `COMMIT` **[verified]**. A lost race inserts nothing and raises nothing: the
guard affects 0 rows, the follow-up statements see no event of this transaction, the store rolls back and returns
`Stream.VersionConflict(expected, actual)` with the actual version read in the same batch **[verified]**.

Loads read the stream row: `T.ReadState` when its `schema` equals `T.StateSchema`; another schema, no state,
`[LoadByReplay]`, or a state that fails to parse (P-12) → read the events and `T.Rehydrate`; the next commit rewrites the
row with the current schema (primitives.md §16) **[verified]**.

## 2. Schema (P-5: shared tables, S-A)

```sql
CREATE TABLE tarol.streams (            -- the concurrency guard and the stored state
  tenant uuid NOT NULL,                 -- 00000000-… = no tenant (as InMemoryAggregateStore)
  type text NOT NULL,                   -- stream type, T.StreamType (P-13), never a CLR name
  id uuid NOT NULL, version bigint NOT NULL,
  schema integer NOT NULL,              -- T.StateSchema; 0 = load by replay
  state jsonb NULL, updated_at timestamptz NOT NULL,
  PRIMARY KEY (tenant, type, id));

CREATE TABLE tarol.events (
  position bigint GENERATED ALWAYS AS IDENTITY,  -- with tx_id: the global order (P-14)
  tenant uuid, type text, stream_id uuid, version bigint,
  event_id uuid,                        -- UUIDv7 from the store's TimeProvider (unique)
  alias text, alias_version integer,    -- the codec (P-7)
  entity_id uuid, parent_id uuid NULL, payload jsonb,
  recorded_at timestamptz,              -- one per commit
  actor_kind text, actor_id text, on_behalf_of text NULL,
  correlation_id uuid, causation_id uuid NULL, traceparent text NULL,   -- P-9
  tx_id xid8 NOT NULL DEFAULT pg_current_xact_id(),
  PRIMARY KEY (tenant, type, stream_id, version));

CREATE TABLE tarol.lookups (tenant, type, lookup, key, stream_id, PRIMARY KEY (tenant, type, lookup, key));  -- P-8
CREATE TABLE tarol.idempotency_keys (tenant, scope, key, stream_id, version, events, stored_at,
                                     PRIMARY KEY (tenant, scope, key));                                  -- C-8, P-11
CREATE TABLE tarol.outbox (id identity PRIMARY KEY, tenant, reaction, type, stream_id, version, event_id,
                           due_at, attempts, lease_until, last_error, UNIQUE (reaction, event_id));       -- P-1, P-10
```

The full script (with indexes) is [`Core/Schema.cs`](../migration/probes/persistence/Core/Schema.cs). The package ships
it as one versioned, idempotent script and `EnsureSchema()` runs it (P-4); the schema name is an option.

* **The tenant comes first in every key** (security.md §5.3): streams, events, lookups, keys and outbox rows are found
  only within a tenant; the same id and the same lookup key exist independently in two tenants **[verified]**.
* **`state jsonb` is queryable** as decided in primitives.md §16: `state->>'lot'`, `jsonb_array_length(state->'bids')`
  **[verified]**. Indexes for queries are the application's (expression or GIN indexes on `state`); the store needs none.
* **Why shared tables**: one script, one feed, one outbox, one set of queries; per-type tables (S-B) would need generated
  DDL per aggregate and a `UNION` over every events table for the feed. Large installations can still add partial
  indexes per type **[unverified]** (not measured; S-B was not probed).

## 3. Concurrency (P-6: G-1, a guarded statement)

| Guard | How | 1 writer | 64 writers on one stream |
|---|---|---|---|
| **G-1** (decided) | conditional `UPDATE` in a CTE; events only if it passed | one round trip, no exception | correct; the slowest under heavy contention (losers queue on the row lock, then return 0 rows) |
| G-2 | no version predicate; `23505` on the events' primary key | one round trip | correct; every loser is an exception and an aborted transaction |
| G-3 | `SELECT … FOR UPDATE`, compare in C#, then write | two round trips | correct; highest throughput |
| G-4 | G-1 + `FOR UPDATE SKIP LOCKED`: a locked stream is a conflict at once | one round trip | lowest median, worst p99, false conflicts when the lock holder rolls back |

All four commit exactly `1 + ok` versions, gapless, with **0 exceptions leaking** from 1, 4, 16 and 64 writers
**[verified]** (numbers: [migration/persistence.md](../migration/persistence.md) §11.2). Contention on one stream is the
rare case (an auction gets bids from many users, but each commit is milliseconds), the caller retries (C-7), and G-1
keeps the common path to one statement without exceptions. Two probe failures shaped the SQL:

* **`ON CONFLICT DO NOTHING` needs a target.** Untargeted, it also swallows a unique violation of *another* index (a
  lookup or a state index), and a taken lookup came back as a version conflict **[verified]**. Every `ON CONFLICT` names
  its key.
* **A lost race must stop the rest of the batch.** With G-1 the batch continues after 0 rows; an unguarded lookup insert
  of the loser hit the second lookup index and threw **[verified]**. Every follow-up statement is guarded by
  `EXISTS (SELECT 1 FROM events WHERE event_id = <this commit's first event id>)`.

`PostgresException` with `23505` is still mapped (G-2 path, lookups by index) by constraint name; any other database
error is an exception (infrastructure, not a domain result).

## 4. Event codec (P-2, P-7, P-12, P-13: K-A in `Tarol.Persistence`)

```csharp
// user code: list the events in the app's context (they are real types, domain-core.md §3.3 c)
[JsonSerializable(typeof(Auction.Opened))] [JsonSerializable(typeof(Bid.Placed))] …
public partial class AppJson : JsonSerializerContext;

var codec = new EventCodec(ValueObjectJson.Options(AppJson.Default))   // V-9: resolver first
    .Add<Auction>()                                  // reads the generated Auction.Events; throws if metadata is missing
    .Upcast<Auction>(new OpenedV1ToV2());

public sealed class OpenedV1ToV2 : IEventUpcaster
{
    public EventType From => new("auction.opened", 1);
    public EventType To => new("auction.opened", 2);
    public void Upcast(JsonObject payload) => payload["currency"] = "EUR";
}
```

* **Generated by Primitives** (it already owns the alias rule, `[EventType]` and TPRM010): `T.Events` (alias, version,
  CLR type per event) and `T.StreamType` (snake case of the aggregate name, pinned by `[StreamType("…")]`, P-13), next to
  the existing `T.EventTypeOf`. The probe writes these tables by hand **[verified]** that the rest works with them.
* **Library**: `EventCodec` encodes with `options.GetTypeInfo(event.GetType())` and decodes alias → type → `JsonTypeInfo`;
  AOT-safe with a source-generated context, 0 IL warnings **[verified]**. A value object in a payload is a plain JSON
  value (`"title":"Sunflowers"`) **[verified]**.
* **Registration fails fast**: an event without JSON metadata throws at `Add<T>()`, naming the events
  **[verified]**, instead of at the first commit.
* **Upcasters** move a stored payload one version (or alias) at a time on a `JsonObject`; a v1 payload inserted by SQL
  loads as v2 **[verified]**. **Unknown alias or version without an upcaster throws** (events are the truth; P-12)
  with a message naming alias, version and the fix **[verified]**. A stored *state* that fails to parse falls back to a
  replay (derived data; P-12).
* **Not chosen**: K-B (the generator writes JSON metadata, as PR #24 did: a second serializer to maintain); K-C (hand
  registration: aliases written twice).

## 5. Metadata (P-9)

`RecordedEvent(StreamId, StreamEvent(Version, EntityId, ParentId, Event), EventId, RecordedAt, OperationContext)` is
filled from the columns: event id UUIDv7 from the store's `TimeProvider`, one `RecordedAt` per commit, actor kind/id/on
behalf of, tenant, correlation, causation, and `traceparent` as text (`Activity.Current?.Id`, `NULL` without an activity;
tracing.md open question answered: text) **[verified]** for every field but `traceparent` (no listener in the probe).

## 6. Lookups (P-8: L-A, a key function)

`DefineLookup<Auction>("lot", a => a.Lot)` is evaluated at commit, as in memory; the key is moved or inserted in
`tarol.lookups` in the same transaction; a key owned by another stream → `Lookup.Taken` and nothing is written
**[verified]**. The same definition serves both stores and `[LoadByReplay]` aggregates. L-B (a partial unique expression
index on `state->>'lot'`) works too and its `Find` uses the index **[verified]**, but needs a stored state and a second
definition for the in-memory store.

## 7. Idempotency keys (C-8, P-11)

Written in the commit, guarded, `ON CONFLICT (tenant, scope, key) DO NOTHING` → 0 rows → `Idempotency.DuplicateKey`
**[verified]**; a failed commit stores no key **[verified]**. Retention: `PurgeKeys(olderThan, batch)` deletes in batches
by `stored_at` **[verified]**; the store never purges on its own; step 9's host calls it (default window 7 days). A
redelivery after the window commits again (documented).

## 8. Outbox (P-1, P-10)

* **Step 8** writes one row per (event, reaction) from a `ReactionTable` delegate (step 9 generates
  `ReactionsTo`; tests pass a fake) in the commit; a failed commit writes none **[verified]**. It also builds the claim
  SQL: `FOR UPDATE SKIP LOCKED` with a lease, `Complete` (delete), `Fail(retryAt, error)`.
* **Ordering O-2**: only the oldest row of a (tenant, type, stream, reaction) is claimable; four workers processed 50 rows
  of 10 streams each once, in version order per stream, never two of one stream at a time **[verified]**; the plan is an
  anti-join on `outbox_stream` **[verified]**. A row that keeps failing blocks its own (stream, reaction) until step 9
  dead-letters it.
* The generated reaction table returns `ReadOnlySpan<string>` over **static arrays**: a collection expression in a
  span-returning lambda is CS9203 **[verified]**.
* **Step 9** adds the worker loop, backoff and dead-letter (C-9), the trace link from `traceparent`; designed in
  [sagas.md](sagas.md): the order stays O-2, the claim becomes a maintained `head` flag (S-8: the anti-join is O(backlog)),
  scheduled reactions are ordered by `due_at` only (S-9), dead letters move to `outbox_dead` (S-5).

## 9. The global feed (P-3, P-14)

`IEventFeed.ReadAll(GlobalPosition after, int limit)` with `GlobalPosition(ulong Transaction, long Position)`, ordered by
`(tx_id, position)`, returning only rows of transactions older than the oldest running one
(`tx_id < pg_snapshot_xmin(pg_current_snapshot())`):

| Reader | Late commit (A takes a position, B commits a later one first) | Older xid, later position |
|---|---|---|
| `position > checkpoint` | **skips A** **[verified]** | skips |
| xmin filter, position checkpoint | returns A | **skips A** **[verified]** |
| xmin filter, `(tx_id, position)` checkpoint | returns A **[verified]** | returns A **[verified]** |

Projections themselves are a later step; the feed is the port they will read.

Found while building 8B **[verified]**: `pg_snapshot_xmin` is the oldest running transaction of the **whole cluster**,
so any long-running or idle-in-transaction session holds every feed back until it ends (a test that kept a
transaction open stalled another test's feed). Readers poll; operators set `idle_in_transaction_session_timeout`. And
the text form of the transaction id must not share the column's name: `SELECT tx_id::text … ORDER BY tx_id` sorts by
the text output column ("1000" < "741") and a checkpoint then skips transactions (259 of 1,416 events in a paging test);
the probe's ids all had three digits, so it did not show.

## 10. AOT

`NpgsqlSlimDataSourceBuilder(…).EnableArrays()`, jsonb written and read as UTF-8 text (no `EnableJsonTypes`), the codec
and the state through source-generated metadata: the probe publishes with **0 IL warnings** and passes every functional
check natively against PostgreSQL 17 (7.9 MB binary) **[verified]**.

## 11. Testing (D-15)

* **One contract suite**, written once against `IAggregateStore` (abstract class, a fresh tenant per test so no cleanup
  is needed and classes run in parallel), run against `InMemoryAggregateStore` and the Npgsql store: 20/20 in ≈ 4 s with
  a Testcontainers `postgres:17` assembly fixture **[verified]**.
* Store-specific tests (schema change, upcasters, feed gaps, contention, outbox claims) stay in the Npgsql test project.
* CI: a separate `integration` job that starts Docker; the `build` job keeps the in-memory half.

## 12. Cost **[verified]** (BenchmarkDotNet, short run, local Docker; §11.1 of the evaluation)

| | 10 events | 100 events |
|---|---|---|
| load, Npgsql, stored state | ≈ 0.28 ms, 7 KB | ≈ 0.41 ms, 48 KB |
| load, Npgsql, replay | ≈ 0.28 ms, 12 KB | ≈ 0.57 ms, 82 KB |
| load, in memory, stored state / replay | 4.0 µs / 1.3 µs | 34 µs / 9.7 µs |
| load + one event + append, Npgsql | ≈ 2.2 ms, 22 KB | ≈ 2.3 ms, 22 KB |

The database round trips dominate; the stored state is as fast as a replay at 10 events and ≈ 30 % faster at 100, with half the allocations. The ≈ 2 ms of a change are mostly the commit (a durable `COMMIT` on a Docker volume) **[unverified]** as an attribution: not profiled. In memory the replay of
objects is cheaper than parsing JSON; the in-memory store stores state to behave like production, not to be faster.

## New insights

1. **A conflict can be a row count, not an exception**: a data-modifying CTE ties the event insert to the guard, so the
   common conflict costs no aborted transaction and no exception.
2. **`ON CONFLICT` without a target is a trap** in a store with several unique indexes.
3. **A sequence is not an order.** Positions are taken before commit; a catch-up reader needs the transaction id in its
   checkpoint or it loses events, and the xmin filter alone is not enough.
4. **The store's in-memory twin must not be cheaper than it looks**: `InMemoryAggregateStore.Load` copies the whole
   history before checking the state (1.6 MB per change on a long stream) **[verified]**; fixed in 8A (P-15).
5. **Fresh tenants make integration tests independent**: every key starts with the tenant, so a test needs no cleanup.

## Decisions taken (owner, 2026-11-14; migration/persistence.md §15)

P-1 outbox table + commit write + claims in 8; P-2 codec in `Tarol.Persistence`; P-3 global position + safe `ReadAll`;
P-4 shipped SQL + `EnsureSchema`; P-5 shared tables; P-6 guarded CTE (G-1); P-7 generated alias table + user context
(K-A); P-8 key-function lookups (L-A); P-9 metadata columns + `traceparent` text; P-10 head-per-(stream, reaction)
ordering; P-11 purge API, 7 days default; P-12 bad state → replay, unknown events throw; P-13 `T.StreamType` with
`[StreamType]`; P-14 `GlobalPosition(tx, position)`; P-15 in-memory codec round trip + history fix; P-16 three PRs.
