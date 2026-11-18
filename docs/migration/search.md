# Global search: evaluation (step 18 design)

Evaluation of [search.md](../architecture/search.md): what Tarol already has that search builds on, what is searchable and how
a user marks it, where the index lives (PostgreSQL full-text search, `pg_trgm`, their combination, Meilisearch, Typesense,
OpenSearch; Elasticsearch, ParadeDB and `pgvector` reasoned), how the index is fed, tenancy, authorization, personal data,
the query API and HTTP, languages, operations, the package split, measurements, the owner's answers, and what each PR must do.
**Design only**: this PR changes no package code. Probe and output: [`probes/search/`](probes/search/),
[`output.txt`](probes/search/output.txt). Labels as in the [conventions](../architecture/conventions.md). There is no old code:
search is new in Tarol-Net (plan row 18, added 2026-11-18).

## 0. Summary

⟨summary⟩

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

⟨measurements⟩

## 5. Found while probing

⟨found⟩

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
CREATE INDEX ON tarol.search_v1 USING gin (⟨tsv index⟩);          -- §4.2 decides
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

## 10. Changes against steps 7–17 (announced here, made in the PRs)

⟨changes⟩

## 11. What the implementation must do (in the decided order)

⟨prs⟩

## 12. Skill evaluation checklist

⟨skills⟩

## 13. Verification of this PR

⟨verification⟩
