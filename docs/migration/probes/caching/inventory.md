# Inventory: caching in the old code (`CLorat/Tarol@6a62f81`) and in Tarol-Net today

All entries are **[verified by reading]**: every path:line below was opened and read at the commit named. The search
patterns were: file names `*Cach*`; `IMemoryCache|IDistributedCache|HybridCache|MemoryCache|OutputCache|ResponseCach|ETag|If-None-Match|Cache-Control|Vary|StackExchange|Redis|Garnet`
over every file except `.git`, `bin`, `obj`; `ConcurrentDictionary|Lazy<|ConditionalWeakTable|FrozenDictionary` over
`*.cs`; `Caching|Hybrid|Redis` over `*.csproj`/`*.props`; `cach|redis|snapshot|etag` over `*.md`.

## 1. The old code (`CLorat/Tarol@6a62f81`)

**There is no caching layer.** 0 projects or files named `*Cach*`; 0 files using `IMemoryCache`, `IDistributedCache`,
`HybridCache` or `MemoryCache`; 0 HTTP caching (`OutputCache`, `ResponseCaching`, `ETag`, `If-None-Match`,
`Cache-Control`, `Vary`) in `src`, `tests`, `examples`, `gen`, `templates` (one hit in an IDE file,
`.idea/config/applicationhost.config:859`, the IIS module list); 0 Redis, StackExchange or Garnet references.

| What | Where | Caches | Key | Invalidation | Tenant | Tests | Used | Verdict |
|---|---|---|---|---|---|---|---|---|
| unused package version | `Directory.Packages.props:43` `Microsoft.Extensions.Caching.Hybrid` 11.0.0 | — | — | — | — | — | no `*.csproj` references it (0 hits) | dead entry; nothing to migrate |
| id JSON converter registry | `src/StronglyTypedId/StronglyTypedId/IdConverterRegistry.cs:36-37` (`ConcurrentDictionary<Type, JsonConverter>`, `ConcurrentDictionary<Type, byte>`), filled at `:47` (`TryAdd`), read at `:54-57` | one `IdJsonConverter<T>` per `Id<T>` | `typeof(Id<T>)` | never (process lifetime) | no | `tests/StronglyTypedId/StronglyTypedId.UnitTests/Id.JsonSerialization.Tests.cs:86-117` | yes, every `Id<T>` in JSON | a registry, not a cache; migrated in step 3 (Tarol-Net `IdConverterRegistry.cs:28`) |
| regex cache | `src/Validations/Validations.Value/ValueValidationHelpers.cs:79-84` (`ConcurrentDictionary<string, Regex>`, `GetOrAdd`, compiled, 100 ms timeout); entry `MatchesPattern` `:73` | compiled `Regex` | the pattern | never, unbounded | no | none | no caller (`docs/review/validations-and-changes.md:41`, `REVIEW.md:239`) | not migrated (step 6 uses `[GeneratedRegex]`) |
| Marten snapshot | `src/Persistence/Persistence.Marten/MartenSnapshotRepository.cs:31`; the doc comment `:25-29` calls the snapshot "purely a fast-read cache" only if events were also appended (not implemented) | the aggregate's snapshot document | `Id<T>.Value` | overwritten on save | no | `tests/Persistence/Persistence.Marten.IntegrationTests/MartenAggregateTests.cs:15-75` | registration only | the source of truth, not a cache; replaced by `streams.state` (step 8, P-12) |
| docs | `docs/review/net11-event-generation.md:659` (codec infos cached per `JsonSerializerOptions` via `ConditionalWeakTable`: planned, not built); `docs/review/messaging.md:88` (Redis named only as a possible **backplane**, not a cache) | — | — | — | — | — | — | ideas only |

Roslyn incremental-generator "caching" (`gen/Primitives/Primitives.Generators/EquatableArray.cs:7` and others) is build
time, not runtime, and out of scope.

## 2. Tarol-Net today (`main` = `ab86313`): what already behaves like a cache

| Where | What | Note for step 13 |
|---|---|---|
| `src/Persistence/Tarol.Persistence/Codec/EventCodec.cs:61-67,165-168` | the codec's tables (stream type ↔ CLR type, `JsonTypeInfo` per (stream, alias), upcasters) as copy-on-write `Dictionary` snapshots | **codec metadata is already cached** at registration; `StreamTypeOf<T>()` and `Decode` cost tens of ns (output.txt §5): nothing to add |
| `src/Validations/Tarol.Validations/ValueObjectJson.cs:25` | a `ConcurrentDictionary<Type, Func<JsonSerializerOptions, JsonTypeInfo>>` of value-object converters | registration, not a cache |
| `src/Queries/Tarol.Queries.Npgsql/NpgsqlReadModels.cs:18-19` | one `NpgsqlReadModelStore<TRow>` per row type | object reuse |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlAggregateStore.cs:111-137` | `Load` reads `streams.state` (one round trip), falling back to replay | the "plain load" the aggregate cache is measured against |
| `src/Persistence/Tarol.Persistence.Npgsql/NpgsqlAggregateStore.cs:172-185` | `Find` = one `SELECT stream_id FROM lookups` | a released lookup changes its holder (§1 of the evaluation: a cached `Find` breaks decisions) |
| `src/Changes/Tarol.Changes.AspNetCore/ChangeEndpoints.cs:369-398` | views: `Load`, `Check(Read<T>, aggregate)` (row rules run), `ETag: "<version>"`, 304 | the 304 is decided **after** the load and the check |
| `src/Changes/Tarol.Changes.AspNetCore/QueryEndpoints.cs:181-237` | lists: `Tarol-Read-After` wait, `Checkpoint` (a round trip), `Search` (a second: checkpoint + rows + count in one batch, `NpgsqlReadModelStore.cs:178-200`), page ETag, 304 | the 304 is decided **after** both round trips; the page's ETag cannot be known earlier |
| `src/Queries/Tarol.Queries/Hosting/ProjectionPositions.cs` | `Checkpoint<TRow>` reads the store every call; `WaitFor` uses the in-process host or polls every 5 ms | a pushed checkpoint would remove the first round trip of every list request |
| `src/Security/Tarol.Security/ChangeAuthorization.cs:51,73` | `Check` (role map + row rules on the loaded aggregate), `CheckList` (role map only, QR-6) | a cache must sit **behind** these calls, never in front of them |
