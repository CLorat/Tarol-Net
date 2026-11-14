# Session prompt: Tarol-Net, Step 6: Domain core (concept review and evaluation first)

## Task

Step 6 of the migration plan: **`Tarol.Primitives`** (aggregate root, entities, domain events, event routing,
rehydration, snapshots, value objects, invariants, and their generator), as **one pull request** (or one PR per part if
the concept review splits it, e.g. events/routing, then snapshots, then invariants). Work in three stages, like Steps 4
and 5 (`docs/migration/kernel.md`, `docs/migration/validations.md` and the concept notes in `docs/architecture/` are the
model):

1. **Concept review stage.** The old code is a seed, not a specification. It is the most developed family in Tarol
   (PR #24 already moved it to .NET 11: generated `closed` event bases, `[UnsafeAccessor]`, `JsonMetadataServices`), but
   it carries the 23 domain-core findings, and the framework around it (Changes, Persistence) never existed. Before
   porting anything, review the concept from first principles for an event-sourced DDD framework on .NET 11. Write down
   **new insights**, not only findings about the old code. Put the result into `docs/architecture/domain-core.md` (one
   concept note; split it if it gets long, e.g. `events.md`, `snapshots.md`) and reference it from the migration note.
   Present a short summary plus the questions that change the scope, then stop and ask.
2. **Evaluation stage.** Inventory the old code and its consumers, run the experiments below, then write
   `docs/migration/primitives.md`. It contains the review, the experiments with numbers, and the proposed API with user
   code per candidate. Then **stop and ask me** the design decisions with the question tool, one question per decision,
   each with your recommendation. Show call-site code in the question previews; I decide from those.
   If an answer is ambiguous or conflicts with another answer, ask again before implementing.
3. **Implementation stage.** Apply the agreed design, with tests, AOT smoke, README, skill and package verification
   (conventions §8). If a part is dropped or deferred to a later step, document what replaces it and update the plan.

Behaviour changes are allowed where they fix a review finding. List every one in the migration note and the PR.

Decided before the session: **child entities stay** (entities with their own events under the root, `EntitySet<T>`,
single children). Their complexity belongs in the library; user code must stay short and readable. Measure the user
code of every candidate against that.

## State at the start (verify, do not trust)

* `main` is at `6f44c0f` (PR #8 Validations merged). Run `git fetch origin`, branch from `origin/main`, never push to
  `main`.
* Migrated: `Tarol.StronglyTypedId`, `Tarol.Results`, `Tarol.Options`, `Tarol.Kernel` (→ Options, StronglyTypedId),
  `Tarol.Validations` (→ Results, Options, StronglyTypedId). 422 tests green.
* Obligations from earlier steps that land here (plan.md row 6, kernel.md §12, validations.md §14):
  * **Time** (kernel.md §12, time.md): delete `DomainClock` and the `Timestamp` field initializer; no clock read when
    raising or deserializing; business time is a payload field passed in as `now`; the store stamps `RecordedAt` per
    commit (step 8). TKRN001 reports `DateTime(Offset).Now/UtcNow`.
  * **Metadata** (kernel.md §12): event metadata slots typed with Kernel types (`Actor`, `Option<Id<ITenant>>`,
    `Id<ICorrelation>`, `Option<Id<ICausation>>`); decide what lives on the event, in the envelope, or only in the store
    (old D-2: envelope as columns, not in the payload).
  * **No `global using`** (options.md, kernel.md): the old `GlobalUsings.cs` imports Kernel, Options and StronglyTypedId;
    files write `using …;` where needed.
  * **Invariants** (validations.md V-3): `[Invariant]` methods returning `Result`, aggregated into a generated
    `CheckInvariants()`, run **after apply on the live path only**; a violation is the rule's `Error` (usually
    `Error.Conflict`); the instance is discarded (no rollback, F-9). Decide whether `Error.Internal` is ever right.
  * **Value objects** (validations.md §7.6, V-2, V-9): `Tarol.Validations` already generates value objects
    (`readonly partial record struct X([Rule] T Value)`, trusting constructor and JSON converter). Drop
    `[GenerateValidation]` from `Value`; decide whether `Primitives.Value` stays at all. Events and state that embed
    value objects use the trusting converters; JSON contexts in the same assembly need `ValueObjectJson.Options` (TVAL009).
  * **Replay safety** (validations.md S-3, V-8): repeat `tests/Validations/Tarol.Validations.Tests/ReplayTests.cs` on the
    real `AggregateRoot` (replay and snapshot: no rule runs, old data loads under stricter rules) and **decide the replay
    analyzer** (a rule call inside an apply/`On` method or a `[JsonConstructor]`).
  * **Permissions are not invariants** (security.md): nothing in the aggregate knows the caller; replay has no caller.
  * **Prerequisites are guards** (validations.md S-1, §5): behaviour methods return `Result` and take `now`; the change
    pipeline (step 7) needs a way to call them, so how a behaviour raises and returns must fit step 7.
  * **StronglyTypedId** (stronglytypedid.md): ids inherited through `AggregateRoot<T>` are registered by the id
    generator already; `Id<T>.Empty` is valid on inbound paths.

## Read first

* Tarol-Net: `AGENTS.md`, `docs/architecture/conventions.md` (§8 is the definition of done), `docs/migration/plan.md`
  (steps 6, 7, 8), `kernel.md` §12, `validations.md` (§4, §7.6, §9, §14), `results.md`, `options.md`, and the concept
  notes `docs/architecture/{time,identity-context,tracing,security,validation}.md`.
* Tarol (`CLorat/Tarol@6a62f81`, read only):
  * `src/Primitives/Primitives` (18 files: `AggregateRoot.cs`, `Entity.cs`, `EntitySet.cs`, `EntityRef.cs`,
    `DomainEvent.cs` with `DomainClock`, `EventEnvolope.cs`, `EventCodec.cs`, `AggregateSnapshort.cs`,
    `ISnapshottable.cs`, `IRegistry.cs`, `Value.cs`, `TarolPrimitivesJson.cs`, `ChangeAccessor/`, `GlobalUsings.cs`);
  * `gen/Primitives/Primitives.Generators` (≈ 2,500 lines: `Discovery.cs`, `Emitter.cs`, `CodecEmitter.cs`, `Models.cs`,
    TAROL001–TAROL019 except 010, and a `Tarol.Primitives.targets` file, which conventions forbid);
  * `tests/Primitives/*` (≈ 64 tests: unit, snapshot-replay equivalence, dispatch order, generator snapshots,
    diagnostics, incrementality, model purity; an AOT smoke app);
  * consumers in `examples/ArtFlow` (Auction with Bids, ArtWork, Bidder, Expert; finding 13: wrong event generic
    arguments) and `src/Sagas`, `src/Persistence` (what they call on the core);
  * reviews: `docs/review/domain-core.md` (F-1 … F-23, §6 event-sourcing invariants audit I-1 … I-10, §8 AOT, §11 .NET 11),
    `docs/review/net11-event-generation.md` (closed bases, SYSLIB1030, `[UnsafeAccessor]`, D-2/D-5/D-7 …),
    `docs/review/generators.md` (§2 diagnostics), `docs/review/ROADMAP.md` Phase 1 (exit criteria), and
    `docs/prompts/implement-event-generation-net11.md` (what PR #24 set out to do).

## Known state (verify, do not trust)

* Bypasses (F-1): `EntitySet` mutators and child constructors are reachable without an event; PR #24 started closing
  them with `[UnsafeAccessor]` — check how far.
* No delete cascade (F-2, decision D-5); a delete of an unknown entity is ignored while an update throws (F-7);
  `ReplayHistory` does not check `AggregateRootId` (F-14).
* Snapshots drop state silently and are opt-out (F-3, F-23: a collection of entities is copied as live objects); the
  generated snapshot records are invisible to a user's STJ context (F-11, SYSLIB1030 — the same "generators do not see
  each other" rule step 5 hit as V-9).
* `DomainEvent.Timestamp` is initialised from `DomainClock` on every construction **and deserialization** (F-12, K-2).
* Envelope members with `internal set` are silently skipped by a source-generated context in the user's assembly (F-10).
* A handler that throws mid-dispatch leaves the aggregate half-applied (F-9).
* Generator: per-type models? `Collect()`? `LocationModel` in models (F-15)? nested/generic types (F-6)? `record`
  aggregates emitted as `class` (F-18)?
* Findings handed over from step 5: validations 1 (no path from a change to `Raise`), 13 (ArtFlow event generic
  arguments), 14 (`Event => new(...)` re-creates on read), 15 (`IChange` interfaces). Decide which belong here and which
  to step 7.

## Concept review (stage 1)

Answer these in the note. Label every claim **[verified]**/**[unverified]**, and end with "New insights" and
"Open questions":
* What does a user of Tarol need from an aggregate? Behaviour methods that decide and raise events; state that changes
  only by applying events; child entities; one consistency boundary per aggregate; loading by replay or snapshot + tail.
* **Decide vs apply**: a behaviour validates its guards (`now` as a parameter, `Result` return) and raises; an apply
  (`On`/dispatch) only mutates. What enforces that apply never fails, never reads a clock, never validates? What happens
  when an apply throws (F-9)?
* **Events**: what is an event's identity, its payload vs its envelope (version, ids, `RecordedAt`, actor, tenant,
  correlation, causation), and what is stored where? Which of it does the domain need at all?
* **Child entities are in scope (owner decision, before stage 1).** They are complex in the library (routing, flat
  maps, cascade, snapshot completeness) but read well in user code, and that trade is wanted: keep the complexity in
  `Tarol.Primitives` and its generator, and keep the user's aggregate, entity and event declarations as short as the
  PR #24 syntax or shorter. Review *how* to build them (routing, ids, cascade, snapshots), not *whether*.
* **Snapshots**: opt-in or opt-out, completeness guarantees, schema version, and who owns the JSON contract (generator
  metadata via `JsonMetadataServices`, verified as AOT-clean in the old review).
* **Invariants and replay**: how the V-3 design fits the root and the entities; how replay safety is enforced (test,
  analyzer, or both).
* What do .NET 11 / C# 15 give us now: unions and `closed` hierarchies (exhaustive dispatch, CS8509 even in generated
  code), `[UnsafeAccessor]`, interceptors, `field`, extension members? What still does not work (generator chaining)?
* What must stay AOT-safe, incremental (generator) and testable (a behaviour test without a host: given events → when
  behaviour → then events or a violation)?

Questions that change the scope, for example:
* One package, or `Tarol.Primitives` plus a separate snapshots or codecs part?
* Does the event codec/registry (aliases, upcasters) belong here or in Persistence (step 8)?
* Is `Raise` callable from the step 7 pipeline (finding 1), and does step 6 already define the behaviour-method
  contract that Changes will call?

## Evaluation (stage 2; scratch project first, then as tests in the repo)

1. **Inventory and consumers.** Every use of the core in Tarol (ArtFlow, Sagas, Persistence, Changes, templates). What do
   steps 7, 8 and 9 need from it?
2. **Port and run the old tests** against the old code on SDK 11.0.x; record which still pass and which findings they
   miss. Turn `docs/review/probes` into regression tests where they reproduce a finding.
3. **Event model candidates**, each with user code, generated code and the JSON of one event:
   * (a) the PR #24 design (generated `closed` base per entity);
   * (b) a hand-written `closed` hierarchy without generated bases;
   * (c) whatever the concept review proposes.

   Measure: exhaustiveness diagnostics, raise/apply cost and allocations per event (BenchmarkDotNet 0.16.0-preview.2),
   replay of 10,000 events, AOT (0 IL warnings), JSON with a source-generated context **in the same assembly** (V-9 and
   F-10 apply).
4. **Bypass closure**: show for every mutating path that it is unreachable without an event (`[UnsafeAccessor]` vs
   analyzer), under AOT.
5. **Snapshots**: snapshot + tail equals full replay for every test model (property-based test, ROADMAP exit
   criterion); a state member not captured is a diagnostic.
6. **Invariants**: the V-3 shape on a root and on a child entity; cost when no invariant is declared; the failure path
   (discard).
7. **Replay test** on the real `AggregateRoot` (S-3) and a prototype of the replay analyzer, with its false positives.
8. **Generator** (whatever survives): equatable per-type models, no `Collect()` across types, no `Location` in models,
   full-name matching, hint names from the full symbol key, one diagnostic per misuse (renumber TAROL* into the
   `TPRM`-style family prefix used by the other packages, or justify keeping them), snapshot and incrementality tests
   with `tests/Shared/Tarol.Testing.Generators`, and **no `build/*.targets`**.
9. **Grouping.** Name every type and its namespace; dependencies on Kernel, Results, Options, StronglyTypedId,
   Validations.

## Decisions to ask me (at least)

1. Event model and user syntax (candidates a–c), and what is payload vs envelope vs store.
2. Child entities (in scope, see above): delete cascade rule (D-5), strict replay rules (F-7, F-14), single children
   (`EntityRef<T>`), and how user code declares and raises child events.
3. Snapshots: opt-in or opt-out, completeness diagnostics, schema version, JSON ownership.
4. Apply failures (F-9): discard, poison, or both.
5. Invariants: confirm V-3 on roots and entities; `Error.Conflict` vs `Error.Internal`.
6. Bypass enforcement: `[UnsafeAccessor]`, analyzer, or both.
7. Replay safety: test only, or also an analyzer (and its severity).
8. Package layout, namespaces, diagnostic ids; what happens to `Value`; which handed-over Changes findings land here.

## Definition of done (after my answers)

* `docs/architecture/domain-core.md` (or the split notes) with "New insights", the decisions taken, and what later
  steps (7, 8, 9, 13) must do because of them. Each later step gets a line in `docs/migration/plan.md`.
* Conventions §8 in full:
  * `src/Primitives/Tarol.Primitives/` (or the agreed home) with a README, a skill `skills/tarol-primitives/SKILL.md`
    (or one per topic), and its evaluation checklist in the note;
  * XML docs on every public member, no `NoWarn`, `IsAotCompatible`.
* Tests in `tests/Primitives/…`: unit tests, behaviour tests (given/when/then), snapshot + tail equivalence, generator
  snapshot, incrementality and diagnostic tests, a test for every fixed finding, and the replay-safety test on
  `AggregateRoot`.
* Generator bundled (D-10) with `AnalyzerReleases.*.md`. The runtime project references its own generator and the
  Options/Results (and, if used, Validations/Kernel) analyzers for its own code (analyzer project references do not flow).
* `Tarol.Primitives.AotSmoke` with 0 IL warnings, `Tarol.Primitives.PackageTests`, a green `eng/verify-packages.sh`, and
  a temporary traps file in the consumer that proves every diagnostic reaches a package consumer.
* The projects in `Tarol.slnx`; the plan status, CHANGELOG, README and `docs/primitives/README.md` updated.
* Green on SDK 11.0.x, **from a clean tree** (`rm -rf artifacts`), in CI order:
  * restore, build generators, `dotnet format --verify-no-changes`;
  * `dotnet build -c Release`;
  * `dotnet test --solution Tarol.slnx`;
  * `eng/aot-smoke.sh`;
  * `eng/verify-packages.sh`.
* The PR uses `.github/pull_request_template.md`. Open it only after I have answered and everything is green.

## Environment notes

* SDK 11.0.x: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0`, then
  `export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet` (BenchmarkDotNet needs `dotnet` on `PATH`). If the host is
  blocked, use the "Tarol" cloud environment.
* BenchmarkDotNet must be `0.16.0-preview.2` (already in `Directory.Packages.props`).
* Roslyn package 5.9 treats unions as preview: analyzer/generator tests need `LanguageVersion.Preview` (results.md §8).
* Lessons from steps 4 and 5:
  * **Generators do not see each other's output.** A `[JsonConverter]`, `[JsonSerializable]` or `[GeneratedRegex]` that
    one generator emits is invisible to the JSON/regex generators (validations.md §3.9, V-9; old F-11, SYSLIB1030). Plan
    the event and snapshot JSON around it (generator-emitted `JsonMetadataServices` metadata, or a resolver).
  * **CI runs `dotnet format` before the build**, after a "Build generators" step (PR #8). Code that compiles only with
    generated members needs that step; verify format from a clean tree.
  * Generator tests that need the STJ generator's members (`MyJson.Default`) must hand-write them in the test source; the
    STJ generator does not run in a test compilation.
  * `dotnet sln add` also adds referenced projects, into the wrong folder: edit `Tarol.slnx` by hand afterwards.
  * Extension members (e.g. `Id<T>.IsEmpty`) need a `using`; generated code without usings must use real members
    (`id.Value == Guid.Empty`).
  * `option.TryGetValue(out var x)` is CS0121; write `out T x` or `option is T x`.
  * xUnit v3's parallelism attribute is `Xunit.v3.Parallelization(MaxThreads = …)`; tests passing a
    `CancellationToken` must use `TestContext.Current.CancellationToken` (xUnit1051).
  * The test executables filter with `-method "*Name*"` / `-class "*Name*"`.
* PostgreSQL 16 is installed in the container (`/usr/lib/postgresql/16/bin`), if a probe needs a database.
* Licence: proprietary, owner-only. Do not change `LICENSE`, add open-source headers, or publish anywhere.
