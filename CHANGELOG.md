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
* `Tarol.Options` (step 3), a package of its own with no dependencies: `Option<T>`, a non-boxing `[Union]` struct of
  `T` and `None`, the same API for value and reference types, `default` = `None`; `Map`, `Bind`, `Filter`, `Match`,
  `GetValueOrDefault`, `GetValueOrThrow`; `T?.ToOption()`, `ToNullable()`; JSON = the value or `null` (the `T?` wire
  format); bundled analyzer TOPT001–TOPT004; agent skill `tarol-options`. `Tarol.Results` depends on it and adds
  `option.ToResult(violation)` and `Result<T>.ToOption()`.
* `Tarol.Kernel` (step 4): `Actor` (`Anonymous`, `User`, `Service`, `System` on behalf of a user; no personal data),
  `OperationContext` (actor, `Option<Id<ITenant>>`, `Id<ICorrelation>`, `Option<Id<ICausation>>`; `Start`, `CausedBy`,
  `AsSystem`) with a fixed AOT-safe JSON contract; bundled analyzer TKRN001 (system clock read, inject `TimeProvider`);
  agent skill `tarol-kernel`. Concept notes on time, identity, tracing and security in `docs/architecture/`.
* `Tarol.Validations` (step 5): rule attributes `Range`, `Length`, `Pattern` (timeout), `Email`, `NotEmpty`, `Count`
  declared once on `readonly partial record struct` value objects; the bundled generator writes `Create(…) : Result<T>`,
  `Check`, limit constants, a conversion to the field, comparisons, an invariant `ToString` and a trusting JSON converter
  (old events always load), and implements `IValidatable` on input records (`Validated()`: all field errors with input
  paths such as `Lines[1].Quantity`, 0 B when valid). `ValueObjectJson` for source-generated contexts in the same
  assembly; diagnostics TVAL001–TVAL009; agent skill `tarol-validations`. Concept note `docs/architecture/validation.md`.
