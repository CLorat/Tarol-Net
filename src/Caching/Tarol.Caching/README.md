# Tarol.Caching

List page caching for [Tarol](https://github.com/CLorat/Tarol-Net) read models, in the API process: a decorator of
`IReadModels` whose stores answer `Search` from an in-process page cache. A page is valid exactly while the projection's
checkpoint it was read at is current; with the checkpoint pushed by `Tarol.Queries.Npgsql`, a hit costs no round trip.
Only list pages are cached: views (GET by id), aggregate loads, `Find` and writes read the store. No HybridCache, no
Redis, no output caching. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.Caching     # brings Tarol.Queries
```

## 30 seconds

```csharp
// the API process
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })   // the worker sets it too: it sends the NOTIFY
    .AddCaching(o => o.MaxBytes = 128L * 1024 * 1024);                            // after the read stores
```

The list endpoints (`Tarol.Changes.AspNetCore`) need no change: they call `Checkpoint` and `Search`, and `Search` is the
cached call. `CheckList` (`Read<T>`) runs before it, so a hit never skips authorization.

## How it works

| Piece | Behaviour |
|---|---|
| `CachedReadModels` | per row type, a store whose `Search` reads `IProjectionPositions.Checkpoint<TRow>()` first, asks the cache for that position, and on a miss searches the inner store and caches the page stamped with that position (a batch during the search makes the next request miss). Nothing is cached while the projection builds; a refused query (`Invalid`) is not cached; `Prepare`, `Checkpoint`, `Load`, `Write`, `Find` pass through |
| `PageKey` | the tenant first, then the projection's name and version, then the canonical query: sort and direction, the filters sorted by column (`[FromClaim]` values are filters), cursor, offset, limit, count; every part length-prefixed |
| `IPageCache` (`TryGet`, `Put`, `Clear`), `MemoryPageCache` | the port and its in-process implementation: immutable entries in a concurrent dictionary; no eviction thread (the idle expiry is checked on read, a write past a cap trims to 90 %: idle entries first, then the least recently used) |
| `CacheOptions` | `MaxEntries` 10,000, `MaxBytes` 64 MB (estimated: the rows' UTF-8 JSON plus the key), `IdleExpiry` 5 min on `TimeProvider`. They bound memory only, never validity |
| `AddCaching(configure)` | decorates the registered `IReadModels` (call it after `UseNpgsqlReadModels`/`UseInMemoryReadModels`; twice throws), registers `MemoryPageCache` unless an `IPageCache` is registered |
| meter `Tarol.Caching` | `tarol.caching.hits`, `tarol.caching.misses`, `tarol.caching.evictions` (tag `reason`: `idle`, `entries`, `bytes`), all tagged `tarol.projection`; gauges `tarol.caching.entries`, `tarol.caching.bytes` |

Any batch of a projection moves its checkpoint, of any tenant: every entry of that projection misses once. Measured:
99.6 % hits at ~75 commits/s. Without `PushCheckpoints` the cache still saves the search, but reading the checkpoint
costs a round trip per request; with `PushCheckpoints` only in the API process, pages lag up to the 1 s poll.

## Links

* Concept: `docs/architecture/caching.md`; decisions and measurements: `docs/migration/caching.md`.
* Agent skill: `skills/tarol-caching/SKILL.md` (in this package).
