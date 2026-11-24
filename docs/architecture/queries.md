# Queries and read models

Concept note for step 12 (see [migration/queries.md](../migration/queries.md)): **reading what the changes wrote.** A
projection is a row record with one `When` per event; a host per projection reads the store's `IEventFeed` after its
stored `GlobalPosition` and writes the rows and the checkpoint in one transaction. The rows live in a table per
projection version, owned by Tarol, with typed columns for filters and sorts plus the row as `jsonb`. A list query is a
record with `[Endpoint("GET …")]`: equality filters, a whitelisted sort, an opaque cursor and a bounded limit, always
tenant-scoped. A projection lags the commit and says by how much; a caller that needs its own write waits for it,
opt-in. A decision never reads a projection. GET by id through `IView<,>` and `Read<T>` shipped in step 10 (R-1, R-8)
and stays the consistent read of one aggregate.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/queries/`](../migration/probes/queries/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: designed** (owner decisions QR-1 … QR-7, [migration/queries.md](../migration/queries.md) §9); PRs 12A–12D. **12A implemented** (`Tarol.Queries`, migration/queries.md §15); **12B implemented** (`Tarol.Queries.Npgsql`, §16); **12C implemented** (list entries in `Tarol.Changes.AspNetCore`, §17); **12D**: the auction list in `samples/Sagas` and the user guide [`docs/queries/`](../queries/README.md) (§18). **Step 12 done.**

## 1. What a user of Tarol needs

| ArtFlow use case | Shape | Built from |
|---|---|---|
| a bidder browses open auctions ending soonest | list: filter `status`, sort `endsAt`, pages | `[Endpoint("GET /auctions")]` on `AuctionSearch` over the `auction-list` projection |
| a seller sees their own auctions | list filtered by the caller | `[FromClaim("sub")] Seller` on the query record (QR-6) |
| the client shows its bid at once after placing it | read-your-writes | `Tarol-Position` from the change, `Tarol-Read-After` on the GET (QR-5) |
| one auction with every detail, always current | GET by id | `IView<Auction, AuctionSummary>` (step 10, unchanged) |
| a new column in the list after a deploy | rebuild | `Version = 2`: a new table built from the start next to v1 (QR-3) |
| totals per seller (across streams) | a table the app shapes | `ISqlProjection`: the app's SQL in the batch transaction (QR-2) |
| test a projection and its query without a database | in memory | `ProjectionTest` (QR-7, 12A) |

## 2. One read, end to end **[verified]** (`selftest postgres`, 21 checks; JIT and Native AOT)

```text
api        POST /auctions/{id}/bids → PlaceBid → commit (e.g. tx 7341, position 1000412) → 204, Tarol-Position: 7341.1000412
           (NOTIFY tarol_feed, opt-in like S-10)
worker     ProjectionHost "auction-list" v1: ReadAll(after checkpoint, 2000) → rows of the streams in the batch
           → When(BidPlaced) per event (skip versions ≤ the row's) → BEGIN; UPDATE checkpoint WHERE = the one read;
           upsert rows; COMMIT                                                       commit → projected p50 3.6 ms (NOTIFY)
api        GET /auctions?status=Open&sort=endsAt  Tarol-Read-After: 7341.1000412
           → waits until checkpoint ≥ 7341.1000412 (≤ 2 s) → SELECT … WHERE tenant = $1 AND status = $2
             AND (ends_at, id) > ($3, $4) ORDER BY ends_at, id LIMIT 51
           → 200 {items, next}, ETag W/"<hash of (id, version), next, total>", Tarol-Position: <checkpoint>
```

## 3. Projections (QR-1, QR-2)

```csharp
[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt, AuctionStatus Status,
    [Sort] decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0);
    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1 };
    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}
