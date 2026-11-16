# Caching

Concept note for step 13 (see [migration/caching.md](../migration/caching.md)): **caches that never break a decision.**
Tarol caches one thing, list pages, in the API process. A page is valid exactly as long as the projection's checkpoint it
was read at is current. The worker pushes that checkpoint with a NOTIFY from its batch transaction; a poll covers a lost
connection. A decision never reads a cache: loads, lookups and the commit read and write the store, as in steps 7–12. GET
by id stays the consistent read. HTTP gets `ETag`/304 and headers that keep shared caches out; there is no output caching.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/caching/`](../migration/probes/caching/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: 13A and 13B done** (pushed checkpoints, [migration/caching.md](../migration/caching.md) §15; the package
`Tarol.Caching`, §16); designed: owner decisions CA-1 … CA-8 (§9); PR 13C to come.

## 1. What a user of Tarol needs

| ArtFlow use case | Shape | Built from |
|---|---|---|
| many bidders browse the open auctions; the list must not cost two queries per request | a cached list page | `AddCaching()` (13B) over the `auction-list` projection; checkpoint pushed (13A) |
| a bidder pages back to a page they saw | 304 without a query | the page ETag (QR-5) answered from the cache |
| the bidder sees their own bid at once | read-after through the cache | `Tarol-Read-After`: wait for the pushed checkpoint, then the cache misses and reads |
| one auction, always current | GET by id | `IView<,>` (step 10), **not cached** (CA-1) |
| a bid decided on the current state | a change | the pipeline loads from the store, **no aggregate cache** (CA-4) |

## 2. One cached list request **[verified]** (`selftest postgres`, 22 checks; JIT and Native AOT)

```text
API process                                        PostgreSQL                         worker process
GET /auctions?status=Open  ──► ListAccess (CheckList: Read<T>, QR-6)
                              Tarol-Read-After? ── wait for the pushed checkpoint ◄── NOTIFY <schema>_checkpoint
                              Checkpoint<TRow>()  = pushed position (0 round trips)     (sent in the batch transaction,
                              Search(query)        ──► cache key (tenant, projection,   delivered after its commit)
                                                       version, canonical query)
                                hit: entry.Position == checkpoint ─► the page          ProjectionHost<TRow>:
                                miss: Search the read table, stamp with the position   rows + checkpoint, one transaction
                                      read before the search
                              ETag == If-None-Match? ─► 304 (no query on a hit)
                              200 + Tarol-Position + Cache-Control: private, no-cache + Vary: Authorization
