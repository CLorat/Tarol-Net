# Global search: evaluation (step 18 design)

Evaluation of [search.md](../architecture/search.md): what Tarol already has that search builds on, what is searchable and how
a user marks it, where the index lives (PostgreSQL full-text search, `pg_trgm`, their combination, Meilisearch, Typesense,
OpenSearch; Elasticsearch, ParadeDB and `pgvector` reasoned), how the index is fed, tenancy, authorization, personal data,
the query API and HTTP, languages, operations, the package split, measurements, the owner's answers, and what each PR must do.
**Design only**: this PR changes no package code. Probe and output: [`probes/search/`](probes/search/),
[`output.txt`](probes/search/output.txt). Labels as in the [conventions](../architecture/conventions.md). There is no old code:
search is new in Tarol-Net (plan row 18, added 2026-11-18).

## 0. Summary

* **Nothing to migrate**: search is new; it builds on the feed, projection hosts and checkpoints (step 12), list authorization
  (R-5, QR-6), the key store's blind index and erasure participants (step 16), and the operations machinery (step 15) (§1).
* **Decided** (GS-0 … GS-12, §9): search is **step 18** before Samples. A user marks **aggregate and entity members**
  (`[Searchable]`, `[SearchTitle]`, `[SearchAudience]`, `[SearchLanguage]`); a generator writes the documents from the **loaded
  aggregate**, entities as their own hits shown through their aggregate (GS-1). The default index is **PostgreSQL full-text search
  in the store's database**, GIN led by the tenant, ranked within the first 1,000 matches, with a trigram fallback for typos (GS-2);
  **OpenSearch and Typesense** get adapters (GS-3). A **projection host** feeds it (GS-4), with an **explicit version and a
  fingerprint**, the old version answering while a new one builds (GS-5). One **shared index, tenant first** (GS-6); **type grants
  and an optional audience filter in the index** (GS-7); **personal data only as a blind exact token**, and the index takes part in
  erasure, retention and export (GS-8). One **runtime endpoint** `MapTarolSearch` with a cursor and an ETag (GS-9), languages **per
  document with a tenant default** (GS-10), the **projection operations plus a `Tarol.Search` meter** (GS-11), **four packages**
  and the endpoint in `Tarol.Changes.AspNetCore` (GS-12).
* **Measured** (§4; 4 cores, every engine alone, 1,000,000 documents, a 10 % tenant, p50 / p99 ms of a page of 20):
  * PostgreSQL as decided: common word 16 / 26, medium 5.4 / 7.5, rare 1.2 / 2.1, prefix 15 / 74, typo fallback 47 / 145, with
    counts 11 / 15, with snippets 7.4 / 9.7. Ranking every match instead: 48 / 64 (common); without the tenant in the GIN 211 / 276.
  * Meilisearch 1.6–4.8 ms p50; Typesense 4.8–16; OpenSearch 2.1–7.0; all p99 ≤ 29 ms in a tenant.
  * Rebuild of 1M: PG 60 s, OpenSearch 46 s, Typesense 126 s, Meilisearch 164 s; through the feeder (`Load` per stream, 4 loaders)
    ~100 s. Size: PG 1.1 GB, Meilisearch 3.5–4.4 GB, Typesense 0.3 GB RAM + 1.3 GB disk, OpenSearch 0.26 GB (2.8 GB JVM).
  * Write → searchable: PG and Typesense synchronous (3.6 / 7.5 ms), Meilisearch ~0.3 s, OpenSearch ~1 s.
  * Erasure: tombstone 4.5 ms + re-index of the subject's streams 18.6 ms; before the re-index the subject is still found.
  * Native AOT: the probe with three hand-written HTTP clients and the Npgsql feeder, **0 IL warnings**, every check natively.
* **Found** (§5): PostgreSQL's cost is ranking every match (bounded: 13× faster); the tenant must lead the GIN and the trigram
  index; pg_trgm's default threshold misses transpositions; `unaccent` only via a configuration; engines differ on stemming and
  accents; Meilisearch caps counts at 1,000 and its `main` now carries BUSL parts; a tombstone alone does not erase an index.

## 1. Inventory: what search builds on **[verified by reading]** (`main` = `15c5706`, after #73)