```

* **Declaration** (QR-2): the row record is the read model and the API's contract (like a view, R-8). A static `When`
  creates the row (the stream's first relevant event), an instance `When` returns the next row; pure functions of the
  row and the event, no services, no I/O, never a rule (validation.md: projections never call rules). The generator writes
  the switch over the events, the column map, the DDL and the upsert; no reflection, AOT-clean **[verified]** (the probe's
  hand-written "generated" code, 0 IL warnings).
* **Key**: per stream, `(tenant, stream id)`; the row keeps the stream version. Cross-stream tables (totals per seller)
  are an `ISqlProjection`: Tarol hands the batch of events and the batch transaction, the app writes its own SQL.
* **Where it runs** (QR-1): a `ProjectionHost` per projection (a hosted service in the worker process, next to the
  reaction worker): `ReadAll(checkpoint, batch)` → filter by stream type → load the rows of the streams in the batch →
  apply → write. Not outbox rows (+0.5 ms per commit, 227 rows/s, no rebuild: rows exist only for new events) and not
  inline (a projection's failure would fail the decision) **[verified]**.
* **Exactly-once effect**: rows and checkpoint in one transaction. The checkpoint update goes first and is guarded by
  the position the batch started from, so a second runner of the same projection blocks on the row lock, finds the
  checkpoint moved and re-reads **[verified]** (two runners at once: exact rows, 20 refused batches). Events whose
  version is ≤ the row's version are skipped, so a replay from an older checkpoint changes nothing **[verified]**. A
  crash before the write leaves rows and checkpoint unchanged **[verified]**.
* **Columns**: every scalar member of the row is a typed column (filters compare it); `[Sort]` adds an index
  `(tenant, column, id)`. The whole row is also stored as `jsonb`, which the query returns.
* **Typed columns are written by the host**, not generated by PostgreSQL from the `jsonb`: a `timestamptz` column
  `GENERATED ALWAYS AS ((doc->>'endsAt')::timestamptz)` is refused (`42P17`, the cast is not immutable) **[verified]**.

## 4. Read tables (QR-2, QR-3) **[verified]**

```sql
CREATE TABLE tarol.rm_auction_list_v1 (
  tenant uuid NOT NULL, id uuid NOT NULL, version bigint NOT NULL,          -- key + the stream version (idempotence)
  title text NOT NULL, seller text NOT NULL, ends_at timestamptz NOT NULL,   -- typed columns: filters and sorts
  status smallint NOT NULL, highest_bid numeric NOT NULL, bids integer NOT NULL,
  doc jsonb NOT NULL,                                                        -- the row as the API returns it
  PRIMARY KEY (tenant, id));
CREATE INDEX … (tenant, ends_at, id);  (tenant, status, ends_at, id);  (tenant, highest_bid, id)   -- one per declared sort
CREATE TABLE tarol.projections (name text, version integer, tx xid8, pos bigint, updated_at timestamptz, PRIMARY KEY (name, version));
```

* The tenant leads every key and every index; every query filters by it (persistence.md §2, the store's rule).
* **Rebuild = a new version** (QR-3): raising `Version` builds `rm_<name>_v<n+1>` from `GlobalPosition.Start` while v<n>
  keeps serving the old code; the new code reads v<n+1> and answers `503` + `Retry-After` while it is still building.
  Old tables are dropped by an explicit operation (step 15), never automatically. Not truncate + replay (readers saw a
  partial table for the 10 s it took) and not a rename swap (12.6 ms, but concurrent queries hit p99 45 ms, max 132 ms).

## 5. List queries (QR-4, QR-6)

```csharp
[Endpoint("GET /auctions", Policy = "auctions:read")]
public sealed partial record AuctionSearch(AuctionStatus? Status, string? Seller, AuctionSort Sort = AuctionSort.EndsAt)
    : IQuery<AuctionRow>;

