# Caching: evaluation (step 13 design)

Evaluation of [caching.md](../architecture/caching.md): inventory of the old code, the candidates of plan row 13 (list pages,
views, aggregate loads, lookups, codec metadata), where a cache lives, how it is invalidated, what it means for decisions,
tenancy, authorization and HTTP, measurements against PostgreSQL 17, Redis 8 and Garnet, the owner's answers, and what
PRs 13A–13C must do. **Design only**: this PR changes no package code. Probe and output:
[`probes/caching/`](probes/caching/), [`output.txt`](probes/caching/output.txt), the inventory
[`inventory.md`](probes/caching/inventory.md). Labels as in the [conventions](../architecture/conventions.md). Source of
the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old code has nothing to migrate** **[verified by reading]**: no caching project, no `IMemoryCache`,
  `IDistributedCache`, `HybridCache`, Redis or HTTP caching; one unused central version of `Microsoft.Extensions.Caching.Hybrid`
  (`Directory.Packages.props:43`), two static registries, and a Marten snapshot that is the source of truth, not a cache.
* **Decided** (CA-1 … CA-7): cache **list pages only** (CA-1). The cache lives **in process**, and the projection's checkpoint is
  **pushed** by NOTIFY (CA-2). It is **invalidated by that checkpoint**: an entry is valid only while the checkpoint it was read at
  is current (CA-5). There is **no aggregate cache** (CA-4), **no lookup or codec cache** (CA-3) and **no output caching**: ETag/304 plus
  `Cache-Control: private, no-cache` and `Vary: Authorization` (CA-6). The code goes in a **new package `Tarol.Caching`**,
  with the pushed checkpoint in `Tarol.Queries.Npgsql` and the headers in `Tarol.Changes.AspNetCore`, in three PRs (CA-7).
* **Measured** on 4 cores, with PostgreSQL 17, Redis 8.10 and Garnet 2.2 in Docker on the same host:
  * **List requests:**
    * The list endpoint today makes two round trips: p50 1.2 / 3.6 / 13.3 ms at 1 / 16 / 64 concurrent, at most ~4,250 per second.
    * A page-cache hit with a pushed checkpoint takes ~1 µs (0 round trips, 2.3 M/s).
    * Over HTTP, a list answer goes from 2.3 ms to 0.07 ms, and from 1,656 to 48,782 req/s with 16 clients.
    * A 304 today still costs 1.2 ms; from the cache it costs 0.06 ms.
  * **Loads and lookups:**
    * A stored-state load takes 0.27 ms, the same round trip as a version probe (0.31 ms). Replaying 1,000 events takes 1.6 ms.
    * A cached load or `Find` takes ~1 µs.
  * **Decisions:** an aggregate cache in two processes raises conflicts from 0.01 to 0.27 per success and costs ~10 % of throughput.
    Without eviction on a conflict it collapses to 3–62 conflicts per success.
  * **Staleness:**
    * Under checkpoint invalidation, a cached page goes stale only for as long as the projection lags (commit → visible p50 7.0 / p99 10.9 ms, uncached 8.7 / 42.6 ms).
    * A 1 s TTL serves stale pages for up to 998 ms.
    * A HybridCache tag removed in another process did not reach the reader within 5 s.
  * **Native AOT:** the probe's caches run natively. HybridCache adds 9 IL warnings (its built-in JSON serializer).
* **Found** (§5):
  * A stale cached aggregate is refused by `Append` and costs exactly one conflict; the claim of row 13 holds, but only if the conflict evicts the entry.
  * A cached `Find` breaks decisions: a released lookup points at its old holder, and no version guard sees it.
  * An aggregate cache makes views stale.
  * HybridCache has no backplane: `RemoveAsync` and `RemoveByTagAsync` stay local to the process that calls them.
  * HybridCache's expiry ignores a `TimeProvider` registered in DI; it follows only `MemoryCacheOptions.Clock`.
  * The output cache never caches authenticated GETs by default; a policy that does leaks tenant A's page to tenant B unless it varies, and it ignores `Tarol-Read-After`.
  * Today's 304s run every query.

## 1. Inventory **[verified by reading]**

Condensed; every file:line is in [`inventory.md`](probes/caching/inventory.md).

| Where | What | Verdict |
|---|---|---|
| `CLorat/Tarol`: 0 `*Cach*` projects; 0 files with `IMemoryCache`/`IDistributedCache`/`HybridCache`/Redis/`OutputCache`/`ETag` in code | — | nothing to migrate |
| `Directory.Packages.props:43` `Microsoft.Extensions.Caching.Hybrid` 11.0.0, referenced by no project | dead central version | — |
| `StronglyTypedId/IdConverterRegistry.cs:36-37`, `Validations.Value/ValueValidationHelpers.cs:79-84` (unused, unbounded regex cache) | static registries | migrated as registries (step 3) / not migrated (step 6) |
| `Persistence.Marten/MartenSnapshotRepository.cs:31` (doc `:25-29`) | snapshot = source of truth | replaced by `streams.state` (step 8) |
| Tarol-Net `EventCodec.cs:61-67,165-168` | codec tables as copy-on-write dictionaries | **codec metadata is already cached** (§4.4) |
| Tarol-Net `QueryEndpoints.cs:181-237`, `ChangeEndpoints.cs:369-398` | lists and views decide 304 **after** their reads | the cache's main gain (§4.5) |

## 2. Candidates

### 2.1 What to cache (question 1; CA-1, CA-3, CA-4)

| Candidate | Uncached **[verified]** | Cached hit **[verified]** | Invalidation needed | Risk to decisions |
|---|---|---|---|---|
| **list pages** | Checkpoint + Search: p50 1.2 / 3.6 / 13.3 ms (1 / 16 / 64) | ~1 µs (pushed checkpoint) | checkpoint passes a commit | none: a decision never reads a projection |
| views (GET by id) | Load 0.27 / 1.2 / 3.4 ms + check + serialize | ~1 µs | a commit of that stream (feed) | none, but the view lags like a list and stops being the consistent read |
| aggregate loads (decisions) | Load 0.27 ms; replay 10 / 100 / 1,000 events 0.65 / 0.87 / 1.6 ms | ~1 µs | none needed for correctness (`Append` guards) | conflicts (§4.3) |
| lookups (`Find`) | 0.30 / 0.96 / 3.3 ms | ~1 µs | a release of the key | **breaks decisions** (§5 item 2) |
| codec metadata | `StreamTypeOf` 12 ns, `Decode` 1.05 µs | — | — | — (already cached) |

### 2.2 Where the cache lives (question 2; CA-2)

| | **L-A in process, checkpoint pushed** | L-B in process, checkpoint read | L-C HybridCache L1 + L2 (Redis / Garnet) | L-D HTTP only |
|---|---|---|---|---|
| hit **[verified]** | ~1 µs, 0 round trips | 0.30 / 0.92 / 2.65 ms (1 round trip) | L1 0.1–5 µs; L2 0.18–0.84 ms (Redis), 0.23–0.84 ms (Garnet) | 304: 1.2 ms (both reads run) |
| shared by processes | no (each warms its own) | no | yes (L2) | — |
| cross-process invalidation | NOTIFY of the checkpoint (+1 s poll) | the row itself | **none**: no backplane; another process sees a removal when its L1 expires | — |
| new infrastructure | one LISTEN connection | none | a Redis/Garnet container | none |
| AOT **[verified]** | 0 warnings | 0 warnings | **9 IL warnings** (`DefaultJsonSerializerFactory`) | 0 warnings |

