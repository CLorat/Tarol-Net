# Probes: changes (step 7 design)

Evaluation of [changes.md](../../../architecture/changes.md) and [the evaluation note](../../changes.md). Output of
every run: [`output.txt`](output.txt) (written by `all.sh`). SDK 11.0.100, linux-x64.

Run: `./all.sh <work-dir>` runs everything; `./run.sh <work-dir> <Project> <dotnet verb> [args]` runs one probe. Both
copy this folder to `<work-dir>/src`, rename `*.csproj.txt` to `*.csproj` and pass `-p:TarolRoot=<repository root>/`, so
the probes reference `src/` projects without being part of `Tarol.slnx`. Nothing here is referenced by the solution.

| Folder | What |
|---|---|
| `Core/` | prototype of `Tarol.Changes` (candidate A): `IChange<T>`, `ICreation<T>`, `ChangeContext`, `ChangePipeline`, `ChangeOptions`, `IChangePermission` hook, entry attributes (`[Endpoint]`, `[OnEvent<T>]`, `[Webhook]`, `[Consumes]`), `DeliveryPolicy`; plus the store port `IAggregateStore` and `InMemoryAggregateStore` (decided to live in `Tarol.Persistence`, C-6) |
| `Gen/` | prototype generator: `Target` per change, TCHG001, typed `Dispatch(object)`, reaction table (`ReactionsTo`, `React`), and — when ASP.NET Core is referenced — HTTP and webhook entries as `RequestDelegate`s (`ProbeEndpointStyle=request-delegate`) or as naive `MapPost(pattern, lambda)` (`delegate`) |
| `App/` | ArtFlow (`Auction` with `Bid` children and lifecycles, `Payout`) and the five changes as user code; console scenarios: 400/404/409, tenant 404, system actor, `TimeProvider`, reaction with causation and redelivery, concurrency, idempotency keys, delivery policy, typed dispatcher; JIT and Native AOT |
| `Web/` | the same changes behind ASP.NET Core (`CreateSlimBuilder`): generated endpoints, a header auth scheme, problem details, `Idempotency-Key`, `If-Match`, a signed webhook; `--selftest` drives it over HTTP; Native AOT; `-p:ProbeEndpointStyle=delegate` builds the naive variants for comparison |
| `GenVisibility/` | candidate B's blocker: an input record emitted by another generator (source output) is invisible to the Validations and JSON generators |
| `GenTest/` | generator driver: output, incrementality (unrelated file, method-body edit), TCHG001 |
| `Bench/` | BenchmarkDotNet (`--job short --inProcess`): pipeline and generated dispatch against a hand-written baseline, same store cost |

Where the probe and the owner's decisions differ, the decisions win (see the evaluation note, "Owner answers"):
the probe's pipeline **retries a version conflict** with a fresh load (C-7 decided: never retry), maps a guard
**Conflict in a reaction to Acknowledge** (C-9 decided: retry with backoff, then dead-letter), and lets the **caller
choose a new aggregate's id** (C-10 decided: the factory generates it). The measurements of the shared path are
unaffected; the retry and caller-chosen-id scenarios document the rejected alternatives.
