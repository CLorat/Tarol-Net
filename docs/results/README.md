# Results

Package guide: [Tarol.Results](../../src/Results/Tarol.Results/README.md).
Agent skill: [tarol-results](../../src/Results/Tarol.Results/skills/tarol-results/SKILL.md).
Design review, measurements and migration notes: [docs/migration/results.md](../migration/results.md).

## Design in five points

1. **No exceptions for expected failures.** An operation returns `Result<T>` (or `Result`). A failure is a
   `Violation`, which is either `Invalid` (a list of field errors) or `Error` (one light, non-fatal error with an
   `ErrorType` and a code). Nothing else: the hierarchy is `closed`, so new situations are new codes.
2. **A union, consumed by `switch`.** `Result<T>` is a hand-written C# 15 `[Union]` struct with the non-boxing
   access pattern. `r switch { T v => …, Invalid i => …, Error e => … }` is exhaustive (CS8509 otherwise), and
   one `Violation v => v` arm passes any failure on.
3. **Small and allocation-free.** The value plus one reference: 16 B for `Result<int>`, 24 B for `Result<Guid>`,
   8 B for `Result`, against 48–64 B in Tarol. Creating and matching a result allocates nothing.
4. **No invalid states.** `default` is the failure `Error.Uninitialized`, a success is never `null`, and
   `Combine`/`Sequence` follow one rule: field errors concatenate, the first `Error` wins.
5. **Fixed contracts, no infrastructure.** JSON uses an AOT-safe envelope (`{"value":…}` / `{"failure":{"kind":…}}`).
   `ToHttpStatusCode()` is a pure mapping. Problem details and `TypedResults` belong to a later web package, so
   this one has no dependencies. Bundled analyzers (TRES001–TRES003) catch what the compiler cannot.
