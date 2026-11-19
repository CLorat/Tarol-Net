# Global search

Concept note for step 18 (see [migration/search.md](../migration/search.md)): **one search box over every aggregate and entity
a user may see.** A user types a term and gets matching aggregates and entities of every searchable type, ranked, paged,
restricted to their tenant and to the types (and, optionally, the rows) they may read. The index is one more read model: it is
fed from the event feed by a projection host, versioned like a projection, and it forgets what erasure and retention forget.
One port, several infrastructures: PostgreSQL full-text search in the store's own database is the default; external engines
are adapters behind the same port.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/search/`](../migration/probes/search/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: built** (owner decisions GS-0 … GS-12, [migration/search.md](../migration/search.md) §9); PRs 18A–18G (§15–§21 there). The guide: [docs/search/README.md](../search/README.md). Where the build differs from this note, the migration note says so (the typo fallback on weight A, the tsquery from Tarol's parser, the checkpoint port for engines, read-after polling the stored checkpoint).

## 1. What a user of Tarol needs

| ArtFlow use case | Shape | Built from |
|---|---|---|
| a bidder types "harbour" and sees auctions, artists and lots | ranked hits of every searchable type, one page | `[Searchable]` members, one index, `GET /search?q=harbour` |
| a lot (an entity of an auction) is found by its own label | an entity hit, shown and authorized through its auction | the generated documents of the aggregate and its entities |
| a seller types "har" while typing | prefix on the last word | the query's `Prefix` |
| a bidder misspells "harbuor" | a typo still finds | trigram fallback on titles (PostgreSQL) or the engine's typo tolerance |
| counts "Auctions (12) · Artists (3)" | counts per type | `Counts = true` |
| tenant A never sees tenant B | tenant first in every key and filter | the index key `(tenant, type, id)` |
| a user without `Read<Payout>` never sees payouts | the type filter in the index, exact pages and counts | `CheckList<T>` per type (R-5, QR-6) |
| a customer is erased; searching their name finds nothing | the index takes part in erasure and retention | an `ErasureParticipant` that re-indexes the subject's streams |

## 2. One search, end to end

```text
api      GET /search?q=sunset+harbour&types=auction,lot&limit=20
         → CheckList<Auction> (Read<Auction>) … for every requested type → allowed types {auction, auction.lot}
         → ISearchIndex.Search(tenant, "sunset harbour", allowed types, audience, limit 21, cursor)
         → PostgreSQL: WHERE tenant = $1 AND tsv @@ websearch_to_tsquery(…) AND type = ANY($3)
                        ORDER BY ts_rank(tsv, q) DESC, id LIMIT 21
         → 200 {items: [{type, id, aggregate, title, snippet, score}], counts?, next}, Tarol-Position: <checkpoint>
worker   SearchHost "search" v1: ReadAll(after checkpoint, 2000) → the streams of searchable types in the batch
         → Load<T> each (its current state) → the generated documents (root + entities)
         → one transaction: guarded checkpoint move, DELETE the streams' documents, INSERT the new ones
```

## 3. What is searchable (GS-1)

```csharp
[Searchable("Artwork")]
public partial class Artwork : AggregateRoot<Artwork>
{
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";                  // weight B
    [PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; }  // a blind token (GS-8)
    [SearchAudience] public string Seller { get; private set; } = "";                    // who may see it (GS-7)
    public EntitySet<Edition> Editions { get; } = [];
}

