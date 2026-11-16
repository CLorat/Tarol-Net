---
name: tarol-caching
description: >-
  Use when a Tarol list endpoint or IReadModels Search must be faster under load, or when sizing, testing or reasoning
  about the page cache. Covers AddCaching, CacheOptions, CachedReadModels, IPageCache, MemoryPageCache, PageKey, the
  meter Tarol.Caching and how pushed checkpoints keep pages valid. Do not add output caching, HybridCache, Redis or a
  TTL cache, and do not cache views, aggregate loads or Find.
---
# Tarol.Caching: cached list pages

## Rules

1. Only list pages are cached (`IReadModelStore<TRow>.Search`). GET by id (`IView<,>`), aggregate loads in a change,
   `ILoads<T>.Find` and projection writes always read the store: a decision never reads a cache.
2. Register in the API process, after the read stores: `.UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true }).AddCaching()`.
   Set `PushCheckpoints = true` in the worker too (its batches send the NOTIFY that moves the checkpoint), but no
   `AddCaching()` there: the worker writes, it does not serve lists. `NotifyFeed` (the store's feed NOTIFY) is separate:
   it wakes the worker's projection sooner; the cache needs only `PushCheckpoints`. `AddCaching()` also works over
   `UseInMemoryReadModels()` (tests). Before any read store, or twice, it throws `InvalidOperationException`.
3. Validity is the projection's checkpoint, never time: an entry answers only while `IProjectionPositions.Checkpoint<TRow>()`
   equals the position read before its search. Any batch of that projection (any tenant) makes its entries miss once.
4. `CacheOptions` (`MaxEntries` 10,000, `MaxBytes` 64 MB estimated from the rows' JSON, `IdleExpiry` 5 min) bound
   memory only. Nothing is cached while the projection is `Building`.
5. The key (`PageKey`) is tenant first, then projection name and version, then the canonical query (sort, direction,
   filters sorted by column, cursor, offset, limit, count). A `[FromClaim]` member is a filter, so each claim value
   has its own entry. Authorization (`CheckList`, `Read<T>`) runs in the endpoint before `Search`: a hit never skips it.
6. Read-your-writes: the client sends `Tarol-Read-After` with the change's `Tarol-Position`; the endpoint waits for the
   pushed checkpoint, then `Search` misses and reads. Never clear the cache from a change.
7. HTTP (`Tarol.Changes.AspNetCore`): list and view answers carry `Cache-Control: private, no-cache` and
   `Vary: Authorization`; a list 304 (`If-None-Match` = the page ETag) from a warm cache runs no query.
8. Watch `tarol.caching.hits` / `tarol.caching.misses` (tag `tarol.projection`), `tarol.caching.entries`,
   `tarol.caching.bytes`, `tarol.caching.evictions` (tag `reason`) on the meter `MemoryPageCache.MeterName`.

## Example

Namespaces: `Tarol.Caching`, `Tarol.Queries` (`InMemoryReadModels`, `ProjectionPositions`, `ListQuery`, `Page<T>`),
`Tarol.Queries.Npgsql`, `Tarol.Persistence` (`GlobalPosition`); `FakeTimeProvider` is in the package
`Microsoft.Extensions.TimeProvider.Testing` (`Microsoft.Extensions.Time.Testing`).

```csharp
// API process (worker: the same UseNpgsqlReadModels options plus AddProjections)
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })
    .AddCaching(o => { o.MaxEntries = 20_000; o.IdleExpiry = TimeSpan.FromMinutes(10); });

// a unit test: in-memory stores, a fake clock, the decorator by hand
var time = new FakeTimeProvider();
var inner = new InMemoryReadModels();
using var cache = new MemoryPageCache(new CacheOptions { IdleExpiry = TimeSpan.FromMinutes(5) }, time);
var store = new CachedReadModels(inner, new ProjectionPositions(inner), cache, AppJson.Default.Options).Store<AuctionRow>();
// rows: IReadOnlyList<Versioned<AuctionRow>>, e.g. [new(new RowKey(tenant, id), 1, new AuctionRow(…))]; tenant: a Guid
await store.Write(new ProjectionBatch<AuctionRow>(rows, GlobalPosition.Start, new GlobalPosition(1, 1), ProjectionState.Current), ct);
var first = await store.Search(new ListQuery(tenant), ct);    // a miss: searches the in-memory store
var second = await store.Search(new ListQuery(tenant), ct);   // a hit: the same Page<AuctionRow> instance
time.Advance(TimeSpan.FromMinutes(6));                        // idle: the next search misses
// Search returns Result<Page<AuctionRow>>: first.TryGetValue(out Page<AuctionRow>? page)
```

## Common mistakes

| Mistake | What happens |
|---|---|
| `AddOutputCache()` / `.CacheOutput()` on a list | the default policy never caches with `Authorization`; one that does serves tenant A's page to B and ignores `Tarol-Read-After` |
| HybridCache or Redis in front of `Search` | no backplane (another process keeps stale pages), 9 IL warnings under Native AOT |
| a TTL as validity | stale pages for up to the TTL after a bid; the checkpoint is already exact |
| `AddCaching()` before `UseNpgsqlReadModels()` | `InvalidOperationException`: it decorates the registered `IReadModels` |
| `PushCheckpoints` only in the API process | the worker sends no NOTIFY: entries stay valid until the 1 s poll sees the move |
| an aggregate or `Find` cache for changes | a stale lookup decides on the wrong aggregate, no version guard sees it |
| asserting hit counts inside a wall-clock window | flaky on CI; count by events (a batch, then one search) or advance a `FakeTimeProvider` |

## AOT

No reflection and no third-party package: the key comes from the generated column map, the size estimate from your
`JsonSerializerContext` (rows not in it count 256 bytes each).
