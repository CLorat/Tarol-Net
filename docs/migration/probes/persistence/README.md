# Probes: persistence (step 8 design)

Evaluation of [persistence.md](../../../architecture/persistence.md) and [the evaluation note](../../persistence.md)
§8–§17. Output of every run: [`output.txt`](output.txt) (written by `all.sh`). SDK 11.0.100, PostgreSQL 17.12 in Docker,
linux-x64.

Run: start Docker (`dockerd > /tmp/dockerd.log 2>&1 &`), then `./all.sh <work-dir>` runs everything against a fresh
`postgres:17` container on port 55432; `./run.sh <work-dir> <Project> <dotnet verb> [args]` runs one probe (set
`PROBE_PG` to a connection string; the default is `localhost:5432`, user `postgres`, password `probe`). Both copy this
folder to `<work-dir>/src`, rename `*.csproj.txt` to `*.csproj` and pass `-p:TarolRoot=<repository root>/`, so the probes
reference `src/` projects without being part of `Tarol.slnx`.

| Folder | What |
|---|---|
| `Core/` | prototype of the decided design: `EventCodec` (candidate K-A: alias table + the user's context, upcasters on `JsonObject`), the schema (S-A), `NpgsqlAggregateStore` implementing the real `IAggregateStore` with the guards G-1 … G-4, lookups L-A and L-B, idempotency keys, outbox rows from a `ReactionTable`, `RebuildStates<T>`, `ReadAll` (naive and safe); `OutboxClaims` (O-1, O-2) and `Retention.PurgeKeys` |
| `App/` | ArtFlow (`Auction` with `Bid` children and a value object `Title`, `[LoadByReplay] Ledger`), the hand-written alias tables the generator would emit, an upcaster; `functional` mode: 55 checks (contract, metadata, lookups, keys, outbox rows, schema change, upcaster, feed gaps, outbox claims); `contention` mode: 1/4/16/64 writers on one stream per guard. JIT and Native AOT |
| `Contract/` | one xUnit v3 store contract suite (abstract class, fresh tenant per test) run against `InMemoryAggregateStore` and the Npgsql store, PostgreSQL from Testcontainers (assembly fixture) |
| `Bench/` | BenchmarkDotNet (`--job short --inProcess`): load from state vs replay (10 and 100 events), in memory vs Npgsql; load + one event + append |

Where the probe and the owner's decisions differ, the decisions win (evaluation note §15): the probe passes lookup
definitions as `KeyLookup`/`StateLookup` records (decided: `DefineLookup` shared by both stores, L-A only), writes the
alias tables by hand (decided: generated `T.Events`, `T.StreamType`), and keeps all four guards selectable (decided:
G-1). The probe's corrupt-state path is not exercised (decided P-12: fall back to replay).
