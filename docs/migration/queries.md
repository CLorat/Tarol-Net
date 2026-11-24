# Queries and read models: evaluation (step 12 design)

Evaluation of [queries.md](../architecture/queries.md): inventory of the old query code, candidates for where projections
run, where read models live and how they are declared, rebuild, list queries and pagination, consistency and the
authorization of lists, measurements against PostgreSQL 17, the owner's answers, and what PRs 12A–12D must do. **Design
only**: this PR changes no package code. Probe and output: [`probes/queries/`](probes/queries/),
[`output.txt`](probes/queries/output.txt), the inventory [`inventory.md`](probes/queries/inventory.md). Labels as in the
[conventions](../architecture/conventions.md). Source of the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old code has almost nothing to migrate** **[verified by reading]**: `Tarol.Paginations` is 17 lines (an offset
  request, a token request keyed by `Id<T>` only, a result of `IAsyncEnumerable` + `TotalCount`), with 0 tests and no
  user; the old CQRS `IQuery`/`IQueryHandler` is never implemented; no projection, read model, sort or filter exists.
  Review finding supporting #27 confirmed. Kept as ideas: both pagination shapes, now bounded, with an opaque cursor.
* **Decided model** (QR-1 … QR-7): a projection is a row record with `When` methods and a generated switch (QR-2); a
  `ProjectionHost` per projection reads `IEventFeed` after its stored `GlobalPosition` and writes rows and checkpoint in
  one transaction, guarded by the position it read from (QR-1); a table per projection version with typed columns and the
  row as `jsonb`, rebuilt as a new version next to the old (QR-3); list queries are query records with
  `[Endpoint("GET …")]`, cursor by default, offset bounded (QR-4); the commit's position and an opt-in read-after wait
  (QR-5); `Read<T>` plus claim-bound filters, no row rules on lists (QR-6); `Tarol.Queries`, `Tarol.Queries.Npgsql` and the
  endpoints in `Tarol.Changes.AspNetCore`, four PRs (QR-7).
* **Measured** (4 cores, PostgreSQL 17 in Docker): catch-up of 1e6 events 133k events/s (batch 2,000), the feed alone
  366k/s; the feed loop costs the commit nothing, an outbox row per event +0.5 ms; lag with NOTIFY p50 3.6 / p99 6.1 ms
  (poll 100 ms: 56 / 108 ms); a page of 50 at depth 900,000: offset 3.2 s, cursor 0.6 ms; Native AOT 0 IL warnings,
  7.8 MB, every check natively **[verified]**.
* **Found** (§5): offset repeats a row when an insert lands before the page; PostgreSQL refuses a `timestamptz` column
  generated from `jsonb`; the checkpoint guard serializes two runners by itself; an open transaction anywhere holds every
  projection back; the stored state writes enums as names and a wrong JSON path is silently empty; a rename swap stalls
  concurrent queries.
* **Plan** (QR-7): two new packages and one extended, four PRs 12A–12D; changes against steps 7, 8 and 10 in §10.

## 1. Inventory of the old code **[verified by reading]** (`CLorat/Tarol@6a62f81`)

Condensed; the full inventory with every file:line is [`inventory.md`](probes/queries/inventory.md).

| Project | Size | Public types | Tests | Users | Verdict |
|---|---|---|---|---|---|
| `src/Paginations/Paginations` | 17 lines, 3 files | `IPaginationRequest` (`IPaginationRequest.cs:3-6`, `init` on an interface), `OffsetPagination(Page = 1, PageSize = 10)` and `TokenPagination<T>(Id<T>? Token, PageSize = 10)` (`Pagination.cs:5,8`, no bounds), `PaginationResult<T>(IAsyncEnumerable<T>, int TotalCount)` (`PaginationResult.cs:3-6`) | `Class1.cs`, empty: 0 | template csprojs only | ideas only (§2.4) |
| `src/CQRS/CQRS` (query side) | 44 lines + 349-line generator | `IQuery<TResponse>` (`IRequest.cs:9`), `IQueryHandler<,>` (`IRequestHandler.cs:14`) | 0 ran (REVIEW.md:175) | none | not migrated (C-3); the name `IQuery<T>` returns as the marker of a query record |
| Marten snapshots | — | `MartenSnapshotRepository` (`MartenSnapshotRepository.cs:31,56`) | — | — | precedent of a per-aggregate read document; replaced by `streams.state` (step 8) |

Review findings re-checked: supporting #27 / §1.6 (`docs/review/supporting.md:48-52`) **confirmed** (no bounds, `init` on
the interface, `IAsyncEnumerable` + `TotalCount` not a serializable contract and wrong for cursors, placeholder tests);
CQRS C1, C5 confirmed (`CqrsDispatcherGenerator.cs:68-69,305`); the old persistence plan's projector with a gap-safe
checkpoint (`persistence.md:180-245`) is built since step 8 (`(tx_id, position)` + the xmin filter, P-3, P-14).

## 2. Candidates, as ArtFlow code

### 2.1 Where projections run (question 1; QR-1)

| | P-A a feed loop per projection | P-B outbox rows per projection | P-C inline in the commit |
|---|---|---|---|
| shape | `ProjectionHost` reads `ReadAll(checkpoint, batch)`, writes rows + checkpoint | the commit writes `project:<name>` per event, the reaction worker runs it | `Append` writes the read rows in the commit's transaction |
| commit cost **[verified]** | none (p50 1.35 ms) | +0.5 ms (1.85 ms) | +0.1 ms as a trigger (1.45 ms); a C# hook in `Append` would be needed |
| throughput **[verified]** | 133k events/s (batch 2,000) | 227 rows/s in one loop (claim, load, upsert, complete per row) | at the commit's rate |
| rebuild | from `GlobalPosition.Start` | impossible: rows exist only for events after the projection was added | replay through a different path |
| order | global (the feed) | per (stream, row name) | the commit's |
| failure | the projection stops and retries; decisions go on | dead letter per row | **the decision fails** |
| read-your-writes | wait for the checkpoint (§2.5) | wait for the row | free |

P-A chosen (QR-1). **Exactly once** within P-A: A1 = rows and checkpoint in one transaction; A2 = rows, then the checkpoint
in its own statement (at least once, made idempotent by the row's stream version). Same speed (130k vs 133k events/s), so A1
**[verified]**; the version guard stays as a second line (a replay from an older checkpoint changes nothing **[verified]**).

### 2.2 Where read models live, and how they are declared (question 2; QR-2)

| | R-A app-owned SQL | R-B generic JSONB documents | **R-C a Tarol table per projection, typed columns + `jsonb`** |
|---|---|---|---|
| table | the app's DDL and statements | one `read_models(tenant, model, id, doc)` | `rm_<name>_v<n>(tenant, id, version, <columns>, doc)` generated |
| filters and sorts | anything SQL can | expression indexes on JSON paths | b-tree on typed columns, one index per declared sort |
| in-memory test | needs a database | possible | possible (the row and the switch are plain C#) |
| generated query | no (the generator cannot know the SQL) | over paths | over columns |

Declaration **D-A** (chosen): the row record with a static `When(first event, RecordedEvent)` and instance `When(event)`;
the generator writes the switch, the column map and the SQL. **D-B** (a class with `void When(Row row, Event e)` mutating a
row) was not probed: mutation makes the "same row" check of the host (an event the projection ignores) and replay
idempotence harder to state. Cross-stream tables use **`ISqlProjection`** (R-A as the escape hatch, in the batch transaction).
Found: a typed column cannot be `GENERATED` from the `jsonb` when the cast is not immutable (`timestamptz`: `42P17`)
**[verified]**, so the host writes typed columns itself.

### 2.3 Rebuild (question 3; QR-3)

| | RB-A truncate + replay | RB-B shadow table + rename swap | **RB-C a new version next to the old** |
|---|---|---|---|
| readers during the rebuild **[verified]** | a partial table for 10 s (0, 2,203, 4,804 … of 100,000) | the old rows | the old rows (old code reads v<n>) |
| switch | none | 3 renames in one transaction: 12.6 ms, concurrent queries p99 45 ms, max 132 ms | the new code reads v<n+1>; 503 + `Retry-After` until caught up |
| row shape may change | yes | only if the code that reads it matches | yes |

Catch-up after a deploy is the same loop from the stored checkpoint. Versioning: `Version` in `[Projection]`; raising it
is the only way to rebuild. Old tables are dropped explicitly (step 15).

### 2.4 Queries and pagination (question 4; QR-4)

* **Q-A** (chosen): a query record with `[Endpoint("GET …")]` over one projection: nullable equality filters named like
  columns, a sort enum over `[Sort]` columns with the id as tie-break, `limit` ≤ 200, an opaque cursor, a bounded offset;
  generated SQL, OpenAPI and binding (R-12's metadata). **Q-B**: hand-written queries behind a port, with `Page<T>` and the
  cursor codec from Tarol; stays possible.
* **Cursor** (keyset) vs **offset** **[verified]** (§4.4): offset grows linearly with depth (19 ms at 10,000, 3.2 s at
  900,000) and repeats a row under a concurrent insert; the cursor is flat (0.5–1.0 ms). The old `TokenPagination<T>`
  keyed by the id only; the cursor here carries the sort value too, and is bound to the sort and the filters (another
  sort or filter is refused). Total count: opt-in (62 ms at 1e6 rows).

### 2.5 Consistency (question 5; QR-5)

* **RYW-A** (chosen): `Append` returns the commit's `GlobalPosition`; responses carry `Tarol-Position`; a GET with
  `Tarol-Read-After` waits up to a timeout, then answers with `Tarol-Stale: true`. **RYW-B**: the same with 503 on timeout.
  **RYW-C**: no wait (the GET by id is the consistent read).
* The wait **[verified]**: in the worker's process (signal after each batch) p50 3.7 / p99 6.4 ms; from another process
  (polling the checkpoint row every 5 ms) p50 8.2 / p99 11.2 ms, both with the feed NOTIFY.
* **ETag of a list**: candidates the projection's checkpoint (changes with every event of every stream: no 304 under load),
  the maximum row version of the filter (an extra aggregate query), **a hash of the page's (id, version) pairs** (chosen:
  exact for the page and free) **[verified]**.

### 2.6 Who sees which rows of a list (question 6; QR-6)

Step 10 authorizes one loaded aggregate: `Read<T>` by role, then row rules (`ChangeHandler<,>`) on the aggregate. A list
cannot load every aggregate. **AZ-L1** (chosen): `Read<T>` + tenant always + filters bound to claims (`[FromClaim("sub")]`).
**AZ-L2**: an own `Query<TQuery>` intent per query record. **AZ-L3**: row rules on each projected row after the query (short
pages, cursors skip rows).

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/queries/output.txt) §1, §9: JIT and Native AOT)

