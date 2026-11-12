# Repository conventions

This is the rulebook for Tarol-Net. Every migrated or new package follows it. Agents read it through
[`AGENTS.md`](../../AGENTS.md); the per-package checklist at the end is mandatory.

Evidence labels: **[verified]** means it was reproduced by a build, a test or a package inspection in
this repository (the command is given); **[unverified]** means it was not tried here.

## 1. Layout

```
Tarol-Net/
├── AGENTS.md                      canonical instructions for coding agents (CLAUDE.md and copilot-instructions point here)
├── README.md  CONTRIBUTING.md  CHANGELOG.md  SECURITY.md
├── global.json  nuget.config  .editorconfig  .gitignore
├── Directory.Build.props          root settings (all projects)
├── Directory.Build.targets        imports eng/Tarol.Packaging.targets for packable projects
├── Directory.Packages.props       central package versions
├── Tarol.slnx                     the one solution; folders mirror the tree
├── eng/                           build logic shared by projects (packaging targets, scripts)
├── src/
│   ├── Directory.Build.props      packable runtime + generator settings
│   └── <Family>/
│       ├── Tarol.<Family>/                     runtime library (package)
│       │   ├── README.md                       = NuGet readme
│       │   └── skills/tarol-<topic>/SKILL.md   agent skills shipped in the package
│       ├── Tarol.<Family>.Generators/          source generator, packed INTO Tarol.<Family>
│       └── Tarol.<Family>.<Part>/              satellite package (e.g. .Npgsql)
├── tests/
│   ├── Directory.Build.props      test settings
│   ├── .editorconfig              test-only analyzer relaxations (each justified)
│   └── <Family>/
│       ├── Tarol.<Family>.Tests/               unit tests
│       ├── Tarol.<Family>.Generators.Tests/    generator snapshot/incrementality/diagnostic tests
│       ├── Tarol.<Family>.IntegrationTests/    needs Docker/Testcontainers (separate CI job)
│       ├── Tarol.<Family>.AotSmoke/            Native-AOT console app, 0 IL warnings
│       └── Tarol.<Family>.PackageTests/        consumer that restores the packed .nupkg from a local feed
├── samples/                       ArtFlow and other runnable samples (non-packable, warning-free)
├── templates/                     dotnet new templates (built and instantiated in CI)
└── docs/
    ├── architecture/              conventions (this file), design decisions
    ├── migration/                 plan.md + one review/migration note per family
    └── <family>/                  user guides (concepts, guides, reference)
```

**Why src/ and tests/ as parallel trees, with the generator inside the src family folder.**
* Generators ship *inside* their runtime package (D-10), so they live next to it under `src/<Family>/`.
* Tests never ship. Keeping them under `tests/` gives one `tests/Directory.Build.props` that sets every
  test property, and no condition on project names is needed to tell shipped from non-shipped code.
* One family folder in each tree keeps a migration step small and reviewable: a step adds
  `src/<Family>/` and `tests/<Family>/`.

## 2. Naming

| Item | Rule | Example |
|---|---|---|
| Project folder and file | full assembly name | `src/StronglyTypedId/Tarol.StronglyTypedId/Tarol.StronglyTypedId.csproj` |
| Assembly, root namespace, package id | the project name (set in `Directory.Build.props`) | `Tarol.StronglyTypedId` |
| Satellite package | `Tarol.<Family>.<Part>` | `Tarol.Persistence.Npgsql` |
| Generator | `Tarol.<Family>.Generators`, namespace the same | `Tarol.StronglyTypedId.Generators` |
| Unit tests | `<Project>.Tests` | `Tarol.StronglyTypedId.Tests` |
| Integration tests | `<Project>.IntegrationTests` | `Tarol.Persistence.Npgsql.IntegrationTests` |
| AOT smoke app | `Tarol.<Family>.AotSmoke` | |
| Package consumer test | `Tarol.<Family>.PackageTests` | |
| Test fixture assembly | `<TestProject>.<Purpose>` (does not end in `.Tests`, so it is not run as tests) | `Tarol.StronglyTypedId.Tests.ExternalAssembly` |
| Shared test infrastructure | `tests/Shared/Tarol.Testing.<Area>` | `Tarol.Testing.Generators` (snapshot + incrementality helpers) |
| Test class | `<TypeUnderTest>Tests`, file name = class name | `IdJsonTests` |
| Test method | `Member_Should_Result_When_Condition` or a plain sentence | `Parse_Should_Throw_When_NotAGuid` |
| Diagnostic ids | `TAROL<family-prefix><nnn>`, one prefix per generator | `TSID001` (StronglyTypedId) |
| Skill name | `tarol-<topic>`, lowercase, hyphens, equals its folder name | `tarol-strongly-typed-id` |