| Piece | Where | What search takes from it |
|---|---|---|
| the feed | `IEventFeed.ReadAll(after, limit)`, `PositionedEvent(Position, StreamType, Event)` (`Tarol.Persistence`) | the only input of the index: every commit, in order, tenant in `Event.Context.Tenant` |
| stored state | `NpgsqlAggregateStore.Load<T>`: one `SELECT` of `streams.state` when its schema matches `T.StateSchema`, else a replay; sealed fields opened with the subject's key | the documents are built from the **current state** of each changed stream, so entities and every member are there without a second fold |
| projection hosts | `ProjectionHost` (`Tarol.Queries/Hosting`): `RunOnce`, `CatchUp`, `WaitFor`, the batch loop, backoff, the meters `tarol.projections.*`, the gate `IProjectionGate` (one runner per version, O-8); subclasses `ProjectionHost<TRow>` and `SqlProjectionHost` | the feeder is a third kind of host. **The constructor and `Prepare`/`LoadCheckpoint`/`Write` are `private protected`**: another package cannot subclass it today (§10) |
| checkpoints | `tarol.projections (name, version, tx, pos, state, updated_at)`; `ISqlProjectionStore.Write` moves it guarded and runs the projection's SQL in the same transaction | the index's checkpoint is a row there, so the lag gauge `tarol.projections.lag` (step 15), `DropVersion` (O-5) and the admin entry see it |
| versions | QR-3: a new version builds next to the old one; lists answer 503 while building | the index is versioned the same way (§2.6) |
| list authorization | `IChangeAuthorization.CheckList<T>` (Read<T>, R-5), `ChangeEndpoints.ListAccess<T>` (cache headers, tenant from the context) | one `CheckList<T>` per searchable type turns into the type filter of the query (§2.8) |
| claims | `[FromClaim("sub")]` on query records (QR-6), `ChangeEndpoints.Claim` | the optional audience filter (§2.8) |
| read-your-writes | `Tarol-Position`, `Tarol-Read-After`, `Tarol-Stale` (`Positions`, QR-5) | the search endpoint answers with the same headers |
| page cache | `IPageCache`, `CachedReadModels` (CA-1, CA-5) | not used for search (§2.9) |
| personal data | `[PersonalData]` (members of events, state, entities, rows), `PersonalShape`, sealing in `EventCodec`; `IKeyStore.BlindIndex(tenant, lookup, value, create)` (HMAC under the tenant's lookup key) | personal members are searchable only as a blind exact token (§2.7) |
| erasure, retention, export | `ErasureParticipant.Forget/ForgetTenant/Evict/Deleted`, `IDataProtectionStore.StreamsOf`, `ProjectionEraser<TRow>` (re-fold), `Retention` (500 streams per batch) | `SearchEraser`: re-index the subject's streams, delete the documents of deleted streams |
| schema | `NpgsqlReadModelSchema`: `CREATE … IF NOT EXISTS` per projection version; the store's versioned steps with `compatible_from` (O-4) | the index tables are per version like read tables; extensions `pg_trgm`, `btree_gin`, `unaccent` are trusted (§4.7) |
| security port model | `Tarol.Security` (port) + `Tarol.Security.Zitadel` (implementation, own Testcontainers tests, AOT smoke) | `Tarol.Search` (port) + `Tarol.Search.Npgsql` + one package per external engine |
| meta-package | `src/Meta/Tarol` references the cores, no implementations (RL-4) | `Tarol.Search` joins it; implementations stay out |

## 2. Candidates

### 2.1 What is searchable, and how a user marks it (question 1; GS-1)

| | Candidate | User code | Entities | Notes |
|---|---|---|---|---|
| A | **attributes on state members, generated documents, built from the loaded aggregate** | `[Searchable] public string Title { get; private set; }`, `[SearchTitle]`, `Weight = SearchWeight.A`; on entity members too | each entity is its own document (`type = "artwork.edition"`), with its aggregate's id and title | the generator emits `Documents(T aggregate)` next to `PersonalShape`; no JSON paths, no reflection |
| B | a search projection record with `When` methods (like `[Projection]`) | a record per type, one `When` per event | one document per stream; entities need a hand-written list | duplicates what the aggregate already folds; more code per type |
| C | index `streams.state` by JSON path | paths in configuration | paths into arrays | R-8 and queries.md §9: the stored state is not an API; a wrong path is silently empty |

**Candidate A probed** **[verified]** (`feeder`, 17 checks, JIT and Native AOT): an `Artwork` with an `EntitySet<Edition>`; the
"generated" map (hand-written in the probe) yields the root and one document per edition; an edition is found by its own label
and shown with its artwork's id and title; a rename re-indexes the artwork and its editions; another stream type is not indexed.

### 2.2 Where the index lives (question 2; GS-2, GS-3)

| | Candidate | Licence (read §4.1) | Runs where |
|---|---|---|---|
| P1 | PostgreSQL full-text search: `tsvector` generated from a per-row `regconfig`, weights A/B, GIN; `websearch_to_tsquery`; `ts_rank`; `ts_headline` | PostgreSQL Licence | the store's database |
| P2 | `pg_trgm` on titles: prefix (`ILIKE`), typo (`<%`, `word_similarity`) | PostgreSQL Licence (contrib) | the store's database |
| P3 | **P1 + P2**: full-text first; trigram on titles when it finds nothing; prefix through `to_tsquery(… ':*')` | as P1 | the store's database |
| E1 | Meilisearch 1.13.3 | MIT at v1.13.3; `main` now has BUSL-1.1 "Enterprise Edition" parts | a separate server |
| E2 | Typesense 28.0 | server GPL-3.0 (a separate process; Tarol's client is its own code) | a separate server |
| E3 | OpenSearch 2.19.1 | Apache-2.0 | a separate server (JVM) |
| E4 | Elasticsearch | AGPL-3.0 **or** SSPL **or** Elastic Licence 2.0 (`main`) | not measured: the API family of E3 **[unverified]** |
| E5 | ParadeDB `pg_search` (BM25 in PostgreSQL) | AGPL-3.0 | an extension managed PostgreSQL services rarely offer; not measured **[unverified]** |
| V | `pgvector` / semantic search | PostgreSQL Licence | needs an embedding model per document and query: §2.10 |

Every adapter talks HTTP with hand-written JSON (`Utf8JsonWriter`, `JsonDocument`): **no engine SDK**, so no client licence and
no reflection **[verified]** (§4.8: 0 IL warnings, all contract checks natively).

### 2.3 How the index is fed (question 3; GS-4)

| | Candidate | Exactly once? | Rebuild | Ops |
|---|---|---|---|---|
| F1 | **a projection host over the feed** (`SearchHost`): the streams of searchable types in each batch, each loaded once, its documents replaced, the checkpoint moved | Npgsql: documents and checkpoint in one transaction; an engine: write acknowledged, then the checkpoint (at least once, idempotent: a document is the current state) | a new version from the start of the feed | the lag gauge, the gate, `DropVersion`, `CatchUp`, `WaitFor` as for projections |
| F2 | the outbox (a reaction per commit) | at least once | none (rows exist only for new commits; queries.md §3) | dead letters per search write; +0.5 ms per commit (queries.md §7) |
| F3 | inline in the commit | yes | none | a search failure fails the decision (rejected for projections, QR-1) |

### 2.4 Tenancy (question 4; GS-6)

Shared index with the tenant first in the key `(tenant, type, id)` and in every filter, measured two ways in PostgreSQL (GIN on
`tsv` with the tenant as a filter, or GIN on `(tenant, tsv)` through `btree_gin`), against one index per tenant (a table,
partition or engine index per tenant: thousands of objects, a migration per tenant, reasoned not measured).

### 2.5 Authorization (question 4; GS-7)

| | Candidate | Pages and counts |
|---|---|---|
| Z1 | **type grants as an index filter**: `CheckList<T>` (Read<T>) per searchable type; entity types follow their aggregate | exact |
| Z2 | Z1 + **audience keys**: a document carries the values of a `[SearchAudience]` member (owner, group); the query adds "audience empty or overlaps the caller's claims" | exact |
| Z3 | post-filter: load each hit's aggregate and run its row rules (`ChangeHandler<,>`) | short pages, unknown totals, a load per hit (0.56 ms each, §4.6) |

### 2.6 Versions and re-indexing (question 3; GS-5)

A change of fields, weights or language changes the documents. Candidates: an explicit `Version` like projections (QR-3) with a
generated fingerprint that refuses to start when the fingerprint of a version changed without a bump; or an automatic version
from the fingerprint. While a new version builds, search can keep serving the previous version, because a hit
(type, id, aggregate, title, snippet) does not depend on the shape; lists answer 503 instead (QR-3).

### 2.7 Personal data (question 5; GS-8)

| | Candidate | Found by | After erasure |
|---|---|---|---|
| D1 | not searchable (a diagnostic) | — | nothing to forget |
| D2 | **blind exact token**: HMAC of the normalised value under the tenant's lookup key (`IKeyStore.BlindIndex`), never the text | the exact whole value ("ada lovelace"), not a part | the participant re-indexes the subject's streams; the token goes |
| D3 | plaintext in the index, forgotten by the participant | any word | gone after the re-index; until then the plaintext sits in the index (and in its backups) unsealed |

### 2.8 The query API and HTTP (question 6; GS-9)

Syntax (`websearch_to_tsquery`: words AND, `"phrase"`, `-word`, `OR`; never a syntax error), prefix on the last word
(search-as-you-type), typo fallback, a type filter, counts per type, snippets, paging (cursor or bounded offset), an ETag,
a runtime endpoint `MapTarolSearch` (the set of types is known at startup; nothing per query record to generate) or a
generated one, a SignalR hint.

### 2.9 Languages (question 7; GS-10)

Per document (a `[SearchLanguage]` member), per tenant (a setting), or one per index; PostgreSQL text-search configurations
`german`, `english`, `simple`, with `unaccent`; the engines' own analysers.

### 2.10 Semantic search (`pgvector`): later, if a scenario asks

A vector index needs an embedding for every document and every query: a model in the process (ONNX: tens to hundreds of MB, CPU
per document) or an external API (a dependency, cost, and the documents' text leaving the system, which collides with personal
data). It answers "similar meaning", not "this word", and needs a hybrid with full-text search to be useful for names and
numbers. `pgvector` is not in the stock `postgres:17` image **[verified]** (§4.7). Nothing in ArtFlow asks for it; the port
leaves room for a later adapter (a document is text plus fields).

## 3. The contract every candidate passes **[verified]** ([`output.txt`](probes/search/output.txt) §2, §8)

The same checks against each engine, JIT and Native AOT (`selftest <engine>`): tenant A never sees tenant B; tenant B sees only
its own; the type filter; a title match ranks above a body-only match; all words must match; prefix ("moun" → "Mountain"); a
typo ("harbuor" → "harbour"); exact counts per type; a snippet marks the word; an upsert replaces; a delete removes.

| | PostgreSQL (P3) | Meilisearch | Typesense | OpenSearch |
|---|---|---|---|---|
| contract (11 checks) | pass | pass | pass (`typo_tokens_threshold ≥ 1`) | pass (`cross_fields`) |
| english stemming ("paintings" → "painting") | yes | **no** | yes with `stem: true` | yes (`english` analyser) |
| accents folded ("cafe" → "Café") | with the `unaccent` configuration | yes | yes | no (needs `asciifolding`) |
| german stemming | with a german configuration | no (typo tolerance finds "gemalden") | no (english stem) | needs a german analyser |
| typo | trigram fallback, threshold 0.4 (0.6 misses a transposition) | built in | built in (≤ 2) | `fuzziness: AUTO` |
| counts | exact (`GROUP BY`) | **capped at 1,000** (`maxTotalHits`) | exact | exact |

## 4. Measurements **[verified]** ([`output.txt`](probes/search/output.txt); 4 cores, 15 GB, SDK 11.0.100, Docker 29.10, each engine alone with the others paused; JIT)

Corpus: deterministic, 20,000 pseudo-words with Zipf frequencies, titles of 3–6 and bodies of 15–40 words (~360 bytes);
50 tenants: one holds 10 %, one 0.1 %; three types. Queries: ten terms per class, round 2 of 2 reported, up to 100 runs in ≤ 8 s,
a page of 20, ranked. "common" = a word in ~30 % of documents, "medium" ~3 %, "rare" ~0.1 %.

### 4.1 Licences (§1 of the output, read from the sources and images)

| Component | Licence read | With AGPL-3.0 Tarol | With the owner's commercial licence |
|---|---|---|---|
| PostgreSQL 17.12, `pg_trgm`, `btree_gin`, `unaccent` (contrib) | PostgreSQL Licence (image copyright file) | yes | yes |
| `pgvector` | PostgreSQL Licence | yes | yes |
| Meilisearch v1.13.3 | MIT | yes (a separate server) | yes |
| Meilisearch `main` | MIT **plus** BUSL-1.1 "Enterprise Edition" files (`LICENSE-EE`) | the community build only; a later version must be re-read before an upgrade | the same |
| Typesense v28.0 server | GPL-3.0 | yes: a separate process over HTTP, Tarol links nothing of it | yes for the same reason; an operator who modifies and distributes Typesense has GPL duties, not Tarol |
| OpenSearch 2.19.1 | Apache-2.0 (source and `LICENSE.txt` in the image) | yes | yes |
| Elasticsearch `main` | AGPL-3.0 / SSPL / Elastic Licence 2.0 (triple) | as a separate server, AGPL is the compatible choice | ELv2 forbids offering it as a managed service; the owner's customers choose |
| ParadeDB `pg_search` | AGPL-3.0 | yes | an extension inside the customer's database: the customer's AGPL duty; not offered by managed PostgreSQL services |
| the adapters' clients | Tarol's own code (HttpClient, `Utf8JsonWriter`) | — | — |

### 4.2 Query latency at 1,000,000 documents (p50 / p99 ms, a ranked page of 20)

The tenant filter is the normal case (user paths always carry a tenant); "all tenants" shows what the filter is worth.
Hits at 1M across all tenants: common 148,070, medium 27,622, rare 1,029 (in the 10 % tenant: 14,788 / 2,730 / 109).

| Query (tenant 10 %) | PG GIN(tsv) | PG GIN(tenant, tsv) | **PG GIN(tenant, tsv), rank first 1,000** | Meilisearch | Typesense | OpenSearch |
|---|---|---|---|---|---|---|
| common word | 211 / 276 | 48 / 64 | **16 / 26** | 3.3 / 6.8 | 16 / 29 | 7.0 / 9.2 |
| medium word | 42 / 53 | 9.1 / 13 | **5.4 / 7.5** | 2.0 / 6.4 | 8.1 / 12 | 3.0 / 4.0 |
| rare word | 2.9 / 5.7 | 1.6 / 2.2 | **1.2 / 2.1** | 1.6 / 7.0 | 4.8 / 7.5 | 2.1 / 2.8 |
| two words | 2.3 / 3.5 | 2.6 / 3.8 | — | 3.3 / 6.7 | 7.3 / 10 | 2.6 / 3.7 |
| prefix (4 letters) | 61 / 500 | 24 / 301 | **15 / 74** | 3.4 / 8.3 | 7.0 / 19 | 3.8 / 13 |
| typo | trigram (tenant, title): 47 / 145 | | | 1.6 / 4.9 | 7.5 / 11 | 5.9 / 9.8 |
| medium + counts per type | 82 / 113 | 19 / 23 | **11 / 15** (counts to 1,000) | 4.8 / 7.4 (to 1,000) | 8.8 / 13 | 3.7 / 5.7 |
| medium + snippets | 43 / 54 | 13 / 14 | **7.4 / 9.7** | 2.6 / 10 | 8.1 / 12 | 3.4 / 4.7 |

| Query (tenant 0.1 %, 1,000 documents) | PG GIN(tsv) | PG GIN(tenant, tsv) | Meilisearch | Typesense | OpenSearch |
|---|---|---|---|---|---|
| common word | 22 / 33 | 2.5 / 3.5 | 1.8 / 3.0 | 4.2 / 8.1 | 2.3 / 3.3 |
| medium word | 4.4 / 6.4 | 1.1 / 1.4 | 1.9 / 5.4 | 1.9 / 2.3 | 1.9 / 4.6 |
| typo | trigram (tenant, title): 7.1 / 14 | | 1.1 / 3.0 | 1.4 / 2.6 | 5.4 / 9.0 |

| Query (all tenants) | PG GIN(tsv) | PG GIN(tsv), rank first 1,000 | Meilisearch | Typesense | OpenSearch |
|---|---|---|---|---|---|
| common word | 250 / 278 | 4.4 / 6.3 | 3.1 / 6.6 | 36 / 69 | 7.8 / 16 |
| medium word | 60 / 77 | 11 / 21 | 4.2 / 17 | 7.2 / 16 | 3.1 / 4.6 |

At 10,000 and 100,000 documents every candidate answers a page below 10 ms p99 in a tenant, except PostgreSQL without the
tenant in the index on a common word at 100,000 (8.1 / 10.7 ms; all tenants 50 / 67 ms) and its prefix (5.6 / 26 ms).

### 4.3 Indexing, size, rebuild, memory

| | PG (P3) | Meilisearch | Typesense | OpenSearch |
|---|---|---|---|---|
| bulk load into a growing index, 100k → 1M | 12,308 docs/s (COPY, indexes maintained) | 5,671 docs/s | 7,698 docs/s | 21,727 docs/s |
| feeder writes at 1M (20,000 changed docs, batches of 500, each searchable on return) | 5,081 docs/s | 852 docs/s (a task per batch, waited) | 8,080 docs/s | 5,001 docs/s (with a refresh) |
| **rebuild of 1M** (a new version) | **60 s** (load 37 s + indexes 24 s) into a new table | 164 s | 126 s | 46 s |
| index size at 1M | 1,141 MB: table 839, GIN(tenant, tsv) 100, title trigram 113 (78 led by the tenant) | 3,542–4,374 MB on disk | 298 MB in memory, 1,307 MB on disk | 260 MB, 693 MB data directory |
| memory of the process at 1M | the database's (container 1.5 GB, `shared_buffers` 1 GB) | 831 MB | 385 MB | 2.8 GB (JVM, heap 2 GB) |
| operational weight | none: the store's database, three trusted extensions | one more server, its data volume and master key | one more server, its API key; the index lives in RAM | a JVM cluster: heap sizing, disk watermarks (§5), refresh |

### 4.4 Write → searchable, and the feed's lag (50 samples, p50 / p99 ms)

| | upsert visible | delete invisible | note |
|---|---|---|---|
| PG | 3.6 / 261 (the GIN pending list, §5) | in the feeder's transaction; the probe's delete by `id` alone scanned (44 ms), the design deletes by `(tenant, aggregate)` | synchronous: visible at commit |
| Meilisearch | 307 / 367 | 152 / 248 | an asynchronous task queue |
| Typesense | 7.5 / 70 | 6.3 / 27 | synchronous |
| OpenSearch | 1,007 / 1,044 (also with `refresh=wait_for`) | 1,011 / 1,036 | the 1 s refresh interval |

The feeder itself lags the commit like a projection: NOTIFY wake-up p50 3.6 / p99 6.1 ms (queries.md §6). Read-your-writes
(`Tarol-Read-After`) therefore costs ~4 ms on PostgreSQL and Typesense, ~0.3 s on Meilisearch and ~1 s on OpenSearch.

### 4.5 Erasure (feeder, JIT)

The tombstone takes 4.5 ms, then the participant finds the subject's streams and re-indexes them (`StreamsOf` + `Load` + replace)
in 18.6 ms (a cold first run). Until the participant ran, the blind token, and in the plaintext variant D3 the text, still
found the subject **[verified]**.

### 4.6 The feeder: rebuild through `Load<T>` (20,000 artworks = 20,128 loads, 60,000 documents; round 2)

| loaders | streams/s | documents/s |
|---|---|---|
| 1 | 1,569 | 4,676 |
| 4 | 3,315 | 9,880 |
| 8 | 3,758 | 11,203 |

One `Load<Artwork>` (stored state, sealed owner, key cached) takes p50 0.56 / p99 1.02 ms. A rebuild of 1,000,000 documents
through the feeder with 4 loaders therefore takes about 100 s (≈ 2× the bulk rebuild of §4.3), dominated by the loads.

### 4.7 PostgreSQL specifics

* A per-document `regconfig` column drives the generated `tsvector` (german row stemmed german); `unaccent()` itself is refused
  in a generated column (`42P17`, not immutable), a text-search configuration mapping through `unaccent` works **[verified]**.
* `pg_trgm`, `btree_gin` and `unaccent` are created by a **non-superuser that owns the database** (trusted extensions);
  `vector` is not in the stock image **[verified]** (§9 of the output).
* `websearch_to_tsquery` takes any input without a syntax error, including `'); DROP TABLE x; --` **[verified]**.

### 4.8 Native AOT

The probe (Npgsql store, the feeder, the three HTTP adapters) publishes with **0 IL warnings**; every contract check passes
natively against all four engines, and the feeder's 17 checks too **[verified]** (§8 of the output).

## 5. Found while probing

1. **Ranking every match is PostgreSQL's cost, not matching.** `ORDER BY ts_rank` reads every matching row: 211 ms for a common
   word in a 10 % tenant at 1M with GIN(tsv), 48 ms with the tenant in the GIN, **16 ms when only the first 1,000 matches are
   ranked**. Meilisearch caps the same way (`maxTotalHits` 1,000). A bounded rank is the design (GS-2).
2. **The tenant must lead the GIN** (`btree_gin`): 4–9× faster in a tenant than GIN(tsv) plus a filter, and smaller (100 vs 116 MB).
3. **The trigram index needs the tenant too**: the title trigram fallback for typos took 155 ms in a 10 % tenant, 47 ms led by
   the tenant. It runs only when full-text finds nothing.
4. **pg_trgm's default `word_similarity_threshold` (0.6) misses a one-letter transposition** ("harbuor"); 0.4 finds it.
5. **`unaccent` cannot be used in a generated column**, only through a text-search configuration.
6. **Engines differ on language**: Meilisearch does not stem (typo tolerance hides part of it); Typesense stems only with
   `stem: true`; OpenSearch does not fold accents without `asciifolding`. The contract suite therefore tests behaviour per
   configured language, not one universal expectation.
7. **Meilisearch counts stop at 1,000** and its deletes take ~150–250 ms through the task queue; the feeder must wait for the
   task before it moves the checkpoint.
8. **OpenSearch's read-your-writes is a second** (the refresh); `refresh=wait_for` waits the same second.
9. **OpenSearch blocked index creation on a host disk at 90 %** (its disk watermarks; the shared volume of a container): an
   operator setting, recorded in the probe README.
10. **Typesense's `typo_tokens_threshold=0` turns typo search off**, and OpenSearch ranked a body-only match first on a tiny
    corpus with `best_fields` (per-field IDF); `cross_fields` fixed it. Adapter defaults matter and belong in the contract suite.
11. **A key's tombstone alone does not erase the index**: a blind token or plaintext keeps finding the subject until the index's
    participant re-indexes. Search must be an `ErasureParticipant`.
12. **A GIN's pending list makes a rare write slow** (p99 261 ms for one upsert): `fastupdate` flushes in the writer. For a
    feeder this is a batch that takes longer, not a user request; the index keeps the default and the note says so.
13. **The probe's own mistakes**, fixed before measuring: a bash loop variable leaked into a helper (a test ran against a paused
    engine), a numeric score read as float crashed the PostgreSQL bench, and Meilisearch's typo tolerance found the previous lag
    sample's token. Each was caught by a check that failed, then rerun.

## 6. Design (sketch for 18A–18F; names final in the PRs)

The sketch assumes the recommended answers (§9); a different answer changes the named part only.

**User code** (Tarol.Search attributes; the Tarol.Search generator, shipped inside `Tarol.Search`, reads aggregates and entities
the way the Primitives generator does):

```csharp
[Searchable("Artwork")]                                    // a searchable aggregate; the label is the display name of its type
public partial class Artwork : AggregateRoot<Artwork>
{
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";               // weight B by default
    [PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; } // a blind token, never text (GS-8)
    [SearchAudience] public string Seller { get; private set; } = "";                 // optional row visibility (GS-7)
    [SearchLanguage] public string Language { get; private set; } = "en";             // optional (GS-10)
    public EntitySet<Edition> Editions { get; } = [];
}

public partial class Edition : Entity<Artwork, Edition>
{
    [Searchable(SearchWeight.A)] public string Label { get; private set; } = "";      // an entity hit, shown through its artwork
}
```

**Generated** (per searchable aggregate, an explicit implementation of `ISearchable<T>`):
`static SearchShape Shape` (types, fields, weights, exact fields, the fingerprint) and
`static void Documents(T aggregate, ISearchDocumentWriter into)`: the root document and one per entity with a searchable member
(type `artwork` / `artwork.edition`, id, aggregate id, the aggregate's title, text per weight A–D, exact values, audience,
language). Diagnostics (TSRCH…): a searchable member that is not `string`/`string?`/an enum/a value object with `ToString`;
`[Searchable]` on `[PersonalData]` without `Exact = true`; two `[SearchTitle]`; `[Searchable]` outside an aggregate or entity;
`[SearchAudience]` not a string or `Guid`.

**Registration and ports** (`Tarol.Search`):

```csharp
builder.AddSearch(search => search.Add<Artwork>().Add<Auction>(), version: 1)     // the index version (GS-5)
       .UseNpgsqlSearch();                                                        // or UseInMemorySearch(), UseOpenSearch(…)
app.MapTarolSearch("/search", policy: "search");                                   // GS-9
```

* `ISearchIndex` (the port each implementation provides): `Prepare`, `Checkpoint`, `Write(SearchBatch)` (documents per
  stream replaced, checkpoint guarded), `Replace`/`Remove` (erasure, retention), `Search(SearchRequest) → Result<SearchPage>`,
  `Documents(tenant, aggregates)` (export).
* `SearchHost : ProjectionHost` (needs §10.1): name `search`, the index version; per batch the streams of registered types, each
  loaded with `IAggregateReader.Load<T>` (`Actor.System("tarol.search")`, the stream's tenant), `Loaders` in parallel (default 4,
  §4.6), then `ISearchIndex.Write`.
* `ISearch` (what an app calls): `Search(SearchQuery, OperationContext)`; the tenant comes from the context, never the query.
* `SearchEraser : ErasureParticipant`: `Forget` re-indexes the subject's streams, `ForgetTenant` its tenant's, `Deleted` removes
  the documents of deleted streams; `Export` contributes the subject's documents (§10.3).
* `SearchTest`: the in-memory store, its feed, the in-memory index, `CatchUp()`, `Search(…)` — the contract suite runs on it.

**Npgsql** (`Tarol.Search.Npgsql`, P3): one table per index version

```sql
CREATE TABLE tarol.search_v1 (
  tenant uuid NOT NULL, type text NOT NULL, id uuid NOT NULL, aggregate uuid NOT NULL, version bigint NOT NULL,
  title text NOT NULL, a text, b text, c text, d text, exact text[] NOT NULL, audience text[] NOT NULL,
  lang regconfig NOT NULL DEFAULT 'tarol.simple',
  tsv tsvector GENERATED ALWAYS AS (setweight(to_tsvector(lang, coalesce(a, '')), 'A') || … 'D') STORED,
  PRIMARY KEY (tenant, type, id));
CREATE INDEX ON tarol.search_v1 USING gin (tenant, tsv);         -- btree_gin: the tenant leads (GS-6, §4.2)
CREATE INDEX ON tarol.search_v1 USING gin (title gin_trgm_ops);  CREATE INDEX ON tarol.search_v1 (tenant, aggregate);
```

The checkpoint is the row `('search', 1)` of `tarol.projections`; documents and checkpoint commit together. Text-search
configurations `tarol.german`, `tarol.english`, `tarol.simple` map words through `unaccent` (§4.7).


## 7. Earlier decisions that constrain search (not re-asked)

* **Tenant first** in every key and query (persistence.md §2, P-*); no query without a tenant on a user path.
* **A decision never reads a projection** (changes.md); search is a read model, never consulted by `Execute`.
* **The stored state is not an API** (R-8): the generator, not configuration, maps members to documents.
* **Versions are rebuilt next to the old one** (QR-3); **old versions are dropped explicitly** (O-5); **one runner per version** (O-8).
* **Meters never carry the tenant** (O-1).
* **Personal data leaves the process sealed** (DP-2); erasure reaches every copy (DP-5); retention deletes streams (DP-7).
* **Lists authorize by type** (`Read<T>`, R-5), row rules do not run on lists (QR-6).
* **No reflection on AOT paths**, generators inside their runtime package (conventions §5), implementations out of the meta-package (RL-4).
* **Docker and Testcontainers for integration tests** (D-15); **never nuget.org** (D-11).

## 8. Plan impact

Search is **step 18** (owner, 2026-11-18): a new row before Samples, so ArtFlow can use it; Samples becomes 19 and Templates 20.

## 9. Owner answers

Decision prefix **GS-** (taken: C-, CA-, D-, DP-, J-, M-, O-, P-, Q-, QR-, R-, RL-, S-, T-).

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| GS-0 | Where does search go in the plan? (rows 18 Samples and 19 Templates open; the KMS key store due after 17) | **A new row 18 before Samples** (recommended), so ArtFlow can use search |
| GS-1 | How does a user mark what is searchable? (candidate A probed: 17/17 feeder checks JIT and AOT; `Load` p50 0.56 ms; an entity hit shown through its aggregate) | **Attributes on aggregate and entity state members, documents generated from the loaded aggregate** (recommended): `[Searchable(Weight)]`, `[SearchTitle]`, `[SearchAudience]`, `[SearchLanguage]`; `ISearchable<T>.Documents` generated; entity documents typed `<aggregate>.<entity>` |
| GS-2 | Where does the default index live? (1M docs, tenant 10 %: GIN(tenant, tsv) ranking the first 1,000 matches: common 16/26, medium 5.4/7.5, rare 1.2/2.1, prefix 15/74, typo fallback 47/145 ms; rebuild 60 s, 1.1 GB, no extra server) | **PostgreSQL full-text search + trigram, bounded rank** (recommended): `Tarol.Search.Npgsql` in the store's database, per-row `regconfig`, GIN(tenant, tsv) through `btree_gin`, rank the first 1,000 matches, trigram on (tenant, title) when full-text finds nothing, counts up to 1,000; documents and checkpoint in one transaction |
| GS-3 | Which external engine adapters in this step? (licences §4.1; ops §4.3; OpenSearch 3–7 ms, 2.8 GB JVM, 1 s refresh; Typesense 8–16 ms, 385 MB, synchronous; Meilisearch 2–3 ms, counts capped, BUSL parts on `main`) | **OpenSearch and Typesense** (the recommendation was OpenSearch alone): two packages, `Tarol.Search.OpenSearch` and `Tarol.Search.Typesense`, each with its own HTTP client (no SDK), Testcontainers integration tests and the contract suite. Meilisearch is not built |
| GS-4 | How is the index fed? (a projection host over the feed: checkpoint in `tarol.projections`; 4 loaders 3,315 streams/s, ~100 s per 1M documents; needs `ProjectionHost` subclassable from other packages) | **A projection host, one load per changed stream** (recommended): `SearchHost : ProjectionHost`; Npgsql exactly once (one transaction), engines at least once (the engine's write acknowledged before the checkpoint commits; a document is the current state, so a repeat is harmless); opt-in read-after like lists |
| GS-5 | How are re-indexes versioned? (rebuild of 1M: ~60 s bulk, ~100 s through the feeder; a hit does not depend on the shape) | **An explicit version and a generated fingerprint; the old version answers while the new one builds** (recommended): `AddSearch(…, version: n)`; the host refuses to start when a version's stored fingerprint differs from the generated one; search reads the newest *current* version (no 503 unless none is current); old versions are dropped by `DropVersion` (O-5) |
| GS-6 | Tenancy? (GIN(tenant, tsv) 4–9× faster in a tenant than GIN(tsv) + filter: 9 vs 42 ms, 1.1 vs 4.4 ms; 100 vs 116 MB) | **A shared index, the tenant leading every key, GIN and filter** (recommended); engines filter on a tenant keyword field; no search without a tenant on a user path |
| GS-7 | Who sees which hits? (an index filter keeps pages and counts exact; a post-filter loads each hit, 0.56 ms, and shortens pages) | **Type grants and an optional audience, both in the index** (recommended): `CheckList<T>` per searchable type → the type filter (entities follow their aggregate); `[SearchAudience]` → "audience empty, or it overlaps the caller's `sub` and roles"; row rules do not run on search (like QR-6) |
| GS-8 | Personal data? (blind token: the exact value only; plaintext: any word, unsealed until the re-index; erasure = tombstone 4.5 ms + re-index 18.6 ms) | **A blind exact token only** (recommended): `[PersonalData, Searchable(Exact = true)]`, HMAC under the tenant's lookup key (`IKeyStore.BlindIndex`), never text; a TSRCH diagnostic for `[Searchable]` on `[PersonalData]` without `Exact`; `SearchEraser` re-indexes on erasure, removes on retention, contributes to export |
| GS-9 | Query API and HTTP? (1M, PG, tenant 10 %: counts +6 ms, snippets +2 ms, prefix 15/74 ms; `websearch_to_tsquery` never fails; search pages are varied queries) | **A runtime endpoint, a cursor, an ETag** (recommended): `MapTarolSearch("/search", policy)` with `q` (words AND, `"phrase"`, `-word`, `OR`), `types`, `prefix`, `counts`, `snippets`, `limit` (≤ 50), `after`; a typo fallback when nothing is found; an opaque cursor bound to the query, depth ≤ 1,000 hits; a weak ETag over (version, checkpoint, hits); `Tarol-Position`/`Tarol-Read-After`; no page cache, no SignalR hint |
| GS-10 | Languages? (PG: a per-row `regconfig` drives the `tsvector`, `unaccent` only through a configuration; engines differ, §3) | **Per document, with a tenant default** (recommended): a `[SearchLanguage]` member (ISO 639-1) or `SearchOptions.Languages` per tenant, default `simple`; PostgreSQL configurations `tarol.german`, `tarol.english`, `tarol.simple` through `unaccent`; the query's language from `?lang` or `Accept-Language`, mapped the same way; engines map de and en to their analysers |
| GS-11 | Operations? (the host brings `tarol.projections.*`, the lag gauge, the gate and `DropVersion`) | **The projection machinery plus a search meter** (recommended): meter `Tarol.Search` with `tarol.search.queries` (`tarol.outcome`), `tarol.search.duration`, `tarol.search.documents`; lag through the projections gauge; `ready` never waits for catch-up (O-2); an adapter's engine is a `ready` dependency; re-index = a version bump; drop through `DropVersion` |
| GS-12 | Packages and the meta-package? | **`Tarol.Search`, `Tarol.Search.Npgsql`, `Tarol.Search.OpenSearch`, `Tarol.Search.Typesense`; the endpoint in `Tarol.Changes.AspNetCore`** (recommended, next to the list endpoints, QR-7); the meta-package `Tarol` gains `Tarol.Search` only (RL-4) |

## 10. Changes against steps 7–17 (announced here, made in the PRs)

### 10.1 `Tarol.Queries`: a projection host other packages can derive from (18A)

`ProjectionHost`'s constructor and `Prepare`, `LoadCheckpoint`, `Write` become `protected` (today `private protected`), so
`Tarol.Search` can host its feeder with the same loop, gate, meters, backoff, `CatchUp` and `WaitFor`. Public API addition;
behaviour unchanged. `ProjectionsBuilder` gains nothing: search registers its host itself.

### 10.2 `Tarol.Changes.AspNetCore`: `MapTarolSearch` (18D)

A new endpoint next to the list endpoints; it uses `ListAccess`-style edge handling (tenant, `CheckList<T>` per type, cache
headers, problems), `Positions`, and a weak ETag. The package gains a reference to `Tarol.Search` (like `Tarol.Queries`, QR-7).

### 10.3 `Tarol.Persistence` / `Tarol.DataProtection`: the index in export (18B)

`IDataSubjects.Export` collects events and states today. The index holds derived copies only (the blind token is not personal
data in clear; the text of non-personal fields is not the subject's), so export **lists** the subject's documents (type, id,
aggregate) through an optional `ErasureParticipant` hook rather than adding their text. If the hook needs a public API change in
`Tarol.Persistence`, 18B lists it; otherwise `SearchEraser` exposes `Documents(tenant, aggregates)` and the guide says how an
app adds it to its export.

### 10.4 Meta-package

`Tarol` references `Tarol.Search` (18B).

## 11. What the implementation must do (in the decided order)

Stacked PRs, one feature each: **retarget each to `main` before its base is merged** (step 17: #68/#69 merged into their bases).
A fix goes into the lowest branch it belongs to and is merged upward.

### PR 18A `queries: a projection host other packages can derive from` (`Tarol.Queries`)
* §10.1; `eng/public-api.py update`; a test that a host defined outside `Tarol.Queries` (in the test assembly) runs, gates and
  catches up; migration note §15; skill `tarol-queries` names the extension point.

### PR 18B `search: Tarol.Search — attributes, generator, host, in-memory index` (new package)
* `src/Search/Tarol.Search/` per conventions §8, generator `Tarol.Search.Generators` shipped inside: `[Searchable]`,
  `[SearchTitle]`, `[SearchAudience]`, `[SearchLanguage]`, `SearchWeight`; `ISearchable<T>` (`Shape`, `Documents`); TSRCH
  diagnostics (§6) with `AnalyzerReleases`; snapshot, incrementality and diagnostic tests; generated code compiles under the
  consumer's language version.
* `ISearchIndex`, `SearchDocument`, `SearchBatch`, `SearchRequest`, `SearchPage`, `SearchHit`; `ISearch`; `SearchHost`
  (GS-4, fingerprint check GS-5); `InMemorySearchIndex` (word, prefix, typo by edit distance 1, counts, cursor); `SearchEraser`
  (GS-8); `SearchTest`; `AddSearch`, `UseInMemorySearch`; meter `Tarol.Search` (GS-11).
* The **contract suite** (`tests/Search/Shared/SearchContract.cs`, source-shared, not shipped): every check of §3, plus tenant
  isolation, the type filter, audience, the blind token, erasure (an erased subject is no longer found), retention, re-index on
  change, version fingerprint refusal, cursor pages without repeat, depth limit. The in-memory index passes it.
* `Tarol.Search.AotSmoke`, `Tarol.Search.PackageTests`, README, skill `tarol-search`; the meta-package (§10.4).

### PR 18C `search.npgsql: PostgreSQL full-text search` (`Tarol.Search.Npgsql`, new package)
* The table of §6 per version, GIN(tenant, tsv) via `btree_gin`, GIN(tenant, title gin_trgm_ops), (tenant, aggregate); the
  text-search configurations with `unaccent`; `CREATE EXTENSION IF NOT EXISTS` for the three trusted extensions, a clear error when
  they are missing; bounded rank (1,000), counts up to the bound, `ts_headline` snippets, prefix through `:*`, trigram fallback
  with `word_similarity_threshold` 0.4 set per statement; checkpoint in `tarol.projections` in the batch transaction.
* Integration tests on Testcontainers PostgreSQL: the contract suite, two runners (gate), a replay, an open transaction elsewhere
  (the feed's xmin), erasure and retention through `Tarol.DataProtection`, read-after. New timing tests bound by events, run 10–25
  times in Release alone.
* `Tarol.Search.Npgsql.AotSmoke` against `TAROL_PG`, README, skill `tarol-search-npgsql`.

### PR 18D `changes.aspnetcore: MapTarolSearch` (`Tarol.Changes.AspNetCore`)
* GS-9 and §10.2; HTTP tests (TestServer): type grants (403 for a requested type without `Read<T>`, silently dropped from the
  default "all types"), audience, tenant from the context, cursor, depth 400, ETag/304, read-after/stale, problems; OpenAPI entry;
  skill `tarol-endpoints` updated.

### PR 18E `search.opensearch: the OpenSearch adapter` (`Tarol.Search.OpenSearch`, new package)
* HttpClient + `Utf8JsonWriter`/`JsonDocument`, no SDK; index per version, keyword tenant/type/audience/exact, text per weight
  with per-language analysers (de, en) and `asciifolding`; `cross_fields` for words, `bool_prefix`, `fuzziness`; exact counts;
  checkpoint after `refresh=wait_for` when read-after is asked, otherwise after the bulk response; the engine in `ready`.
* Testcontainers `opensearchproject/opensearch:2.19.1` (security plugin off, disk threshold off in tests), the contract suite, AOT
  smoke (`TAROL_OPENSEARCH`), README, skill `tarol-search-opensearch`. No secret: a URL; credentials through the app's
  `HttpClient` configuration.

### PR 18F `search.typesense: the Typesense adapter` (`Tarol.Search.Typesense`, new package)
* As 18E with Typesense 28: a collection per version, `stem` per field, `typo_tokens_threshold` ≥ 1 for typos, synchronous
  imports; the API key from configuration (`TarolSearch:Typesense:ApiKey`), never in the repository; in CI the container's key is a
  throwaway value generated per run.
* Testcontainers `typesense/typesense:28.0`, the contract suite, AOT smoke (`TAROL_TYPESENSE`), README, skill `tarol-search-typesense`.

### PR 18G `docs: the search guide; ArtFlow searches auctions`
* `docs/search/README.md`; `samples/Sagas` makes auctions, bids (entities) and bidder profiles (personal: exact email) searchable;
  CHANGELOG; plan row 18 done; root README package table; architecture note status.

## 12. Skill evaluation checklist

For each skill, an agent given only the skill and the package must do these tasks correctly (checked before each PR is merged):

| Skill | Tasks | Expected API | Mistakes to avoid (and what they produce) |
|---|---|---|---|
| `tarol-search` (18B) | make an aggregate and its entity searchable; register the index; search in a test | `[Searchable]`, `[SearchTitle]`, `AddSearch(s => s.Add<T>(), version)`, `SearchTest`, `ISearch.Search` | `[Searchable]` on a `[PersonalData]` member without `Exact` (TSRCH diagnostic); changing weights without a version bump (the host refuses to start); querying without a tenant (impossible: the tenant comes from the context) |
| `tarol-search-npgsql` (18C) | use PostgreSQL search; add German; read-after | `UseNpgsqlSearch()`, `[SearchLanguage]`, `Tarol-Read-After` | expecting exact counts beyond 1,000; a database user without CREATE (missing extensions: the error names them) |
| `tarol-search-opensearch` / `-typesense` (18E/F) | point the index at the engine; run the contract suite | `UseOpenSearch(uri)`, `UseTypesense(uri, apiKey from configuration)` | putting the API key in code or appsettings committed to git; expecting OpenSearch read-after in milliseconds |
| `tarol-endpoints` (18D) | expose search over HTTP with a policy | `MapTarolSearch("/search", "search")` | expecting row rules to filter hits (they do not: use `[SearchAudience]`) |

## 13. Verification of this PR

Design only: this PR adds the two notes, the plan row, a CHANGELOG line and the probes; no package code changes. Checked on
this branch over `main` = `15c5706` (after #73) **[verified]**, SDK 11.0.100, 4 cores, Docker 29.10:

| Check | Result |
|---|---|
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet format Tarol.slnx --verify-no-changes` | no changes |
| `dotnet test --solution Tarol.slnx` (Docker) | **1,896 passed**, 0 failed, 0 skipped (the session prompt expected 1,864: #73 added 32 tests). The first run failed 56 integration tests in two assemblies at fixture start: Docker Hub answered 429 for `postgres:17`; the images were pulled from `mirror.gcr.io` and tagged, the rerun passed |
| `eng/aot-smoke.sh` (`TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`) | **24 apps**, 0 IL warnings, all checks pass |
| `dotnet pack -c Release` | **25 packages** |
| `eng/verify-packages.sh` | OK |
| the probe (`probes/search/all.sh`) | sections 1–11, JIT and Native AOT (0 IL warnings), [`output.txt`](probes/search/output.txt) |
| `main`'s CI on `15c5706` | `build`, `integration`, `aot-smoke`, CodeQL green; `v0.1.0-preview.1` released (2026-11-18) |

## 14. Open items

* Meilisearch: not built (GS-3); the port and the contract suite leave room for it. Re-read its licence before any adapter
  (`main` carries BUSL-1.1 parts, §4.1).
* Semantic search (`pgvector`): later, if a scenario asks (§2.10).
* Elasticsearch: the OpenSearch adapter's API family; not verified against Elasticsearch **[unverified]**.
* PostgreSQL: a query without hits pays for the typo fallback (§17.4); skip it when every word of a multi-word query exists.

## 15. Implementation: PR 18A (2026-11-18)

`queries: a projection host other packages can derive from` (§10.1).

### 15.1 Public API (`Tarol.Queries`, `eng/public-api.py update`)

* `ProjectionHost(string name, int version, IEventFeed feed, ProjectionOptions? options, TimeProvider? time, ILogger? logger,
  IFeedSignal? signal, IProjectionGate? gate, IMeterFactory? meters)`: `protected` (was `private protected`).
* `protected abstract ValueTask Prepare(CancellationToken)`, `ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken)`,
  `ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition through,
  ProjectionState state, CancellationToken)`: `protected` (were `private protected`), with XML docs.
* `override ProjectionHost.ExecuteAsync(CancellationToken)`: unchanged code, now in the API file because the class is derivable.

### 15.2 Deliberate differences from §10.1

* `Write`'s last position parameter is named **`through`** (was `to`): CA1716 reports `to` as a reserved word for overriders in
  Visual Basic. `ISqlProjectionStore.Write` already names it `through`.

### 15.3 Changes against steps 7–17

* Public API addition only; no behaviour change. `ProjectionHost<TRow>` and `SqlProjectionHost` override with `protected`.

### 15.4 Tests **[verified]**

`DerivedHostTests` (3): a host defined in the test assembly catches up through small batches and turns current; a refused write
counts a conflict and the host reads again; `WaitFor` returns once it passed a position. `Tarol.Queries.Tests` 53 passed.

### 15.5 Skill

`tarol-queries` gains "A host of your own (step 18)". Evaluation: given the skill, derive a host that counts events (expected:
`ProjectionHost` subclass with the three members; mistake to avoid: moving the checkpoint without the guard → a second runner
double-counts).

### 15.6 Suppressions and analyzer configuration

None.

### 15.7 Verification (SDK 11.0.100, 2026-11-18) **[verified]**

Build 0 warnings; format no changes; `dotnet test --solution Tarol.slnx` with Docker **1,899 passed** (+3); `eng/aot-smoke.sh`
**24 apps**, 0 IL warnings; pack 25 packages; `eng/verify-packages.sh` OK.

## 16. Implementation: PR 18B (2026-11-18)

`search: Tarol.Search — attributes, generator, host, in-memory index` (new package, generator inside).

### 16.1 Public API (`eng/public-api.py update`; 333 lines, most of them record members)

* Attributes: `SearchableAttribute` (class: label; property: `SearchWeight`, `Exact`), `SearchTitleAttribute`,
  `SearchAudienceAttribute`, `SearchLanguageAttribute`; `enum SearchWeight { A, B, C, D }`.
* Generated contract: `ISearchable<T>` (`static abstract SearchShape Shape`, `static abstract void Documents(T, ICollection<SearchDocument>)`);
  `SearchShape` (`Of<T>(label, fingerprint, entities)`), `SearchType(Type, Label, Aggregate)`, `SearchDocument` (type, id,
  aggregate, title; `A`–`D`, `Exact`, `Audience`, `Language`), `SearchDocuments` (helpers the generated code calls).
* The port: `ISearchIndex` (`Prepare`, `Checkpoint`, `Write`, `Replace`, `Search`, `Documents`), `SearchIndexDefinition`,
  `StreamDocuments`, `SearchBatch`, `SearchRequest`, `SearchHit`, `SearchPage` (with the answering `Version`, `Position` and
  `State`), `SearchLimits` (page 50, depth and rank 1,000, counts 1,000, text 256).
* `SearchText` (normalise, words, the web-search parser, validation, the cursor), `SearchClause`.
* `InMemorySearchIndex`; `SearchHost : ProjectionHost` (name `search`, `Reindex`, `Remove`, meter `Tarol.Search`);
  `SearchRegistry` (`Add<T>`, `Version`, `Loaders` = 4, `Projection`, `Definition()`); `SearchTokens` (blind tokens through
  `IKeyStore.BlindIndex`, lookup `tarol.search`); `ISearch`, `SearchQuery`, `SearchService`; `SearchEraser : ErasureParticipant`
  (and `Documents(tenant, subject)` for an export); `SearchTest`.
* Registration: `AddSearch(configure)`, `UseInMemorySearch()`.
* Diagnostics: TSRCH001 (declaration), TSRCH002 (member type), TSRCH003 (personal data as text), TSRCH004 (placement),
  TSRCH005 (warning: nothing searchable); `AnalyzerReleases.Unshipped.md`.

### 16.2 Deliberate differences from §6 and §11

* **The typo fallback matches the weight-A text, not the display title.** An entity's display title is its aggregate's (§6);
  matching it found an edition for a misspelt word that the correctly spelt word did not find (the AOT smoke caught it). Weight A
  holds the document's own titles and names. 18C indexes `(tenant, a gin_trgm_ops)` accordingly.
* `AddSearch(configure)` takes the version inside the registry (`s.Version = 2`) instead of a second argument.
* Without a key store, an exact value's token is an unkeyed SHA-256 of (tenant, normalised value): never text, but guessable for
  short values; the skill says to register a key store before marking personal members.
* Nested entities are `Entity<Root, Self>` in Tarol (TPRM012); their type is `<stream type>.<entity>.<child>`.
* The export lists the subject's documents through `SearchEraser.Documents(tenant, subject)`; `IDataSubjects.Export` is not
  changed (§10.3's fallback).

### 16.3 Changes against steps 7–17

* The meta-package `Tarol` references `Tarol.Search` (18 included packages; its consumer test expects `Tarol.Search.Generators`).

### 16.4 Tests **[verified]**

* `Tarol.Search.Tests` (36): the **contract suite** on the in-memory index (17: tenant first, other stream types, an entity through
  its aggregate, the type filter and exact counts, ranking and AND, phrases/exclusions/OR and hostile input, prefix, typo,
  snippets, cursor pages and refused cursors, re-index on change, audience, blind tokens, erasure after the participant (and not
  before), retention and export, the fingerprint guard, the checkpoint guard), `SearchText` (normalise, words, parse, cursor,
  limits), the registry (generated shape, documents with audience/language/exact, fingerprint independent of order),
  `AddSearch` in a container with the meter, and the missing-index message.
* `Tarol.Search.Generators.Tests` (18): snapshots (an aggregate with nested entities, an `EntityRef`, every member kind, audience
  from a string and from `List<Guid>`, a nested containing type), compile-clean with the Primitives generator and under C# 12,
  TSRCH001–TSRCH005, incrementality (an unrelated file and an apply body regenerate nothing, a new weight does).
* `Tarol.Search.AotSmoke`: 0 IL warnings, 13 checks natively. `Tarol.Search.PackageTests`: the packed package, its generator.

### 16.5 Skill evaluation checklist (`tarol-search`)

| Task | Expected API | Mistake to avoid |
|---|---|---|
| make an aggregate and an entity searchable | `[Searchable("Label")]` on the class, `[SearchTitle, Searchable(SearchWeight.A)]`, `[Searchable]` | marking the entity class (TSRCH001: only aggregates) |
| make a personal field findable | `[PersonalData, Searchable(Exact = true)]` | plain `[Searchable]` (TSRCH003) |
| register and search in a test | `SearchTest.Create(s => s.Add<T>())`, `Given`, `CatchUp`, `Find` | putting a tenant in `SearchQuery` (it has none) |
| change a weight | raise `s.Version` | forgetting it (start refused: "raise the version") |

### 16.6 Suppressions and analyzer configuration

None.

### 16.7 Verification (SDK 11.0.100, 2026-11-18) **[verified]**

Build 0 warnings; format no changes (after ordering one file's imports); `dotnet test --solution Tarol.slnx` with Docker **1,960
passed** (+61: 43 unit and contract, 18 generator); `eng/aot-smoke.sh` **25 apps**, 0 IL warnings; pack **26 packages**;
`eng/verify-packages.sh` OK (after dropping an unneeded import in the package consumer).

## 17. Implementation: PR 18C (2026-11-18)

`search.npgsql: PostgreSQL full-text search` (new package `Tarol.Search.Npgsql`).

### 17.1 Public API (`eng/public-api.py update`)

* `NpgsqlSearchIndex : ISearchIndex` (`Schema`, `Table`); `NpgsqlSearchOptions` (`Schema`, `Rank` = 1,000, `TypoThreshold` = 0.4);
  `NpgsqlSearchSchema` (`Prepare(schema, version)`, `TableOf`, `Configuration(schema, language)`, `Validate`);
  `UseNpgsqlSearch(options, dataSource)`.

### 17.2 Deliberate differences from §6 and §11

* **The tsquery is built from Tarol's own parser, not by `websearch_to_tsquery`.** With no query language the text must match
  each document in its own stemming, so the three configurations are OR-ed; OR-ing three `websearch_to_tsquery` results let an
  exclusion (`-morning`) leak through the configuration that did not stem it, and made the last word both exact and a prefix.
  Now every clause is its alternatives in every configuration (`phraseto_tsquery`, a prefix through `to_tsquery(… ':*')`),
  OR-ed; an excluded clause is negated across all of them; clauses are AND-ed. The in-memory index reads the same clauses. With
  a query language: that configuration and `simple`. Measured in §17.4.
* **Typo candidates come from trigrams, the decision from the shared rule.** `pg_trgm` treats a prefix as similar
  ("moun" ≈ "Mountain", 0.8); the index takes up to 200 trigram candidates on `(tenant, a)` and keeps those within one edit
  (`SearchText.WithinOneEdit`, added to 18B), so both indexes answer alike.
* The typo index is `GIN (tenant, a gin_trgm_ops)` (weight A), not the title (§16.2).
* Within the rank bound the candidate set is the first 1,000 matches the GIN returns, unordered: pages are stable while the index
  does not change; a write between two pages can change a page of a query with more than 1,000 matches (documented).
* The fingerprint is stored in `<schema>.search_versions`; `Prepare` runs under `pg_advisory_xact_lock(hashtext('<schema>.search'))`
  and, first, a database-wide `pg_advisory_xact_lock(hashtext('tarol.search.extensions'))`: two indexes in different schemas
  preparing at once raced on `CREATE EXTENSION IF NOT EXISTS` (23505 on `pg_extension`). The full suite found it (the test
  classes run in parallel on one database); a test now prepares four schemas at once in a fresh database.
* Text-search configurations `<schema>.german`, `.english`, `.simple` map words through the `unaccent` dictionary of whatever schema
  the extension lives in (looked up in `pg_extension`).

### 17.3 Tests **[verified]**

`Tarol.Search.Npgsql.IntegrationTests` (23, Testcontainers PostgreSQL 17): the **contract suite** (17) on the PostgreSQL index;
German and English documents stem in their language and fold accents (`Häusern` → `Häuser`, `gemalde` → `Gemälde`, `house` →
`houses`, `cafe` → `Café` and `Cafe`); the previous version answers while a new one builds (`Version` 1, `Current`); without a
current version the building one answers and says so; the extensions are created and no personal text is stored (one blind token);
**fed from the Npgsql store with sealing: an erased subject is no longer found after the re-index, retention removes the
documents**; a database user without CREATE on the database gets the error naming the three extensions.

### 17.4 Measurement of the built index **[verified]** (probe §12, [`output.txt`](probes/search/output.txt))

1,000,000 documents written through `NpgsqlSearchIndex.Replace` (batches of 2,000, one transaction each, indexes maintained):
**113 s, 8,821 docs/s**; table with indexes 1,364 MB. Queried through `Search` (round 2 of 2, page of 20, p50 / p99 ms):

| Query | tenant 10 %, language "en" | tenant 10 %, no language (3 OR-ed) | tenant 0.1 %, no language |
|---|---|---|---|
| common word | 19 / 31 | 20 / 33 | 4.5 / 9.3 |
| medium word | 7.8 / 12 | 7.0 / 7.7 | 1.5 / 1.9 |
| rare word | 3.3 / 5.4 | 1.6 / 2.1 | 1.0 / 11 |
| two words | 5.6 / 8.3 | 3.7 / 5.0 | 17 / 27 (no hit: the typo fallback runs) |
| prefix (4 letters) | 22 / 97 | 25 / 67 | 18 / 67 |
| typo (fallback) | 52 / 135 | 49 / 130 | 8.1 / 12 |
| medium + counts per type | 12 / 16 (counts stop at 1,001+) | 12 / 22 | 2.6 / 3.5 |
| medium + snippets | 7.3 / 14 | 7.2 / 8.4 | 1.9 / 2.2 |

* The built index answers like the probe's bounded GIN(tenant, tsv) (§4.2: common 16 / 26, medium 5.4 / 7.5): the three
  configurations OR-ed and the phrase-built tsquery cost nothing measurable.
* **A query without hits pays for the typo fallback** (17 ms in the small tenant for two words that never co-occur): the fallback
  runs only on a first page with no hits; an open item is to skip it for multi-word queries whose words all exist.
* The typo fallback finds fewer hits than raw trigrams (7 instead of 20 in the 10 % tenant): the shared one-edit rule drops the
  prefix-like candidates, as designed.

### 17.5 Skill evaluation checklist (`tarol-search-npgsql`)

| Task | Expected API | Mistake to avoid |
|---|---|---|
| run search on PostgreSQL | `UseNpgsqlReadModels()`, `AddSearch(…)`, `UseNpgsqlSearch()` | a data source without `EnableArrays()` |
| search German documents | `[SearchLanguage]` "de", `SearchQuery.Language = "de"` | expecting `unaccent()` in an own generated column (42P17) |
| a database user without rights | the error names `pg_trgm`, `btree_gin`, `unaccent` | granting superuser instead of creating the trusted extensions once |

### 17.7 Verification (SDK 11.0.100, 2026-11-19) **[verified]**

Build 0 warnings; format no changes; `dotnet test --solution Tarol.slnx` with Docker **1,984 passed** (+24); `eng/aot-smoke.sh`
**26 apps**, 0 IL warnings (`Tarol.Search.Npgsql.AotSmoke` with `TAROL_PG`); pack **27 packages**; `eng/verify-packages.sh` OK.
The first full run failed one test (the extension race, §17.2); fixed, then green, and the Npgsql tests ran 5 times in a row green.

### 17.6 Suppressions and analyzer configuration

None.

## 18. Implementation: PR 18D (2026-11-18)

`changes.aspnetcore: MapTarolSearch` (GS-7, GS-9; §10.2).

### 18.1 Public API

* `Tarol.Changes.AspNetCore`: `SearchEndpoints.MapTarolSearch(this IEndpointRouteBuilder, string pattern = "/search", string? policy = null)`
  → `IEndpointConventionBuilder`; `SearchEndpoints.BuildingCode` (`Search.Building`). The package references `Tarol.Search`.
* `Tarol.Search`: `ISearchAccess.CanRead<T>(CancellationToken)` and `SearchRegistry.Readable(ISearchAccess, CancellationToken)`:
  the readable document types (an entity type follows its aggregate).

### 18.2 Behaviour

* Query string: `q` (one, 1–256 characters), `types` (comma-separated), `prefix`, `counts`, `snippets` (`true`/`false`, else
  400), `limit` (1–50), `after`, `lang` (else the first `Accept-Language` code).
* Edge as for lists: the tenant from the claim, `Cache-Control: private, no-cache`, `Vary: Authorization`, problems; 401
  anonymous; 403 for a requested type without `Read<T>` (`CheckList<T>`), or when no type is readable; 400 for an unknown type.
* The caller's audience keys: the subject claim (`ChangeEndpointOptions.SubjectClaim`) and every role claim.
* `Tarol-Read-After` polls the index's **stored** checkpoint every 5 ms (timeout `Queries.ReadAfterTimeout`; `Tarol-Stale: true`
  after it). Not `SearchHost.WaitFor`: in an API process the host is not the runner (the gate, O-8), its own position never
  moves, and a read-after would always time out. Found while wiring the sample (18G).
* 503 with `Retry-After: 5` while the answering version is building (no version current yet).
* `200 { "items": [{ "type", "id", "aggregate", "title", "score", "snippet"? }], "counts"?, "countsCapped"?, "next" }`,
  `Tarol-Position`, a weak `ETag` over (version, position, next, hits, counts); a matching `If-None-Match` answers 304.

### 18.3 Deliberate differences from §11

* The endpoint is in `SearchEndpoints` (a static class with the extension), not on `ChangeEndpoints`; it uses
  `ChangeEndpoints.Edge` and `Problem` like the list entries.
* No OpenAPI entry yet (the generated entries describe themselves; the runtime endpoint has `WithName("TarolSearch")`). Open item.

### 18.4 Tests **[verified]**

`SearchEndpointTests` (8, TestServer, in-memory index): read-after finds the caller's new auction with position, cache headers,
a weak ETag and 304; another tenant sees nothing; a requested type without `Read<T>` is 403 and the default leaves it out of
the counts; a caller with no readable type is 403; 400 for an unknown type, limit 51, a bad flag, a missing `q`, a bad cursor
and a bad `Tarol-Read-After`; 401 anonymous; the audience is the subject and the roles (a memo for "bidder" is seen by bidders
only, one of "alice" by alice only); cursor pages and a prefix; 503 + `Retry-After` while building. The class ran 5 times in a
row green. The test domain's `Auction`, `Payout` and a new `Memo` are `[Searchable]`. `Tarol.Changes.AspNetCore.AotSmoke`
gains the search check: 0 IL warnings, natively green.

### 18.5 Skill evaluation checklist (`tarol-endpoints`)

| Task | Expected API | Mistake to avoid |
|---|---|---|
| expose search over HTTP | `app.MapTarolSearch("/search", "policy")` after `AddSearch` | mapping without `AddSecurity` (entries fail closed) |
| hide documents per user | `[SearchAudience]` on the aggregate | expecting row rules (`ChangeHandler<,>`) to filter hits |

### 18.6 Verification (SDK 11.0.100, 2026-11-19) **[verified]**

Build 0 warnings; format no changes; `dotnet test --solution Tarol.slnx` with Docker **2,004 passed** (after `main`'s #74;
an earlier run failed one Persistence test with `53300 too many clients`, a fixture limit fixed separately in #79);
`eng/aot-smoke.sh` **26 apps**, 0 IL warnings (the AspNetCore smoke's search check natively); pack 27 packages;
`eng/verify-packages.sh` OK.

## 19. Implementation: PR 18E (2026-11-18)

`search.opensearch: the OpenSearch adapter` (new package `Tarol.Search.OpenSearch`, GS-3).

### 19.1 Public API

* `Tarol.Search.OpenSearch`: `OpenSearchIndex : ISearchIndex` (`IndexOf(version)`, `Problem`), `OpenSearchOptions`
  (`IndexPrefix` = `tarol-search`, `WaitForRefresh`, `Shards` = 1, `Replicas` = 1), `UseOpenSearch(http, options)` (also a
  `ReadinessCheck` "search-opensearch").
* `Tarol.Search`: `ISearchCheckpoints` (`Prepare`, `Checkpoint`, `Move`, `NewestCurrent`), `InMemorySearchCheckpoints`.
* `Tarol.Search.Npgsql`: `NpgsqlSearchCheckpoints` (the rows (`search`, n) of `projections`), `UseNpgsqlSearchCheckpoints`.

### 19.2 Deliberate differences from §11

* The checkpoint port is new (§11 assumed the engine adapters write `projections` directly): an engine has no transaction with
  Tarol's, so the index writes the documents, then moves the checkpoint guarded; `Write` first compares the stored checkpoint
  and writes nothing for another runner's batch.
* Stale documents (a removed entity, a deleted stream) go with `_delete_by_query` (tenant, the batch's aggregates, not the new
  ids) after a refresh, because a delete-by-query sees only refreshed documents. A batch therefore costs one refresh.
* Readiness: `ReadinessCheck` is synchronous, so the adapter records its last failed connection and `ready` reports it until the
  next answer; no request per probe.
* No Elasticsearch claim: the API family is close, but nothing here ran against Elasticsearch **[unverified]**.
* The CI `aot-smoke` job gets no OpenSearch service; the smoke app skips its engine checks without `TAROL_OPENSEARCH` and was run
  natively against `opensearchproject/opensearch:2.19.1` here (§19.4). Adding the service is an owner's CI choice.

### 19.3 Tests **[verified]**

`Tarol.Search.OpenSearch.IntegrationTests` (20, Testcontainers OpenSearch 2.19.1 with the security plugin and the disk threshold
off, PostgreSQL 17 for the checkpoints): the **contract suite** (17); German/English stemming and folding; the checkpoint in
`projections` and a new version building while version 1 answers; an unreachable cluster is a readiness problem until it answers.
Finding while testing: a Testcontainers wait path with a query string (`/_cluster/health?wait_for_status=yellow`) never succeeded
and the fixture hung; the wait now asks `/`.

### 19.4 AOT **[verified]**

`Tarol.Search.OpenSearch.AotSmoke`: 0 IL warnings; with `TAROL_OPENSEARCH` (a local container) 9 checks pass natively. One check
first assumed an order between two title matches; BM25 puts the shorter title first, which the contract allows.

### 19.5 Skill evaluation checklist (`tarol-search-opensearch`)

| Task | Expected API | Mistake to avoid |
|---|---|---|
| run search on OpenSearch | `UseNpgsqlSearchCheckpoints()`, `UseOpenSearch(p => client)` | forgetting the checkpoints (start fails) |
| authenticate | the `HttpClient`'s handler or header from configuration | credentials in code or committed settings |
| read your own write | `WaitForRefresh = true` | expecting it within milliseconds by default |

### 19.6 Verification (SDK 11.0.100, 2026-11-19) **[verified]**

Build 0 warnings; `dotnet test --solution Tarol.slnx` with Docker **2,024 passed** (OpenSearch in Testcontainers);
`eng/aot-smoke.sh` **27 apps**, 0 IL warnings; pack 28 packages; `eng/verify-packages.sh` OK. The first run's format check
rejected the compact JSON writer lines in `OpenSearchIndex` (`WHITESPACE`); one statement per line since, and
`dotnet format Tarol.slnx --verify-no-changes` reports no changes.

## 20. Implementation: PR 18F (2026-11-18)

`search.typesense: the Typesense adapter` (new package `Tarol.Search.Typesense`, GS-3).

### 20.1 Public API

`TypesenseIndex : ISearchIndex` (`CollectionOf(version)`, `Problem`), `TypesenseOptions` (`CollectionPrefix` = `tarol_search`),
`UseTypesense(http, options)` (also a `ReadinessCheck` "search-typesense"), `TypesenseTarolBuilderExtensions.Client(server, apiKey)`.

### 20.2 Deliberate differences from §11

* **One `multi_search` per query.** Typesense has no `OR`, cannot OR a text match with a filter (the exact token), and tokenizes
  a query by the **first** field's locale: with a `de` field first, "gemalde" no longer matched the folded base field (probed by
  hand, §20.3). So every combination of `OR` alternatives (at most 8) × every locale group (base, German) and the exact-token
  lookup run as one `multi_search`, merged by score; counts over several searches come from the merged hits (fetched up to the
  count bound; `CountsCapped` when a search found more).
* **German documents are written to the folded base fields too**: the `de` locale keeps umlauts.
* **An empty audience is a boolean field** (`open`): Typesense cannot filter on an empty array.
* The fingerprint is a document of `<prefix>_versions` (collection metadata is not used).
* The API key comes from the caller's configuration; the tests and the smoke app generate or pass a throwaway key for a local
  container. CI needs no secret for the tests (the container takes any key); a production key is the owner's to configure.

### 20.3 Tests **[verified]**

`Tarol.Search.Typesense.IntegrationTests` (20, Testcontainers Typesense 28.0 with a per-run key, PostgreSQL 17 for the
checkpoints): the **contract suite** (17), German/English stemming and folding, a new version building while version 1 answers,
an unreachable server as a readiness problem. By hand against the image: `query_by=b_de,b` found 0 for "gemalde", `b,b_de` found 1.

### 20.4 AOT **[verified]**

`Tarol.Search.Typesense.AotSmoke`: 0 IL warnings; with `TAROL_TYPESENSE`/`TAROL_TYPESENSE_KEY` (a local container, a throwaway
key) 9 checks pass natively.

### 20.5 Skill evaluation checklist (`tarol-search-typesense`)

| Task | Expected API | Mistake to avoid |
|---|---|---|
| run search on Typesense | `UseNpgsqlSearchCheckpoints()`, `UseTypesense(_ => Client(server, key))` | the API key in code or committed settings |
| search German documents | `[SearchLanguage]` "de", `SearchQuery.Language = "de"` | expecting the `de` locale to fold umlauts |

## 21. Implementation: PR 18G (2026-11-18)

`docs: the search guide; ArtFlow searches auctions`.

* The guide [`docs/search/README.md`](../search/README.md): marking, the indexes, versions, the HTTP entry, `SearchTest`, the
  measured costs, operations.
* `samples/Sagas`: `Auction` (its seller, `[SearchTitle]`) and its `Bid` entities (their bidder) and `BidderProfile` (its login;
  the email as an exact blind token) are `[Searchable]`; the worker hosts the index (`AddSearch(…).UseNpgsqlSearch()`), the API
  registers the same and maps `GET /search`; operators (`tarol-admin`) may read bidder profiles. Both projects build with warnings
  as errors **[verified]**; the AppHost run is not part of this PR's checks **[unverified]**.
* Plan row 18 and the concept note's status updated; CHANGELOG.