* `Tarol.Primitives` (step 6, part A): event-sourced `AggregateRoot<T>` and child `Entity<TRoot, T>` (`EntitySet`,
  `EntityRef`, children with several owner types via `CreatedIn<TParent>`); events are pure-payload records deriving from
  the library's nested `Event`/`Created`/`Deleted` bases (`sealed record Repriced(decimal Price) : Event;`), routed with a
  `StreamEvent` (version, entity id, parent id) beside them; a bundled generator writes the dispatch (target, owners,
  root), identity maps, cascade, strict replay, `Rehydrate`, `EventTypeOf` and the invariant check; `[Invariant]` rules
  returning `Result` run at the hand-off (`AggregateChanges.Prepare`), never on replay; a throwing `On` handler poisons
  the instance; `RecordedEvent` for stored events with Kernel's `OperationContext`; `AggregateTest` given/when/then
  helper; diagnostics TPRM001–TPRM015 (including "state assigned outside an On handler" and "rule, throw, clock or Raise
  inside an On handler"); agent skill `tarol-primitives`. Concept note `docs/architecture/domain-core.md`.
* `Tarol.Primitives` (step 6, part B): stored state. Every aggregate gets generated `StateSchema`,
  `WriteState(Utf8JsonWriter, aggregate, options)` and `ReadState(id, ref Utf8JsonReader, options) : Option<T>` on
  `IAggregateRoot<T>`: every instance field of the root and its entities as queryable camelCase JSON (children nested by
  id, `id`/`version`/`schema` on the root, `null` written, enums by name), read back without running a handler,
  invariant or rule; `StateSchema` is a stable FNV-1a hash of the shape, `None` on another shape (the store replays).
  `AggregateState.Write/Read`, `AggregateTest.StoredStateMatchesReplay`, `[StateName]`, `[LoadByReplay]`; diagnostics
  TPRM024–TPRM027 (a field the state cannot hold, a state name used twice or reserved, a state attribute without effect,
  a `[Transient]` member filled in `On`).
* `Tarol.Primitives` (step 6, part C): lifecycles. `[Lifecycle]` on a state enum and `[To(...)]` on its members declare
  the allowed steps; the generator writes `<Enum>Lifecycle` (`Initial`, Mermaid `Diagram`, `IsEdge`, `IsTerminal`,
  `Edges`, extension members `CanTransitionTo(to) : bool` and `RequireTransitionTo(to) : Result` with
  `Error.Conflict("<Enum>.NotAllowed", "<From> -> <To>")`) and, inside the aggregate's dispatch, a live-only check of
  every step of a lifecycle field: a step no `[To]` allows, or a created root/entity not in the initial state, is
  returned by `AggregateChanges.Prepare` before the invariants as
  `Error.Internal("Lifecycle.ForbiddenStep", "<Type>.<Field>: <From> -> <To> by <Event>")`. Replay and state loads never
  check. `[Transition<TEnum>(from, to)]` labels an edge in the diagram; `LifecycleViolation`; the protected,
  generated-use-only `ApplyLive`/`ApplyRaised`/`ForbiddenStep` on `AggregateRoot<T>`; diagnostics TPRM016–TPRM023;
  suppressor TPRMSPR01 (CS8524 on complete switches over lifecycle enums). Concept note
  `docs/architecture/state-transitions.md`.

### Changed

* Validations: `[GenerateValidation]`, `ValidateValues()`, `ValidateInvariants()`, `[Invariant]` `bool` rules and
  `IPrerequisite`/`IPrerequisiteAsync` are gone. Rules live on value objects and inputs; invariants return to step 6 as
  `[Invariant]` rules returning `Result`; prerequisites are guard clauses returning `Result` with `now` as a parameter.
  Full list: docs/migration/validations.md §10.

* `Tarol.Results`: the typed `Result<T>.Value` is replaced by patterns, `TryGetValue` and `GetValueOrThrow()`
  (C# unions require `object? Value`); `Combine<T1,T2>` now combines failures like `Combine(params)`; `TapError` is
  `TapFailure`. Full list: docs/migration/results.md §7.
* Options: the C# 14 extension members on every `T?` (`IsSome`, `IsNone`, `Value`, `T.Some`, `T.None`, `Or`, `Match`,
  `Map`, `Bind`, `Filter`) are replaced by `Option<T>`. Full list: docs/migration/options.md §8.
* `Tarol.Results` analyzers: new TRES005 (ignored `TryGetValue`) and TRES006 (`ok ? value : default`) for
  `Result<T>`.
* Step 3 follow-up (after PR #5): `Option<T>` moved from `Tarol.Results` (namespace `Tarol.Results`) into
  `Tarol.Options` (namespace `Tarol.Options`); `Option<T>.ToResult` is now an extension in `Tarol.Results`; the Option
  rules TRES003 (Option part), TRES004, TRES005 (Option part) and TRES006 (Option part) are TOPT001–TOPT004.
  Mapping: docs/migration/options.md §7.1.

* Primitives (step 6): the envelope left the event (no `Version`/`Timestamp`/ids on event objects; `StreamEvent` beside
  it), `DomainClock` is gone, deletes are raised by the entity itself, the old generic event bases
  (`DomainEvent<R, X>`, `CreateDomainEvent`, `DeleteDomainEvent`) and the generated `closed` bases are replaced by the
  library's nested bases with the same user syntax. Full list: docs/migration/primitives.md §10.

### Removed

* Kernel (step 4): the ambient `Tarol.Clock`, the markers `IUser` and `ITrace`, `IUserContext`/`ContextUserType`
  and `Security.IAuthorizer` are not carried over (docs/migration/kernel.md §8). `ITenant` stays as a phantom type.
* Primitives (step 6): `Value`, `TarolPrimitivesJson`, `AggregateRootChangeAccessor`, `IDomainEvent*`, the writable
  `FindRef` lookups and `GlobalUsings.cs`; the event codec (`IEventCodec`, `EventColumns`, `IEventUpcaster`) moves to
  step 8 and snapshots become stored state in step 6 part B (docs/migration/primitives.md §10).

* `IdJsonConverterFactory` (see docs/migration/stronglytypedid.md).
* `Tarol.StronlgyTypedId.EntityFrameworkCore` is not carried over: EF Core is not Tarol's persistence provider.
* `Tarol.Results`: `IResult`/`IResult<T>`, `Violation.None`, `ViolationType` (replaced by `Invalid` + `ErrorType`),
  `Violation.Aggregate`, `Switch`, `MatchAsync`, `TryGetError` and every `Task`-based overload.
* The old `Tarol.Options` `T?` extensions are not carried over; neither are `Options.Persistence.Relational` (EF Core)
  and `Options.Serialization.Json`, which were already removed in CLorat/Tarol `e4722c4`.
