# Probes: sagas and reactions (step 9 design)

Evaluation of [sagas.md](../../../architecture/sagas.md) and [the evaluation note](../../sagas.md). Output of every run:
[`output.txt`](output.txt) (written by `all.sh`) and [`Claim/output.txt`](Claim/output.txt) (written by `Claim/claims.sh`).
SDK 11.0.100, PostgreSQL 17.12 in Docker (the Aspire resource: `postgres:18.3`), linux-x64, 4 cores.

Run: start Docker (`dockerd > /tmp/dockerd.log 2>&1 &`), then `./all.sh <work-dir>` runs everything against a fresh
`postgres:17` container on port 55432; `./run.sh <work-dir> <Project> <dotnet verb> [args]` runs one probe (set `PROBE_PG`
to a connection string; the default is `localhost:55432`, user `postgres`, password `probe`). Both copy this folder to
`<work-dir>/src`, rename `*.csproj.txt` to `*.csproj` and pass `-p:TarolRoot=<repository root>/`, so the probes reference
`src/` projects without being part of `Tarol.slnx`.

| Folder | What |
|---|---|
| `Core/` | prototype of the decided runtime: `[OnEvent<T>]` (`Name`, `DueAt`), `ReactionSet` (what the generator emits per assembly, `Combine` across assemblies, the reaction context and key), the outbox port `IReactionOutbox` with the SQL of both dead-letter candidates (DL-A column, DL-B table) on the step 8 tables, the `traceparent` in claims, scheduled reactions exempt from the head rule (T-2), `InMemoryOutbox`, `ReactionWorker` with the C-9 `DeliveryPolicy`, `OutboxWake` (LISTEN/NOTIFY), `ReactionHost` (`BackgroundService` with the key purge) |
| `App/` | ArtFlow: `Auction` with `Bid` children, `Payout`, the settlement saga as an aggregate (candidate SA-A); the reaction changes (`MarkPayoutDue`, `StartSettlement` load-or-create, `ConfirmSettlementPayment`, `ExpireSettlement` with a due time, `ReopenAuction` as compensation, `AcknowledgeNote` that can fail forever); `Generated.cs` = the code the generator would emit, written by hand; `SagaB.cs` = the JSON risk of candidate SA-B. Modes: `functional` (64 checks, JIT and Native AOT), `crash-child` (a real process that dies after the reaction's commit), `throughput` (1/4/16 workers, poll vs LISTEN/NOTIFY, NOTIFY's commit cost), `serve` (the hosted worker with OpenTelemetry, what the AppHost starts) |
| `Aspire/` | an Aspire 13.6.5 AppHost on SDK 11: a PostgreSQL resource, the worker as a project or as the Native AOT binary (`PROBE_WORKER_NATIVE`), the dashboard; `apphost.sh` reads the result from the resource database and the spans from the dashboard's telemetry API |
| `Claim/` | `EXPLAIN ANALYZE` of the claim on five backlog shapes: the shipped O-2 anti-join vs a maintained `head` flag with a partial index (candidate H) |

Where the probe and the owner's decisions differ, the decisions win ([evaluation note](../../sagas.md) §11): the probe
keeps both dead-letter designs selectable (decided: DL-B, S-5) and the anti-join claim (decided: H, S-8); it writes
`due_at` with a trigger that reads the event's `dueAt` (decided: the store takes it from the generated schedule at
commit, S-9); the in-memory outbox derives its rows from the feed (decided: `InMemoryAggregateStore.Append` writes them);
`OnEventAttribute` lives in the probe's `Tarol.Sagas` namespace (decided: `Tarol.Changes`, S-6).
