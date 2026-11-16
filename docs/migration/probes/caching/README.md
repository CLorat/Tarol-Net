# Probes: Caching (step 13 design)

Evidence for [docs/migration/caching.md](../../caching.md) and [docs/architecture/caching.md](../../../architecture/caching.md).
The probe is copied and adapted, never referenced, and stays out of `Tarol.slnx`. `*.csproj.txt` becomes a project file in a
work directory (`run.sh`); `TarolRoot` points at this repository, so the probe builds against **today's** packages
(`Tarol.Persistence.Npgsql`, `Tarol.Queries.Npgsql`, `Tarol.Changes`, the Queries and Primitives generators). Nothing in it is
package code.

| File | What it is |
|---|---|
| `Caching/Caches.cs` | the would-be runtime: `CachingStore` (an aggregate and lookup cache as an `IAggregateStore` decorator: state bytes, a fresh instance per hit, write-through, eviction on conflict), `PageCache` (list pages validated by the checkpoint read, the checkpoint pushed, a tenant tag or a TTL), `CheckpointWatch` (LISTEN + poll fallback), `TaggingStore`/`TenantWatch` (per-tenant tags) |
| `Caching/Domain.cs` | `Auction` (stored state, a `lot` lookup), `Ledger` (`[LoadByReplay]`), the `auction-list` projection, `AuctionView`, the JSON context |
| `Caching/Hybrid.cs` | HybridCache with an optional L2 on Redis or Garnet; `ViewSerializer` (source-generated `IHybridCacheSerializer<T>`); its self-test and tier bench |
| `Caching/Http.cs` | Kestrel on loopback: output-cache checks (default policy, authenticated policies with and without vary, read-after, evict by tag); list and view endpoints plain and cached |
| `Caching/SelfTest.cs` | `selftest postgres` (22 checks), `local`/`redis`/`garnet` (HybridCache), `http` |
| `Caching/Bench.cs` | `bench seed`, `load`, `find`, `micro`, `list`, `hits`, `stale`, `conflict`, `http`, `l2` |
| `inventory.md` | the old code (`CLorat/Tarol@6a62f81`) and what Tarol-Net already caches, file:line |
| `output.txt` | the last `all.sh` run (sections 0–12) |

```bash
dockerd > /tmp/dockerd.log 2>&1 &     # postgres:17 (tarol-probe-pg, 55432), redis:8 (tarol-probe-redis, 56379),
                                      # ghcr.io/microsoft/garnet:latest (tarol-probe-garnet, 56380); all.sh starts them
docs/migration/probes/caching/all.sh /tmp/c        # resumable: sec-<n>.txt + .ok per section; ALL_REDO=<n> redoes one
docs/migration/probes/caching/run.sh /tmp/w Caching run -c Release -- selftest postgres   # one run; PROBE_PG / PROBE_REDIS / PROBE_GARNET override
```

The checkpoint NOTIFY is a row trigger on `projections` (`Lab.CheckpointTrigger`), standing in for the batch option of 13A.
"Two processes" are two cache instances over one store, each with its own connections.
