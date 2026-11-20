# Global search

One search box over every aggregate and entity a user may see: ranked, paged, in the user's tenant, restricted to the types
(and rows) they may read. Design: [architecture/search.md](../architecture/search.md); evaluation and measurements:
[migration/search.md](../migration/search.md).

## 1. Mark what is searchable

```csharp
[Searchable("Auction")]
public partial class Auction : AggregateRoot<Auction>
{
    public EntitySet<Bid> Bids { get; } = [];
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";            // weight B
    [SearchAudience] public string? Owner { get; private set; }                    // optional: only these subjects/roles see it
    [SearchLanguage] public string Language { get; private set; } = "en";          // optional: "de", "en", else simple
}

public partial class Bid : Entity<Auction, Bid>
{
    [Searchable(SearchWeight.A)] public string Bidder { get; private set; } = "";  // a bid is its own hit, shown through its auction
}
```

* Weights A–D: A for titles and names, B (the default) for descriptions, C and D for the rest.
* `[PersonalData]` members are searchable only as `[Searchable(Exact = true)]`: the whole normalised value (an email) finds its
  aggregate, a part does not, and the text never enters the index. Erasure and retention reach the index (`SearchEraser`).
* The generator reports mistakes at build time: TSRCH001 (not a partial aggregate), TSRCH002 (no text in the member's type),
  TSRCH003 (personal data as text), TSRCH004 (two titles, an audience on an entity), TSRCH005 (nothing marked).

## 2. Register an index

| Where the index lives | Package | Register |
|---|---|---|
| PostgreSQL, the store's database (default) | `Tarol.Search.Npgsql` | `.AddSearch(s => s.Add<Auction>()).UseNpgsqlSearch()` |
| OpenSearch | `Tarol.Search.OpenSearch` | `.UseNpgsqlSearchCheckpoints().UseOpenSearch(_ => client)` |
| Typesense | `Tarol.Search.Typesense` | `.UseNpgsqlSearchCheckpoints().UseTypesense(_ => TypesenseTarolBuilderExtensions.Client(url, key))` |
| memory (tests) | `Tarol.Search` | `.UseInMemorySearch()`, or `SearchTest` |

The worker hosts the index's feeder (`SearchHost`, a projection host: one runner per version, its lag on the projections gauge).
An API process registers the same `AddSearch` and the same index; its host stays idle behind the gate.

**Change a searchable member, a weight or a flag → raise the version** (`s.Version = 2`). The host compares a generated
fingerprint and refuses to start otherwise. The new version builds from the start of the feed while the old one keeps answering;
drop the old one with `DropVersion("search", 1)` (PostgreSQL) or delete its index/collection (engines).

## 3. Search

```csharp
app.MapTarolSearch("/search", policy: "search");
```

```http
GET /search?q=sunset harbour&prefix=true&counts=true&snippets=true&limit=20
Tarol-Read-After: 7341.1000412
```

* `q`: words (all must match), `"a phrase"`, `-word`, `a OR b`. It never fails on what a user types.
* `types`: the document types to search (default: every type the caller may read: `Read<T>` per aggregate; a requested type
  without it is 403). An entity type (`auction.bid`) follows its aggregate.
* `prefix=true`: the last word is a prefix. A typo fallback (one edit) runs when nothing else matches.
* `counts=true`: matches per type (PostgreSQL counts up to 1,000; `countsCapped` beyond). `snippets=true`: a plain-text
  `snippet` and `highlights: [{start, length}]` (UTF-16 offsets, a JavaScript string's indexes) for the matched words. The
  snippet is never markup: show it as text and wrap the ranges yourself (no `{@html}`, no `innerHTML`).
* Pages of at most 50; `next` is the cursor, down to 1,000 hits. A weak `ETag` answers 304.
* `Tarol-Read-After` waits for the index to pass the caller's commit (PostgreSQL and Typesense: milliseconds; OpenSearch: its
  refresh, about a second, unless `WaitForRefresh`).
* 503 while no index version is current yet (the first build).

In code: `ISearch.Search(new SearchQuery("harbour") { Counts = true }, context, ct)`; the tenant comes from the context.

## 4. Test without a database

```csharp
var test = SearchTest.Create(s => s.Add<Auction>());
test.Given(Auction.Open("Sunset over harbour", "oil on canvas"), tenant);
await test.CatchUp();
var page = (await test.Find(new SearchQuery("harb") { Prefix = true }, tenant)).GetValueOrThrow();
```

## 5. What it costs (1,000,000 documents, a tenant of 10 %, p50 / p99, 4 cores)

| | common word | medium | rare | prefix | write → searchable |
|---|---|---|---|---|---|
| PostgreSQL (as built) | §17.4 of the evaluation | | | | in the batch's transaction |
| OpenSearch | 7.0 / 9.2 ms | 3.0 / 4.0 | 2.1 / 2.8 | 3.8 / 13 | ~1 s (refresh) |
| Typesense | 16 / 29 ms | 8.1 / 12 | 4.8 / 7.5 | 7.0 / 19 | 7.5 ms |

PostgreSQL needs no extra server and the trusted extensions `pg_trgm`, `btree_gin` and `unaccent` (the database owner creates
them). OpenSearch is a JVM (2.8 GB in the probe); Typesense keeps its index in memory (0.4 GB at 1M documents).

## 6. Operations

Meter `Tarol.Search`: `tarol.search.queries` (by `tarol.outcome`), `tarol.search.duration`, `tarol.search.documents`; the host's
`tarol.projections.*` and the lag gauge. An engine that does not answer is a `ready` problem until it answers again.