| Check | memory | postgres |
|---|---|---|
| one row per auction from the feed, another stream type ignored | ✓ | ✓ |
| rows equal the aggregates (bids, highest, status) | ✓ | ✓ |
| tenant isolation | ✓ | ✓ |
| cursor pages = the ordered list, no duplicate; offset = cursor while nothing changes | ✓ | ✓ |
| filter + descending sort with the id tie-break | ✓ | ✓ |
| a cursor bound to its sort and filters | ✓ | ✓ |
| page ETag stable, then changed by a bid on the page | ✓ | ✓ |
| `WaitFor` times out without a host, returns once the host passed the commit | ✓ | ✓ |
| replay from an older checkpoint is idempotent (A2, version guard) | ✓ (no rewind in memory: trivially) | ✓ |
| crash before the write: nothing written; the restart applies every event once | | ✓ |
| two runners at once: exact rows (20 refused batches) | | ✓ |
| offset under an insert before the page repeats a row; the cursor does not | | ✓ |
| a new version built from the start equals v1 while v1 serves | | ✓ |
| `timestamptz GENERATED` from `jsonb` refused (`42P17`) | | ✓ |
| an open transaction elsewhere holds the projection back until it ends | | ✓ |
| read-your-writes across processes by polling the checkpoint row | | ✓ |
| the stored state: counts match; a wrong path silently empty | | ✓ |

12 of 12 (memory), 21 of 21 (postgres), on JIT and Native AOT.

## 4. Measurements **[verified]** ([`output.txt`](probes/queries/output.txt); 4 cores, 15 GB, SDK 11.0.100, PostgreSQL 17 in Docker with default settings)

### 4.1 Seed and catch-up (§2, §3)

Seed: 10,000 auctions × (open, 8 bids, close) through `NpgsqlAggregateStore.Append`, 8 writers, one commit per event:
100,000 commits in 153 s (655 commits/s); copied by SQL to 1e6 events (393 MB).

| Catch-up from the start, 1e6 events → 1e5 rows | JIT | Native AOT |
|---|---|---|
| A1, batch 500 | 67k events/s (14.9 s) | 71k (14.1 s) |
| A1, batch 2,000 | 133k (7.5 s) | 132k (7.6 s) |
| A1, batch 5,000 | 153k (6.5 s) | 124k (8.1 s) |
| A2, batch 2,000 | 130k (7.7 s) | 106k (9.5 s) |
| the feed alone (read + decode, 2,000) | 366k (2.7 s) | 283k (3.5 s) |

The first run of a section reads a cold cache; the order of the runs is the same in both columns. Default batch: 2,000.

### 4.2 Commit cost (§5; 2,000 sequential bids each)

| Store | p50 | p99 |
|---|---|---|
| baseline (warm, second run) | 1.35 ms | 2.34 ms |
| + one outbox row per event (P-B) | 1.85 ms | 3.15 ms |
| + inline trigger upsert (P-C) | 1.45 ms | 2.27 ms |
| + feed NOTIFY trigger | 1.36 ms | 2.45 ms |

### 4.3 Lag commit → projected (§8; 50 commits/s for 10 s, first 10 % skipped)

| Wake-up | 1 writer p50 / p99 | 4 writers p50 / p99 |
|---|---|---|
| poll 100 ms | 56 / 108 ms | 58 / 109 ms |
| poll 10 ms | 9.2 / 16.9 ms | 10.5 / 20.9 ms |
| NOTIFY (+ 1 s poll) | 3.6 / 6.1 ms | 4.4 / 7.8 ms (Native AOT 4.0 / 7.4) |
| read-your-writes, in process / other process | 3.7 / 6.4 ms; 8.2 / 11.2 ms (Native AOT 3.1 / 6.2; 9.0 / 11.0) | |

### 4.4 Query latency (§6; page of 50, p50 / p99; 1e6 rows of tenant A + 1e5 of tenant B in one table)

| Tenant rows | Query | Depth | Offset | Cursor |
|---|---|---|---|---|
| 1e6 | all, `endsAt` | 0 | 0.99 / 1.39 ms | 1.03 / 2.52 ms |
| 1e6 | all, `endsAt` | 1,000 | 2.37 / 5.30 ms | 0.91 / 1.48 ms |
| 1e6 | all, `endsAt` | 10,000 | 19.2 / 30.4 ms | 0.87 / 1.44 ms |
| 1e6 | all, `endsAt` | 100,000 | 362 / 402 ms | 0.66 / 2.87 ms |
| 1e6 | all, `endsAt` | 500,000 | 1,763 / 1,878 ms | 0.50 / 1.02 ms |
| 1e6 | all, `endsAt` | 900,000 | 3,187 / 3,371 ms | 0.56 / 0.88 ms |
| 1e6 | `status=Closed`, `endsAt` | 100,000 | 374 / 456 ms | 0.73 / 0.97 ms |
| 1e6 | all, `highestBid desc` | 900,000 | 3,340 / 3,675 ms | 0.57 / 0.88 ms |
| 1e5 | all, `endsAt` | 10,000 | 21.4 / 27.2 ms | 0.75 / 1.24 ms |

Every row of the run is in `output.txt` §6. The plan of offset 500,000: an index scan of 500,051 rows (2.08 s). Total
count (`status=Closed`): 62 ms at 1e6 rows, 5.9 ms at 1e5. The table: 676 MB for 1.1e6 rows with 4 indexes.

### 4.5 The stored state as a read model (§7; tenant with 25,000 of 100,000 streams)

| Page 1, `highestBid desc` | p50 / p99 |
|---|---|
| `streams.state`, no index | 29.8 / 33.9 ms |
| `streams.state`, expression index `((state->>'highestBid')::numeric)` | 0.53 / 0.72 ms |
| projection table, SQL only | 0.35 / 0.52 ms |
| projection table, `Search` (SQL + 51 rows deserialized + cursor + ETag) | 1.07 / 3.28 ms |
| `streams.state`, + `status='Open'` (not in the index) | 18.9 / 23.2 ms |

### 4.6 Native AOT (§9)

0 IL warnings, 7,761,312 bytes; `selftest memory` 12/12 and `selftest postgres` 21/21 natively; lag, read-your-writes
and catch-up as in §4.1 and §4.3.

## 5. Found while probing

1. **Offset is wrong before it is slow** **[verified]**: one insert sorting before page 1 makes offset page 2 repeat the
   last row of page 1 (any depth). The cursor continues after the last row it returned.
2. **PostgreSQL cannot generate a typed column from `jsonb` with a non-immutable cast** **[verified]**: `GENERATED ALWAYS
   AS ((doc->>'endsAt')::timestamptz) STORED` → `42P17 generation expression is not immutable`. The host writes the columns.
3. **The checkpoint guard serializes runners** **[verified]**: the guarded `UPDATE projections … WHERE tx = $read AND
   pos = $read` first in the batch transaction locks the row; the second runner waits, finds 0 rows, rolls back, re-reads
   (20 refused batches, exact rows). No advisory lock needed; the implementation may add one to avoid wasted batches.
4. **An open transaction anywhere holds every projection back** **[verified]** (persistence.md §9, now seen on a
   projection): NOTIFY wakes the host at the commit, but the xmin filter hides the commit until the older transaction ends;
   the fallback poll picks it up. A projection's own batch transaction also holds the horizon for the length of a batch.
5. **The stored state and the events disagree on representation** **[verified]**: `streams.state` stores
   `"status": "Open"` while the event JSON carries numbers; a query on the wrong path (`state->>'Status'`) is empty, not an error.
6. **A rename swap stalls readers** **[verified]**: 12.6 ms for the swap, but concurrent page queries reached p99 45 ms and
   max 132 ms (the `ACCESS EXCLUSIVE` lock queues them).
7. **A per-stream row is idempotent by its version** **[verified]**: skipping events at or below the row's version makes
   A2 (no transactional checkpoint) and any replay exact.
8. **Commit position**: `Append` returns `Result` without the commit's position; the probe reads it back
   (`SELECT tx_id, max(position) … WHERE stream_id AND version > expected`). The implementation returns it from the
   commit's batch (`pg_current_xact_id()` and the inserted positions) — a change to the port (§10).

## 6. Generator and diagnostics (12A, 12C; numbers final in the PRs)

| Id | Severity | When |
|---|---|---|
| TQRY001 | error | `[Projection]` on a type that is not a `partial record` |
| TQRY002 | error | no static `When` creating the row, or two for the same event |
| TQRY003 | error | a `When` with a parameter that is not an event of the projection's aggregate (or `RecordedEvent`) |
| TQRY004 | error | a `[Sort]` member of a type without a column mapping |
| TQRY005 | error | a query record whose filter member names no column, or whose sort enum member names no `[Sort]` column |
| TQRY006 | warning | `[FromClaim]` member also bound from the route or query string |