public partial class Edition : Entity<Artwork, Edition>
{
    [Searchable(SearchWeight.A)] public string Label { get; private set; } = "";
}
```

* Members of the aggregate's **state** and of its **entities** are marked; the generator (inside `Tarol.Search`) writes
  `ISearchable<T>.Documents(T)`: one document for the aggregate and one per entity with a searchable member. An entity
  document has its own type (`artwork.edition`) and id, and carries its aggregate's id and title: it is **found by its own fields,
  shown and authorized through its aggregate**.
* The feeder **loads the aggregate** (the stored state, one round trip: p50 0.56 ms) rather than folding events a second time
  or reading JSON paths of the stored state (R-8). No reflection; AOT-clean **[verified]**.
* Weights A–D map to `setweight` in PostgreSQL, field boosts in OpenSearch, `query_by_weights` in Typesense.

## 4. Where the index lives (GS-2, GS-3, GS-6)

| Implementation | Package | Fits | 1M documents, a 10 % tenant, p50 / p99 ms |
|---|---|---|---|
| **PostgreSQL full-text search** in the store's database (default) | `Tarol.Search.Npgsql` | every Tarol system; no new server; documents and checkpoint in one transaction | common 16 / 26, medium 5.4 / 7.5, rare 1.2 / 2.1, prefix 15 / 74, typo 47 / 145 |
| OpenSearch (Apache-2.0) | `Tarol.Search.OpenSearch` | large indexes, exact counts at any size, a team that already runs it | 2.1–7.0 / ≤ 13; write → visible ~1 s |
| Typesense (server GPL-3.0, a separate process) | `Tarol.Search.Typesense` | a small, fast server; synchronous writes | 4.8–16 / ≤ 29; write → visible 7.5 ms |
| in memory | `Tarol.Search` | tests (`SearchTest`), the contract suite | — |

* **One shared index per version, the tenant first** in the key `(tenant, type, id)`, in every GIN (`btree_gin`) and in every
  filter: 4–9× faster in a tenant than a GIN on the text alone **[verified]**.
* **Bounded rank**: PostgreSQL ranks the first 1,000 matches of the tenant-led GIN, not every match (13× faster for a common word,
  48 → 16 ms; Meilisearch caps at the same 1,000) **[verified]**. Counts are exact up to the bound and say "1,000+" beyond it.
* **Typos**: when full-text finds nothing, a trigram match on titles (`GIN (tenant, title gin_trgm_ops)`, threshold 0.4) **[verified]**.
* Adapters speak HTTP with Tarol's own JSON code: no engine SDK, no client licence, 0 IL warnings **[verified]**.
* Meilisearch is not built (counts capped, no stemming, BUSL parts on its `main`); Elasticsearch, ParadeDB and `pgvector` are
  reasoned only ([migration/search.md](../migration/search.md) §2.2, §2.10).

## 5. Feeding, consistency and versions (GS-4, GS-5)

* `SearchHost` is a projection host (`ProjectionHost`, opened to other packages in 18A): the feed after its checkpoint → the
  streams of registered types in the batch → `Load<T>` each (4 in parallel) → the generated documents → `ISearchIndex.Write`:
  the stream's documents replaced and the checkpoint moved. PostgreSQL commits both in one transaction (exactly once); an engine
  acknowledges its write before the checkpoint commits (at least once; a document is the current state, so a repeat changes nothing).
* The checkpoint is the row `('search', n)` of `tarol.projections`: the lag gauge, the one-runner gate, `CatchUp`, `WaitFor` and
  `DropVersion` work as for projections.
* **Consistency**: search lags the commit like a list (NOTIFY wake-up p50 3.6 ms) plus the engine's own delay (PostgreSQL and
  Typesense none, OpenSearch ~1 s). `Tarol-Read-After` waits, opt-in, as for lists.
* **Versions**: `AddSearch(…, version: n)`; the generator's fingerprint of every shape is stored with the version, and the host
  refuses to start when it differs without a bump. A new version builds from the start of the feed next to the old one (1M
  documents in ~100 s through the feeder); **search keeps answering from the newest current version meanwhile**, because a hit
  does not depend on the shape. Old versions are dropped explicitly (`DropVersion`, O-5).

## 6. Authorization (GS-7)

* `CheckList<T>` (`Read<T>`, R-5) for every searchable type the caller asks for (default: all registered) becomes the type filter
  **in the index**: pages and counts stay exact. An entity type follows its aggregate's grant. A requested type the caller may not
  read is a 403; the default set silently leaves it out.
* An optional `[SearchAudience]` member puts values (an owner's `sub`, a group) on the document; the query adds "audience empty, or
  it overlaps the caller's `sub` and roles". Row rules (`ChangeHandler<,>`) never run on search, as on lists (QR-6).

## 7. Personal data (GS-8)

* A `[PersonalData]` member is searchable only with `Exact = true`, as an **HMAC token** under the tenant's lookup key
  (`IKeyStore.BlindIndex`): the whole normalised value finds its aggregate, a part does not, and the text never lands in the index
  **[verified]**. `[Searchable]` without `Exact` on a personal member is a diagnostic.
* `SearchEraser` (an `ErasureParticipant`): after a subject's tombstone it re-indexes the subject's streams, whose personal fields
  now read `null` (tombstone 4.5 ms + re-index 18.6 ms); a tenant's erasure re-indexes the tenant; retention removes the documents of
  deleted streams. Without it, the index kept finding the erased subject **[verified]**.
* Export lists the subject's documents (type, id, aggregate).

## 8. The query and HTTP (GS-9, GS-10)

```http
GET /search?q=sunset harbour&types=auction,auction.lot&prefix=true&counts=true&snippets=true&limit=20
Tarol-Read-After: 7341.1000412