```

## 3. What is cached, and what is not (CA-1, CA-3, CA-4)

| Read | Cached | Why **[verified]** |
|---|---|---|
| list pages | **yes** | two round trips (p50 1.2 / 3.6 / 13.3 ms at 1 / 16 / 64) → ~1 µs; a decision never reads a projection |
| views (GET by id) | no | 0.27 ms for the load. The consistent read would lag like a projection, and finding the current version costs the same round trip as the load |
| aggregate loads in the pipeline | no | the load is 0.27 ms of a ~7 ms change. Two processes with caches have 0.27 instead of 0.01 conflicts per success (−10 %). A stale entry that is not evicted on conflict costs 3–62 conflicts per success |
| `Find` (lookups) | no | a released lookup points at its old holder: the decision runs on the wrong aggregate, and no version guard sees it |
| codec metadata | already | the codec's tables are built at registration: `StreamTypeOf` 12 ns, `Decode` ≈ 1 µs |

## 4. Validity: the projection's checkpoint (CA-5)

* An entry is stamped with the checkpoint read **before** the search. It is a hit only while the pushed checkpoint still equals
  that stamp. A batch that lands during the search makes the next request miss.
* The checkpoint is the projection's own durable position. It moves only after rows and checkpoint are committed together
  (QR-1). So a cached page is never older than an uncached one would be at the same instant, and with an open transaction
  elsewhere both show the same page **[verified]**.
* Staleness is the projection's lag: commit → visible in the cached answer p50 7.0 / p99 10.9 ms (uncached 8.7 / 42.6 ms) **[verified]**.
* Any batch moves the checkpoint, of any tenant. That is coarse but enough: hit ratio 99.6 % at ~75 commits/s, 99.8 % when another
  tenant writes 50 commits/s **[verified]**. Per-tenant tags are an open item.
* No TTL decides validity: a 1 s TTL served stale pages for up to 998 ms **[verified]**. An idle expiry on `TimeProvider` only frees
  memory. Caps on entries and bytes bound the process.

## 5. Where it lives (CA-2, CA-7)

* **In each API process.** With a pushed checkpoint a hit takes 0 round trips. Reading the checkpoint per request would cost one
  (0.3 / 0.9 / 2.7 ms) **[verified]**.
* **Not HybridCache, not Redis or Garnet** **[verified]**:
  * no backplane: a removal in one process reached another only when its L1 expired;
  * its expiry ignores a `TimeProvider` in DI;
  * 9 IL warnings under Native AOT from its default serializer;
  * an L2 hit costs 0.18–0.84 ms.
* **Packages:**
  * `Tarol.Caching` (new): the port `IPageCache`, `MemoryPageCache`, and `CachedReadModels`, a decorator of `IReadModels` whose
    stores answer `Search` from the cache, with `AddCaching()`. It depends on `Tarol.Queries` only.
  * `Tarol.Queries.Npgsql`: the checkpoint NOTIFY and `NpgsqlProjectionPositions`, the pushed `IProjectionPositions`, which also wakes
    read-after without polling.
  * `Tarol.Changes.AspNetCore`: the headers.

## 6. Security and tenancy

* **Keys:** the tenant comes first in every key. The canonical query follows (sort, direction, filters, cursor, offset, limit,
  count); `[FromClaim]` values are filters, so each caller's claim value has its own entry.
* **Authorization:** `ListAccess` → `CheckList` runs before the cache is asked, so a hit never skips `Read<T>`. Lists have no row
  rules (QR-6), so a page is the same for every caller of a tenant with the same filters.
* **Views:** views are not cached, so `Check(Read<T>, aggregate)` and its row rules run on every GET by id.
* **Personal data** (step 16): a page holds projected rows in process memory only, bounded by caps and idle expiry. A deletion
  is an event; the projection's next batch moves the checkpoint and drops every entry of that projection.

## 7. HTTP (CA-6)

* List and view answers carry `Cache-Control: private, no-cache` and `Vary: Authorization`: browsers may store them and must
  revalidate; shared caches must not store them.
* `ETag`/`If-None-Match` → 304 as in steps 10 and 12. From the page cache a list 304 costs 0.06 ms instead of 1.2 ms **[verified]**.
* **No output caching** **[verified]**:
  * its default policy never caches authenticated requests;
  * a policy that does served tenant A's page to tenant B without a vary rule;
  * a cached answer ignores `Tarol-Read-After`;
  * the Tarol cache already answers in ~70 µs over HTTP (vs 83 µs for output caching on an anonymous list).

## 8. Measurements **[verified]** (4 cores; PostgreSQL 17, Redis 8.10, Garnet 2.2 in Docker on the same host; [`output.txt`](../migration/probes/caching/output.txt))

| | p50 1 / 16 / 64 concurrent | max/s |
|---|---|---|
| list endpoint today (checkpoint + search + serialize) | 1.17 / 3.59 / 13.3 ms | 4,254 |
| page cache hit, checkpoint pushed | 0 / 1 / 1 µs | 2.4 M |
| stored-state load / replay of 1,000 events | 0.27 / 1.18 / 3.4 ms; 1.60 / 10.2 / 39.2 ms | 12,430; 1,529 |
| HTTP list 200: plain / page cache (16 clients) | 8.9 ms / 0.26 ms | 1,656 / 48,782 req/s |

## New insights

1. "A stale hit only costs a conflict" is true only when the conflict evicts the entry. The pipeline would need to know the cache,
   and it saves 0.27 ms of ~7 ms.
2. A lookup cache is the one cache that silently breaks a decision; the version guard protects the aggregate, not the way it was found.
3. Today's 304s are not cheaper than 200s by much: the ETag is known only after the reads. A cache keyed by the checkpoint is
   what makes 304 cheap.
4. The checkpoint NOTIFY is safe where the feed NOTIFY is not: the checkpoint moves only after the rows are committed and visible.

## Decisions taken (owner, 2026-11-16; migration/caching.md §9)

| # | Decision |
|---|---|
| CA-1 | list pages only; views stay uncached |
| CA-2 | in process, the checkpoint pushed by NOTIFY with a poll fallback |
| CA-3 | no lookup cache, no codec cache |
| CA-4 | no aggregate cache; C-7 unchanged |
| CA-5 | invalidation by the projection checkpoint |
| CA-6 | ETag/304 + `Cache-Control: private, no-cache` + `Vary: Authorization`; no output caching |
| CA-7 | a new package `Tarol.Caching`; three PRs 13A–13C |

## Open questions

* Per-tenant invalidation, a shared L2, cached views: [migration/caching.md](../migration/caching.md) §14.