Generated per projection: the switch, the column map, the DDL and upsert text, `IProjection<TRow>` metadata; per query
record: the SQL builder over parameters, the binder (query string → record, `after`, `limit`, `offset`), OpenAPI metadata.

## 7. Decided API (sketch for 12A–12C; names final in the PRs)

* **`Tarol.Queries` (12A)**: `[Projection(name, Version)]`, `[Sort]`, `IProjection<TRow>`, `ISqlProjection`,
  `IReadModelStore<TRow>`, `ProjectionHost` (`BackgroundService`; `ProjectionOptions`: `Batch` = 2,000, `Poll` = 1 s with
  a wake-up, 100 ms without), `IProjectionPositions.WaitFor(name, position, timeout)`, `InMemoryReadModels`,
  `ProjectionTest`, `Page<T>`, `Cursor`, `IQuery<TRow>`; `TarolBuilder.AddProjections(configure)`;
  `ActivitySource "Tarol.Queries"` (a span per batch, linked to nothing: batches mix commits).
* **`Tarol.Queries.Npgsql` (12B)**: `NpgsqlReadModels` (`EnsureSchema` per projection version, the checkpoint table, the
  guarded batch transaction, the version state "building" / "current"), `UseNpgsqlReadModels()`.
* **`Tarol.Persistence(.Npgsql)` (12B)**: `Append` returns the commit's `GlobalPosition`; `NpgsqlStoreOptions.NotifyFeed`.
* **`Tarol.Changes(.AspNetCore)` (12C)**: `ChangeOutcome.Position`; `Tarol-Position` on change responses; `[Endpoint("GET …")]`
  on an `IQuery<TRow>` record, `[FromClaim(type)]`, `Tarol-Read-After` / `Tarol-Stale`, 503 while building, page ETag and
  `If-None-Match` → 304; OpenAPI for list entries; `QueryEndpointOptions` (`MaxLimit` 200, `MaxOffset` 1,000,
  `ReadAfterTimeout` 2 s).

## 8. Plan impact (question 7)

* Row 12 → the decided split (four PRs, QR-7).
* Row 13 (caching): the page ETag and `Tarol-Position` are in place; a cache key is (tenant, query, filters, cursor), its
  invalidation the projection's checkpoint passing a commit.
* Row 15 (operations): lag per projection (checkpoint vs feed head), dropping old versions, the fallback poll's log line
  when the xmin horizon holds the feed.
* Row 18 (samples): the auction list in `samples/Sagas` (12D).

## 9. Owner answers (2026-11-16)

Rounds: 1 (after the catch-up, rebuild and commit numbers) QR-1 … QR-3; 2 (after the query, lag and AOT numbers) QR-4 … QR-7.
Not asked because already decided: GET by id through `IView<,>` / `Read<T>` (R-1, R-8); the stored state is not an API
(R-8); tenant in every key (P-2); the feed and its ordering (P-3, P-14); NOTIFY opt-in (S-10); no reflection on AOT paths
(conventions); routes, policies and OpenAPI metadata as for changes (R-12); a decision never reads a projection (plan row 12).

| # | Question | Answer |
|---|---|---|
| QR-1 | Where do projections run? (feed loop: catch-up 133k events/s, no commit cost; outbox rows: +0.5 ms per commit, 227 rows/s, no rebuild; inline: +0.1 ms, a projection error fails the decision) | **Own feed loop**: a hosted `ProjectionHost` per projection in the worker process, reading `IEventFeed` after its stored `GlobalPosition`; rows and checkpoint in one transaction plus a per-stream version guard; a second runner is refused by the checkpoint guard (recommended). |
| QR-2 | Where do read models live, and how is one declared? (typed columns + `jsonb`, 100k rows = 65 MB; `timestamptz` cannot be generated from `jsonb`) | **Row record + `When`**: `[Projection("auction-list", Version = 1)]` on a row record with static/instance `When(event)` methods; the generator writes the switch, column map, DDL and upsert; per-stream rows by default; `ISqlProjection` for the app's own SQL in the batch transaction (recommended). |
| QR-3 | How is a projection rebuilt? (truncate + replay: 10 s of partial reads; rename swap: 12.6 ms, concurrent p99 45 ms / max 132 ms) | **Versioned tables**: raising `Version` builds `rm_<name>_v<n+1>` from the start next to v<n>; the new code reads it and answers 503 + `Retry-After` until caught up; old tables dropped by an explicit operation, never automatically; no renames (recommended). |
| QR-4 | How are list queries written? (offset 1 ms … 3.2 s at depth 900,000, repeats a row under an insert; cursor 0.5–1 ms flat; count 62 ms at 1e6) | **Generated GET, cursor by default**: `[Endpoint("GET /auctions")]` on a query record over a projection; equality filters on columns, sorts from an enum whitelist (an index per sort, id tie-break), an opaque cursor bound to sort + filters, limit ≤ 200; offset only up to a maximum (default 1,000, deeper → 400); total count opt-in; hand-written queries stay possible (recommended). |
| QR-5 | Read-your-writes for the caller of a change? (NOTIFY lag p50 3.6 / p99 6.1 ms; waiting from another process p50 8.2 / p99 11.2 ms; needs `Append` to return the position) | **Opt-in wait**: every change response carries `Tarol-Position`; a GET with `Tarol-Read-After: <position>` waits until the checkpoint passes it (default 2 s), then answers 200 with `Tarol-Stale: true`; every list answer carries the projection's position; its ETag is a hash of the page's (id, version); feed NOTIFY opt-in like S-10 (recommended). |
| QR-6 | Who may see which rows of a list? | **`Read<T>` + claim filters**: a list needs the `Read<T>` grant and is always tenant-scoped; row visibility is a filter bound to the caller (`[FromClaim("sub")]`), never from the request; row rules do not run on lists, and the docs say so (recommended). |
| QR-7 | Packages and PRs? | **The recommended split, four PRs**: 12A `Tarol.Queries` (+ generator): `[Projection]`, the host, the in-memory store, `ProjectionTest`; 12B `Tarol.Queries.Npgsql`: tables + DDL, checkpoint, versions, the feed NOTIFY in `Tarol.Persistence.Npgsql`, `Append` returning the position; 12C the list endpoints in `Tarol.Changes.AspNetCore`: query records, cursor/offset, ETag, read-after, OpenAPI; 12D the sample and the docs. The option was labelled "3 packages": two are new, the third (`Tarol.Changes.AspNetCore`) is extended. |

## 10. Changes against steps 7–11 (announced here, made in 12B and 12C)

