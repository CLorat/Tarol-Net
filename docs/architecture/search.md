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

**Status: designing** (decisions GS-1 … in [migration/search.md](../migration/search.md) §9).

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
