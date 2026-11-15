# Migration plan: CLorat/Tarol → CLorat/Tarol-Net

Tarol-Net is a fresh repository. Instead of restructuring `CLorat/Tarol` in place, each package family
is moved over in its own step, reviewed against .NET 11 / C# 15, and only arrives once it meets the
[conventions](../architecture/conventions.md) (definition of done: §8 there).

Source of truth for the old code: `CLorat/Tarol@main` = `6a62f81` (after PR #24, .NET 11 domain core).
Review material that stays authoritative until a family is migrated: `CLorat/Tarol` `docs/review/*`.

## Status

| # | Step | Packages in Tarol-Net | Status |
|---|---|---|---|
| 0 | Foundation | — | done: layout, layered props, CPM, `.editorconfig`, `global.json`, slnx, packaging targets and scripts, shared generator test harness, agent files, CI, Dependabot |
| 1 | StronglyTypedId | `Tarol.StronglyTypedId` (+ generator) | done: 79 tests, AOT smoke 0 IL warnings, package verified; see [stronglytypedid.md](stronglytypedid.md) |
| 2 | Results | `Tarol.Results` (+ analyzers) | done: non-boxing C# 15 `[Union]` shape (D-9 measured), `Invalid` \| `Error` violations, 141 tests, AOT smoke 0 IL warnings, package verified; see [results.md](results.md) |
| 3 | Options | `Tarol.Options` (+ analyzer); `Tarol.Results` depends on it (owner revised Q-6 after PR #5) | done: `[Union]` struct `T` \| `None` (candidates A–E measured), analyzer TOPT001–TOPT004, AOT smoke 0 IL warnings, both packages verified with their own skill; see [options.md](options.md) |
| 4 | Kernel | `Tarol.Kernel` (+ analyzer): `Actor`, `OperationContext`, `Id<ITenant/ICorrelation/ICausation>`; no clock (inject `TimeProvider`); Security designed, built in step 10 (D-7) | done: concept notes `docs/architecture/{time,identity-context,tracing,security}.md`, clock isolation proved, TKRN001, 47 tests, AOT smoke 0 IL warnings, package verified; see [kernel.md](kernel.md) |
| 5 | Validations | `Tarol.Validations` (+ generator and analyzer TVAL001–TVAL009); rules once on struct value objects, `IValidatable` inputs, `ValueObjectJson`; invariants and prerequisites designed for steps 6–7 | done: concept note [validation.md](../architecture/validation.md), 83 tests, AOT smoke 0 IL warnings, package verified; see [validations.md](validations.md) |
| 6 | Domain core | `Tarol.Primitives` (+ generator, analyzer TPRM); concept note [domain-core.md](../architecture/domain-core.md), evaluation [primitives.md](primitives.md); three PRs: (A) events (library-nested bases, pure payload, `StreamEvent`), children, invariants at the hand-off, discard + poison, analyzers, test helper; (B) stored state (current state per commit, queryable JSON); (C) lifecycles ([state-transitions.md](../architecture/state-transitions.md)). Delete `DomainClock` and the `Timestamp` initializer, metadata slots typed with Kernel types, no `global using` (kernel.md §12); `[Invariant]` rules returning `Result` + generated `CheckInvariants()` after apply, live only; drop `[GenerateValidation]` from `Value`; replay test on `AggregateRoot` and the replay analyzer decision (validations.md §14) | **done**: part A (events, children, invariants, failure rules, analyzers TPRM001–TPRM015, test helper) and part B (stored state: generated `WriteState`/`ReadState`/`StateSchema`, `[LoadByReplay]`, `[StateName]`, TPRM024–TPRM027, property test state == replay); AOT smoke 0 IL warnings, package verified, see [primitives.md](primitives.md) §16; part C (lifecycles: `[Lifecycle]`/`[To]`/`[Transition<T>]`, generated `<Enum>Lifecycle` guards and diagram, live-only step check returned by `Prepare`, creation in the initial state, TPRM016–TPRM023, suppressor TPRMSPR01), see [state-transitions.md](state-transitions.md) §9 |
| 7 | Changes | **designed** ([changes.md](../architecture/changes.md), evaluation [changes.md](changes.md), C-1 … C-16); two PRs: (A) **`Tarol.Persistence`**: store port `IAggregateStore` (load tenant-scoped, append with expected version and idempotency key, seen), `IdempotencyKey`, `InMemoryAggregateStore` (C-6); (B) **`Tarol.Changes`** (+ generator): input records `IChange<T>`/`ICreation<T>` whose `Execute` calls one behaviour method (`Raise` stays protected), `ChangeContext(operation, now)`, library `ChangePipeline` (`Validated()` before load, automatic load of the target, special changes `ILoads<T>` and `[Read]` read-only aggregates (C-16), `IChangeAuthorization` hook, `Execute`, `AggregateChanges.Prepare`, append, `Committed`; discard on any failure; no retry of version conflicts, C-7), creation by the factory (it generates the id, C-10), context as a parameter (C-11), entry attributes as data, generated `Target` and typed `Dispatch(object)`, TCHG diagnostics, `ChangeTest` (C-14); only the internal (typed) entry is built here (C-1) | **done**: 7A `Tarol.Persistence` ([persistence.md](persistence.md)), 7B `Tarol.Changes` ([changes.md](changes.md) §14) |
| 8 | Persistence | **designed** ([persistence.md](../architecture/persistence.md), evaluation [persistence.md](persistence.md) §8–§19, P-1 … P-16): `Tarol.Persistence.Npgsql` implements step 7's `IAggregateStore` with plain Npgsql (Marten not migrated, D-1; no EF Core, D-14); shared tables `streams`/`events`/`lookups`/`idempotency_keys`/`outbox`, tenant first in every key (P-5); one commit = a guarded statement (conditional `UPDATE` in a data-modifying CTE, events only if it passed) + guarded follow-ups in one batch, a lost race is a row count → `Stream.VersionConflict` (P-6); stored state per commit, another schema or a state that fails to parse → replay, rewrite on the next change, `RebuildStates<T>()` (primitives.md §16, P-12); codec in `Tarol.Persistence` over a generated `T.Events`/`T.StreamType` (`[StreamType]`) and the user's context with `ValueObjectJson.Resolver` first, `IEventUpcaster`, unknown alias throws (P-2, P-7, P-13, R-2); metadata columns + `traceparent` text (P-9); key-function lookups shared by both stores (P-8); keys in the commit, `PurgeKeys`, 7 days default (C-8, P-11); outbox rows from a `ReactionTable` in the commit + `OutboxClaims` (`SKIP LOCKED`, head per (stream, reaction)) (P-1, P-10); `IEventFeed.ReadAll(GlobalPosition(tx, position))` (P-3, P-14); shipped SQL + `EnsureSchema` (P-4); in-memory codec round trip + history fix (P-15); one contract suite on both stores, Testcontainers PostgreSQL (D-15), CI `integration` job. Three PRs (P-16): **8A** codec, stream types, feed port, in-memory parity; **8B** `Tarol.Persistence.Npgsql`; **8C** outbox rows and claims | **8A done** ([persistence.md](persistence.md) §20: codec, stream types, feed port, in-memory parity, shared contract suite); **8B done** (§21: `Tarol.Persistence.Npgsql`, integration tests, CI `integration` job); **8C done** (§22: outbox rows in the commit, `Outbox.Claim`/`Complete`/`Fail`, head per (stream, reaction)): **step 8 done** |
| 9 | Sagas and reactions | **designed** ([sagas.md](../architecture/sagas.md), evaluation [sagas.md](sagas.md), S-1 … S-15): **reactions are changes** with an `[OnEvent<T>]` factory (S-2), generated per assembly by the Changes generator as a public `GeneratedReactions.Set` (table, schedule, (name, event) → factory → `ChangePipeline.Run` with `AsSystem`, causation = event id, key = (reaction, event id)), composed across assemblies (S-3, S-6), name = type name, pinned to rename (S-14), TVAL006 exempt in the factory (C-13); **one outbox worker** (`Tarol.Sagas`, the only new package, S-11) on an outbox port: claim → run → complete, C-9 policy with NotFound/422 retrying too (S-7), 10 attempts 1 s … 5 min (S-13), dead letters moved to `outbox_dead` and re-driven by a human (S-5), poll 100 ms with LISTEN/NOTIFY opt-in (S-10), `ReactionHost` (`BackgroundService`, no package reference on .NET 11) purges keys (P-11), `ActivityLink` from the row's `traceparent`; claims by a **maintained head flag** (S-8; the order of P-10 unchanged) and **scheduled reactions** (`DueAt`) ordered by time only (S-9); **a saga is an aggregate** whose steps are reactions, started load-or-create (`ILoads<T>`), timeouts are scheduled reactions, compensations forward changes (S-4, S-15); in-memory outbox in `InMemoryAggregateStore` + `ReactionTest`; Aspire AppHost probed on .NET 11 with a Native AOT worker (D-15). Three PRs (S-12): **9A** reactions (Changes), **9B** outbox port, head flag, scheduled rows, dead letters (Persistence, Persistence.Npgsql), **9C** `Tarol.Sagas` worker, hosting, `ReactionTest`, saga guide, AppHost prototype | |
| 10 | Security and REST | (was CQRS; ROADMAP D-3 decided by C-3: Changes is the only write model, old CQRS not migrated) **`Tarol.Security`** (security.md) implements `IChangeAuthorization` with `IAuthorizationService`; **`Tarol.Changes.AspNetCore`** (+ generator): `[Endpoint]` entries as generated `RequestDelegate`s (C-12; the RDG does not see generated `MapPost` calls), context from the `ClaimsPrincipal`, `Idempotency-Key`, `If-Match`, `AcceptsMetadata`, fail closed without a permission; `Invalid` → problem details with JSON-cased paths, `Error` → `ToHttpStatusCode`, `IProblemDetailsService`/`AddProblemDetails`, `ValueObjectJson.Resolver` in ASP.NET Core's options (validations.md §14); `[Webhook]` entries: raw body, HMAC + timestamp window, delivery-id key, unknown type 202 (changes.md §8) | |
| 11 | Messaging | after decision ROADMAP D-6; `[Consumes(contract, version)]` consumers through the change pipeline, inbox = the idempotency key table (contract, message id), contract versions mapped by `From` factories, delivery policy C-9 as built in step 9 (the worker, `DeliveryPolicy` and dead letters of `Tarol.Sagas`; NotFound retries, S-7); envelope from `OperationContext`, injected `TimeProvider`, tenant-scoped topic authorization (kernel.md §12) | |
| 12 | Paginations | small API seed; decide then (Contexts went into Kernel in step 4; Security is built in step 10) | |
| 13 | Samples | ArtFlow rebuilt on the migrated API; run through an **Aspire AppHost** (the standard way to start it), end-to-end tests with `Aspire.Hosting.Testing` (D-15) | |
| 14 | Templates | new template pack built and instantiated in CI | |

Order follows the dependency graph of the old repository: StronglyTypedId has no Tarol dependency;
Options and Results (which depends on Options), and Kernel (Options and StronglyTypedId) sit at the bottom; Validations depends on Results, Options and StronglyTypedId;
Primitives needs all of them; everything else needs Primitives.

## What is not migrated

From the inventory of `CLorat/Tarol` (89 csproj; build and test on SDK 11.0.100: 0 errors,
403 tests, 21 failures = 12 Messaging integration + 8 Marten Testcontainers (no Docker) + 1 flaky
backplane test):

| Old project | Reason |
|---|---|
| `gen/Messaging/Messaging.Generators`, `gen/Validations/Validations.Generators` | empty (csproj only) |
| `src/Sagas/Sagas.Hosting`, `src/Diagnostics/*`, `src/TestUtility/TestUtility`, `TestUtility.Serialization` | empty or broken references |
| `src/Changes/Changes.Aggregates` (`Class1.cs`), `src/Changes/Changes.DomainEvent` (unused attributes) | leftovers |
| `src/Messaging/Messaging.Client` | one empty class named `IMessagingClient` |
| `src/TestUtility/TestUtility.IntegrationTests` | EF Core helper used only by the outdated template |
| `analyzers/Analzyer.NamingConventions` (+ Sample, Tests) | sample rules; its one Tarol rule NC0003 contradicts the current event conventions and its fix does not converge |
| `tests/Sagas/Sagas.Tests/Tarol.Sagas.Testing.Tests.csproj` | stray duplicate csproj |
| `tests/Persistence/Persistence.Marten.UnitTests`, `tests/Options/Options.Persistence.Relational` | not in the solution, do not compile |
| `tests/Messaging/Messaging.UnitTests`, `tests/Validations/Validations.UnitTests`, `tests/Paginations/Paginations.UnitTests` | zero tests |
| `src/Persistence/Persistence.Marten`, `src/Sagas/Sagas.Persistence.Marten` | replaced by the Npgsql store (ROADMAP D-1) |
| `templates/TarolSolution`, `templates/Entity` | outdated API, not buildable; replaced in step 14 |
| `plan-messagingIntegrationTests.prompt.md` | content moves with its family |
| `src/CQRS/CQRS`, `gen/CQRS/CQRS.Generators`, `tests/CQRS/CQRS.UnitTests` | ROADMAP D-3 decided in step 7 (C-3): Changes is the only write model; the old dispatcher has 15 open findings and 0 tests; queries are a later decision |
| `src/Changes/Changes` (`IChange`, `IApplysEvent`, `IHttpInvoke`, `IReactsTo`) | replaced by name and shape in step 7 (changes.md §1); no code is ported |
| `Validations.Prerequisite/` at the root, `gen/Validations/Validations.Invariant.Generators`, `InvariantAttribute` | prerequisites became guards in behaviour methods; invariants are rebuilt in step 6 (validations.md §10) |

Typos that disappear by construction (new names): `Analzyer`, `StronlgyTypedId`, `EventEnvolope.cs`,
`AggregateSnapshort.cs`, `ISnapshottable.cs`, `AppDbContextExtentions.cs`, `MartinUnitOfWork`,
`JsonAotMessageProtocoll`, namespaces `Tarol.Validation.Generators`, `Tarol.Primitives.Generator`,
`Tarol.StronglyTypedId.Generator`, `Messagings.*`, `Messaging.*`/`Changes.*` without `Tarol.`,
`YourApp.*`, `Tarol.Samples.*`. `IApplysEvent` is removed in step 7 (changes call behaviour methods; changes.md §1).

## How a step is done

1. Branch from `main` of Tarol-Net; the step becomes one pull request (see [Pull requests](#pull-requests)).
2. Write `docs/migration/<family>.md` first: review of the old code (link the old review findings),
   the .NET 11 / AOT / performance / developer-experience assessment, and the planned API changes.
3. Port the code into `src/<Family>/` (new names, conventions). Copy, do not re-type: keep the old
   commit id in the note so history can be followed in `CLorat/Tarol`.
4. Apply the improvements from step 2. Every public API change and every behaviour change is listed in
   the note under "Changes against Tarol".
5. Tests, AOT smoke, README, skill, package verification (conventions §8).
6. Update the status table above, `CHANGELOG.md` and the root `README.md`.

## Pull requests

Every change reaches `main` through a pull request with **one clear feature**:

* **One step (or one self-contained part of a step) per PR.** A migration step is one PR (for example
  "Step 2: Results"). Cross-cutting work (build configuration, CI, conventions) gets its own PR and is not mixed
  into a family's PR. If a step turns out large, split it into feature PRs that each build and pass on their own
  (e.g. runtime package, then generator, then docs), never into half-done states.
* **Title** `<area>: <what the PR delivers>`, e.g. `results: migrate Tarol.Results with a non-boxing union shape`.
* **Body** follows `.github/pull_request_template.md`: what and why, the public API and behaviour changes
  (or "none"), suppressions added (or "none"), verification output (build, tests, format, AOT smoke, package
  verification), and what is deliberately left out.
* **Branch** per PR, from current `main`; small commits inside (moves in their own commits); no unrelated changes.
* **Green before review**: the required checks (`build`, `aot-smoke`) pass; the family's migration note and this
  plan's status table are updated in the same PR.
* **Merge** with squash or rebase so `main` stays linear; delete the branch after merging.

## CI and release

* **CI** (`.github/workflows/ci.yml`): see conventions §7. Required checks to configure on `main`:
  `build`, `aot-smoke`, `integration` (since step 8B). Require a PR, linear history, no force push.
* **Release** (to add before the first tag): on tag `v*`, `dotnet pack -c Release` (MinVer reads the
  tag), `eng/verify-packages.sh`, push `.nupkg` + `.snupkg` to the owner's **private** GitHub Packages feed
  (never nuget.org, D-1), GitHub release notes from `CHANGELOG.md` and the `AnalyzerReleases.Shipped.md` files.
  Keep the repository private so the packages stay private.
* **Dependabot** (`.github/dependabot.yml`): NuGet and GitHub Actions, weekly, grouped.
* **CodeQL** for C#: to add with the release workflow.

## Decisions

Recommendations are applied in the foundation unless marked *open*.

| # | Decision | Applied / recommendation |
|---|---|---|
| D-1 | Licence | **decided by the owner:** proprietary, owner-only, all rights reserved (`LICENSE`, packed as `PackageLicenseFile`). Consequence: packages go only to a private feed (GitHub Packages of the owner), never to nuget.org. |
| D-2 | Test tree | parallel `src/` and `tests/` trees with one family folder each; generators inside the src family folder |
| D-3 | Solution format | `Tarol.slnx` only. **[verified]** SDK 11.0.100 migrates, lists and builds slnx; Visual Studio 17.13+ and Rider 2025.1+ open it **[unverified here]** |
| D-4 | Versioning | MinVer 8 from `v*` tags (no version file); AssemblyVersion = major.0.0.0 |
| D-5 | `TreatWarningsAsErrors` | always (local and CI) |
| D-6 | PublicAPI analyzers | not yet; adopt (`PublicAPI.Shipped/Unshipped.txt`) before 1.0 so the first release is the baseline |
| D-7 | Kernel / Clock / Contexts / Security | **decided by the owner:** group them into one `Tarol.Kernel` where that makes sense or makes testing easier; the split is settled in step 4 (e.g. an ambient clock that tests replace belongs with the markers). **Settled in step 4:** Contexts → `Tarol.Kernel` (`OperationContext`, `Actor`); Clock dropped (inject `TimeProvider`, TKRN001); Security designed, built as `Tarol.Security` in step 10; see [kernel.md](kernel.md) |
| D-8 | Meta-package `Tarol` | not now; reconsider when step 9 is done (step 9 design: still not, `Tarol.Sagas` is not the top of the graph until steps 10–11; sagas.md S-11). A meta-package must reference its dependencies so that analyzers flow (verify then) |
| D-9 | Snapshot tests | keep the in-repo harness from Tarol PR #24; Verify needs a licence/sponsorship MSBuild claim (SC021) that only the owner can make |
| D-10 | Generator packaging | inside the runtime package (ROADMAP D-10), verified per family |
| D-11 | Publishing | GitHub Packages only (private, `GITHUB_TOKEN` with `packages: write`); nuget.org Trusted Publishing is not used because of D-1 |
| D-12 | Doc site | plain Markdown in `docs/` for now; DocFX later if API reference pages are wanted |
| D-13 | Action pinning | major-version tags plus Dependabot for now; SHA pinning once the release workflow publishes packages |
| D-14 | EF Core | **decided by the owner:** not used. EF Core is not Tarol's persistence provider; `Tarol.StronglyTypedId.EntityFrameworkCore` is not migrated. Id mapping for the Npgsql store comes with step 8. |
| D-15 | Docker, Testcontainers, Aspire | **decided by the owner (step 7 follow-up):** Docker is used for integration tests (in cloud sessions the daemon is started per session: `dockerd &`); step 8 tests the Npgsql store with Testcontainers PostgreSQL; prototypes from step 9 on and the samples (step 13) run through an Aspire AppHost, end-to-end tests with `Aspire.Hosting.Testing`. **No runtime package depends on Aspire**; at most a thin optional integration later (e.g. registering the Npgsql store from Aspire's connection). Aspire on .NET 11 / Native AOT is **[unverified]**: probe it in step 9 |