public enum AuctionSort { EndsAt, [Descending] HighestBid }
```

* **Filters**: nullable members named like a column, equality only; **sorts**: an enum whose members name columns
  (`[Descending]` per member), the row id the tie-break, one index per sort. Nothing from the request becomes SQL text.
* **Pages**: `?limit=` (default 50, at most 200) and `?after=<cursor>`; the answer `{ "items": […], "next": "<cursor>" }`.
  The cursor is opaque (base64url) and carries the sort, a hash of the filters, the last sort value and the id; a cursor
  of another sort or filter is a `400`, never a wrong page **[verified]**. `?offset=` up to a maximum (default 1,000;
  deeper is a `400`); a total count only when the query asks for it.
* **Why the cursor is the default** **[verified]** (page of 50, tenant of 1e6 rows): offset 1.0 ms at page 1, 19 ms at
  depth 10,000, 362 ms at 100,000, 3.2 s at 900,000; the cursor 0.5–1.0 ms at every depth. An insert before the page makes
  offset page 2 repeat the last row of page 1; the cursor continues without repeat or gap. A total count costs 62 ms at
  1e6 rows, 5.9 ms at 1e5.
* **Who sees which rows** (QR-6): a list needs the `Read<T>` grant (R-5) and is always tenant-scoped. Row visibility is a
  filter bound to the caller: a member marked `[FromClaim("sub")]` is set from the principal, never from the request.
  Row rules (`ChangeHandler<,>`) authorize one loaded aggregate; they do not run on lists, and the docs say so.
  A set of visible rows that is domain data, not a claim (TN-6), comes from a server-side `IListScope<TQuery>`: its
  `ListScope` adds set filters (`column = ANY(…)`, `array && …`, or the row id) after the request's filters; an empty
  set lists nothing. `[Endpoint(…, RequiresScope = true)]` makes the registration mandatory (startup fails without it).
  The scope binds the cursor hash and the page cache key, and the OpenAPI operation says `x-tarol-scoped: true`.
* **Hand-written queries** stay possible behind the app's own port (the projection's table name and the data source
  come from DI); `Page<T>` and the cursor codec are public.

## 6. Consistency (QR-5)

| Read | Consistency | Stated how |
|---|---|---|
| a decision (`Execute`) | the aggregate as loaded, guarded by its version | never reads a projection (changes.md) |
| GET by id (`IView<,>`) | current: the stored state of the stream | `ETag: "<version>"` |
| a list | lags the commit (NOTIFY: p50 3.6 / p99 6.1 ms; poll 100 ms: 56 / 108 ms) | `Tarol-Position: <checkpoint>` |
| a list after the caller's own change | waits for it (opt-in) | `Tarol-Read-After`; `Tarol-Stale: true` on timeout |

* **Read-your-writes** (QR-5): `Append` returns the commit's `GlobalPosition`; every change response carries it as
  `Tarol-Position`. A GET with `Tarol-Read-After: <position>` waits until the projection's checkpoint passes it, up to a
  timeout (default 2 s), then answers `200` with `Tarol-Stale: true`. Measured with NOTIFY: in the worker's process (a
  signal) p50 3.7 / p99 6.4 ms; from the API's process (polling the checkpoint row every 5 ms) p50 8.2 / p99 11.2 ms
  **[verified]**.
* **Wake-up**: the store sends `NOTIFY <schema>_feed` with every commit when asked (`NpgsqlStoreOptions.NotifyFeed`,
  opt-in like S-10; the probe's trigger costs nothing measurable per commit); the host polls as the fallback, because
  `pg_snapshot_xmin` holds a commit back while any older transaction in the cluster runs (persistence.md §9) — an open
  transaction elsewhere held the projection back until it ended **[verified]**.
* **ETag of a list**: a weak hash of the page's (id, version) pairs, its `next` cursor and its total: equal while the page's
  rows, `next` and the count are unchanged, different after a bid on one of them **[verified]** and after a row appended behind
  a full last page (BC-6, [client-contract.md](../migration/client-contract.md) §5) **[verified]**; it costs nothing beyond the
  page. Step 13 caches with it.

## 7. Measurements **[verified]** (4 cores, PostgreSQL 17 in Docker, default settings; [`output.txt`](../migration/probes/queries/output.txt))

| What | Result |
|---|---|
| catch-up of 1e6 events into 1e5 rows (batch 500 / 2,000 / 5,000) | 67k / 133k / 153k events/s; checkpoint in the transaction or not: same (130k) |
| the feed alone (read + decode, batch 2,000) | 366k events/s |
| commit p50: baseline / + outbox row / + inline trigger / + feed NOTIFY | 1.35 / 1.85 / 1.45 / 1.36 ms |
| outbox-row projection (one loop, a round trip per row) | 227 rows/s |
| lag commit → projected, 50 commits/s, 1 and 4 writers: poll 100 ms / poll 10 ms / NOTIFY | p50 56 / 9–10 / 3.6–4.4 ms; p99 108 / 17–21 / 6–8 ms |
| page of 50 at depth 0 … 900,000 (tenant of 1e6 rows), offset vs cursor | 1.0 ms … 3.2 s vs 0.5–1.0 ms |
| page 1 from `streams.state` (expression index) vs the projection table | 0.53 vs 0.35 ms (SQL); a filter the index does not cover: 19 ms |
| Native AOT | 0 IL warnings, 7.8 MB; every check, the lag (p50 4.0 ms) and the catch-up (132k events/s) as on JIT |

## 8. Testing (QR-7)

| Level | Tool | Covers |
|---|---|---|
| projection | unit test of the `When` methods | the mapping |
| feed → projection → query | `ProjectionTest`: in-memory store, its feed, an in-memory read store, `CatchUp()`, the query | rows, tenant isolation, cursor pages, filters, ETag, read-your-writes **[verified]** (`selftest memory`, 12 checks) |
| PostgreSQL | a contract suite on Testcontainers | the in-memory checks + crash, two runners, replay, versions, xmin, offset under insert |
| generator | snapshots | switch, column map, DDL, query SQL, diagnostics |
| native | an AOT smoke app per package | host + table + query against `TAROL_PG` |

## 9. The stored state is not a read model

`streams.state` answers simple lists without a projection (page 1 in 0.53 ms with an expression index) **[verified]**, but
its paths are the aggregate's internals: the enum is stored as `"Open"` (the events carry it as a number), a renamed
property changes the path, and a wrong path is silently empty — no error **[verified]**. Step 10 already decided that the
stored state is not an API (R-8). An app may still query it by hand; Tarol generates queries only over projections.

## 10. What Queries owns, and what it does not

Owns: `[Projection]` and its generator, `ProjectionHost`, the read stores (in-memory, Npgsql) and their checkpoints,
versions and rebuild, `ISqlProjection`, `ProjectionTest`, the list queries (query records, cursor, offset, ETag,
read-after) in `Tarol.Changes.AspNetCore`. Does not own: the feed and its NOTIFY (Persistence), GET by id and `Read<T>`
(Changes.AspNetCore, Security), caching (step 13), dropping old versions and lag metrics (step 15). Step 13 ([caching.md](caching.md)) caches list
pages in the API process, valid while the projection's checkpoint is current (CA-1, CA-5); GET by id stays uncached.

## New insights

1. **A per-stream row is idempotent by its stream version.** Skipping events at or below the row's version makes any
   replay harmless; the transactional checkpoint then only saves work.
2. **The checkpoint guard is the lock.** Updating the checkpoint first, guarded by the position the batch read from,
   serializes two runners without an advisory lock.
3. **Typed columns cannot be derived from `jsonb` by PostgreSQL** when the cast is not immutable (`timestamptz`), so the
   host writes them.
4. **Offset is wrong before it is slow**: an insert before the page repeats a row, at any depth.
5. **The feed is only as fresh as the oldest transaction in the cluster**: NOTIFY wakes the host, the poll catches
   commits that an older transaction held back.
6. **The stored state and the events disagree on representation** (enum as name vs number): one more reason the state is
   not a query surface.

## Decisions taken (owner, 2026-11-16; migration/queries.md §9)

QR-1 a feed loop per projection, rows + checkpoint in one transaction; QR-2 a row record with `When` methods and a
generator, Tarol-owned tables with typed columns + `jsonb`, `ISqlProjection` for the rest; QR-3 versioned tables, 503 while
building, explicit drop; QR-4 a generated GET list endpoint, cursor by default, offset bounded, count opt-in; QR-5 the
commit's position, opt-in read-after with a timeout and a stale flag, the page ETag; QR-6 `Read<T>` + claim-bound filters,
no row rules on lists; QR-7 two new packages and the endpoints in `Tarol.Changes.AspNetCore`, four PRs 12A–12D.

## Open questions

* A stream-type filter on `IEventFeed.ReadAll` (a projection reads every event today; the feed alone does 366k events/s,
  so not needed yet; it would need an index `(type, tx_id, position)` written by every commit).
* Range filters (`endsAt >= …`) and full-text search: later, if a scenario asks; equality and sorts cover ArtFlow.
* Pushing read-model changes to the UI (messaging.md §10): the host could publish after its batch; step 13 or later.
* Lag metrics per projection and the drop of old versions: step 15. Designed in step 15: [operations.md](operations.md) (O-1: `tarol.projections.lag`; O-5: `DropVersion` explicit with `lock_timeout`; O-8: one runner per version).
