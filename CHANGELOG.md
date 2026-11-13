# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) and are taken from git tags (`v1.2.3`) by MinVer.
Generator diagnostics are tracked per generator in `AnalyzerReleases.Shipped.md` / `Unshipped.md`.

## [Unreleased]

### Added

* Repository foundation: layered build configuration, central package management, packaging conventions
  (README, agent skills and bundled generators in every runtime package), CI.
* `Tarol.StronglyTypedId`, migrated from CLorat/Tarol with: open generic JSON converter (no reflection fallback),
  allocation-free JSON reads, `Id<T>.New(TimeProvider)`, `IComparisonOperators`, generator registration of ids
  inherited from generic bases, diagnostic TSID001, agent skill `tarol-strongly-typed-id`.
* `Tarol.Results`, migrated from CLorat/Tarol and redesigned on C# 15 unions: `Result<T>` and `Result` are
  non-boxing `[Union]` structs (16/24/8 B instead of 48–64 B); failures are a closed `Violation` with exactly two
  kinds, `Invalid` (`FieldError`s) and `Error` (`ErrorType`, code, detail); `default` is `Error.Uninitialized`;
  `ValueTask` pipelines with `CancellationToken`; AOT-safe JSON envelope; bundled analyzers TRES001–TRES003;
  agent skill `tarol-results`.
* `Option<T>` in `Tarol.Results` (step 3; there is no `Tarol.Options` package): a non-boxing `[Union]` struct of
  `T` and `None`, the same API for value and reference types, `default` = `None`; `Map`, `Bind`, `Filter`, `Match`,
  `GetValueOrDefault`, `GetValueOrThrow`; `ToResult(violation)`, `Result<T>.ToOption()`, `T?.ToOption()`,
  `ToNullable()`; JSON = the value or `null` (the `T?` wire format); analyzers TRES004–TRES006 and TRES003 extended
  to `Option<T>`; agent skill `tarol-options`.

### Changed

* `Tarol.Results`: the typed `Result<T>.Value` is replaced by patterns, `TryGetValue` and `GetValueOrThrow()`
  (C# unions require `object? Value`); `Combine<T1,T2>` now combines failures like `Combine(params)`; `TapError` is
  `TapFailure`. Full list: docs/migration/results.md §7.
* Options: the C# 14 extension members on every `T?` (`IsSome`, `IsNone`, `Value`, `T.Some`, `T.None`, `Or`, `Match`,
  `Map`, `Bind`, `Filter`) are replaced by `Option<T>`. Full list: docs/migration/options.md §8.
* `Tarol.Results` analyzers: TRES005 (ignored `TryGetValue`) and TRES006 (`ok ? value : default`) also report
  `Result<T>`; TRES003 also reports `Option<T>.Value`.

### Removed

* `IdJsonConverterFactory` (see docs/migration/stronglytypedid.md).
* `Tarol.StronlgyTypedId.EntityFrameworkCore` is not carried over: EF Core is not Tarol's persistence provider.
* `Tarol.Results`: `IResult`/`IResult<T>`, `Violation.None`, `ViolationType` (replaced by `Invalid` + `ErrorType`),
  `Violation.Aggregate`, `Switch`, `MatchAsync`, `TryGetError` and every `Task`-based overload.
* `Tarol.Options` is not carried over as a package; neither are `Options.Persistence.Relational` (EF Core) and
  `Options.Serialization.Json`, which were already removed in CLorat/Tarol `e4722c4`.
