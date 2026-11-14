# Probes: state transitions (stage 2)

Evaluation of [state-transitions.md](../../../architecture/state-transitions.md) and
[the evaluation note](../../state-transitions.md). Output of every run: [`output.txt`](output.txt).
SDK 11.0.100. Rename `*.csproj.txt` to `*.csproj`; paths are relative to the repository root.

| Folder | What |
|---|---|
| `facts/` | stage 1: enum/`closed`/`union` exhaustiveness and JSON facts |
| `Core/` | `Tarol.Primitives` at `8ead467` with `AggregateRoot.cs.patch` (live-only `ApplyLive` hook, `ForbiddenStep`, `Prepare` returns the first forbidden step) and `Lifecycle.cs` (`[Lifecycle]`, `[To]`, `LifecycleViolation`). Copy `src/Primitives/Tarol.Primitives/*.cs` and `Testing/` in, apply the patch |
| `Gen/` | prototype generator for `[Lifecycle]` enums (edge table, `CanTransitionTo`/`RequireTransitionTo` extension members, `Initial`, `IsTerminal`, Mermaid `Diagram`, TPRM020–024) and the CS8524 suppressor |
| `App/` | ArtFlow auction as candidates (a) enum + lifecycle, (b) `closed` hierarchy, (c) baseline; `*.Lifecycle.g.cs` are hand-written stand-ins for what the Primitives generator would add; scenarios, allocations, JSON; Native AOT publish |
| `Bench/` | BenchmarkDotNet: one full flow (8 events + `Prepare`) per candidate, and the guard helpers |
| `GenTest/` | generator driver: generated source, diagnostics on misuse, incrementality steps |