### 2.3 Invalidation (question 3; CA-5)

| | **I-A projection checkpoint** | I-B per-tenant tags | I-C by version | I-D TTL only |
|---|---|---|---|---|
| valid while | the checkpoint is the one the entry was read at | no batch touched the tenant | the entry's version is current | the TTL runs |
| stale for **[verified]** | the projection's lag: p50 7.0 / p99 10.9 ms (pushed), 7.5 / 12.4 ms (read) | 7.4 / 12.1 ms (in process), 8.0 / 11.0 ms (NOTIFY) | never stale, but finding the version is a round trip (0.31 ms = the load itself) | up to the TTL (p99 998 ms at 1 s) |
| hit ratio at ~75 commits/s **[verified]** | 99.6 % | 99.6 % | — | 100 % (stale) |
| commits in another tenant (50/s) | 99.8 % | 100 % | — | 100 % |
| needs | NOTIFY in the batch | the host reports the tenants of a batch | the version per key | — |

Across processes: the feed NOTIFY arrives before a commit is visible (step 12, §5 item 4); the **checkpoint** NOTIFY is sent in the
projection's batch transaction and delivered after its commit, so an entry is never validated by a position whose rows are not
yet visible **[verified]** (open-transaction check, §3). The outbox is not needed: the checkpoint is already the durable signal.

### 2.4 HTTP (question 6; CA-6)

| | **H-A ETag/304 from the page cache + `Cache-Control`/`Vary`** | H-B output caching (opt-in policy) |
|---|---|---|
| authenticated GET | 304 without a query: 0.06 ms | the default policy refuses it (Authorization header) **[verified]** |
| tenancy | the tenant is in the cache key | needs a vary rule; without it tenant B got tenant A's page **[verified]** |
| `Tarol-Read-After` | waits, then reads (correct by construction) | ignored by a cached answer **[verified]** |
| `[FromClaim]` lists | the claim value is a filter, so part of the key | needs a vary rule per claim |
| store | per process | per process (in-memory default) |

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/caching/output.txt) §1, §12: JIT and Native AOT)

| Check | postgres | hybrid local / redis / garnet | http |
|---|---|---|---|
| aggregate cache: hit; tenant in the key; a fresh instance per hit | ✓ | | |
| stale hit → `Stream.VersionConflict`, nothing written; the retry reloads (evicted) and decides on the current version; the store holds exactly the accepted bids | ✓ | | |
| write-through after an own commit; a view through the aggregate cache answers a stale version (found) | ✓ | | |
| a version probe sees the new version; expiry on a `FakeTimeProvider` | ✓ | | |
| a cached `Find` points at the old holder of a released lookup (found) | ✓ | | |
| page cache: hit; tenant in the key; a commit of tenant B misses checkpoint entries, not tenant tags | ✓ | | |
| a bid on page 1: checkpoint, pushed and tag entries miss and show it; the TTL entry stays stale (found), expires on the `TimeProvider` | ✓ | | |
| an open transaction elsewhere: cached page = uncached page; after it ends both show the bid | ✓ | | |
| HybridCache: L1 hit; stampede (20 callers, 1 factory); L2 shared; `RemoveAsync` not seen by another process's L1 (found); a typed value with `IHybridCacheSerializer<T>` | | ✓ (L2 rows: redis, garnet) | |
| HybridCache expiry: ignores a DI `TimeProvider` (found), follows `MemoryCacheOptions.Clock` | | ✓ | |
| output cache: anonymous cached, authenticated not; a caching policy without vary leaks across tenants (found); varied by tenant; ignores `Tarol-Read-After` (found); evict by tag | | | ✓ |

22 of 22 (postgres), 3 / 8 / 8 (hybrid), 6 of 6 (http), on JIT (Release) and Native AOT.

## 4. Measurements **[verified]** ([`output.txt`](probes/caching/output.txt); 4 cores, 15 GB, SDK 11.0.100, PostgreSQL 17.12 with default settings, Redis 8.10.3, Garnet 2.2.12, all in Docker on the same host; JIT runs in Release)

Seed (§2): 20,000 auctions (open + 8 bids, one commit each; 1 in 5 of tenant B) in 14.9 s; 200 replay-only ledgers each of
10, 100, 1,000 events; the projection caught up 402,000 events in 2.8 s. The client and the servers share the 4 cores: at 64
concurrent the numbers measure the machine's saturation, not the database alone.

### 4.1 Loads (§3; 1,000 auctions round robin; p50 / p99)

| Load | 1 | 16 | 64 | max/s |
|---|---|---|---|---|
| stored state (215 B) | 0.27 / 0.88 ms | 1.18 / 7.2 ms | 3.4 / 25.6 ms | 12,430 |
| replay 10 events | 0.65 / 1.34 ms | 1.95 / 9.8 ms | 6.5 / 30.8 ms | 8,235 |
| replay 100 events | 0.87 / 1.77 ms | 2.55 / 11.9 ms | 9.4 / 36.7 ms | 5,840 |
| replay 1,000 events | 1.60 / 3.11 ms | 10.2 / 31.6 ms | 39.2 / 107 ms | 1,529 |
| version probe (`SELECT version`) | 0.31 / 0.63 ms | 0.96 / 5.9 ms | 3.1 / 16.3 ms | 17,669 |
| aggregate cache hit (bytes → fresh instance) | 1 / 3 µs | 1 / 3 µs | 1 / 3 µs | 2.85 M |
| HybridCache L1 hit + `ReadState` | 1 / 2 µs | 1 / 4 µs | 1 / 3 µs | 2.42 M |
| HybridCache L2 (Redis) hit + `ReadState` | 0.20 / 0.44 ms | 0.39 / 1.15 ms | 0.70 / 1.83 ms | 81,381 |

Native AOT (§12): stored state 0.27 / 1.00 / 3.67 ms p50; replay 1,000 events 2.37 / 11.2 / 39.0 ms; hits as JIT.

### 4.2 Lookups (§4)

`Find` 0.30 / 0.96 / 3.3 ms p50 (1 / 16 / 64), 13,289/s; cached 1 µs, 4.4 M/s.

### 4.3 Conflicts under contention (§9; `BidUp` through `ChangePipeline`, the caller retries ≤ 20 times, two processes = two caches over one store, 6 s per cell)