200  ETag: W/"…"   Tarol-Position: 7341.1000420
{ "items": [ { "type": "auction.lot", "id": "…", "aggregate": "…", "title": "Sunset over harbour", "snippet": "… <b>harbour</b> …", "score": 0.61 } ],
  "counts": { "auction": 12, "auction.lot": 3 }, "next": "<cursor>" }
```

* `q` in web-search syntax (words AND, `"phrase"`, `-word`, `OR`); it never fails on user input **[verified]**. `prefix=true`
  makes the last word a prefix (search-as-you-type). A typo fallback runs when nothing is found.
* An opaque cursor bound to the query and the index version; depth ≤ 1,000 hits. A weak ETag over (version, checkpoint, hits)
  answers 304. No page cache (search pages are varied queries), no SignalR hint.
* `app.MapTarolSearch("/search", policy)` lives in `Tarol.Changes.AspNetCore` next to the list endpoints.
* **Languages**: a `[SearchLanguage]` member or a tenant default (`simple` otherwise); PostgreSQL configurations `tarol.german`,
  `tarol.english`, `tarol.simple` with `unaccent` (a configuration, because `unaccent()` is refused in a generated column
  **[verified]**); the query's language from `?lang` or `Accept-Language`.

## 9. Operations (GS-11)

Meter `Tarol.Search`: `tarol.search.queries` (by `tarol.outcome`), `tarol.search.duration` (s), `tarol.search.documents`; the host's
`tarol.projections.*` and the lag gauge. `ready` never waits for search to catch up (O-2); an adapter's engine is a `ready`
dependency. A re-index is a version bump; old versions go through `DropVersion`. PostgreSQL needs the trusted extensions `pg_trgm`,
`btree_gin`, `unaccent`, which a database owner without superuser rights may create **[verified]**.

## 10. Packages (GS-12)

| Package | Holds |
|---|---|
| `Tarol.Search` | the attributes and the generator, `ISearchIndex`, `ISearch`, `SearchHost`, `SearchEraser`, the in-memory index, `SearchTest`; in the meta-package `Tarol` |
| `Tarol.Search.Npgsql` | PostgreSQL full-text search |
| `Tarol.Search.OpenSearch` | the OpenSearch adapter |
| `Tarol.Search.Typesense` | the Typesense adapter |
| `Tarol.Changes.AspNetCore` | `MapTarolSearch` |

## New insights

1. **A search index is a projection**: the same host, checkpoint, gate, versions and erasure participant; only its target differs.
2. **Index the loaded aggregate, not its events**: the stored state already holds the fold, entities included, at 0.56 ms a load.
3. **Ranking, not matching, is PostgreSQL's cost**: bound the ranked candidates and the tenant-led GIN answers like an engine.
4. **The tenant must lead every search index** (the GIN and the trigram index alike), or a small tenant pays for the large ones.
5. **A key's tombstone does not reach an index**: derived copies need their own participant, even for a blind token.
6. **Search hits do not depend on the document shape**, so a new version can build while the old one keeps answering.