Namespaces equal the assembly name. The only allowed deviation is a type that extends another family's
namespace on purpose (for example `Tarol.StronglyTypedId` extension methods inside
`Tarol.Persistence.Npgsql` are **not** allowed; they live in that package's namespace).
A deviation is listed in the family's migration note with its reason.

## 3. Build configuration

All settings are in the layered props; a csproj only states what is specific to it.

| Setting | Value | Why |
|---|---|---|
| `TargetFramework` | `net11.0`; generators `netstandard2.0` | Roslyn loads analyzers as netstandard2.0. |
| `LangVersion` | not set (C# 15 default); generators `latest` | Pinning hides the default; netstandard2.0 would default to C# 7.3. |
| `Nullable`, `ImplicitUsings` | enable (generators: no implicit usings) | |
| `TreatWarningsAsErrors` | true, locally and in CI | One behaviour everywhere; a warning that only CI rejects wastes a round trip. |
| `AnalysisLevel` | `latest-recommended` | Measured on the old repo: ≈60 findings in src, all fixable or justified. |
| `EnforceCodeStyleInBuild` | true | IDE rules from `.editorconfig` fail the build. |
| `GenerateDocumentationFile` | true for runtime libraries (CS1591 active) | Public API ships XML docs; every migrated package is fully documented. |
| `UseArtifactsOutput` | true, `artifacts/` | One output tree (`artifacts/bin`, `obj`, `package`, `publish`), easy CI upload. |
| `Deterministic`, `ContinuousIntegrationBuild` | true / true in CI | Reproducible packages. SourceLink is built into the SDK. |
| `IsAotCompatible` | true for every runtime library unless the csproj opts out with a comment | Turns on trim/AOT analyzers; the AOT smoke app proves it. |
| `EnablePackageValidation` | true | API compatibility across TFMs now; `PackageValidationBaselineVersion` after the first release. |
| Versioning | MinVer from `v*` tags; untagged builds are `0.0.0-alpha.0.<height>` | No version file to edit; the tag is the release. |
| `NuGetAudit` | on, mode `all` | Vulnerable transitive packages fail the build (TWAE). |
| Central package management | `Directory.Packages.props`, transitive pinning on | One version per package. |
| NU1510 | stays an error | Do not reference packages the shared framework provides; use `FrameworkReference`. |
| Lock files | not used (yet) | CPM plus audit covers drift; revisit when the first release ships. |

Suppressions: never `NoWarn` in props. A diagnostic is either fixed, configured in `.editorconfig`
with a reason, or suppressed at the narrowest scope (`[SuppressMessage]`/`[UnconditionalSuppressMessage]`
with `Justification`, or `#pragma` with a comment). Each one is listed in the family's migration note.

## 4. Required files per project kind

### 4.1 Runtime library (`src/<Family>/Tarol.<Family>`)

| File | Required | Notes |
|---|---|---|
| `Tarol.<Family>.csproj` | yes | `Description`, `PackageTags`; opt out of `IsAotCompatible` only with a comment |
| `README.md` | yes | packed as the NuGet readme: what it is, install, 30-second example, links |
| `skills/tarol-<topic>/SKILL.md` | yes for every package a user writes code against | see §6 |
| `InternalsVisibleTo` | only to its own test project, via `<InternalsVisibleTo Include="$(AssemblyName).Tests" />` | never to another runtime package |
| `PublicAPI.*.txt` | no (decision D-6: adopt before 1.0) | |

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>One sentence: what the package gives the user.</Description>
    <PackageTags>tarol;ddd;event-sourcing</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <!-- Bundled generator (D-10): packed into analyzers/dotnet/cs, not a package dependency. -->
    <ProjectReference Include="../Tarol.<Family>.Generators/Tarol.<Family>.Generators.csproj"
                      ReferenceOutputAssembly="false" PrivateAssets="all" TarolGenerator="true" />
    <InternalsVisibleTo Include="$(AssemblyName).Tests" />
  </ItemGroup>
</Project>
```

### 4.2 Source generator / analyzer (`src/<Family>/Tarol.<Family>.Generators`)

* Inherits netstandard2.0, `EnforceExtendedAnalyzerRules`, `IsRoslynComponent`, Roslyn package
  references and `AnalyzerReleases.*.md` as `AdditionalFiles` from `src/Directory.Build.props`.
* `IIncrementalGenerator` only; models are equatable records of strings, value types and `EquatableArray<T>`
  (no `ISymbol`, `Compilation`, `SyntaxNode` or `Location`; a source position travels as an equatable
  `LocationInfo` record, and the incrementality test proves unrelated edits regenerate nothing).
* Output starts with `// <auto-generated/>` and `#nullable enable`; uses `global::` names.
* Every diagnostic is declared in `AnalyzerReleases.Unshipped.md`.
* Never packed on its own; never ships a `build/*.targets` that adds the analyzer again.
* Tests: snapshot tests of the output (committed `*.verified.cs`), an incrementality test
  (`TrackIncrementalGeneratorSteps`, all steps `Cached`/`Unchanged` on an unrelated edit), and one
  test per diagnostic.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Everything is inherited from src/Directory.Build.props (TarolIsGenerator). -->
</Project>
```

### 4.3 Unit tests (`tests/<Family>/<Project>.Tests`)

* Inherits from `tests/Directory.Build.props`: `OutputType=Exe`, `IsTestProject`, not packable,
  xUnit v3 on Microsoft.Testing.Platform v2 (`xunit.v3.mtp-v2`), Shouldly, TRX and code coverage extensions.
* No relaxation of `TreatWarningsAsErrors`. The only analyzer changes are in `tests/.editorconfig`.
* A project that uses a generator references it explicitly
  (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`): analyzer project references do not flow.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../../src/<Family>/Tarol.<Family>/Tarol.<Family>.csproj" />
  </ItemGroup>
</Project>
```

### 4.4 Integration tests (`<Project>.IntegrationTests`)

* Testcontainers; every test class carries `[Trait("Category", "Integration")]` so CI can split jobs.
* When Docker is not reachable the tests call `Assert.Skip("Docker is not available: <reason>")`
  **locally**; when `CI=true` they fail instead, so CI can never pass by skipping.

### 4.5 AOT smoke app (`Tarol.<Family>.AotSmoke`)

* One per family that claims AOT. Console app, `PublishAot=true`, `TrimmerSingleWarn=false`,
  `JsonSerializerIsReflectionEnabledByDefault=false`; exercises the public API, prints `ok <check>`
  per check and exits non-zero on failure.
* CI (`eng/aot-smoke.sh`): `dotnet publish -c Release -r linux-x64`, fails on any `IL2xxx`/`IL3xxx` warning, then runs the binary.
* Use the AOT-safe APIs in the app (`JsonSerializerContext`, `TypeDescriptor.GetConverterFromRegisteredType`,
  the source-generated configuration binder) so the smoke test shows what a real AOT app can do.

### 4.6 Package consumer test (`Tarol.<Family>.PackageTests`)

* Not in `Tarol.slnx`: it restores `Tarol.*` from the local feed `artifacts/package/release` (its own
  `nuget.config` maps `Tarol.*` to that feed only) with `VersionOverride="$(TarolPackageVersion)"`, so it proves
  the **package** works, including the bundled generator and the skill paths in `project.assets.json`.
  `eng/verify-packages.sh` packs, inspects every `.nupkg`, then runs each consumer with an isolated packages folder.

### 4.7 Sample (`samples/`)

Non-packable, `TreatWarningsAsErrors`, built by CI as part of the solution. Uses ProjectReferences
(generators referenced as analyzers), like a test project.

### 4.8 Template (`templates/`)

A template pack project (`PackageType=Template`). CI packs it, runs `dotnet new install` from the
artifact, instantiates every template against the local package feed and builds the result on net11.0.

## 5. Packaging

* Package id = assembly name. Metadata (authors, URLs, copyright, SourceLink, snupkg) in the root props;
  `Description` and `PackageTags` per csproj; `PackageReadmeFile` set automatically when `README.md` exists.
* Generators are packed into their runtime package by `eng/Tarol.Packaging.targets` (target
  `_TarolPackGenerators`, which adds the generator's `GetTargetPath` output as a `TfmSpecificPackageFile`
  under `analyzers/dotnet/cs`).
  **[verified]** for `Tarol.StronglyTypedId` (output in [docs/migration/stronglytypedid.md](../migration/stronglytypedid.md#4-verification-sdk-1100100-2026-11-12)):
  the generator is inside the package and not a dependency, there is no `build/` folder, and a consumer that
  references only the package runs the generator. Earlier, a prototype on the old repository showed that a
  bundled generator also runs for a *transitive* consumer (`Tarol.Primitives` → `Tarol.StronglyTypedId`).
* `eng/verify-packages.sh` enforces this for every package on every CI run.
* Licence: proprietary, owner-only; the root `LICENSE` is packed into every package as `PackageLicenseFile` (plan D-1). Packages are published only to a private feed.

## 6. Agent skills

Format ([agentskills.io](https://agentskills.io), as used by the NuGet post
<https://devblogs.microsoft.com/dotnet/delivering-skills-in-nuget-packages/>):

```markdown
---
name: tarol-strongly-typed-id
description: >-
  Use when … (what it covers, when an agent should load it, and what it should NOT do instead,
  e.g. "don't decompile the assembly").
---
# Body: the rules, the minimal correct example, the common mistakes and their diagnostics.
```

* `name` equals the folder name; lowercase letters, digits and hyphens. (agentskills.io itself could not be
  reached from this environment; the field rules here follow the NuGet post and the markout package **[unverified
  against the spec text]**.)
* `description` says *when* to use the skill; it is what an agent sees before loading it.
* A package may ship several skills (one base skill plus workflow skills).

Reference layout **[verified]** by downloading `markout 0.40.0` from nuget.org and listing the `.nupkg`:
`skills/<name>/SKILL.md` per skill plus `skills/plugin.json`, next to `lib/`, `analyzers/dotnet/cs/` and
`README.md`. Tarol does not ship `plugin.json` (no tool needs it; **[unverified]** whether any consumer tool reads it).

How a consumer gets them: `dotnet restore` extracts the files to the NuGet cache and lists them under the
package's `files` in `obj/project.assets.json` (**[verified]** with `Tarol.StronglyTypedId.PackageTests`);
`dotnet-package-skills install` copies them to `.agents/skills/` (**[unverified]**, tool not run here).

How they are evaluated: each skill has a checklist in its family's migration note (tasks an agent must
solve with the skill, the expected API usage, and the mistakes it must avoid). Run the checklist with a
frontier model with and without the skill and record token cost and correctness; remove or rewrite a skill
that does not help ("do no harm", per the post). Testing only with small models is an anti-pattern.

**Supply chain.** Package-shipped Markdown is now read and acted on by agents. A skill must never instruct
an agent to run commands that download or execute anything, change credentials or settings, or disable
analyzers. Review `skills/` in every PR like code. Consumers should only use packages from maintainers they trust.

## 7. Continuous integration

Workflow `.github/workflows/ci.yml` on every push and pull request:

| Job | Steps | Required check |
|---|---|---|
| `build` | setup-dotnet from `global.json` → `dotnet format --verify-no-changes` → `dotnet build Tarol.slnx -c Release` → `dotnet test --solution Tarol.slnx` (unit tests, TRX + coverage, integration tests filtered out) → `dotnet pack` → `eng/verify-packages.sh` | yes |
| `aot-smoke` | for each `tests/*/*.AotSmoke`: publish, fail on IL warnings, run | yes |
| `integration` | Docker available; runs `Category=Integration` | yes once a family has integration tests |

Release (tag `v*`), CodeQL and Dependabot are described in [the migration plan](../migration/plan.md#ci-and-release).

## 8. Per-package checklist (definition of done for every migration step)

An agent that migrates or adds a package completes **all** of these in the same PR:

1. **Review first.** Write `docs/migration/<family>.md`: what came from Tarol (commit), what changed and why
   (.NET 11 / C# 15 features, AOT, performance, developer experience), every public API change, every
   suppression, open questions.
2. **Code** in `src/<Family>/…` following §2–§4; no `NoWarn`, no reflection without
   `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`.
3. **Generator** (if any) inside the runtime package (§5) with `AnalyzerReleases.*.md`.
4. **Tests**: unit tests; generator snapshot + incrementality + diagnostics tests; integration tests if
   the package talks to infrastructure.
5. **AOT smoke** app if the package claims `IsAotCompatible`, publishing with 0 IL warnings.
6. **README.md** in the project folder (it is the NuGet readme).
7. **Skill**: `skills/tarol-<topic>/SKILL.md` for every package users write code against, plus its
   evaluation checklist in the migration note.
8. **Package check**: `eng/verify-packages.sh` passes: the `.nupkg` contains `lib/`, `README.md`, the
   generator under `analyzers/dotnet/cs/` and `skills/**/SKILL.md`; the PackageTests consumer restores
   from the local feed, builds and runs; its `project.assets.json` lists the skill paths.
9. **Solution and CI**: project added to `Tarol.slnx` in the matching folder; new AOT smoke app is
   picked up by the CI glob.
10. **Docs**: user guide under `docs/<family>/`, entry in `CHANGELOG.md` (Unreleased) and in the
    package table of the root `README.md`; `docs/migration/plan.md` status updated.
11. **Green**: `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test`, AOT smoke and
    package verification pass locally with SDK 11.0.x; results pasted into the migration note.
