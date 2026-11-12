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
| 1 | StronglyTypedId | `Tarol.StronglyTypedId` (+ generator), `Tarol.StronglyTypedId.EntityFrameworkCore` | done: 82 tests, AOT smoke 0 IL warnings, packages verified; see [stronglytypedid.md](stronglytypedid.md) |
| 2 | Results | `Tarol.Results` | next: drop the StronglyTypedId dependency (doc `cref` only), fix `default(Result)` (R-1), evaluate the non-boxing union shape (ROADMAP D-9) with C# 15 |
| 3 | Options | `Tarol.Options` | |
| 4 | Kernel | `Tarol.Kernel` (absorbs Clock; decision D-7) | |
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

1. Branch from `main` of Tarol-Net.
2. Write `docs/migration/<family>.md` first: review of the old code (link the old review findings),
   the .NET 11 / AOT / performance / developer-experience assessment, and the planned API changes.
3. Port the code into `src/<Family>/` (new names, conventions). Copy, do not re-type: keep the old
   commit id in the note so history can be followed in `CLorat/Tarol`.
4. Apply the improvements from step 2. Every public API change and every behaviour change is listed in
   the note under "Changes against Tarol".
5. Tests, AOT smoke, README, skill, package verification (conventions §8).
6. Update the status table above, `CHANGELOG.md` and the root `README.md`.

## CI and release

* **CI** (`.github/workflows/ci.yml`): see conventions §7. Required checks to configure on `main`:
  `build`, `aot-smoke` (and `integration` once it exists). Require a PR, linear history, no force push.
* **Release** (to add before the first tag): on tag `v*`, `dotnet pack -c Release` (MinVer reads the
  tag), `eng/verify-packages.sh`, push `.nupkg` + `.snupkg` to nuget.org with Trusted Publishing (OIDC,
  `NuGet/login` action) or an API key secret, prereleases to GitHub Packages, GitHub release notes from
  `CHANGELOG.md` and the `AnalyzerReleases.Shipped.md` files.
* **Dependabot** (`.github/dependabot.yml`): NuGet and GitHub Actions, weekly, grouped.
* **CodeQL** for C#: to add with the release workflow.

## Decisions

Recommendations are applied in the foundation unless marked *open*.

| # | Decision | Applied / recommendation |
|---|---|---|
| D-1 | Licence | ***open*** — no `LICENSE` file and no `PackageLicenseExpression` until the owner picks one (MIT and Apache-2.0 are the usual choices for .NET libraries). Packages are not published before this is settled. |
| D-2 | Test tree | parallel `src/` and `tests/` trees with one family folder each; generators inside the src family folder |
| D-3 | Solution format | `Tarol.slnx` only. **[verified]** SDK 11.0.100 migrates, lists and builds slnx; Visual Studio 17.13+ and Rider 2025.1+ open it **[unverified here]** |
| D-4 | Versioning | MinVer 8 from `v*` tags (no version file); AssemblyVersion = major.0.0.0 |
| D-5 | `TreatWarningsAsErrors` | always (local and CI) |
| D-6 | PublicAPI analyzers | not yet; adopt (`PublicAPI.Shipped/Unshipped.txt`) before 1.0 so the first release is the baseline |
| D-7 | Kernel / Clock / Contexts / Security | ***open*** — recommendation: one `Tarol.Kernel` package (markers, ambient clock, user context, authorizer port) instead of four 1-file packages |
| D-8 | Meta-package `Tarol` | not now; reconsider when step 9 is done. A meta-package must reference its dependencies so that analyzers flow (verify then) |
| D-9 | Snapshot tests | keep the in-repo harness from Tarol PR #24; Verify needs a licence/sponsorship MSBuild claim (SC021) that only the owner can make |
| D-10 | Generator packaging | inside the runtime package (ROADMAP D-10), verified per family |
| D-11 | Trusted Publishing | yes, if the nuget.org account supports it when the release workflow is added; otherwise an API key secret |
| D-12 | Doc site | plain Markdown in `docs/` for now; DocFX later if API reference pages are wanted |
| D-13 | Action pinning | major-version tags plus Dependabot for now; SHA pinning once the release workflow publishes packages |
| D-14 | EF Core version | 11.0.0 (GA). `Npgsql.EntityFrameworkCore.PostgreSQL` is only needed if step 8 uses EF Core, which it does not (thin Npgsql store) |
