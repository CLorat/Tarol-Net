# Probes: Queries and read models (step 12 design)

Evidence for [docs/migration/queries.md](../../queries.md) and [docs/architecture/queries.md](../../../architecture/queries.md).
The probe is copied and adapted, never referenced, and stays out of `Tarol.slnx`. `*.csproj.txt` becomes a project file
in a work directory (`run.sh`); `TarolRoot` points at this repository, so the probe builds against **today's** packages
(`Tarol.Persistence(.Npgsql)` and the Primitives generator). Nothing in it is package code.

| File | What it is |
|---|---|
| `Queries/Projections.cs` | the would-be runtime: `IProjection<TRow>`, `IReadModelStore<TRow>`, `ProjectionHost<TRow>` (feed loop, checkpoint guard, per-stream version guard, `WaitFor`) |
| `Queries/AuctionList.cs` | user code (the row record with `When` methods), the "generated" switch, the query record `AuctionSearch`, `Cursor` (opaque keyset cursor bound to sort + filters), page ETag |
| `Queries/Stores.cs` | `InMemoryAuctionTable` (the `ProjectionTest` shape) and `NpgsqlAuctionTable` (typed query columns + the row as jsonb, the checkpoint row in the batch transaction, the "generated" query); `Listener` (LISTEN/NOTIFY) |
| `Queries/Domain.cs` | the write side: `Auction` with a stored state, `Lot` (another stream type) |
| `Queries/SelfTest.cs` | `selftest memory` (12 checks) and `selftest postgres` (21 checks) |
| `Queries/Bench.cs` | `bench seed`, `catchup`, `rebuild`, `commit`, `query`, `state`, `lag`, `ryw` |
| `inventory.md` | the old code (`CLorat/Tarol@6a62f81`): `Paginations`, the CQRS query side, file:line, the review findings re-checked |
| `output.txt` | the last `all.sh` run |

```bash
dockerd > /tmp/dockerd.log 2>&1 &                          # D-15; image postgres:17 (container tarol-probe-pg, port 55432)
docs/migration/probes/queries/all.sh /tmp/q                # writes output.txt; ALL_FROM=<n> / ALL_TO=<m> run sections n…m
docs/migration/probes/queries/run.sh /tmp/w Queries run -- selftest memory   # one run; PROBE_PG=<connection string> for another database
```

The NOTIFY of the feed is a statement trigger on `events` (`Lab.Store(notify: true)`), standing in for a store option; the
commit's feed position is read back after `Append` (`Lab.Commit`), standing in for `Append` returning it.