| Writers, hot auctions | plain: successes/s, conflicts per success | cache, 2 processes | cache, 1 process | cache, no eviction on conflict |
|---|---|---|---|---|
| 8, 1 | 98, 5.85 | 134, 5.07 | 136, 5.05 | 126, 13.0 (441 gave up) |
| 32, 1 | 69, 10.95 | 78, 9.29 | 79, 9.50 | 38, 61.5 (664 gave up) |
| 8, 10 | 758, 0.60 | 688, 0.98 | 808, 0.66 | 97, 17.8 (515 gave up) |
| 32, 10 | 560, 1.29 | 509, 1.77 | 479, 1.51 | 148, 15.4 (657 gave up) |
| 8, 1,000 | 1,091, 0.01 | 976, 0.27 | 1,193, 0.01 | 395, 3.11 (368 gave up) |
| 32, 1,000 | 1,058, 0.03 | 950, 0.27 | 1,152, 0.03 | 437, 3.50 (458 gave up) |

A change costs ~7 ms p50 at 8 writers on 1,000 auctions; the load is 0.27 ms of it. The commit dominates, so the cache saves
little; in two processes every write of the other process turns a hit into one wasted decide-and-append.

### 4.4 CPU per operation (§5 JIT, §12 Native AOT)

| Operation | JIT | Native AOT |
|---|---|---|
| `codec.StreamTypeOf<Auction>()` | 12 ns | 16 ns |
| `codec.Decode` (one event) | 1,050 ns | 618 ns |
| `AggregateState.Read` / `Write` (204 B) | 1,877 / 1,458 ns | 912 / 579 ns |
| view from aggregate + serialize | 817 ns | 435 ns |
| page of 50 rows (8,080 B): serialize / deserialize | 9.1 / 31.6 µs | 11.9 / 46.1 µs |
| the canonical cache key | 477 ns | 292 ns |

Every one is below 1 % of a round trip (≥ 270 µs): nothing in the codec needs a cache.

### 4.5 List pages (§6) and HTTP (§8)

| Page of 50, 9 pages round robin | 1 | 16 | 64 | max/s |
|---|---|---|---|---|
| `Search` only (1 round trip) | 1.30 / 2.21 ms | 2.29 / 11.0 ms | 8.1 / 44.2 ms | 5,889 |
| the endpoint today: `Checkpoint` + `Search` + serialize | 1.17 / 2.02 ms | 3.59 / 14.2 ms | 13.3 / 43.6 ms | 4,254 |
| page cache hit, checkpoint read | 0.30 / 0.59 ms | 0.92 / 5.4 ms | 2.65 / 17.5 ms | 18,343 |
| **page cache hit, checkpoint pushed** | 0 / 1 µs | 1 / 4 µs | 1 / 4 µs | 2.4 M |
| HybridCache L2 (Redis) hit of the page bytes | 0.23 / 0.61 ms | 0.27 / 0.83 ms | 0.35 / 1.30 ms | 168,788 |
| `Search`, page of 200 | 0.96 / 2.04 ms | 4.18 / 20.0 ms | 16.3 / 60.8 ms | 3,463 |

| HTTP over loopback (Kestrel) | 1 client | 16 clients |
|---|---|---|
| list, plain 200 / 304 | 2.32 ms (421/s) / 1.21 ms (787/s) | 8.94 ms (1,656/s) / 3.95 ms (3,621/s) |
| list, page cache 200 / 304 | 0.071 ms (12,176/s) / 0.060 ms (14,218/s) | 0.261 ms (48,782/s) / 0.196 ms (63,464/s) |
| list, output cache (anonymous, upper bound) | 0.083 ms (10,206/s) | 0.279 ms (46,474/s) |
| view, plain 200 / 304 | 0.52 ms / 0.49 ms | 1.63 ms (8,689/s) / 1.43 ms (9,924/s) |
| view, aggregate cache 200 | 0.064 ms | 0.202 ms (64,221/s) |

### 4.6 Staleness (§10) and hit ratio under writes (§11)

| Invalidation | commit returned → visible in the cached answer (40 commits) |
|---|---|
| uncached `Search` (= projection lag) | p50 8.7 / p99 42.6 ms |
| checkpoint read per request | 7.5 / 12.4 ms |
| checkpoint pushed (NOTIFY, other connection) | 7.0 / 10.9 ms |
| tenant tag in the worker / by NOTIFY | 7.4 / 12.1 ms; 8.0 / 11.0 ms |
| TTL 1 s | 875 / 998 ms |
| HybridCache L1 1 s + L2 Redis, tag removed by the worker | **not visible within 5 s in 40 of 40** |

Hit ratio (16 readers, 8 pages of tenant A, 8 s): with no writes 100 % everywhere; with the writer at 10 / 50 / "200" commits/s
(reached 10 / 50 / ~75 commits/s: the readers saturate the cores) **pushed 100 / 99.7 / 99.6 %**, tenant tag 100 / 99.7 / 99.6 %,
checkpoint read 98.7 / 93.6 / 90.2 % (its round trip limits it to ~12,000 requests/s, so it sees each move more often); 50
commits/s in tenant B: pushed 99.8 %, tag 100 %. In the pushed mode, between 1.6 % and 15.6 % of the sampled hits were behind
the database's checkpoint at that instant: the NOTIFY in flight, a few ms (the staleness row above bounds it).

### 4.7 Native AOT (§12)

The probe (Npgsql, ASP.NET Core, StackExchange.Redis, HybridCache): 20.9 MB. **9 IL warnings, all from HybridCache**:
`DefaultJsonSerializerFactory` (3 × IL2026, 3 × IL2070, 3 × IL3050), emitted although every typed value used a
source-generated `IHybridCacheSerializer<T>` and pages were `byte[]`. Every check passes natively (22 + 3 + 8 + 8 + 6).

## 5. Found while probing

1. **"A stale hit only costs a conflict" holds, if the conflict evicts** **[verified]**: decided on a cached version 1 while the
   store was at 2, `Append` returned `Stream.VersionConflict` and wrote nothing; the retry reloaded and decided on version 2. Without
   eviction, every retry hits the same stale entry until its TTL: 3–62 conflicts per success, hundreds of give-ups (§4.3).
2. **A cached `Find` breaks a decision** **[verified]**: lot `L7` released by auction X (`Withdraw`) and claimed by Y; the cached
   lookup still returned X, and the bid was decided on X (refused `Auction.Closed`, where Y would have accepted it). X's version
   guard is current, so `Append` would accept any decision taken on it. Lookups must stay store reads.
