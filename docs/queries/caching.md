# Caching list pages

Step 13 ([architecture/caching.md](../architecture/caching.md), decisions CA-1 … CA-8 in
[migration/caching.md](../migration/caching.md) §9). Tarol caches one thing: **list pages, in the API process**. A page is
valid exactly as long as the projection's checkpoint it was read at is current. A decision never reads a cache.

## What is cached

| Read | Cached | Why |
|---|---|---|
| list pages (`IQuery<TRow>` entries, `IReadModelStore<TRow>.Search`) | **yes** | two round trips (1.2 / 3.6 / 13.3 ms at 1 / 16 / 64 concurrent) become ~1 µs |
| views (GET by id, `IView<,>`) | no | the consistent read; a load is 0.27 ms, and a cached view would lag like a projection |
| aggregate loads in a change | no | a stale state costs a conflict; the load is 0.27 ms of a ~7 ms change |
| `ILoads<T>.Find` | no | a stale lookup decides on the wrong aggregate, and no version guard sees it |

## Set it up

```csharp
// worker: hosts the projection, sends the checkpoint NOTIFY in every batch transaction
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { NotifyFeed = true })
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })
    .AddProjections(p => p.Add<AuctionRow>());

// API: listens for the checkpoint (one LISTEN connection) and caches the pages
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })
    .AddCaching(o => o.MaxBytes = 128L * 1024 * 1024)
    .AddEndpoints();
```

The list entries need no change: they call `IProjectionPositions.Checkpoint<TRow>()` and `Search`, and `Search` is the
cached call. `samples/Sagas` runs exactly this.

## How a request goes

1. `ListAccess` → `CheckList<T>` (`Read<T>`): a hit never skips authorization; a denied caller gets 403 even when another
   caller warmed the entry.
2. `Tarol-Read-After`: the endpoint waits for the pushed checkpoint (woken by the NOTIFY, no 5 ms polling); on timeout it
   answers `Tarol-Stale: true`.
3. `Checkpoint<TRow>()` from memory (0 round trips with `PushCheckpoints`).
4. `Search`: the key is the tenant, the projection's name and version, and the canonical query (sort, direction, the
   filters sorted by column with `[FromClaim]` values among them, cursor, offset, limit, count). A hit returns the page
   stamped with the current checkpoint; a miss searches the read table and stamps the page with the checkpoint read
   **before** the search, so a batch that lands during it makes the next request miss.
5. The page's ETag answers `If-None-Match` with 304 without a query.

Any batch of the projection, of any tenant, moves the checkpoint and makes its entries miss once (99.6 % hits at ~75
commits/s). A cached page is never older than an uncached one at the same instant, also with an open transaction elsewhere.

## HTTP headers

List answers (200, 304 and the problems) and view answers (200, 304) carry:

```http
Cache-Control: private, no-cache
Vary: Authorization
```

Browsers may keep the answer and must revalidate it (ETag → 304); shared caches and proxies must not store it.

## Why there is no output caching

ASP.NET Core output caching (`AddOutputCache`, `.CacheOutput()`) does not fit authenticated, tenant-scoped lists
([migration/caching.md](../migration/caching.md) §5 item 7, measured):

* its default policy never caches a request with `Authorization`;
* a policy that does served tenant A's page to tenant B unless it varies by the tenant claim;
* a cached answer ignores `Tarol-Read-After`: no wait, no `Tarol-Stale`;
* it is invalidated by time or tags, not by the projection's checkpoint;
* the page cache already answers in ~70 µs over HTTP (output caching: 83 µs on an anonymous list).

Not HybridCache or Redis either: no backplane (another process keeps stale pages until its local expiry), its expiry
ignores a `TimeProvider` in DI, and 9 IL warnings under Native AOT.

## Tuning and operations

* `CacheOptions`: `MaxEntries` (10,000), `MaxBytes` (64 MB, estimated from the rows' JSON), `IdleExpiry` (5 min on
  `TimeProvider`). They bound memory; they never decide validity.
* Meter `Tarol.Caching`: `tarol.caching.hits`, `tarol.caching.misses` (tag `tarol.projection`), `tarol.caching.evictions`
  (tag `reason`), `tarol.caching.entries`, `tarol.caching.bytes`.
* `NpgsqlProjectionPositions.IsListening` for a health check; logs 12101 (connection lost), 12102 (polling while it is
  down), 12103 (back).
* `LISTEN` needs a session: behind a pooler in transaction mode, give the read models a direct data source, else the
  positions fall back to the 1 s poll.

## What not to do

* Do not add output caching or a TTL cache in front of a list.
* Do not cache views, loads or `Find`.
* Do not clear the cache from a change: the client sends `Tarol-Read-After` with the change's `Tarol-Position`.
* Do not set `PushCheckpoints` in the API process only: the worker then sends no NOTIFY, and pages lag up to the 1 s poll.
* Do not assert hit counts inside a wall-clock window in tests: count by events (a batch, then one search) or use a
  `FakeTimeProvider`.
