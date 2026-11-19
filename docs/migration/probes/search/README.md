# Probes: global search (step 18 design)

Evidence for [docs/migration/search.md](../../search.md) and [docs/architecture/search.md](../../../architecture/search.md).
The probe is copied and adapted, never referenced, and stays out of `Tarol.slnx`. `*.csproj.txt` becomes a project file in a
work directory (`run.sh`); `TarolRoot` points at this repository, so the feeder prototype builds against **today's**
`Tarol.Persistence.Npgsql` and the Primitives generator. Nothing in it is package code.

| File | What it is |
|---|---|
| `Search/Corpus.cs` | the deterministic corpus: 20,000 pseudo-words with Zipf frequencies; document *i* is the same at every size; tenants (one 10 %, one 0.1 %, 48 others) and three types |
| `Search/Engine.cs` | the probe interface every candidate implements: `Reset`, `Load`, `Upsert`, `Delete`, `Search(Query)`, `Size` |
| `Search/Pg.cs` | PostgreSQL: a `tsvector` generated from a per-row `regconfig` (weights A/B), GIN(tsv) or GIN(tenant, tsv) through `btree_gin`, title trigram, bounded ranking (`Candidates`), `ts_headline`, counts per type |
| `Search/Http.cs`, `Meili.cs`, `Typesense.cs`, `OpenSearch.cs` | the external engines over HttpClient with hand-written JSON (no SDK) |
| `Search/SelfTest.cs` | the contract every candidate passes (11 checks), language checks, and PostgreSQL's language and syntax checks |
| `Search/Domain.cs`, `Search/Feeder.cs` | the feeder prototype: `Artwork` with an `EntitySet<Edition>` and a sealed owner, the hand-written "generated" document map, a feed loop with the checkpoint in the documents' transaction, blind tokens, erasure, retention, export (17 checks); `feeder bench` measures a rebuild through `Load<T>` |
| `Search/Bench.cs` | `bench <engine>` (load 10k → 100k → 1M, sizes, the query matrix, feeder writes, rebuild), `lag <engine>`, `bounded` (PostgreSQL at 1M: bounded ranking, the tenant-led trigram index) |
| `all.sh` | runs everything and appends to `output.txt`; resumable (one `.ok` marker per section; a section whose probe crashed is not marked) |
| `output.txt` | the run of 2026-11-18 (section 3 was rerun after a fix, its first run is noted in place) |

```bash
dockerd > /tmp/dockerd.log 2>&1 &                                  # D-15; images from mirror.gcr.io
setsid nohup docs/migration/probes/search/all.sh /tmp/sw > /tmp/search-all.log 2>&1 &   # FRESH=1 starts over
docs/migration/probes/search/run.sh /tmp/w Search run -- selftest pg                     # one run; PROBE_PG=<connection string>
```

Containers (ports chosen not to collide with other probes): `postgres:17` on 55433 (`shared_buffers=1GB`), Meilisearch v1.13
on 57700 (`MEILI_ENV=development`, no master key: local only), Typesense 28.0 on 58108 (API key `probe-local-only`, a
throwaway value for a local container, not a secret), OpenSearch 2.19.1 on 59200 (security plugin off, heap 2 GB). OpenSearch
runs with `cluster.routing.allocation.disk.threshold_enabled=false`: the container sees the host volume at 90 % and its flood-stage
watermark blocked index creation; this is a probe setting, not a recommendation. Each engine is benched alone: `all.sh` pauses the
others.
