# Probe: stored state (step 6 part B, stage 1)

Evaluation of the stored state in [primitives.md §16](../../../primitives.md#16-part-b-stored-state-stage-1-evaluation-2026-11-14). Output of every run:
[`output.txt`](output.txt). SDK 11.0.100. Rename `*.csproj.txt` to `*.csproj` and build from a scratch copy with
`-p:TarolNetRepo=<clone>`; the probe uses the generator **prototype** of this branch (`src/Primitives`), not a copy.

| Folder | What |
|---|---|
| `App/` | `Domain.cs`: a Gallery with every member kind (`Option<T>`, `Id<T>`, enum, `T?`, `List<T>`, nested record, value object, plain and `readonly` fields, get-only and `[StateName]` properties, `[Transient]` cache), three levels, an `EntityRef`, a child with two owner types, and a `[LoadByReplay]` aggregate. `Program.cs`: 28 checks (state == replay, round trip, jsonb key order, continuing on a loaded instance, cascade on a loaded tree, schema mismatch, malformed states, no rule runs). `MetadataVariant.cs`: the same writer as hand-written `JsonMetadataServices` type infos (write side only). Run on JIT and as a Native AOT publish |
| `Bench/` | BenchmarkDotNet 0.16.0-preview.2, ShortRun, in process: write, metadata write, read, replay in memory, replay with event JSON, for 1/10/100 entities |
