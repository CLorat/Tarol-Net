# Release

Concept note for step 17 (see [migration/release.md](../migration/release.md)): **a tag `v*` turns a green commit on `main`
into 24 private packages and a GitHub release, with nothing done by hand except creating the tag.**
* The release workflow re-runs CI on the tagged commit, packs once (MinVer reads the tag), verifies the packages, checks
  that the release ships its API and diagnostics, writes the release notes from `CHANGELOG.md` and the
  `AnalyzerReleases.Shipped.md` files, and only then pushes to the owner's private GitHub Packages feed (never nuget.org, D-1).
* Every runtime package records its public API in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, generated from the
  code by the analyzer itself (D-6). A change to the public surface is a build error until the file says so.
* Every action is pinned to a full commit SHA, kept current by Dependabot (D-13).
* CodeQL scans C# where GitHub allows it for this repository (RL-1, RL-7).

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/releasing/`](../migration/probes/releasing/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: designed** (owner decisions RL-1 … RL-8, [migration/release.md](../migration/release.md) §9). Built in PRs 17A–17D
(§11 there).

## 1. What exists today **[verified by reading]**

| Piece | Where | State |
|---|---|---|
| Versions | MinVer 8.0.0, `MinVerTagPrefix=v`, `alpha.0` default pre-release | today's builds are `0.0.0-alpha.0.<height>` |
| Packing | `eng/Tarol.Packaging.targets`, `IncludeSymbols` + `snupkg`, `EnablePackageValidation`, `LICENSE` packed | 24 `.nupkg` + 24 `.snupkg` |
| Package check | `eng/verify-packages.sh`: contents, generators under `analyzers/`, 24 consumer apps | runs in CI `build` |
| CI | `ci.yml`: `build`, `integration`, `aot-smoke`; actions by major tag (`@v5`, `@v4`) | green on `main` |
| Dependabot | NuGet and GitHub Actions, weekly, grouped | |
| Diagnostics | 10 generators, 83 rules, all in `AnalyzerReleases.Unshipped.md`, every `Shipped.md` empty | |
| Public API | no analyzer, no files | |
| Release, CodeQL | none | |
| Repository | **public** (`CLorat/Tarol-Net`); the plan says "keep the repository private so the packages stay private" | RL-1 |

## 2. The release flow (RL-6)

```
tag v1.2.3[-pre] ──► release.yml
   ├─ ci (workflow_call of ci.yml on the tag): build + unit, integration, aot-smoke
   ├─ pack (needs ci)
   │    dotnet pack -c Release                    MinVer: 1.2.3[-pre] from the tag
   │    eng/verify-packages.sh --no-pack          contents + 24 consumer apps
   │    eng/release.py check <version>            the version is shipped (§4); fails before anything leaves
   │    eng/release.py notes <version>            > release-notes.md
   │    upload artifact: packages + notes
   └─ publish (needs pack; environment "release"; skipped on a dry run)
        dotnet nuget push *.nupkg  --source https://nuget.pkg.github.com/CLorat/index.json  (GITHUB_TOKEN, packages: write)
        gh release create v1.2.3 --notes-file release-notes.md [--prerelease]  (+ symbol assets, RL-2)