| Where | Change | Why |
|---|---|---|
| `Tarol.Persistence` `IAggregateStore.Append` | returns the commit's `GlobalPosition` (`Result<GlobalPosition>`), in-memory and Npgsql | QR-5; §5 item 8 |
| `Tarol.Persistence.Npgsql` `NpgsqlStoreOptions` | `NotifyFeed`: `NOTIFY <schema>_feed` with every commit (in the commit's batch, not a trigger) | QR-5, lag §4.3 |
| `Tarol.Changes` `ChangeOutcome` | gains `Position` (replayed outcomes: none, the key stores no position) | QR-5 |
| `Tarol.Changes.AspNetCore` | `Tarol-Position` on change responses; `[Endpoint("GET …")]` accepts `IQuery<TRow>` records; `[FromClaim]` | QR-4, QR-5, QR-6 |
| `docs/architecture/security.md`, `rest.md` | lists: `Read<T>` + claim filters, no row rules | QR-6 |

## 11. What the implementation must do (in the decided order)

### PR 12A `queries: projections from the feed` (`Tarol.Queries` + generator)
* `[Projection]`, `[Sort]`, `IProjection<TRow>`, the generated switch and column map; TQRY001–TQRY004; snapshots,
  incrementality, diagnostics tests.
* `ProjectionHost`: the loop of the probe (filter by stream type, load rows of the batch, skip versions ≤ the row's, the
  guarded write, `WaitFor`), backoff on exceptions, a span per batch, source-generated logs; `ISqlProjection`.
* `InMemoryReadModels` (rows, checkpoint, the query semantics of §2.4), `ProjectionTest`, `Page<T>`, `Cursor`.
* Tests: every "memory" row of §3; the skill `tarol-queries` (projections part); an AOT smoke app.

### PR 12B `queries.npgsql: read tables and checkpoints` (`Tarol.Queries.Npgsql`)
* The generated DDL per version, `projections` table with the version state, the guarded batch transaction, `503`
  state while building; `NotifyFeed` and `Append` returning the position (§10).
* Integration tests on Testcontainers PostgreSQL: every "postgres" row of §3, the catch-up of 1e5 events, added to the
  CI `integration` job; AOT smoke with `TAROL_PG`.

### PR 12C `changes.aspnetcore: list queries` (`Tarol.Changes.AspNetCore`)
* `IQuery<TRow>` records with `[Endpoint("GET …")]`: binding, the generated SQL over parameters, cursor and bounded
  offset, `limit`, count opt-in, `Read<T>`, `[FromClaim]`, tenant from the caller; `Tarol-Position`, `Tarol-Read-After` /
  `Tarol-Stale`, page ETag and 304, 503 while building; OpenAPI; TQRY005, TQRY006.
* Tests: the HTTP rows (bad cursor 400, offset over the maximum 400, claim filter, stale flag, 304, OpenAPI document).

### PR 12D `samples: the auction list`
* `samples/Sagas`: an `auction-list` projection in the worker, `GET /auctions` in the API, read-after from the client;
  the Aspire run; `docs/queries/` user guide; CHANGELOG, README.

## 12. Skill evaluation checklist (`tarol-queries`, 12A–12C)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| a list of open auctions sorted by end | `[Projection]` row record + `When`; `[Endpoint("GET /auctions")]` on an `IQuery<AuctionRow>` record with `Status` and a sort enum | querying `streams.state`; reading a projection inside a change's `Execute` |
| page through a large list | `?after=<next>` | offset beyond the maximum; building cursors by hand; a total count on every page |
| add a column to a list | `Version = 2`, deploy, wait for "current" | altering the table; truncating it |
| "my auctions" | `[FromClaim("sub")] Seller` | a `seller` query parameter the caller sets |
| show the caller's bid at once | send `Tarol-Read-After` with the change's `Tarol-Position` | polling the list in a loop; reading the GET by id and merging by hand without saying why |
| totals per seller | `ISqlProjection` with an upsert keyed by seller | a per-stream row record keyed by seller |
| test it | `ProjectionTest` | a database in a unit test |

## 13. Verification of this PR (SDK 11.0.100, 2026-11-16) **[verified]**

Docs and probes only; the package code is `main` at `833f37c` (step 11 merged). `dotnet build Tarol.slnx` 0 warnings,
0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx` with Docker: 1262 of 1262 (at the
start on `main` and again at the end on this branch); `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`:
18 apps, 0 IL warnings, all checks passed; `dotnet pack -c Release` + `eng/verify-packages.sh`: OK. CI of `main` after
#35 (run 122): `build`, `aot-smoke`, `integration` green. The probe: `all.sh` sections 1–9 ([`output.txt`](probes/queries/output.txt)).

## 14. Open items

* A stream-type filter on `IEventFeed.ReadAll` (not needed at 366k events/s; would add an index to every commit).
* Range filters and full-text search; projections of another system's feed (T-4, messaging.md §2.1): later, if asked.
* Pushing read-model changes to the UI (messaging.md §10).
* Lag metrics, dropping old versions, an advisory lock to avoid wasted batches with several worker processes: step 15.

## 15. Implementation: PR 12A (2026-11-16)

`queries: projections from the feed`: the new package `Tarol.Queries` with its bundled generator.

### 15.1 Public API (new package `Tarol.Queries`, depends on `Tarol.Persistence`)

| Type | Role |
|---|---|
| `ProjectionAttribute(name)` (`Version` = 1), `SortAttribute` | declare a row record and its sort members |
| `IProjection<TRow>` (static abstract, implemented explicitly by the generator) | `Name`, `Version`, `StreamType`, `Columns`, `Apply(row, recorded)` |
| `Projections` | `StreamTypeOf<TAggregate>()` (generated code), `TableOf<TRow>()` (`rm_<name>_v<n>`) |
| `ProjectionColumn<TRow>`, `ProjectionColumn<TRow, TValue>`, `ColumnType`, `ColumnValues` | the generated column map: `Name` (snake case), `Member`, `Type`, `Nullable`, `Sortable`, `Value`, `Values` (typed array for a batch write), `CompareTo`; `Timestamp`, `Normalize`, `Format`, `TryParse`, `Compare`, `CompareIds` |
| `RowKey`, `Versioned<TRow>`, `ProjectionCheckpoint`, `ProjectionState` (`Building`, `Current`), `ProjectionBatch<TRow>` | rows, checkpoint and one write |
| `IReadModelStore<TRow>` (`Prepare`, `Checkpoint`, `Load`, `Write`, `Find`, `Search`), `IReadModels` (`Store<TRow>()`) | the read store port |
| `ListQuery` (tenant, `Filters`, `Sort`, `Descending`, `After`, `Offset`, `Limit`, `Count`; `MaxLimit` 200, `MaxOffset` 1,000, `DefaultLimit` 50), `ColumnFilter`, `ListQueryPlan<TRow>`, `Page<T>` (`Items`, `Next`, `ETag`, `Position`, `Total`), `Cursor` (`Encode`, `TryDecode`, `Hash`, `ETag`) | list queries |
| `ProjectionHost` (abstract `BackgroundService`: `Name`, `Version`, `Position`, `State`, `Conflicts`, `RunOnce`, `CatchUp`, `WaitFor`; `ActivitySourceName` = `Tarol.Queries`), `ProjectionHost<TRow>`, `SqlProjectionHost` | the hosts |
| `ISqlProjection`, `ISqlProjectionStore` | the app's own SQL in the batch transaction (the store implementation is 12B) |
| `ProjectionOptions` (`Batch` 2,000, `Poll` 1 s with a signal / 100 ms without, `Backoff` 100 ms, `MaxBackoff` 30 s), `IFeedSignal` | host options and the wake-up port (12B: the feed NOTIFY) |
| `IProjectionPositions` (`Checkpoint<TRow>`, `WaitFor<TRow>`), `ProjectionPositions` (`PollInterval` 5 ms) | read-your-writes |
| `InMemoryReadModels`, `InMemoryReadModelStore<TRow>` (`Rows`, `Refused`), `ProjectionTest<TRow>` (`Create`, `Over`, `Given`, `CatchUp`, `Rows`, `Row`, `Search`) | in memory and tests |
| `QueriesTarolBuilderExtensions` (`UseInMemoryReadModels`, `AddProjectionPositions`, `AddProjections`), `ProjectionsBuilder` (`Add<TRow>`, `Add(ISqlProjection)`, `Defaults`) | registration on the one builder (R-14) |
| `IQuery<TRow>`, `DescendingAttribute` | markers the list endpoints of 12C read |

Logs: 12001 (a batch failed, backoff), 12002 (another runner moved the checkpoint), 12003 (the projection is current).

### 15.2 Deliberate differences from §6, §7 and §11

* **The generator writes the column map, not SQL text.** QR-2 says the generator writes "the DDL and the upsert". The
  generator ships inside the store-neutral `Tarol.Queries`; it emits the typed column map (name, `ColumnType`,
  nullability, sort flag, a typed getter), and `Tarol.Queries.Npgsql` (12B) derives the DDL and the upsert from it once
  per projection version (snapshot-tested there). Same result, one place that knows PostgreSQL.
* **List queries are a runtime `ListQuery` checked against the column map**, not SQL generated per query record:
  `ListQueryPlan<TRow>` resolves the filters and the sort against `TRow.Columns` (an unknown column is an
  `ArgumentException`, a programming error), so 12C's generator only maps a query record to a `ListQuery`. Nothing from
  the request becomes SQL text: column names come from the generated map, values are parameters.
* **`IProjection<TRow>` is a static abstract interface, implemented explicitly**, so a row member named `Name` or
  `Version` stays free; the host is generic over `TRow` (no reflection, no boxing of the projection).
* **An event the projection ignores leaves the row and its version untouched** (the probe rewrote the row with the new
  version): the page ETag stays the same and nothing is written. A replay still skips by version.
* **Store order equals PostgreSQL order in memory**: text ordinally (12B declares `COLLATE "C"`), uuids by their bytes
  (`ColumnValues.CompareIds`; `Guid.CompareTo` does not match PostgreSQL), times in UTC truncated to the microsecond in
  the column, so a cursor taken from a row's JSON compares exactly with the stored column.
* **The cursor** carries a format tag, the sort as a key (`ends_at`, `-highest_bid`, `id`), a 64-bit filter hash (the
  probe: 32), the id and the sort value; any mismatch is `Invalid` on field `after` (HTTP 400 in 12C). `limit` and
  `offset` out of range are `Invalid` on their field.
* **A batch shorter than `Batch` marks the projection `Current`** (also an empty feed); the state never goes back.
* **`IProjectionPositions` is generic over the row** (`WaitFor<TRow>`) instead of taking a name.
* **`ColumnType` uses PostgreSQL's short names** (`Int2`, `Int4`, `BigInt`, `Float8`): CA1720 refuses `Int16`/`Int32`/`Int64`/`Integer`.
* **Diagnostics**: TQRY001 also covers the name (lowercase letters, digits, hyphens, ≤ 40, so the table name fits 63
  bytes) and the version (≥ 1); TQRY003 also covers the return type; TQRY004 also a nullable or reserved sort member.
  Members named `Id`, `Tenant`, `Version`, `Doc` and non-scalar members get no column and no diagnostic.
* **A projection added twice** (same name and version) is refused at registration.
* **The user guide `docs/queries/`** comes with 12D (QR-7); the README and the skill ship here.

### 15.3 Changes against steps 7–11

None in the packages: 12A only adds `Tarol.Queries`. (12B changes `IAggregateStore.Append` and adds `NotifyFeed`, §10.)

### 15.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Queries.Tests` | 46 | every "memory" row of §3: one row per auction with another stream type ignored, rows equal the aggregates, tenant isolation, cursor pages = the ordered list, offset = cursor while nothing changes, filter + descending sort with the id tie-break, a cursor bound to its sort and filters, the page ETag (stable, changed by a bid on the page, unchanged by one off it), `WaitFor` (timeout, then passed); plus: small batches and `Building` → `Current`, replay from an older checkpoint changes nothing, an ignored event keeps the row, a crash before the write, a second runner refused by the guard, two runners at once, the hosted loop recovering from a failing feed, offset repeating a row under an insert where the cursor does not, limit/offset bounds, null filters, count, the column map, value round trips, uuid order, the builder (hosts, positions with and without a host, duplicates, SQL projections), the `SqlProjectionHost` guard and stream-type filter, the skill example verbatim |
| `Tarol.Queries.Generators.Tests` | 34 | snapshots (every column type, nullable columns, a computed `[Sort]` member, an entity event, a nested projection, `RecordedEvent` on create and update), generated code compiles without warnings, incrementality (unrelated file, `When` body edit: nothing regenerated; a new column regenerates), each diagnostic TQRY001–TQRY004 with its message |
| `Tarol.Queries.AotSmoke` | 9 checks | ProjectionTest catch-up, cursor pages, filter + count, offset, cursor binding, offset bound, ETag, the hosted projection with read-your-writes through DI; Native AOT, 0 IL warnings |
| `Tarol.Queries.PackageTests` | 1 | the packed package: the bundled generator implements `IProjection` on a consumer's record, a page sorted descending |

Timing: no test asserts a count inside a wall-clock window; waits are bounded by events with a 30 s cap.

### 15.5 Skill evaluation checklist (`tarol-queries`, projections part)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| a list of open auctions sorted by end | `[Projection("auction-list")]` row record with a static and instance `When`s, `[Sort] EndsAt`; `ListQuery { Filters = [new("status", AuctionStatus.Open)], Sort = "ends_at" }` | querying `streams.state`; reading the projection in a change's `Execute` |
| page through a large list | `After = page.Next` | `Offset` beyond 1,000; building cursors by hand; `Count = true` on every page |
| add a column to a list | a new member, `Version = 2`, wait for `ProjectionState.Current` | altering the read table; truncating it |
| totals per seller | `ISqlProjection` with an upsert keyed by (tenant, seller) | a per-stream row record keyed by seller |
| test it | `ProjectionTest<TRow>.Create().Given(…)`, `CatchUp()`, `Search(…)` | a database in a unit test |
| host it | `UseInMemoryReadModels()` / `UseNpgsqlReadModels()` + `AddProjections(p => p.Add<AuctionRow>())` in the worker | a hand-written polling loop over `IEventFeed` |

### 15.6 Suppressions and analyzer configuration

* `CA1040` on `IQuery<TRow>` (`[SuppressMessage]` with justification): an empty marker the endpoint generator reads.
* Test and smoke domains: `#pragma warning disable CA1822` around an `On` handler whose event changes nothing the
  aggregate decides on (not package code).

### 15.7 Verification (SDK 11.0.100, 2026-11-16) **[verified]**

`main` at `7af7845` before the change: 1262 tests, 18 AOT smoke apps. On this branch: `dotnet build Tarol.slnx` 0 warnings,
0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx` with Docker: **1342 of 1342**
(1262 + 46 `Tarol.Queries.Tests` + 34 `Tarol.Queries.Generators.Tests`); `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`,
`TAROL_RABBITMQ`: **19 apps** (18 + `Tarol.Queries.AotSmoke`), 0 IL warnings each, all checks passed; `dotnet pack -c Release`
+ `eng/verify-packages.sh`: OK (`Tarol.Queries` contains `lib/`, README, LICENSE, `analyzers/dotnet/cs/Tarol.Queries.Generators.dll`
and `skills/tarol-queries/SKILL.md`; its package consumer runs (`5 rows, first page note 4, note 3`) and lists the skill).

## 16. Implementation: PR 12B (2026-11-16)

`queries.npgsql: read tables and checkpoints`: the new package `Tarol.Queries.Npgsql`, and in `Tarol.Persistence(.Npgsql)`
`Append` returning the commit's position and the opt-in feed `NOTIFY` (§10).

### 16.1 Public API

| Type | Role |
|---|---|
| `NpgsqlReadModelStore<TRow>` (`IReadModelStore<TRow>`; `Table`, `NotPreparedCode`) | the read table of one projection version: `Prepare` (the script under an advisory lock), `Checkpoint`, `Load`, the guarded `Write`, `Find`, `Search` |
| `NpgsqlReadModels` (`IReadModels`, `ISqlProjectionStore`; `Schema`, `Typed<TRow>()`, `DropVersion(name, version)`) | the stores of an application, SQL projections, the explicit drop of an old version |
| `NpgsqlReadModelSchema` (`DefaultSchema`, `ProjectionsTable`, `Script<TRow>`, `SqlType`) | the SQL derived from the generated column map |
| `NpgsqlFeedSignal` (`IFeedSignal`, `IAsyncDisposable`; `Channel`) | `LISTEN <schema>_feed`, reconnecting |
| `NpgsqlReadModelOptions` (`Schema`, `Listen`), `NpgsqlQueriesTarolBuilderExtensions.UseNpgsqlReadModels(options, dataSource)` | registration: `IReadModels`, `ISqlProjectionStore`, `IFeedSignal`, `IProjectionPositions` |
| `Tarol.Persistence`: `IAggregateStore.Append` → `ValueTask<Result<GlobalPosition>>`; `StoreErrors.IsVersionConflict<T>(Result<T>)` | the commit's position (§10) |
| `Tarol.Persistence.Npgsql`: `NpgsqlStoreOptions.NotifyFeed`, `NpgsqlAggregateStore.FeedChannel`, `NotifiesFeed` | the feed's wake-up |

Tables: `<schema>.projections (name, version, tx xid8, pos, state 'building'|'current', updated_at)`, one row per projection
version; `<schema>.rm_<name>_v<n> (tenant, id, version, <columns>, doc jsonb)`, key (tenant, id), index `rm_…_s<k>` (tenant,
column, id) per `[Sort]` column.

### 16.2 Deliberate differences from §6, §7 and §11

* **The DDL and the upsert are derived from the generated column map** at run time (once per store), as announced in
  §15.2; `NpgsqlReadModelSchema.Script<TRow>` is public for migration tools and tested against `information_schema`.
* **Text columns are `COLLATE "C"`**: the database's default collation (`en_US.utf8` in the image) would order pages
  differently from the in-memory store and from the cursor's ordinal comparison.
* **The commit's position comes from the commit's batch**: the statement that read the stream version now also returns
  `pg_current_xact_id()` and the commit's highest event position (`version > expected AND tx_id = pg_current_xact_id()`),
  no extra round trip. A refused commit returns its violation as before. Nothing to append returns `GlobalPosition.Start`.
* **`NotifyFeed` is one `pg_notify` statement in the commit's batch** guarded like the outbox's (only when this commit's
  first event exists), not a trigger; a refused commit rolls back and notifies nothing.
* **Before any host prepared the table**, `Checkpoint` answers `Building` at `Start` and `Search` answers
  `Error.Unavailable("Projection.NotPrepared")` instead of throwing `42P01` (found by the AOT smoke app: the API process
  asks before the worker has started). 12C answers both with 503.
* **`UseNpgsqlReadModels` listens only when the registered store notifies** (`Listen` = `null` → `NotifiesFeed`): a
  `LISTEN` without `NotifyFeed` would replace the 100 ms poll by the 1 s fallback and nothing would ever wake it.
* **`DropVersion(name, version)`** is the explicit drop of QR-3 (step 15 may schedule it); it refuses a version this
  process uses.
* **No advisory lock between runners** (§5 item 3): the checkpoint guard serializes them; two runners at once give exact
  rows (tested). The DDL runs under an advisory lock (parallel `CREATE … IF NOT EXISTS` race on the catalog, as for the bus).

### 16.3 Changes against steps 7–11

| Where | Change |
|---|---|
| `IAggregateStore.Append`, `InMemoryAggregateStore`, `NpgsqlAggregateStore` | returns `Result<GlobalPosition>` instead of `Result` (QR-5). Callers that test `IsSuccess` or `TryGetValue(out Violation?)` compile unchanged (the pipeline, `ChangeTest`, `ReactionTest`, `MessageTest`, `MessagingTest`, `ProjectionTest`); test code comparing with `Result.Success()` now calls `.ToResult()` (19 assertions in the persistence tests, mechanical) |
| `NpgsqlStoreOptions` | `NotifyFeed` (default `false`); `NpgsqlAggregateStore.FeedChannel`, `NotifiesFeed` |
| `StoreErrors` | `IsVersionConflict<T>(Result<T>)` next to `IsVersionConflict(Result)` |
| store contract (`tests/Persistence/Shared`) | the returned position is the commit's last feed position, later than any earlier commit; `Start` for nothing appended |
| CI `integration` job | runs `tests/Queries/Tarol.Queries.Npgsql.IntegrationTests` |

### 16.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Queries.Npgsql.IntegrationTests` | 23 | every "postgres" row of §3: one row per auction equal to the aggregates with another stream type ignored and tenants isolated; cursor pages, filters, descending sort, offset, ETag, `Next`, `Position` and count equal to the in-memory store's for six queries; a cursor bound to sort and filters, bounds; the page ETag; read-your-writes from "another process" (`ProjectionPositions` polling the row); replay from an older checkpoint changes nothing; a crash before the write; two runners at once (exact rows, versions) and a deterministic refusal; offset repeating a row under an insert where the cursor does not; v2 built from the start next to v1 that keeps serving, `DropVersion`; `42P17` for a `timestamptz` generated from `jsonb`; an open transaction holding the projection back until it ends (the fallback poll picks it up); the feed signal waking at a commit; the stored state's counts and its silently empty wrong path; the catch-up of 1e5 events; plus the DDL against `information_schema`, the not-prepared answers, a SQL projection with two runners, `UseNpgsqlReadModels` wiring (schema, `LISTEN` only with `NotifyFeed`), a row type missing from the JSON context, the skill example verbatim |
| `Tarol.Persistence.Npgsql.IntegrationTests` | +3 | the contract's position checks; `NotifyFeed` on: one notification per committed commit, none for a refused one; off: none |
| `Tarol.Persistence.Tests` | +1 (contract) | the position checks on the in-memory store |
| `Tarol.Queries.Npgsql.AotSmoke` | 10 checks | the DDL; with `TAROL_PG`: `Append`'s position, read-your-writes from another process, `NotifyFeed` waking the host, the checkpoint, cursor pages, descending sort + count, a bad cursor, a SQL projection; Native AOT 0 IL warnings, 7.7 MB |
| `Tarol.Queries.Npgsql.PackageTests` | 1 | the packed package: the Queries generator flows, the script from a consumer's row, `UseNpgsqlReadModels` resolves |

**Integration tests on a shared cluster catch up to a known position**, not "until the feed is empty": a parallel test's
open transaction hides later commits from the feed (§5 item 4), so a single `CatchUp` may stop early. The first run showed
it (13 of 20 rows in one test); `Setup.CatchUp(host)` runs until the host passed the last commit `Append` returned, with
a 60 s cap. The integration project ran **10 times in a row in Release, 21 of 21 each**, while the AOT apps were
publishing (before the last two tests were added).

### 16.5 Skill evaluation checklist (`tarol-queries-npgsql`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| store the auction list in PostgreSQL | `UseNpgsqlStore(…)` + `UseNpgsqlReadModels()`, the row in the `JsonSerializerContext` | hand-written DDL; an ORM |
| make the list follow commits within milliseconds | `NpgsqlStoreOptions { NotifyFeed = true }` | a tight poll loop over `events`; removing the fallback poll |
| add a column | `Version = 2`, wait for `Current`, later `DropVersion("auction-list", 1, ct)` | `ALTER TABLE`, `TRUNCATE` |
| totals per seller | `ISqlProjection` writing on the given connection and transaction, keyed by tenant first | its own connection (not exactly once) |
| an integration test of a projection | catch up until `host.Position >=` the position `Append` returned | one `CatchUp()` on a shared database |

### 16.6 Suppressions and analyzer configuration

None.

### 16.7 Verification (SDK 11.0.100, 2026-11-16) **[verified]**

VERIFICATION_12B

## 17. Implementation: PR 12C (2026-11-16)

`changes.aspnetcore: list queries`: list entries in `Tarol.Changes.AspNetCore` and its generator, the position of a commit
on change answers, and the list authorization in `Tarol.Changes` / `Tarol.Security`.

### 17.1 Public API

| Type | Role |
|---|---|
| `[Endpoint("GET …")]` on a record implementing `Tarol.Queries.IQuery<TRow>` | a generated list entry (`List_<input>` delegate, `MapChanges`) |
| `FromClaimAttribute(type)` | a member set from the caller's claim (403 `Auth.Claim` without it), never from the request |
| `Positions` (`PositionHeader` = `Tarol-Position`, `ReadAfterHeader` = `Tarol-Read-After`, `StaleHeader` = `Tarol-Stale`, `Format`, `TryParse`) | the text form `<tx>.<pos>` of a `GlobalPosition` |
| `QueryEndpointOptions` (`ChangeEndpointOptions.Queries`: `MaxLimit` 200, `DefaultLimit` 50, `MaxOffset` 1,000, `ReadAfterTimeout` 2 s, `RetryAfter` 5 s) | limits and waits; the limits may be lowered, not raised above `ListQuery`'s |
| `ChangeEndpoints.ListAccess<TAggregate>`, `Paging`, `List<TRow>`, `Claim`, `One`, `TryFilter<T>`, `TryEnum<TEnum>`, `BuildingCode` | the runtime the generated delegates call (`EditorBrowsable(Never)`) |
| `TarolEndpointMetadata.Query`, `QueryParameter` | the list's query parameters, for OpenAPI |
| `Tarol.Changes`: `ChangeOutcome.Position`; `IChangeAuthorization.CheckList<T>(context, principal, ct)` (default: refuse) | QR-5, QR-6 |
| `Tarol.Security`: `ChangeAuthorization.CheckList<T>` | `AuthorizeAsync(principal, resource: null, Read<T>)`: only the role map can grant; logged (1002) and counted like a denied change |

A list request: route values → `ListAccess` (the edge: tenant from the claims, then `CheckList<TAggregate>`) → claim
members → route filters → query-string filters (`One`: a key given twice is `Multiple`; enums by name, case-insensitive,
numbers refused; other types `ISpanParsable<T>` in the invariant culture) → `sort` → `Paging` (`limit`, `after`, `offset`,
`count`; `offset` with `after` is refused) → every field error at once as one 400 → `List<TRow>`: `Tarol-Read-After`
(`IProjectionPositions.WaitFor`, then `Tarol-Stale: true`), 503 + `Retry-After` while `Building` or not prepared, `Search`,
`Tarol-Position`, weak `ETag` and 304 (RFC 9110 weak comparison), the page `{ "items", "next", "total" }`.

### 17.2 Deliberate differences from §6, §7 and §11

* **The generated delegate maps the request to a `ListQuery`; the SQL is the read store's** (§15.2): no per-record SQL
  builder is generated. The generator checks every member against the row's columns at compile time (TQRY005), so a
  wrong name never reaches the runtime check.
* **The aggregate of `Read<T>` is found from the row's `When` methods** (the first event's aggregate), as the Queries
  generator does; the endpoint generator cannot see the other generator's output.
