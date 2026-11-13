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
| 3 | Options | `Tarol.Options` | next |
| 4 | Kernel | `Tarol.Kernel` (Kernel, Clock, Contexts, Security grouped where it makes sense or eases testing; D-7) | |
| 5 | Validations | `Tarol.Validations` (+ Value and Invariant generators), Prerequisites | |
| 6 | Domain core | `Tarol.Primitives` (+ generator) | |
| 7 | Changes | `Tarol.Changes` (+ generator, ROADMAP Phase 3) | |
| 8 | Persistence | `Tarol.Persistence`, `Tarol.Persistence.Npgsql` (ROADMAP Phase 2; Marten is not migrated) | |
| 9 | Sagas | `Tarol.Sagas` (+ generator), `.Engine`, `.Testing`, `.Persistence.InMemory`; Npgsql store after step 8 | |
| 10 | CQRS | after decision ROADMAP D-3 | |
| 11 | Messaging | after decision ROADMAP D-6 | |
| 12 | Paginations, Contexts, Security | small API seeds; decide per package | |
| 13 | Samples | ArtFlow rebuilt on the migrated API | |
| 14 | Templates | new template pack built and instantiated in CI | |

Order follows the dependency graph of the old repository: StronglyTypedId has no Tarol dependency;
Results, Kernel and Validations depend only on StronglyTypedId (Results only for a doc `cref`, which goes);
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
| `plan-messagingIntegrationTests.prompt.md`, `Validations.Prerequisite/` at the root | content moves with its family |

Typos that disappear by construction (new names): `Analzyer`, `StronlgyTypedId`, `EventEnvolope.cs`,
`AggregateSnapshort.cs`, `ISnapshottable.cs`, `AppDbContextExtentions.cs`, `MartinUnitOfWork`,
`JsonAotMessageProtocoll`, namespaces `Tarol.Validation.Generators`, `Tarol.Primitives.Generator`,
`Tarol.StronglyTypedId.Generator`, `Messagings.*`, `Messaging.*`/`Changes.*` without `Tarol.`,
`YourApp.*`, `Tarol.Samples.*`. `IApplysEvent` is renamed when Changes is migrated (step 7).

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
  `build`, `aot-smoke` (and `integration` once it exists). Require a PR, linear history, no force push.
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
| D-7 | Kernel / Clock / Contexts / Security | **decided by the owner:** group them into one `Tarol.Kernel` where that makes sense or makes testing easier; the split is settled in step 4 (e.g. an ambient clock that tests replace belongs with the markers) |
| D-8 | Meta-package `Tarol` | not now; reconsider when step 9 is done. A meta-package must reference its dependencies so that analyzers flow (verify then) |
| D-9 | Snapshot tests | keep the in-repo harness from Tarol PR #24; Verify needs a licence/sponsorship MSBuild claim (SC021) that only the owner can make |
| D-10 | Generator packaging | inside the runtime package (ROADMAP D-10), verified per family |
| D-11 | Publishing | GitHub Packages only (private, `GITHUB_TOKEN` with `packages: write`); nuget.org Trusted Publishing is not used because of D-1 |
| D-12 | Doc site | plain Markdown in `docs/` for now; DocFX later if API reference pages are wanted |
| D-13 | Action pinning | major-version tags plus Dependabot for now; SHA pinning once the release workflow publishes packages |
| D-14 | EF Core | **decided by the owner:** not used. EF Core is not Tarol's persistence provider; `Tarol.StronglyTypedId.EntityFrameworkCore` is not migrated. Id mapping for the Npgsql store comes with step 8. |