```

* **Permissions**: the workflow default is `contents: read`. Only `publish` gets `packages: write` and `contents: write` (the
  GitHub release). No other secret exists or is needed: `GITHUB_TOKEN` pushes to the repository owner's feed.
* **One artifact**: the packages pushed are the bytes `pack` verified; `publish` downloads them, it does not rebuild.
* **Dry run**: `workflow_dispatch` with `dry_run` (default `true`) and a `pull_request` trigger limited to the release files
  run everything except `publish`. A `workflow_dispatch` can only be started once the file is on the default branch
  **[unverified]**, so the PR that adds the workflow proves itself through its `pull_request` run.
* **Idempotent**: `dotnet nuget push --skip-duplicate`, so a re-run after a failed release step does not fail on the
  packages that already went up. A version is never pushed twice with different bytes: MinVer derives it from the tag.
* **The tag** is created by the owner only. A tag ruleset (Settings → Rules) restricting `v*` to the owner is recommended;
  the workflow cannot enforce it.

## 3. The public API baseline (D-6, RL-5)

* `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0 on every runtime package (`src/*/*`, not the generators, tests or samples:
  generated code in consumer assemblies is the consumer's API). `PublicAPI.Shipped.txt` holds what a release shipped,
  `PublicAPI.Unshipped.txt` what was added since. RS0016 (missing) and RS0017 (removed) are errors like every warning.
* **Generated from the code**: `eng/public-api.py update` builds the 24 packages once with a SARIF log per project and writes
  the exact line the analyzer asks for (`APIName`) **[verified]** (§5.2 of the evaluation). `dotnet format`'s code fix gives
  the same lines but misses the 39 members System.Text.Json generates into the three public `JsonSerializerContext`s
  (`KernelJsonContext`, `SignalRJson`, `OperationsJson`).
* **The baseline**: 3,694 lines in 24 packages, written straight into `PublicAPI.Shipped.txt` (the first release is the
  baseline, D-6); every `PublicAPI.Unshipped.txt` is empty.
* The analyzer adds nothing measurable to the build (6.60 vs 6.58 s, best of 3) **[verified]**.
* Rules that need a decision (RL-5): RS0026/RS0027 (12 overload pairs with optional parameters, differing in a required
  parameter's type) and RS0041 (30 symbols the analyzer reads as nullable-oblivious: generated JSON contexts, and nested
  types of a generic type).

## 4. What "shipped" means at a tag

A release has nothing unshipped. `eng/release.py check <version>` fails when:
* any `PublicAPI.Unshipped.txt` has a line (`eng/public-api.py ship` moves them);
* any `AnalyzerReleases.Unshipped.md` lists a rule (they move to `AnalyzerReleases.Shipped.md` under `## Release <x.y.z>`);
* `CHANGELOG.md` has no `## [<x.y.z>]` section for the tag (a pre-release tag `x.y.z-pre.n` uses the `[x.y.z]` section), or the
  section is empty.

So every release starts with a small PR that ships (17D for the first one). Pre-releases of the same version share the
`Release x.y.z` heading; a rule added between two previews moves into it.

## 5. Release notes

`eng/release.py notes <version>` writes:
1. the `CHANGELOG.md` section for the version, as is;
2. "Diagnostics": the rules of `## Release <x.y.z>` from every `AnalyzerReleases.Shipped.md`, one table;
3. the install line for the private feed.

The first release's CHANGELOG section gets an "Upgrade notes" part, from step 16 (data-protection.md §8): schema version 3 and
the rolling-deploy rule DP-10 (a sealing store raises `compatible_from` to 3; plaintext is sealed only by an explicit
`SealHistory<T>`), and the guide's backup note (backups hold the wrapped keys).

## 6. Action pinning (D-13)

Every `uses:` names a full commit SHA with the tag as a comment (`actions/checkout@<sha> # v5.1.0`). Dependabot's
`github-actions` ecosystem updates SHA pins and their comments **[unverified]**. Only `actions/*` and `github/*` actions are
used: releases go through the preinstalled `gh` CLI, not a third-party action.

## 7. CodeQL (RL-7)

`codeql.yml`: C#, `build-mode: manual` (the repository's own `dotnet build`, so generators and SDK 11 are used as in CI), on
pushes to `main`, pull requests and weekly; `security-extended` queries. Results go to code scanning. Available only while the
repository is public, or with GitHub Code Security on a private one (RL-1) **[unverified]**.

## 8. Meta-package (D-8, RL-4)

Generators already flow to a consumer through package dependencies: a consumer of `Tarol.Sagas` alone gets all seven generators
below it **[verified]**, although every dependency is packed with `exclude="Build,Analyzers"`. A meta-package is therefore only a
naming convenience; what it should hold is the owner's decision (RL-4).

## 9. Out of scope

* nuget.org, Trusted Publishing, a public symbol server (D-1, D-11).
* Package validation against the previous release (`PackageValidationBaselineVersion`): it needs the private feed's
  credentials in every build; the API files cover the same breaks at compile time.
* Signing packages (no certificate; the feed is private).