* **`CheckList<T>` is a default interface method** that refuses, so an existing `IChangeAuthorization` compiles and fails
  closed on lists; `Tarol.Security` implements it.
* **The query record is not constructed**: the delegate binds its members straight into the `ListQuery` (a record of
  nullable filters has no behaviour to run).
* **The sort is the member named `Sort`** (an enum); its members name `[Sort]` members of the row or `Id`; its default is
  the member's default value, else the first enum member; without a `Sort` member the list is ordered by id.
* **Route members of a list filter too** (`GET /sellers/{seller}/auctions`), typed `Guid`, `string`, `int` or `long`.
* **`[FromClaim]` members are string or Guid**; a missing or unparsable claim is 403 `Auth.Claim`. TQRY006 warns when the
  route also names the member (the claim wins).
* **The page is written from a buffer** (`ContentLength` set): serializing a row through a `Utf8JsonWriter` on the
  response stream flushes synchronously, which ASP.NET Core refuses.
* **OpenAPI**: the entry declares the row as its 200 type (it is in the app's JSON context) and the transformer wraps it
  in the page schema; enum filters and the sort list their values. The generator tests' compilation suppresses CS1701/
  CS1702 like the SDK does by default (the generated `JsonNode` use meets `Microsoft.OpenApi`'s `System.Text.Json` 8 reference).
* **A replayed change has no `Tarol-Position`** (the idempotency key stores none; §10 said so).

### 17.3 Changes against steps 7–11

| Where | Change |
|---|---|
| `ChangeOutcome` | `Position` (last positional member, default `Start`); the pipeline sets it from `Append` |
| `IChangeAuthorization` | `CheckList<T>` (default method) |
| `ChangeAuthorization` | implements `CheckList<T>`; log event 1002 |
| `ChangeEndpoints.Write` | `Tarol-Position` on 201/204 |
| `ChangeEndpointOptions` | `Queries` |
| `TarolEndpointMetadata` | `Query` (optional last member) |
| `Tarol.Changes.AspNetCore` | references `Tarol.Queries` (its generator flows to package consumers) |
| tests linking the ArtFlow domain of the endpoint tests | also link `Queries.cs` and reference the Queries generator |

### 17.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Changes.AspNetCore.Tests` | +13 | the HTTP rows of §11: a change's `Tarol-Position` and a list read after it; the stale flag after a timeout and a malformed header (400); filters and sorts from the query string (case-insensitive enums, numbers refused, unknown sort, a key twice); cursor pages, offset, bad cursor, cursor of another sort, limit and offset bounds, `offset` with `after`, every error at once; count opt-in; ETag, 304, a bid on the page changes it; 401, 403 without `Read<T>` in the role map, a reader sees rows a row rule would hide from GET by id, another tenant sees nothing; `[FromClaim]` ignores a request value; a route filter with a descending default sort; 503 + `Retry-After` while building; a replay has no position; the OpenAPI document (parameters, sort values, 304, 503, the page schema, no claim parameter); position text round trips |
| `Tarol.Changes.AspNetCore.Generators.Tests` | +9 | the list snapshot (delegates, claim and route filters, OpenAPI parameters), the generated lists compile without warnings, TQRY005 (five shapes), TQRY006, a list with POST (TWEB002) |
| `Tarol.Changes.Tests` | +1 | `ChangeOutcome.Position` is the commit's last feed position |
| `Tarol.Security.Tests` | +2 | `CheckList` grants by role only (a row rule does not), refuses anonymous callers; the default method refuses |
| `Tarol.Changes.AspNetCore.AotSmoke` | +2 checks | a list read after a bid's `Tarol-Position` (row, weak ETag, position), a cursor page and a bad cursor; in memory and with `TAROL_PG` (Npgsql store and read models); Native AOT 0 IL warnings |

### 17.5 Skill evaluation checklist (`tarol-queries`, queries part; `tarol-endpoints`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| a list of open auctions sorted by end | `[Endpoint("GET /auctions")]` on `AuctionSearch(AuctionStatus? Status, AuctionSort Sort = AuctionSort.EndsAt) : IQuery<AuctionRow>` | a hand-written `MapGet` over `streams.state` |
| page through a large list | `?after=<next>` | `offset` beyond 1,000; building cursors; `count=true` on every page |
| "my auctions" | `[FromClaim("sub")] string Seller` | a `seller` query parameter the caller sets |
| show the caller's bid at once | send `Tarol-Read-After` with the change's `Tarol-Position` | polling the list in a loop |
| who may list | `roles.Role("bidder").May<Read<Auction>>()` | expecting a row rule (`ChangeHandler<Read<Auction>, Auction>`) to filter a list |

### 17.6 Suppressions and analyzer configuration

* Generator tests only: CS1701/CS1702 suppressed in the test compilation, as the .NET SDK's default `NoWarn` does in every
  real build.

### 17.7 Verification (SDK 11.0.100, 2026-11-16) **[verified]**

On this branch: `dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean; `dotnet test
--solution Tarol.slnx` with Docker: **1395 of 1395** (12B's 1370 + 13 endpoint tests + 9 generator tests + 1 pipeline test
+ 2 authorization tests). The first full run had one failure in a step-11 test
(`Tarol.Messaging.SignalR.Tests` … `A_connection_that_closed_for_good_is_started_again_by_the_client`: the client's own
restart did not reconnect within its 30 s wait) while native binaries were being published in parallel on this machine;
the project then ran 10 times in a row without load, 18 of 18 each, and the suite again, 1395 of 1395. `eng/aot-smoke.sh`
with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **20 apps**, 0 IL warnings each, all checks passed (the AspNetCore app with
its two list checks, in memory and on PostgreSQL); `dotnet pack -c Release` + `eng/verify-packages.sh`: OK (the AspNetCore
package consumer now also references `Tarol.Queries.Npgsql`: it shares the smoke app's `Program.cs`).

## 18. Implementation: PR 12D (2026-11-16)

`samples: the auction list`: the projection and the lists in `samples/Sagas`, and the user guide `docs/queries/README.md`.

### 18.1 What the sample shows

| Where | What |
|---|---|
| `Worker/AuctionList.cs` | `[Projection("auction-list")] AuctionRow` (seller, reserve, highest bid, bids, settled, paid, opened at; `[Sort]` on the highest bid and the opening time), `When` over `Auction.Opened`, `Bid.Placed` (an entity event), `Settled`, `Reopened`, `PaymentRecorded`; linked into the API |
| `Worker/Program.cs` | `UseNpgsqlStore(…, new NpgsqlStoreOptions { NotifyFeed = true })`, `UseNpgsqlReadModels()`, `AddProjections(p => p.Add<AuctionRow>())`; traces `Tarol.Queries` |
| `Api/Program.cs`, `Api/Http.cs` | `UseNpgsqlReadModels()`; `GET /auctions` (`AuctionSearch(bool? Settled, string? Seller, AuctionSort Sort)`), `GET /my/auctions` (`[FromClaim("sub")] Seller`); the existing `Read<Auction>` grants of `seller` and `bidder` |
| `apphost.sh` | after each bid: the list with `Tarol-Read-After` = the bid's `Tarol-Position` (the bid must be in it, no `Tarol-Stale`); at the end: total, a page with its cursor, alice's and bob's own auctions, the page ETag and a 304, the projection's checkpoint and row count |

### 18.2 Deliberate differences from §11

* The sample's list rows are the auctions of the settlement saga (no title or end time in this domain): the opening time
  (`RecordedEvent.RecordedAt`) stands in for `endsAt` as the second sort.
* Billing (the second system) has no projection: the step needs one list, and billing's reads are not part of ArtFlow.

### 18.3 Changes against steps 7–11

None in the packages.

### 18.4 Run **[verified]**

`samples/Sagas/apphost.sh samples/Sagas/AppHost/bin/Debug/net11.0 5` (SDK 11.0.100, Aspire 13.6.5, Zitadel v4.20.3,
NATS), JIT projects, then again with the Native AOT API and worker:

```text
api /health: 200 after 29s (native: 16s)
auction 1: bid 204 (Tarol-Position 1696.2), list read after it: bids 1, highestBid 150, stale 0; settle by bob 403, settle by alice 204
settlement sagas completed: 5; payments recorded in ArtFlow from billing: 5; dead letters: outbox 0/0, messages 0/0
auction list: total 5, settled 3 of the first 3, next cursor yes; stale read-after answers: 0 of 5
alice's own auctions (claim-bound): 5, bob's: 0
list ETag W/"08a0ebf35e486d1c", again with If-None-Match: 304
projection checkpoint: auction-list v1 current, rows 5
spans: … GET /auctions 8, GET /my/auctions 2, … project auction-list 19, …
```

Every read after a bid contained the bid (no `Tarol-Stale` in 5 of 5, both runs); the steps 9–11 results of the sample are
unchanged (5 payouts, 5 sagas completed, every event with a `traceparent`, the dashboard lists `api`, `worker`, `billing`
and now the `project auction-list` spans). The Native AOT API (25.1 MB) and worker (20.2 MB) publish with 0 IL warnings.

### 18.5 Skill evaluation (§12, §15.5, §16.5, §17.5) **[verified]**

The seven tasks of §12, each answered as code by a fresh agent of the session's frontier model: one given only the three
shipped skills (`tarol-queries`, `tarol-queries-npgsql`, `tarol-endpoints`), one given nothing (no files, no web).
Graded against the "Expected API" and "Mistakes to avoid" columns.

| Task | With the skills | Without |
|---|---|---|
| open auctions by end | `[Projection]` row + `When`s, `[Endpoint("GET /auctions")]` on `IQuery<AuctionRow>` with `Status?` and a `Sort` enum, `UseNpgsqlReadModels`, `AddProjections`, `Read<Auction>` grant ✓ | invented `[Query]`, `IListProjection<,>`, a LINQ `Query` method ✗ |
| page through a large list | `?after=<next>`, same filters and sort, no `offset` beyond 1,000, no `count` on every page ✓ | cursor idea right, names invented |
| add a column | `Version = 2`, wait for current, `DropVersion` later ✓ (called it as a static method ✗) | `Version = 2` on an invented attribute |
| my auctions | `[FromClaim("sub")] string Seller` ✓ | a claim-based LINQ filter on an invented API |
| caller's bid at once | `Tarol-Read-After` with the change's `Tarol-Position` ✓ | an invented `Tarol-Min-Position` / `If-Match` |
| totals per seller | `ISqlProjection` on the given connection and transaction, tenant first ✓ | an invented key-by-seller row projection (the mistake the checklist names) |
| test it | `ProjectionTest<AuctionRow>.Create().Given(…).CatchUp()`, `Search` ✓ | an invented `ProjectionTester` |
| tokens | 3 skills read, ≈ 77k tokens for the run | ≈ 64k tokens; confidence in its own names "20–30 %" |

With the skills: 7 of 7 on the expected API, none of the listed mistakes; without: the right concepts, no correct API name.
The gaps the skilled agent named were closed in this PR: the `sort` key, its default direction and the default `limit`,
the text form of `Tarol-Position`, that the claim column must hold the subject, that only the row goes into the JSON
context and that SQL projections are not served by list endpoints (`tarol-queries`), and that `DropVersion` is called
on the registered `NpgsqlReadModels` (`tarol-queries-npgsql`).

### 18.6 Verification (SDK 11.0.100, 2026-11-16) **[verified]**

The packages and tests are those of 12C (§17.7: 1395 of 1395, 20 AOT apps, packages OK); this PR changes only the sample
(not in `Tarol.slnx`), documentation and two skills (text only): `eng/verify-packages.sh` OK with the edited skills. `dotnet build samples/Sagas/AppHost` 0 warnings (besides the AppHost's
existing ASPIRE010 note), `dotnet publish -c Release -r linux-x64` of the API and the worker: 0 IL warnings; the run above.

## 19. TN-6: lists scoped by the caller's rows (2026-11-24)

Feedback from Tatalu ([docs/feedback/tatalu.md](../feedback/tatalu.md#proposed-changes), TN-6): "parents see only their own
children" (a parent with two children, a substitute assigned to three children for two weeks). Row rules do not run on lists
and `[FromClaim]` binds one claim value with equality; the set is domain data. This step adds a server-side hook per query
and set filters.

### 19.1 Public API (all additions)

| Package | API | Role |
|---|---|---|
| `Tarol.Queries` | `SetFilter(Column, Values)`, `SetFilter.Of<T>`, `SetFilter.IdColumn` (`"id"`) | `column = ANY(values)`; over an array column the array shares a value (`&&`); `id` is the row id. An empty set matches no row |
| | `ListQuery.Sets`, `ListQuery.Within(ListScope)` | the set filters of a query; adds a scope's filters to the request's own |
| | `ListScope` (`All`, `None`, `In<T>`, `Where`, `AndIn<T>`, `AndWhere`, `Filters`, `Sets`, `IsAll`) | what a scope answers; `None` is a set filter on the id with an empty set |
| | `ProjectionArrayColumn<TRow, TElement>`, `ProjectionColumn<TRow>.IsArray`, `.Matches(row, set)` | array columns (generated for `T[]` and `IReadOnlyList<T>` of Guid, string, short, int, long, an int enum) |
| | `ListQueryPlan<TRow>.Sets`, `SetFilterPlan<TRow>` | the checked sets: distinct values in stored order, the column (`null` = the id) |
| | `Cursor.Hash(filters, sets)` | the filter hash with sets; without sets it equals `Cursor.Hash(filters)`, so existing cursors stay valid |
| `Tarol.Queries.Npgsql` | `NpgsqlReadModelSchema.SqlType<TRow>(column)` | `uuid[]`, `text[] COLLATE "C"`, … for an array column |
| `Tarol.Changes.AspNetCore` | `IListScope<TQuery>.Scope(ChangeContext, ClaimsPrincipal, CancellationToken)` | the hook, resolved per request from the request's services |
| | `EndpointAttribute.RequiresScope` | the registration is mandatory: `MapChanges` throws without it (fail closed at startup) |
| | `TarolEndpointMetadata.Scoped` | a list with a registered scope; OpenAPI `x-tarol-scoped: true` and a "Scope:" paragraph |
| | `ChangeEndpoints.Scope<TQuery>`, `ChangeEndpoints.HasScope<TQuery>` | the runtime the generated code calls (`EditorBrowsable(Never)`) |

### 19.2 Behaviour changes

* **Array members become columns.** A `Guid[]`, `string[]`, `int[]`, `long[]`, `short[]`, int enum array or
  `IReadOnlyList<T>` of those member was jsonb-only; it is now an array column with a GIN index. Its read table gains the
  column: `NpgsqlReadModelStore.Prepare` adds missing columns to an existing table, fills them from the stored rows' `doc`
  (the row's shape is the version's, so the JSON still reads; batches of 500 in the prepare transaction under its advisory
  lock), sets `NOT NULL` where the member is not nullable, then creates the indexes. The checkpoint does not move; nothing
  is rebuilt. A migration tool that runs `NpgsqlReadModelSchema.Script` on an existing table does not get the new column
  (`CREATE TABLE IF NOT EXISTS`): let `Prepare` run once, or add the column yourself.
* `[Sort]` on an array member is TQRY004 "an array column takes set filters only" (it was "has no column type").
* An equality filter (`ColumnFilter`) on an array column throws `ArgumentException`.
* A filter or set may name the member (`nameof(Row.Children)`) as well as the column (`children`).
* `RequiresScope = true` on a change or a view is TWEB002.
* Every generated list delegate calls `ChangeEndpoints.Scope<TQuery>` after the request's filters; without a registered
  `IListScope<TQuery>` (and without `RequiresScope`) the list answers as before.
* `PageKey` (`Tarol.Caching`) includes the set filters: two callers of one tenant with different scopes never share a
  cached page. Without the fix they did: `PageKeyTests.Two_callers_of_one_tenant_with_different_scopes_never_share_a_page`
  fails on the previous `PageKey` **[verified]**.

### 19.3 Design notes

* **The scope is per query record** (`IListScope<TQuery>`), as the request needs ("z. B. IListScope<TQuery>"): two lists over
  one row can be scoped differently ("my entries" and "the group's"). It runs after `CheckList` and gets what `CheckList`
  gets (`ChangeContext` with the tenant, actor and `Now`, and the principal), so a period that ends ("for two weeks") is
  checked against the same clock as the change pipeline.
* **An empty set is an empty page.** SQL `= ANY('{}')` and `&& '{}'` are false; the in-memory store matches nothing. A scope
  that means "the caller sees nothing" returns `ListScope.None` or an empty set; `ListScope.All` means no restriction.
* **The cursor is bound to the scope**: a cursor read under one scope is a 400 under another (the set changed, e.g. a
  child was assigned), never a page of other rows. Set order and duplicates do not matter (sorted, distinct).
* **Array columns travel as text arrays of PostgreSQL array literals** in the batch upsert: `unnest` over an array of
  arrays would flatten it. Every element is quoted and escaped (`\`, `"`); the test stores `a "quoted", {odd} \ label`.
* **Search** (`MapTarolSearch`) is not scoped by `IListScope`; it has its own audience (`[SearchAudience]`, step 18D).

### 19.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Queries.Tests` | +9 (`ListScopeTests`) | a parent with two children (uuid column); overlap over a uuid[] column; `None` and empty sets give an empty page with `total` 0 and no `next`; the id; with the request's filters; never another tenant; the cursor bound to the set but not its order or duplicates; the hash without sets is unchanged; programming errors |
| `Tarol.Queries.Npgsql.IntegrationTests` | +3 (`ListScopeTests`) | seven scopes answer like the in-memory store, with counts; uuid[] and text[] stored as arrays (escaping) with GIN indexes; `Prepare` adds, fills and sets `NOT NULL` on columns a table of 0.1.0-preview.5 lacks, without moving the checkpoint |
| `Tarol.Changes.AspNetCore.Tests` | +6 (`ListScopeEndpointTests`) | a parent sees two sellers' rows; a substitute sees three within the period and none after it; an empty scope answers an empty page; request filters with the scope, the cursor of another scope is 400, ETags differ; `x-tarol-scoped` and the description in the document; `RequiresScope` without a registration fails at startup |
| `Tarol.Caching.Tests` | +1 | the page key of each scope differs; order and duplicates do not |
| `Tarol.Queries.Generators.Tests` | snapshot, TQRY004 | `Guid[]?`, `IReadOnlyList<string>`, an enum array and `int[]` as array columns; `[Sort]` on an array |
| `Tarol.Changes.AspNetCore.Generators.Tests` | snapshot, +1, TWEB002 | the list snapshot with `RequiresScope`; the skill's scope example compiles; `RequiresScope` on a change |
| AOT smoke | +2 checks | `Tarol.Queries.Npgsql.AotSmoke`: a set over a text[] column and `ListScope.None` on PostgreSQL; `Tarol.Changes.AspNetCore.AotSmoke`: a scoped list natively (a parent's rows, a stranger's empty page), in memory and with `TAROL_PG` |

### 19.5 Skill evaluation checklist (`tarol-queries`, `tarol-endpoints`, `tarol-caching`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| "parents see only their children's daily logs" | `IListScope<DailyLogs>` returning `ListScope.In(nameof(Row.Child), children)`; `[Endpoint(…, RequiresScope = true)]` | a row per (child, viewer); a hand-written endpoint; a claim with the child ids; a row rule |
| a group entry naming several children | a `Guid[]` member and `ListScope.In("tagged", children)` | `ColumnFilter` on the array (throws); one row per child |
| a substitute for two weeks | compare the period with `context.Now` in the scope | `DateTimeOffset.UtcNow`; caching the set across requests |
| the caller may see nothing | `ListScope.None` or an empty set | `ListScope.All` (every row) |

### 19.6 Suppressions and analyzer configuration

None.

### 19.7 Verification (SDK 11.0.100, 2026-11-24) **[verified]**

`dotnet build Tarol.slnx` 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx`
with Docker: **2143 of 2143** (one earlier full run had `PushTests.A_timed_out_restart_attempt_does_not_end_the_restarts` fail
on timing under load; the project then ran three times, 31 of 31 each); `dotnet pack -c Release` + `eng/verify-packages.sh`:
OK; `eng/aot-smoke.sh` with `TAROL_PG`: **28 apps**, 0 IL warnings each, all checks passed.

## 20. TN-7: DateOnly and TimeOnly columns (2026-11-24)

Feedback from Tatalu (TN-7): attendance by day, the monthly list per child, closing days and validity periods
(`MembershipRow.ValidUntil`) are calendar days. Until now only `Guid, string, bool, short, int, long, decimal, double,
DateTimeOffset, enum` got columns; a `DateOnly` lived in the row's jsonb only, so it could be neither filtered nor sorted.

### 20.1 Public API (additions)

| Package | API | Role |
|---|---|---|
| `Tarol.Queries` | `ColumnType.Date` (`date`), `ColumnType.Time` (`time`), appended after `TimestampTz` (the existing numbers stay) | the stored types |
| | `ColumnValues.Time(TimeOnly)`, `ColumnValues.Time(TimeOnly?)` | a time of day as `time` stores it: truncated to the microsecond |
| `Tarol.Changes.AspNetCore` | `ChangeEndpoints.TryDate`, `ChangeEndpoints.TryTime` (`EditorBrowsable(Never)`) | the binder of a `DateOnly?` / `TimeOnly?` filter member |

### 20.2 Behaviour changes

* A `DateOnly` / `TimeOnly` (or nullable) member of a projection row is a column; `[Sort]` on one is allowed. An existing read
  table gets the column through `Prepare`'s upgrade (§19.2: added, filled from `doc`, `NOT NULL` where the member is not
  nullable, indexes created; no rebuild).
* A list query record may filter on such a member: `?endsOn=2026-11-24` (RFC 3339 `full-date` only) and `?at=08:15`,
  `08:15:30` or `08:15:30.1234567` (no zone: a `TimeOnly` has none). Any other form (`24.11.2026`, `11/24/2026`, a
  `date-time`, `9pm`, an offset) is the field error `Format`. The OpenAPI parameters carry `format: date` / `time`; a value
  object over `TimeOnly` is `{type: string, format: time}`.
* The cursor carries a day as its day number and a time of day as its ticks; a cursor of another column type does not decode.

### 20.3 tarol-codegen

`tarol-codegen` types a `{type: string, format: date|time}` parameter or member as `string` (it only special-cases
`uuid` with `x-tarol-id`, `generate.ts:238`) **[verified by reading]**; the client sends the text as it is. No client change
is needed; the clients format a day as `yyyy-MM-dd`.

### 20.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Queries.Tests` | +6 (`DateColumnTests`) | the column map; a day as equality filter and as descending sort with cursor pages; a nullable day (`IS NULL`, a value) and a set of days; a time of day stored to the microsecond; cursor text round trips and bounds; an instant is not a day |
| `Tarol.Queries.Npgsql.IntegrationTests` | +3 | `date` / `time without time zone` columns; six queries answer like the in-memory store; `Prepare` adds and fills the columns on a table without them, with their sort indexes, without moving the checkpoint |
| `Tarol.Changes.AspNetCore.Tests` | +7 (`DateFilterEndpointTests`) | day and time filters and the day sort over HTTP, the row's JSON; five wrong formats are 400 `Format`; the document's `format: date` / `time` |
| generator snapshots | | a `[Sort] DateOnly` and a `TimeOnly?` column; the list binder with `TryDate` / `TryTime` and their OpenAPI kinds |

Red before the fix **[verified]**: the test domain built on the TN-6 commit fails with `TQRY004: [Sort] on
'AttendanceRow.Day': System.DateOnly has no column type` (and the same for `TimeOnly`).

### 20.5 Skill evaluation checklist (`tarol-queries`)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| attendance per day | a `[Sort] DateOnly Day` row member; `?day=2026-11-24` | a `DateTimeOffset` at midnight in some zone; filtering in memory |
| a validity period ends on a day | `DateOnly? ValidUntil` | a `string` column with a formatted date |

### 20.6 Suppressions

None.

### 20.7 Verification (SDK 11.0.100, 2026-11-24) **[verified]**

`dotnet build` 0 warnings; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx` with Docker:
**2158 of 2159** — the one failure was `Tarol.Messaging.SignalR.Tests` `PushTests.A_connection_that_closes_again_while_the_client_restarts_is_started_again`
(timing, while other builds ran on the machine); the project then ran three times, 31 of 31 each. `dotnet pack` +
`eng/verify-packages.sh`: OK. `eng/aot-smoke.sh` with `TAROL_PG`: 28 apps, 0 IL warnings, all checks passed (the new
`DateOnly`/`TimeOnly` check of `Tarol.Queries.Npgsql.AotSmoke` among them).