3. **An aggregate cache makes views stale** **[verified]**: after another process committed version 4, the cached load answered
   version 3 with `ETag: "3"` until the TTL. Views are the consistent read (R-1, QR-5's RYW-C); caching them needs an invalidation
   from the feed, which then lags like a projection.
4. **HybridCache has no backplane** **[verified]**: `RemoveAsync` and `RemoveByTagAsync` in process A leave B's L1 entry: B
   answered the removed value until `LocalCacheExpiration` (1.0 s). With the page bytes in L2, B kept the pre-commit page for
   longer than the 5 s cap of §4.6 (the tag removal of A did not mark B's L2 reads stale).
5. **HybridCache's clock** **[verified]**: a `FakeTimeProvider` registered in DI does not move its expiry (+31 s on 30 s: still
   a hit); `MemoryCacheOptions.Clock` (an `ISystemClock` adapter over the `TimeProvider`) does. A `TimeProvider`-driven design
   cannot rely on HybridCache defaults.
6. **HybridCache is not AOT-clean** **[verified]**: 9 IL warnings from its default JSON serializer factory, whatever serializers
   the application registers (§4.7).
7. **Output caching and authenticated, tenant-scoped GETs do not fit** **[verified]**: the default policy refuses any request
   with `Authorization`; a policy that caches them serves tenant A's body to tenant B unless it varies by the tenant claim; a
   cached answer ignores `Tarol-Read-After` (no wait, no `Tarol-Stale`).
8. **Today's 304s run every query** **[verified by reading, verified]**: `List` reads the checkpoint, runs `Search`, then
   compares the ETag (`QueryEndpoints.cs:203-221`); `Load` loads and checks before comparing (`ChangeEndpoints.cs:379-396`). A list
   304 costs 1.21 ms, a view 304 0.49 ms; from the page cache a list 304 costs 0.06 ms.
9. **The pushed checkpoint is the cheaper read-after too**: `ProjectionPositions.WaitFor` outside the worker polls every 5 ms
   (`ProjectionPositions.cs`); woken by the checkpoint NOTIFY it waits exactly as long as the projection lags (§4.6).
10. **A version-keyed entry saves nothing for one aggregate** **[verified]**: finding the current version is a round trip of
    0.31 ms, the same as loading the stored state (0.27 ms).

## 6. Decided design (sketch for 13A–13C; names final in the PRs)

* **`Tarol.Queries.Npgsql` (13A)**:
  * the projection's batch transaction sends `NOTIFY <schema>_checkpoint, '<name>:<version>:<tx>:<pos>'` (opt-in, `NpgsqlReadModelOptions.PushCheckpoints`, like `NotifyFeed`; the same option makes the API process listen);
  * `NpgsqlProjectionPositions : IProjectionPositions` holds the checkpoint of every projection pushed (one LISTEN connection per
    process, a poll fallback of 1 s, reconnect with backoff);
  * `Checkpoint<TRow>` answers without a round trip;
  * `WaitFor` is woken by the notification instead of polling every 5 ms;
  * `UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })` registers it.
* **`Tarol.Caching` (13B, new; depends on `Tarol.Queries` only, no HybridCache, no Redis)**:
  * the port `IPageCache` (`TryGet(key, position)`, `Set(key, position, page)`, `Clear()`);
  * `MemoryPageCache`, bounded by entries and bytes (defaults 10,000 / 64 MB), with an idle expiry on `TimeProvider` (default 5 min, memory only, never correctness);
  * `CachedReadModels : IReadModels`, a decorator that returns, per row type, a store whose `Search` answers from the cache.
    * **Key:** (tenant, projection, version, canonical query: sort, direction, filters including `[FromClaim]` values, `after`, `offset`, `limit`, `count`).
    * **Validity:** an entry is valid only while `IProjectionPositions.Checkpoint<TRow>()` equals the position read **before** the search.
    * Every other member passes through, so the worker's writes are untouched.
  * `CacheOptions`; `TarolBuilder.AddCaching(configure)`;
  * the meter `Tarol.Caching` (hits, misses, entries, bytes, evictions);
  * the skill `tarol-caching`.
* **`Tarol.Changes.AspNetCore` (13C)**:
  * list and view answers carry `Cache-Control: private, no-cache` and `Vary: Authorization` (also 304s and problems for lists);
  * the list 304 is then answered from the cached page's ETag without a query. This needs no endpoint change: `Checkpoint` and `Search` are the cached calls;
  * `Tarol-Read-After` waits on the pushed checkpoint, then reads (a cached page is valid only for the checkpoint it was read at, so the wait and the cache compose);
  * the docs say why output caching is not used.
* **Unchanged**:
  * views (GET by id) and every load and `Find` read the store;
  * `ChangePipeline` and C-7;
  * `ListAccess`/`CheckList` run before the cache is asked, so a hit never skips authorization (QR-6).

## 7. Earlier decisions that constrain caching (not re-asked)

| Decision | Text (as recorded) | Consequence here |
|---|---|---|
| C-7 (changes.md §9) | version conflicts: **never retry** in the pipeline; the caller decides | no "reload on conflict" path; a cache would have to evict on conflict (§5 item 1); CA-4 drops it anyway |
| C-16 | auto-load + `ILoads<T>` escape hatch (`Find` for another key) | `Find` must stay a store read (§5 item 2) |
| `IAggregateStore` contract (step 8) | tenant isolation: a stream is (tenant, type, id); another tenant's stream does not exist | every cache key starts with the tenant |
| P-12 | a state that fails to parse → replay; the stored state is derived data | the stored state is not a cache to trust across schemas; not cached again |
| P-3, P-14 | the feed and its `(tx, position)` ordering below `pg_snapshot_xmin` | invalidation by the checkpoint inherits the xmin gap, never shows less than the uncached path (§3) |
| R-1, R-8 | one GET by id with ETag/304 (R-1); view records `IView<,>` (R-8) | views stay the consistent read (CA-1) |
| R-6 | the principal decides; HTTP always passes one | the cache sits behind `CheckList` |
| S-10 | waking: poll by default, LISTEN/NOTIFY opt-in | checkpoint NOTIFY opt-in, poll fallback |
| QR-5 | `Tarol-Position`, opt-in `Tarol-Read-After` + `Tarol-Stale`, page ETag = hash of (id, version) | the cached page keeps the ETag and the position it was read at |
| QR-6 | `Read<T>` + claim-bound filters, no row rules on lists | a page is the same for every caller of a tenant with the same claim filters: cacheable per tenant |
| conventions | no reflection on AOT paths; packages AOT-clean | HybridCache's 9 IL warnings rule it out as a dependency |

## 8. Plan impact

* **Row 13:** the decided split, three PRs (CA-7).
* **Row 15 (operations):**
  * the `Tarol.Caching` meter (hit ratio, bytes);
  * a health check that the checkpoint LISTEN connection is up;
  * the fallback poll's log line.
* **Row 16 (data protection):** a list page is projected rows held in process memory.
  * They may hold personal data, held in memory only, never on disk or in another service; the cap is the entry, byte and idle limits.
  * A deletion is an event: the projection's next batch moves the checkpoint and drops every entry of that projection.
  * Crypto-shredding (row 16) must therefore project the shredded rows. Then nothing in the cache outlives the projection.
* **Row 18 (samples):** the auction list in `samples/Sagas` uses `AddCaching()` in the API process (13C).

## 9. Owner answers (2026-11-16)

Rounds:
* **Round 1**, after the load, lookup, CPU, list and L2 numbers: CA-1 … CA-3.
* **Round 2**, after the conflict, HTTP, staleness, hit-ratio and AOT numbers: CA-4 … CA-7.
* **Round 3**, at the start of the implementation (13A): CA-8.

The prefix CA- was free (taken: C-, D-, M-, P-, Q-, QR-, R-, S-). Not asked because already decided: §7.

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| CA-1 | Which reads to cache? (list endpoint p50 1.2 / 3.6 / 13.3 ms at 1 / 16 / 64, ~4,200/s; a cached page ~1 µs; a load 0.27 ms, a version check 0.31 ms; an aggregate cache answered a stale view) | **List pages only**: a page cache for `IQuery<TRow>` lists validated by the projection's checkpoint; views stay uncached, the consistent read with ETag/304 (recommended). |
| CA-2 | Where does it live? (in process + pushed checkpoint ~1 µs, 0 round trips; checkpoint read 0.3 / 0.9 / 2.7 ms; HybridCache L2 0.23–0.35 ms, shared, no backplane: a removal reached another process after its L1 expired, 1 s) | **In process, pushed**: a per-process memory cache behind a port, the checkpoint pushed by NOTIFY (one LISTEN connection per process) with a poll fallback, bounded, no new infrastructure (recommended). |
| CA-3 | Lookups and codec metadata? (`Find` 0.3 / 3.3 ms, cached ~1 µs; a cached `Find` returned the old holder of a released lot and the decision ran on the wrong aggregate; `StreamTypeOf` 12 ns, `Decode` 1 µs) | **Cache neither**: `Find` stays a store read; the codec stays as it is (recommended). |
| CA-4 | The optional aggregate cache for decisions? (stale hit → `VersionConflict`, retry reloads after eviction; load 0.27 ms of a ~7 ms change; two processes: conflicts 0.01 → 0.27 per success, −10 %; one process +9 %; without eviction 3–62 conflicts per success) | **No aggregate cache**: loads keep reading `streams.state`; C-7 unchanged; the probe stays as the reason (recommended). |
| CA-5 | Invalidation of a cached page? (staleness: uncached 8.7 ms; checkpoint pushed 7.0 / 10.9 ms; tenant tag 8.0 / 11.0 ms; TTL 1 s up to 998 ms; HybridCache tags across processes not within 5 s; hit ratio at ~75 commits/s 99.6 % both; another tenant's writes 99.8 % vs 100 %; an open transaction elsewhere: cached = uncached) | **The projection checkpoint**: an entry is stamped with the checkpoint it was read at; every move (NOTIFY from the batch transaction, 1 s poll fallback) makes it miss; never staler than the uncached path; bounded size and an idle TTL for memory only (recommended). |
| CA-6 | HTTP caching? (the default output-cache policy never caches with `Authorization`; a caching policy without vary served tenant A's page to B; a cached answer ignores `Tarol-Read-After`; per-process store; a list 304 today 1.2 ms vs 0.06 ms from the page cache) | **ETag/304 + headers**: no output caching; lists and views answer `Cache-Control: private, no-cache` and `Vary: Authorization`; 304 from the page cache's ETag without a query; read-after waits on the pushed checkpoint, then reads; the docs explain why (recommended). |
| CA-7 | Packages and PRs? (HybridCache 9 IL warnings under Native AOT; the page cache needs no new dependency) | **A new `Tarol.Caching` package** with the port, the in-memory cache and the glue (the recommendation was no new package, three PRs). §11 places it: the glue is a decorator of `IReadModels` (no ASP.NET Core dependency is needed, because the endpoints call `Checkpoint` and `Search`); the pushed checkpoint stays in `Tarol.Queries.Npgsql` (it needs Npgsql, and read-after gains from it without a cache); the headers stay in `Tarol.Changes.AspNetCore` (they are right without a cache). |
| CA-8 | Package placement (asked at the start of 13A, because CA-7's option text said `Tarol.Caching` holds "the port, the in-memory cache and the ASP.NET Core glue" while §9 and §11 place the glue elsewhere) | **Split placement** (recommended): `Tarol.Caching` depends on `Tarol.Queries` only (`IPageCache`, `MemoryPageCache`, the `IReadModels` decorator, `AddCaching()`; no ASP.NET Core); the pushed checkpoint is in `Tarol.Queries.Npgsql` (it needs Npgsql and helps read-after without a cache); the headers are in `Tarol.Changes.AspNetCore` (they are right without a cache). |

## 10. Changes against steps 7–12 (announced here, made in 13A and 13C)

| Where | Change | Why |
|---|---|---|
| `Tarol.Queries.Npgsql` `NpgsqlReadModelStore.Write`, `NpgsqlReadModelOptions.PushCheckpoints` | opt-in `NOTIFY <schema>_checkpoint` in the batch transaction (after the guarded checkpoint update) | CA-2, CA-5 |
| `Tarol.Queries.Npgsql` | new `NpgsqlProjectionPositions` (LISTEN, poll fallback); `UseNpgsqlReadModels` option to register it instead of `ProjectionPositions` | CA-2; read-after without 5 ms polling (§5 item 9) |
| `Tarol.Changes.AspNetCore` list and view answers | `Cache-Control: private, no-cache` and `Vary: Authorization` on 200, 304 and the list's problems | CA-6 (a behaviour change: shared caches no longer store these answers) |
| `docs/architecture/queries.md`, `rest.md` | the cache, the headers, why no output caching | CA-1, CA-6 |
| `Directory.Packages.props` | nothing new: `Tarol.Caching` needs no third-party package | CA-7 |

No change to `IAggregateStore`, `IAggregateReader`, `ChangePipeline`, `IChangeAuthorization` or the view path (CA-3, CA-4).

## 11. What the implementation must do (in the decided order)

The three PRs are stacked (13B on 13A, 13C on 13B): **retarget each PR to `main` before it is merged** (step 12: #40 was
merged into its stale stacked base and #42 had to bring 12C to `main`).

### PR 13A `queries.npgsql: pushed checkpoints` (`Tarol.Queries.Npgsql`)
* `NpgsqlReadModelOptions.PushCheckpoints`: the guarded batch sends the checkpoint NOTIFY in its transaction (delivered
  after the commit). `ISqlProjection` batches too.
* `NpgsqlProjectionPositions : IProjectionPositions`:
  * one LISTEN connection; positions only move forward;
  * poll every 1 s and after a reconnect; `Checkpoint<TRow>` from memory once seen (else one read);
  * `WaitFor` woken by the notification; source-generated logs (connection lost, fallback poll used); disposal.
* Registration through `UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })`, replacing `AddProjectionPositions`'s `ProjectionPositions`.
* Tests on Testcontainers PostgreSQL:
  * the checkpoint is pushed within a known position (wait for the position the batch wrote, capped generously; never a count in a wall-clock window);
  * the open-transaction case (§3);
  * the poll fallback when LISTEN is cut (terminate the backend);
  * `WaitFor` from another process.
* Run the new tests 10–25 times in Release, not in parallel with the full suite. The AOT smoke `Tarol.Queries.Npgsql.AotSmoke`
  gains the pushed-checkpoint check. Skill `tarol-queries-npgsql` updated.

### PR 13B `caching: list pages` (`Tarol.Caching`, new package)
* `src/Caching/Tarol.Caching/` per the conventions §8:
  * `IPageCache`, `MemoryPageCache` (entry and byte caps, idle expiry on `TimeProvider`, no eviction thread: expiry checked on read, trimmed on write);
  * `CachedReadModels`, `CacheOptions`, `AddCaching()`; the meter `Tarol.Caching`;
  * README, XML docs, the skill `skills/tarol-caching/SKILL.md`.
* Keys: the tenant first; every `ListQuery` member in canonical order, filters sorted by column; `[FromClaim]` values are filters.
* The position read **before** `Search` stamps the entry (the probe's rule).
* Tests (unit, with `InMemoryReadModels` and `FakeTimeProvider`):
  * hit, miss after a checkpoint move, the tenant in the key, filters and claims in the key;
  * caps and idle expiry on the fake clock;
  * a moved checkpoint during a search makes the next request miss;
  * pass-through of `Write`/`Load`/`Find`;
  * concurrency: no torn entries.
* Integration with `Tarol.Queries.Npgsql`: every "postgres" page row of §3 (checkpoint, open transaction, tenant B commit).
* `Tarol.Caching.AotSmoke` (0 IL warnings) and `Tarol.Caching.PackageTests`; `eng/verify-packages.sh` lists the skill.

### PR 13C `changes.aspnetcore: cache headers; samples and docs`
* `Cache-Control: private, no-cache` and `Vary: Authorization` on list and view answers (200, 304, list problems).
* HTTP tests:
  * the headers;
  * a list 304 from the cache runs no query: count `Search` calls on a decorated store;
  * read-after with the cache: wait, then a fresh page, `Tarol-Stale` on timeout;
  * `CheckList` runs on every hit: a denied caller gets 403 after another caller warmed the entry;
  * a `[FromClaim]` list caches per claim value.
* `samples/Sagas`: `AddCaching()` in the API, `PushCheckpoints` on.
* `docs/queries/` gains a caching page; `docs/architecture/caching.md` status; CHANGELOG, README; plan row 13 "done".

## 12. Skill evaluation checklist (`tarol-caching`, 13B; `tarol-endpoints` and `tarol-queries-npgsql` updates, 13A/13C)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| make the auction list fast under load | `AddCaching()` in the API process + `UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })` in worker and API | `AddOutputCache` / `CacheOutput()` on a list entry; a TTL cache; HybridCache with Redis |
| cache GET by id | nothing: views are the consistent read, ETag/304 already | an aggregate cache in front of `Load`; caching the view's bytes by id |
| speed up a change that loads its aggregate | nothing (loads are 0.27 ms of a ~7 ms change) | a cached `IAggregateStore`; caching `Find` for `ILoads<T>` |
| a cached list must show the caller's write | `Tarol-Read-After` with the change's `Tarol-Position` | clearing the cache from the change; disabling the cache |
| tune memory | `CacheOptions` entry/byte caps, idle expiry | a TTL as invalidation |
| test it | `FakeTimeProvider` + `InMemoryReadModels` under `CachedReadModels` | asserting hit counts inside a wall-clock window |

## 13. Verification of this PR (SDK 11.0.100, 2026-11-16) **[verified]**

Docs and probes only; the package code is `main` at `ab86313` (step 12 merged). On this branch: `dotnet build Tarol.slnx` 0 warnings,
0 errors; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx` with Docker: 1395 of 1395; `eng/aot-smoke.sh`
with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: 20 apps, 0 IL warnings, all checks passed; `dotnet pack -c Release` +
`eng/verify-packages.sh`: OK.
At the start on `main`: `dotnet build Tarol.slnx` 0 warnings; `dotnet test --solution Tarol.slnx` with Docker: 1394 of 1395.
The one failure was `PushTests.A_connection_that_closed_for_good_is_started_again_by_the_client` (SignalR, 30 s timeout under
full-suite load). It passed 5 of 5 alone and in CI run 135 of `main` (`build`, `aot-smoke`, `integration` green).
The probe: `all.sh` sections 0–12 ([`output.txt`](probes/caching/output.txt)).

## 14. Open items

* Per-tenant tags (I-B) if many tenants write and lists stay hot: measured 99.8 % vs 100 % at 50 commits/s, not worth it yet.
* A shared L2 (Redis/Garnet) if many API processes warm the same pages: needs a backplane and an AOT-clean client first (§5 items 4–6).
* Caching views if a measured use needs it: an invalidation from the feed per stream, and the docs must say the view then lags.
* The SignalR reconnect test's timeout under full-suite load (§13): step 15 (test infrastructure), or earlier if CI flakes.

## 15. Implementation: PR 13A (2026-11-16)

`queries.npgsql: pushed checkpoints`: the checkpoint `NOTIFY` in the batch transaction and `NpgsqlProjectionPositions` in
`Tarol.Queries.Npgsql` (CA-2, CA-8). No new package.

### 15.1 Public API (`Tarol.Queries.Npgsql`)

| Member | Role |
|---|---|
| `NpgsqlReadModelOptions.PushCheckpoints` (default `false`) | every batch sends the checkpoint `NOTIFY`, and `IProjectionPositions` is `NpgsqlProjectionPositions`; set it in the worker and the API process |
| `NpgsqlReadModels(dataSource, json, schema, pushCheckpoints = false)`, `NpgsqlReadModels.PushesCheckpoints` | row and SQL projection batches send `SELECT pg_notify('<schema>_checkpoint', '<name>:<version>:<tx>:<pos>:<state>')` after the guarded checkpoint update, in the batch transaction |
| `NpgsqlReadModelStore<TRow>(dataSource, json, schema, pushCheckpoints = false)`, `NpgsqlReadModelStore<TRow>.PushesCheckpoints` | the same for one row type |
| `NpgsqlProjectionPositions : IProjectionPositions, IAsyncDisposable` (`dataSource`, `schema`, `hosts`, `time`, `logger`) | one `LISTEN <schema>_checkpoint` connection per process, opened on the first call; `Checkpoint<TRow>` from memory once seen (else one read, then remembered); `WaitFor<TRow>` woken by the notification (a host of this process answers for its own projection, as in `ProjectionPositions`) |
| `NpgsqlProjectionPositions.Channel`, `ChannelSuffix` (`_checkpoint`), `IsListening` | the channel; whether the `LISTEN` connection is up (for the health check of step 15) |
| `NpgsqlProjectionPositions.PollInterval` (1 s), `Backoff` (250 ms), `MaxBackoff` (10 s) | the poll of every checkpoint (one `SELECT` of the `projections` table, also after every reconnect); the reconnect backoff, doubling |

Logs: 12101 (Warning: the `LISTEN` connection failed, reconnecting in …), 12102 (Information: the connection is down, the
checkpoints are polled every 1 s until it is back; once per outage), 12103 (Information: `LISTEN` is up again).

### 15.2 Deliberate differences from §6 and §11

* **The payload carries the state**: `<name>:<version>:<tx>:<pos>:<state>` (§6: `<name>:<version>:<tx>:<pos>`). A list
  answers 503 while the projection builds (QR-3); with the state in the notice, the move `Building` → `Current` at the same
  position is pushed too, and `Checkpoint<TRow>` from memory returns the same value as the store.
* **The notice is a second statement** (`SELECT pg_notify($1, $2)`) after the guarded `UPDATE`, only when the update moved
  the checkpoint (a refused batch rolls back and sends nothing). One more statement per batch, not per request.
* **"Only forward" includes the state**: a notice or poll result is taken when its position is later, or equal with
  `Current` over `Building`; older or malformed notices are ignored. Consequence: after `DropVersion` and a rebuild of the
  *same* version in a running process, the remembered checkpoint stays ahead until the process restarts. `DropVersion`
  is meant for versions no running code reads (QR-3), so this is documented, not handled.
* **The poll runs every second also while connected** (one `SELECT` of the small `projections` table on the `LISTEN`
  connection), not only after a reconnect: a notification is delivered reliably while connected, but the poll is what
  makes a worker without `PushCheckpoints` (a configuration mistake) still visible within 1 s.
* **While the connection is down**, the poll runs on pooled connections of the data source every `PollInterval` during the
  backoff, so positions keep moving at the poll's pace; log 12102 says so once per outage.
* **`IsListening`** is public (§6 did not name it): the health check of step 15 and the tests need it.
* **Registration**: with `PushCheckpoints`, `UseNpgsqlReadModels` registers `NpgsqlProjectionPositions` (also as itself)
  before `AddProjectionPositions`, so `ProjectionPositions` is not registered; it takes the read models' schema and the
  process's `ProjectionHost`s.

### 15.3 Changes against steps 7–12

| Where | Change |
|---|---|
| `NpgsqlReadModelOptions` | new `PushCheckpoints` (opt-in; default unchanged) |
| `NpgsqlReadModels`, `NpgsqlReadModelStore<TRow>` constructors | a new optional last parameter `pushCheckpoints` (source compatible; binary change of the constructors) and `PushesCheckpoints` |
| `UseNpgsqlReadModels` | with `PushCheckpoints`, `IProjectionPositions` is `NpgsqlProjectionPositions` instead of `ProjectionPositions` (read-after no longer polls every 5 ms) |

No change without the option: nothing is sent, `ProjectionPositions` is registered as before.

### 15.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Queries.Npgsql.IntegrationTests` (`PushedCheckpointTests`, new class in the CI `integration` job's existing step) | 10 | a listener learns the checkpoint from the batch's `NOTIFY` with the poll an hour away (equal to the stored checkpoint, `Current`); the open-transaction case (an older transaction holds xmin: the host and the pushed checkpoint stay below the hidden commit, `WaitFor` times out; after it commits both pass it); the poll fallback after `pg_terminate_backend` of the `LISTEN` connection (no reconnect: log 12101 and 12102, the checkpoint moves by the poll); the reconnect (12101, 12103, then a batch is delivered by the new `LISTEN` with the poll an hour away); `WaitFor` from a second `NpgsqlProjectionPositions` ("another process") woken by the worker's batch, and the worker's own wait through its host; an `ISqlProjection` batch sends the notice; positions only move forward and malformed notices are ignored; the registration with and without `PushCheckpoints`; a schema whose channel name is too long is refused |
| `Tarol.Queries.Npgsql.AotSmoke` | +1 check | "a pushed checkpoint wakes WaitFor in another process" (Native AOT, 0 IL warnings) |

Every wait is for a known position (the one `Append` or the host returned) with a 30 s cap; nothing counts inside a
wall-clock window. Repeated in Release with no other load: `PushedCheckpointTests` 20 of 20 runs; the whole
`Tarol.Queries.Npgsql.IntegrationTests` project 10 of 10 runs (after moving the open-transaction test into its own
collection, see below).

Found on the way **[verified]**: the open-transaction test first ran in parallel with the other classes of the project and
made two `NpgsqlReadModelTests` fail in the full suite (a single `CatchUp` saw an empty feed). The xmin horizon is
cluster-wide, so a test that holds an older transaction hides commits from every test on the container. It now runs in
`PushedCheckpointXminTests`, a collection with `DisableParallelization = true`.

### 15.5 Skill evaluation checklist (`tarol-queries-npgsql`, pushed checkpoints)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| read-after without 5 ms polling | `UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })` in worker and API | a `LISTEN` loop of one's own; a shorter `ProjectionPositions.PollInterval` |
| health check of the API process | `NpgsqlProjectionPositions.IsListening` | a query per probe; opening a second `LISTEN` |
| run behind PgBouncer in transaction mode | a direct data source for the read models (`dataSource:` of `UseNpgsqlReadModels`) | expecting notices through the pooler (`LISTEN` needs a session; only the 1 s poll then) |
| the worker does not push | `PushCheckpoints = true` in the worker too | sending `pg_notify` from application code |

### 15.6 Suppressions and analyzer configuration

None. The broad `catch (Exception e) when (e is not OutOfMemoryException)` of the listener and the fallback poll follows
`ProjectionHost.ExecuteAsync` (a background loop must survive any failure of the connection).

### 15.7 Verification (SDK 11.0.100, 2026-11-16) **[verified]**

`main` at `593aefb` before the change: 1395 tests, 20 AOT smoke apps; `main`'s CI (`build`, `aot-smoke`, `integration`)
green at the start. On this branch (`4e807ef`):
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors. `dotnet format --verify-no-changes`: clean.
* `dotnet test --solution Tarol.slnx` with Docker: **1404 of 1405** (1395 + 10 `PushedCheckpointTests`). The one failure was
  `Tarol.Messaging.SignalR.Tests.PushTests.After_a_reconnect_the_subscriptions_are_replayed`: a 30 s timeout under
  full-suite load while another build ran on the machine, the known flake of §14. All 17 `PushTests` passed in 5 of 5
  runs alone.
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **20 apps**, 0 IL warnings each, all checks passed
  (`Tarol.Queries.Npgsql.AotSmoke` with the new check).
* `dotnet pack -c Release` + `eng/verify-packages.sh`: OK.

## 16. Implementation: PR 13B (2026-11-16)

`caching: list pages`: the new package `Tarol.Caching` (CA-1, CA-2, CA-5, CA-7, CA-8). It depends on `Tarol.Queries`
only: no ASP.NET Core, no Npgsql, no third-party package.

### 16.1 Public API (new package `Tarol.Caching`)

| Type | Role |
|---|---|
| `CachedReadModels : IReadModels` (`inner`, `positions`, `cache`, `json`; `Inner`, `Cache`, `Store<TRow>()`) | the decorator: per row type a store whose `Search` reads `IProjectionPositions.Checkpoint<TRow>()` first, asks the cache for that position, on a miss searches the inner store and caches the page stamped with that position; `Prepare`, `Checkpoint`, `Load`, `Write`, `Find` pass through |
| `IPageCache` (`TryGet<TRow>(key, position, out page)`, `Put<TRow>(key, position, page, size)`, `Clear()`) | the port |
| `MemoryPageCache : IPageCache, IDisposable` (`options`, `time`, `meters`; `Count`, `Bytes`, `MeterName` = `Tarol.Caching`) | the in-process cache: entry and byte caps, idle expiry on `TimeProvider`, no eviction thread |
| `PageKey` (`Tenant`, `Projection`, `Version`, `Query`; `Of<TRow>(ListQueryPlan<TRow>)`) | the key: the tenant first, then name and version, then the canonical query |
| `CacheOptions` (`MaxEntries` 10,000, `MaxBytes` 64 MB, `IdleExpiry` 5 min) | the bounds (memory only) |
| `CachingTarolBuilderExtensions.AddCaching(configure)`, `InnerReadModelsKey` | the registration: the registered `IReadModels` becomes a keyed service (`Tarol.Caching.Inner`), `IReadModels` the decorator; `MemoryPageCache` unless an `IPageCache` is registered |

Meter `Tarol.Caching`: counters `tarol.caching.hits`, `tarol.caching.misses`, `tarol.caching.evictions` (tag `reason`:
`idle`, `entries`, `bytes`), each tagged `tarol.projection`; observable gauges `tarol.caching.entries`, `tarol.caching.bytes`.

### 16.2 Deliberate differences from §6 and §11

* **`Put` instead of `Set`**: CA1716 refuses `Set` as an interface member (a reserved word in Visual Basic).
* **`Put` takes the page's size**: the cache cannot measure a `Page<TRow>`; the decorator estimates it (the rows' UTF-8
  JSON through the application's `JsonSerializerContext`, written to a counting buffer without allocating the bytes, plus
  the key, ETag and cursor; 256 bytes per row for a row not in the context). The objects in memory take more than their
  JSON (≈ 2–3×), so `MaxBytes` is an estimate; the README says so.
* **The cache holds `Page<TRow>` objects, not answer bytes** (the probe cached the serialized body): the decorator sits
  below HTTP (CA-8), and the endpoints serialize the page as before. A hit returns the same immutable page instance.
* **Nothing is cached while the projection is `Building`**: its pages are incomplete, and its checkpoint moves every batch.
* **A query the plan refuses** (`Invalid`: limit, offset, cursor) goes to the inner store unchanged and is not cached; an
  unknown column throws `ArgumentException` as before.
* **A stale entry is replaced, not removed, on a miss**: the miss is followed by `Put`, so a removal would only cost a
  second dictionary operation. An older stamp never replaces a newer one (a slower search that read an older checkpoint).
* **Trimming** goes to 90 % of the caps (idle entries first, then the least recently used, by a snapshot of the last use),
  so a full cache does not trim on every write; one writer trims at a time, others skip. The meter's gauges are
  observable (read at collection), not up-down counters.
* **`MemoryPageCache` takes an `IMeterFactory`** (the host's, from DI) and owns its meter only without one.
* **`AddCaching()` throws** before any read store is registered and when called twice.

### 16.3 Changes against steps 7–12

None in the existing packages: 13B only adds `Tarol.Caching`. `Directory.Packages.props` is unchanged.

### 16.4 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Caching.Tests` | 29 | hit (the same page instance, one inner search); a batch moves the checkpoint and the next request misses and shows it; the tenant in the key; filters in the key in any order, a claim value (a `[FromClaim]` member is a filter) per entry; every member of the query in the key (sort, direction, cursor, offset, limit, count; `id` = the default sort, offset 0 = none); a batch during the search (a gated inner store): the entry carries the checkpoint read before it and the next request misses; nothing cached while building; refused queries pass through, uncached; pass-through of `Prepare`, `Checkpoint`, `Load`, `Write`, `Find` (counted on a decorated store); idle expiry and renewal on `FakeTimeProvider`; the entry cap evicts the least recently used; the byte cap and a page above it; the size estimate with and without the JSON context; `MemoryPageCache`: position-bound entries, another row type is no hit, an older stamp does not replace a newer one, `Clear`, idle entries trimmed first, bad bounds refused, 8 threads × 20,000 operations without a torn entry and with exact totals, the meter (a scoped `IMeterFactory`); `PageKey`: layout, length prefixes (no forged keys, `null` ≠ `"-"`), canonical values; `AddCaching`: decoration without a DI cycle, a registered `IPageCache` kept, read stores registered by instance or type, errors; the skill's example |
| `Tarol.Caching.IntegrationTests` (new; CI `integration` job) | 3 | the "postgres" page rows of §3 with the checkpoint pushed by the worker's batch: a hit per tenant and a commit of tenant B makes the entries miss (same rows, same ETag); a bid on page 1 misses and shows it (ETag = the uncached page's); an open transaction elsewhere (run alone: it holds the cluster-wide xmin): cached page = uncached page, after it ends both show the bid |
| `Tarol.Caching.AotSmoke` (new) | 8 checks | `AddCaching` over the in-memory stores: decoration, a hit counted by the meter, the tenant in the key, a miss after a batch, entries and bytes; with `TAROL_PG`: the pushed checkpoint, a hit over the read table, the batch's NOTIFY makes the next search miss and show the bid; Native AOT, 0 IL warnings |
| `Tarol.Caching.PackageTests` (new) | 1 | the packed package over the in-memory stores: the second search is a hit |

Timing: no test asserts a count inside a wall-clock window; the concurrency test is bounded by operations, the idle expiry
by a `FakeTimeProvider`, the integration waits by known positions with a 30 s cap. Repeated in Release: `Tarol.Caching.Tests` 15 of 15 runs,
`Tarol.Caching.IntegrationTests` 15 of 15 runs (part of them while the Aspire AppHost of 13C ran on the machine).

Found on the way **[verified]**: the concurrency test first failed with `Array.Sort`'s "IComparer.Compare() method returns
inconsistent results". The trim sorted entries by their last use, which readers kept moving. It now sorts a snapshot of
the timestamps.

### 16.5 Skill evaluation checklist (`tarol-caching`)

§12 is the checklist; the skill's rules cover each row: `AddCaching()` + `PushCheckpoints` in worker and API (rule 2),
nothing for GET by id or loads (rule 1), read-after instead of clearing (rule 6), `CacheOptions` instead of a TTL (rules
3, 4), `FakeTimeProvider` + `InMemoryReadModels` under `CachedReadModels` (the example). Run in 13C (§17).

### 16.6 Suppressions and analyzer configuration

None. Test and smoke domains: `#pragma warning disable CA1822` around an `On` handler whose event changes nothing the
aggregate decides on (not package code).

### 16.7 Verification (SDK 11.0.100, 2026-11-16) **[verified]**

On this branch (`b5b032f`, 13A merged in):
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors. `dotnet format --verify-no-changes`: clean.
* `dotnet test --solution Tarol.slnx` with Docker: **1437 of 1437** (13A's 1405 + 29 `Tarol.Caching.Tests` + 3
  `Tarol.Caching.IntegrationTests`).
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **21 apps** (20 + `Tarol.Caching.AotSmoke`), 0 IL warnings
  each, all checks passed.
* `dotnet pack -c Release` + `eng/verify-packages.sh`: OK. `Tarol.Caching` contains `lib/`, its XML docs, README, LICENSE and
  `skills/tarol-caching/SKILL.md`. Its consumer runs (`3 rows, the second search a hit, 1 entry of 938 bytes`) and lists the
  skill in `project.assets.json`.
