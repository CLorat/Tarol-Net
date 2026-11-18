# Release: evaluation (step 17 design)

This note evaluates [release.md](../architecture/release.md). It covers:
* the inventory: versions, packing, CI, diagnostics, the repository's visibility;
* the candidates for the release workflow, symbols, the public API baseline, pinning, CodeQL and the meta-package, with numbers;
* the owner's answers, and what PRs 17A–17D must do.

**Design only**: this PR changes no package code. Probe and output: [`probes/releasing/`](probes/releasing/),
[`output.txt`](probes/releasing/output.txt), the baseline generator [`public-api.py`](probes/releasing/public-api.py) (it moves to
`eng/` in 17B). Labels as in the [conventions](../architecture/conventions.md). Measured on `main` at `45da502` (step 16
merged), SDK 11.0.100, 4 cores.

## 0. Summary

* **Blocker found**: `CLorat/Tarol-Net` is **public** **[verified]**. The plan says "keep the repository private so the packages
  stay private" (D-1). The repository's visibility decides whether the packages can stay private by inheritance, whether CodeQL
  is free, and whether CI minutes are billed (RL-1).
* **Measured** **[verified]**:
  * **Public API**: 3,694 lines across the 24 packages (Tarol.Persistence 727, Tarol.Messaging 445, Tarol.Primitives 363, …,
    Tarol.Security.Zitadel 19). Generated from the build's SARIF log in 17.4 s; the `dotnet format` code fix gives the same
    3,655 lines in about 10 minutes and misses 39 System.Text.Json-generated members. The analyzer costs nothing measurable
    (6.60 vs 6.58 s for a full rebuild of the packages).
  * **Rules beyond the baseline**: RS0026 on 12 overload pairs and RS0027 once (optional parameters on overloads), RS0041 on
    30 symbols (oblivious nullability, all generated or nested in a generic type). RS0016 and RS0017 are 0 after generation.
  * **Packages**: 24 `.nupkg` 1.39 MB, 24 `.snupkg` 0.50 MB; PDBs embedded instead: 1.95 MB (+41 %), no `.snupkg`.
  * **Generators flow through dependencies**: a consumer of `Tarol.Sagas` alone gets all 7 generators below it, although every
    dependency (32 of 32) is packed with `exclude="Build,Analyzers"`.
  * **CI**: 15.7 runner-minutes per full run (build 3m50s, integration 3m51s, aot-smoke 8m02s); 233 workflow runs in the last
    6 days; billed 0 (public).
  * **Actions**: 4 actions, 12 `uses:`, all on major tags; each resolves to one SHA. Newer majors exist for all four.
  * **Security rules**: `AnalysisLevelSecurity=latest-all` finds 80 diagnostics, 31 of them in the packages: 30 CA2100 (SQL built
    from validated identifiers) and 1 CA5394 (retry jitter). Corrected in 17A: the first measurement used
    `AnalysisModeSecurity=All`, which switches nothing on, and reported 0.
  * **`main`**: 1863 of 1863 tests, 24 AOT apps with 0 IL warnings, package verification OK.
* **Decided by the owner** (RL-1 … RL-9, §9):
  * the repository stays public, under **AGPL-3.0**;
  * PDBs are embedded;
  * the first version is `0.1.0`, first tagged `v0.1.0-preview.1`;
  * a meta-package `Tarol` of 17 packages (no Npgsql, no transports, no SignalR, no Zitadel);
  * RS0026/RS0027/RS0041 off with reasons;
  * the release re-runs CI, behind an environment `release`;
  * CodeQL and the .NET security rules;
  * six PRs 17A–17F;
  * packages public on GitHub Packages; nuget.org still excluded.

## 1. Inventory **[verified by reading]**

| Item | Today | Step 17 |
|---|---|---|
| Version | MinVer 8.0.0 (`src/Directory.Build.props`): tag prefix `v`, default pre-release `alpha.0`, so `0.0.0-alpha.0.<height>`; no tag exists | a tag `v<x.y.z>[-pre]` is the version |
| Pack | `IncludeSymbols` + `SymbolPackageFormat=snupkg`, `EnablePackageValidation` (no baseline), `ContinuousIntegrationBuild` on Actions, Source Link via the SDK, `PackageLicenseFile=LICENSE` | unchanged, except symbols (RL-2) |
| Verify | `eng/verify-packages.sh`: names, `lib/`, README, LICENSE, skills, generators under `analyzers/dotnet/cs`, no `build/`; 24 `*.PackageTests` restore from the local feed and run | the release runs it on the packed bytes (`--no-pack`) |
| CI | `ci.yml`: push to `main` and `claude/**`, every PR, `workflow_dispatch`; `permissions: contents: read`; jobs `build`, `integration`, `aot-smoke` | `workflow_call` added so the release reuses it |
| Actions | `checkout@v5`, `setup-dotnet@v5`, `cache@v4`, `upload-artifact@v4`, 3 each | SHA-pinned (D-13) |
| Dependabot | `nuget` and `github-actions`, weekly, grouped | updates the SHA pins |
| Diagnostics | 10 generators, 83 rules (TCHG 11, TWEB 6 + TQRY005/006 in `Changes.AspNetCore`, TKRN 1, TMSG 7, TOPT 4, TPRM 33, TQRY 4, TRES 5, TSID 1, TVAL 9), no id twice; every `Shipped.md` empty | moved to `Shipped.md` under `## Release 0.1.0` (RL-3) |
| CHANGELOG | Keep a Changelog, one `[Unreleased]` section of 424 lines (40 KB): Added, Fixed, Changed, Removed | `[Unreleased]` becomes `[0.1.0]` with "Upgrade notes" (§8) |
| Public API | none | D-6 |
| Release, CodeQL | none | |
| Visibility | `CLorat/Tarol-Net` public, `CLorat/Tarol` private | RL-1 |

## 2. Candidates

### 2.1 Visibility (RL-1)

The plan's CI section says the repository stays private so its packages stay private. It is public today.

| | (a) private before the first tag | (b) stays public |
|---|---|---|
| Packages | private by inheritance from the repository **[unverified]** | linked to a public repository; whether a new NuGet package starts private or public is not documented clearly. NuGet supports granular permissions, so each package can be set private by hand **[unverified]** (24 packages, again for every new one) |
| Source | private | readable by anyone (the licence still forbids use) |
| CodeQL | only with GitHub Code Security (Team or Enterprise plan) **[unverified]**; on a personal Free/Pro account: none | free |
| CI minutes | billed: 15.7 per full CI run **[verified]**, the release about 18; 233 runs in 6 days **[verified]**; a Free plan has 2,000 minutes a month, Pro 3,000 **[unverified]** | free |
| Fallback for CodeQL | .NET security rules (`AnalysisLevelSecurity=latest-all`): 31 review findings in the packages, 49 in tests and smoke apps **[verified]** (corrected, §14) | |

Recommended: **(a)**, as D-1 and the plan say, before the first tag: the packages' privacy then does not depend on 24 manual
settings. CodeQL is then replaced by the .NET security rules (RL-7 b). To save minutes, CI stops running twice for a PR
branch (push to `claude/**` and `pull_request` both start it today).

### 2.2 Symbols (RL-2)

GitHub Packages' NuGet registry is not known to accept `.snupkg` **[unverified]**: the documentation did not answer, and a
community answer says it does not. A push can only be tested by a release.

| | size | debugging a consumer |
|---|---|---|
| (a) **PDB embedded in each DLL** (`DebugType=embedded`, no `.snupkg`) | 1.95 MB for 24 packages, +0.56 MB (+41 %) **[verified]** | works from the feed with Source Link; nothing else to host |
| (b) `.snupkg` pushed to the feed | 1.39 + 0.50 MB | only if the registry accepts and serves them **[unverified]** |
| (c) `.snupkg` as GitHub release assets | 1.39 MB in the feed | a developer downloads them by hand |

Recommended: **(a)**. A private framework is debugged by its owner; 0.56 MB across 24 packages is cheap, and nothing depends on
an unverified feed feature.

### 2.3 The first version (RL-3)

MinVer takes the version from the tag. D-6 wants the API baseline before 1.0.

* Recommended: **`0.1.0`**, first tagged as **`v0.1.0-preview.1`** (a real run of the whole workflow that consumers can ignore),
  then `v0.1.0`. The diagnostics move to `## Release 0.1.0`, the CHANGELOG section is `[0.1.0]`, the API files are shipped.
  0.x signals that the API may still change between minors; the API files show every change.
* Alternative: `1.0.0` (a stable promise now).

### 2.4 Meta-package (RL-4, D-8)

Generators already reach a consumer through package dependencies **[verified]** (§3.4), so a meta-package adds no analyzer
behaviour.

| | references | brings in |
|---|---|---|
| (a) **none** | the app references what it uses (e.g. `Tarol.Messaging` + `Tarol.Persistence.Npgsql` + `Tarol.Changes.AspNetCore`) | only what it uses |
| (b) `Tarol` = the in-process core | `Tarol.Messaging` (which already pulls Sagas, Changes, Persistence, Primitives, Kernel, Validations, Results, Options, StronglyTypedId) | the same as referencing `Tarol.Messaging` |
| (c) `Tarol` = everything | 24 packages | Npgsql, NATS.Net, RabbitMQ.Client, the SignalR client, JwtBearer, Zitadel, for every app |

Recommended: **(a)**, with a "which packages" table in the root README. (b) is one reference that exists already under another
name; (c) contradicts the small graphs every family was designed for.

### 2.5 PublicAPI rules (RL-5)

After generation only three rules remain **[verified]** (output §2):
* **RS0026** (24 hits, 12 pairs) / **RS0027** (1): "do not add multiple overloads with optional parameters". Every pair differs
  in a required parameter's type (`Run<T>(IChange<T>, …)` / `Run<T>(ICreation<T>, …)`, `Add<TJob>(string cron, …)` /
  `Add<TJob>(Schedule, …)`, `BindAsync` sync/async lambdas, `UseNpgsqlStore`/`UseNpgsqlBus` by connection string or data
  source). No call is ambiguous today (it compiles, 1863 tests). Removing the defaults from one overload breaks every caller of it.
* **RS0041** (30): "public members should not use oblivious types". 27 are members System.Text.Json generates into the three public
  JSON contexts, 3 are `Raise(Event)`/`Raise(Deleted)` (a nested type of a generic type). `Nullable` is enabled everywhere and
  nullable warnings are errors, so code cannot be oblivious without `#nullable disable`, which `src/` does not use.

Recommended: **(a)** `.editorconfig` severity `none` for RS0026, RS0027 and RS0041 with these reasons (listed in this note's
suppressions); RS0016/RS0017 stay errors. Alternative (b): change the 12 pairs (breaking) and make the three JSON contexts
internal (breaking: `KernelJsonContext.Default.OperationContext` is documented in Kernel's README and skill).

### 2.6 The release gate (RL-6)

| | on the tag | time |
|---|---|---|
| (a) **re-run CI** (`ci.yml` as `workflow_call`), then pack → check → publish | the full suite on the exact commit | ≈ 16 + 2 min |
| (b) pack → check → publish, trusting `main`'s checks | nothing proves the tagged commit passed (a tag can point at any commit) | ≈ 3 min |

Recommended: **(a)**, plus a GitHub environment `release` on the publish job: the owner can add a required reviewer there
(a click before anything leaves), and a tag ruleset restricting `v*` to the owner. Both are settings the owner makes; the
workflow names the environment.

### 2.7 CodeQL (RL-7)

* (a) **CodeQL** (`codeql.yml`, C#, `build-mode: manual` with the repository's own build, push to `main`, PRs, weekly,
  `security-extended`): if the repository stays public (RL-1 b), or with GitHub Code Security.
* (b) **.NET security rules** as errors (`AnalysisLevelSecurity=latest-all`; asked with "0 findings today", which was wrong: 31 in the
  packages, 49 in tests, §14): works in a private repository on any plan.
Recommended: **(b)** if RL-1 is (a); **(a) and (b)** if the repository stays public.

### 2.8 Split and branches (RL-8)

Recommended, four PRs, each green on its own, in this order:
* **17A** `ci: pin actions by SHA; reusable CI` (+ CodeQL or the security rules, RL-7);
* **17B** `build: the public API baseline` (analyzer, 48 files, `eng/public-api.py`, rule settings);
* **17C** `release: the release workflow` (`release.yml`, `eng/release.py`, its tests, the dry run);
* **17D** `release: ship 0.1.0` (Unshipped → Shipped for the diagnostics, `CHANGELOG` `[0.1.0]` with upgrade notes, symbols
  setting, plan row 17 done); then the owner creates `v0.1.0-preview.1`.

This session may push only to `ccr-23c21594-m0xy0g`. The design PR uses it; the four build PRs need four more branches
(`ccr-23c21594-m0xy0g-17a` … `-17d`), stacked as in step 16.

## 3. Measurements **[verified]** ([`output.txt`](probes/releasing/output.txt))

### 3.1 The public API (output §1, §3)

| | lines |
|---|---|
| Tarol.Persistence 727, Tarol.Messaging 445, Tarol.Primitives 363, Tarol.Queries 305, Tarol.Changes 215, Tarol.Operations 192, Tarol.Persistence.Npgsql 192, Tarol.Results 184, Tarol.Changes.AspNetCore 183, Tarol.Jobs 104, Tarol.Messaging.SignalR 91, Tarol.Sagas 84, Tarol.Queries.Npgsql 79, Tarol.Messaging.Npgsql 76, Tarol.Validations 69, Tarol.Kernel 63, Tarol.StronglyTypedId 63, Tarol.Caching 57, Tarol.Options 47, Tarol.Security 38, Tarol.Messaging.Nats 36, Tarol.DataProtection 33, Tarol.Messaging.RabbitMq 29, Tarol.Security.Zitadel 19 | **3,694** |
| of which oblivious (`~`, synthesized record members and nested types of generic types) | 72 |

### 3.2 Generating the baseline (output §3)

| Way | time | lines | misses |
|---|---|---|---|
| `dotnet format analyzers <project> --diagnostics RS0016 RS0037`, per project, repeated until stable (a solution run fixes one project only) | ≈ 10 min | 3,655 | the 39 members generated into the JSON contexts, also with `--include-generated` |
| **`eng/public-api.py update`**: one `dotnet build` with `ErrorLog=<project>.sarif,version=2.1`, the analyzer's `APIName` per RS0016, removals per RS0017 | **17.4 s** | 3,694 | nothing; identical to `dotnet format` on the 3,655 |

After generation, a strict build reports RS0016 0, RS0017 0 **[verified]**.

### 3.3 Build cost (output §4)

A full rebuild of the 24 packages (`--no-incremental`, alternating, min of 3): **6.60 s with the analyzer, 6.58 s without**.

### 3.4 Packages and generators (output §5, §10)

* 24 `.nupkg` 1,385,053 bytes + 24 `.snupkg` 495,792 bytes; with `DebugType=embedded` 1,949,447 bytes, no `.snupkg`.
* Every Tarol dependency in a nuspec carries `exclude="Build,Analyzers"` (32 of 32), the default `PrivateAssets` of a
  `ProjectReference`. The compiler still receives the dependencies' generators: `Tarol.Changes` alone → 7 generators
  (Changes, Kernel, Options, Primitives, Results, StronglyTypedId, Validations), `Tarol.Sagas` alone → the same 7.

### 3.5 CI and actions (output §6, §7)

* Run 44243854282 on `main`: build 3m50s, integration 3m51s, aot-smoke 8m02s = 15.7 runner-minutes; billable 0 ms.
* 233 workflow runs from 2026-11-12 to 2026-11-18.
* Pins: `checkout` v5 = v5.1.0 `fbc6f39…`, `setup-dotnet` v5 = v5.4.0 `26b0ec1…`, `cache` v4 = v4.3.0 `0057852…`,
  `upload-artifact` v4 = v4.6.2 `ea165f8…`, for the release `download-artifact` v5 = v5.0.0 `634f93c…`, for CodeQL
  `codeql-action` v4 = v4.39.3 `33d7b85…`. Newer majors exist (checkout v7, setup-dotnet v6, cache v6, upload-artifact v7):
  pinning keeps today's majors; moving majors stays Dependabot's PRs.

## 4. Found while probing

1. **The repository is public** (§2.1).
2. **`dotnet format` fixes one project per solution run** and skips generated code: a baseline made that way misses the JSON
   contexts' members. The SARIF log carries the exact line, so the generator script needs no Roslyn code of its own.
3. **`exclude="Build,Analyzers"` on every dependency does not stop generators from flowing** in SDK 11.0.100. The meta-package
   question is therefore not about analyzers. A package consumer test of a meta-package would have to prove it again (RL-4 b, c).
4. **`workflow_dispatch` needs the workflow on the default branch** **[unverified]**: the release PR proves its dry run through a
   `pull_request` trigger limited to the release files.
5. **The SignalR reconnect test failed once** while the probe builds ran beside the suite (`client.State` Disconnected after
   the server's restart), and passed 12 of 12 alone and in the rerun of the suite. Recorded; if it recurs it gets its own PR as
   #52 did.

## 5. Decided design

### 5.1 Licence (RL-1; 17D)

* `LICENSE` becomes the unmodified text of the GNU Affero General Public License v3.0 as published by the FSF. `README.md` and
  a `NOTICE` file carry the copyright line `Copyright (C) 2026 Christoph Lorat` and say that a commercial licence can be had
  from the owner.
* Packages declare `PackageLicenseExpression` `AGPL-3.0-only` (no "or later", so the owner keeps the choice) and still carry
  `LICENSE` as a plain file. NuGet allows only one of `PackageLicenseExpression` and `PackageLicenseFile` **[unverified]** until
  17D's pack.
* No per-file licence headers: AGPL recommends them, but does not require them.
* Contributions need a CLA before they are merged, so relicensing stays possible (§9.1). `CONTRIBUTING.md` says so.
* AGENTS.md, the conventions, plan D-1 and the root README change in the same PR. nuget.org stays excluded (RL-9).

### 5.2 Public API (RL-5; 17B)

* `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0 on the 24 runtime packages and the meta-package.
* `eng/public-api.py` with `update`, `ship` and `check-shipped`.
* The baseline goes straight into `PublicAPI.Shipped.txt`; every `Unshipped.txt` is empty.
* `.editorconfig`: RS0026, RS0027 and RS0041 `none`, each with its reason.
* RS0016/RS0017 stay errors: a public change without its line fails the build.

### 5.3 Meta-package (RL-4; 17C)

* `src/Meta/Tarol` (package id `Tarol`): no code (`IncludeBuildOutput=false`); references the 17 packages of §9 round 2.
* README and the skill `tarol-overview` (which package for what).
* A package consumer test references only `Tarol` and builds an aggregate, a change, a query and a consumer, which proves the
  generators flow.
* `verify-packages.sh` learns a package without `lib/`.

### 5.4 Release (RL-2, RL-3, RL-6, RL-9; 17E, 17F)

* `release.yml`:
  * triggers: `push: tags: [v*]`; `workflow_dispatch` with `dry_run` (default `true`); `pull_request` on the release files
    (dry run);
  * jobs: `ci` (uses `ci.yml`), `pack`, `publish` (environment `release`, `packages: write`, `contents: write`, skipped on a
    dry run);
  * push: `dotnet nuget push --skip-duplicate` to `https://nuget.pkg.github.com/CLorat/index.json` with `GITHUB_TOKEN`;
  * release: `gh release create` (a pre-release for `-…` versions).
* `eng/release.py` with `check <version>` and `notes <version>`, and unit tests (Python `unittest`, run in CI `build`).
* Symbols: `DebugType=embedded`, `IncludeSymbols` off (RL-2).
* Version: first `0.1.0`; the owner creates the first tag, `v0.1.0-preview.1` (RL-3).
* Packages public on GitHub Packages (RL-9).

### 5.5 CI (RL-7, D-13; 17A)

SHA pins for every `uses:`; `ci.yml` gains `workflow_call`; `codeql.yml`; the .NET security rules as decided in §14 (RL-10).

## 6. Earlier decisions that constrain the release (not re-asked)

| # | Decision | Consequence here |
|---|---|---|
| D-1 | proprietary, owner-only; packages only to a private feed, never nuget.org | GitHub Packages of `CLorat`; no `PackageLicenseExpression`; RL-1 |
| D-6 | PublicAPI analyzers before 1.0, the first release is the baseline | 17B, Shipped.txt = the baseline |
| D-8 | meta-package decided in step 17; analyzers must flow | RL-4; §3.4 |
| D-10 | generators inside their runtime package | unchanged; verified by `verify-packages.sh` |
| D-11 | GitHub Packages, `GITHUB_TOKEN` with `packages: write`, no Trusted Publishing | the publish job's only permission besides `contents: write` |
| D-13 | SHA pinning once a workflow publishes | 17A |
| DP-10 | version 3 steps compatible from 1; a sealing store raises `compatible_from` to 3; plaintext sealed only by `SealHistory<T>` | named in the release notes (§8) |

## 7. Plan impact

* **Row 17:** "designed", split 17A–17D (RL-8).
* **CI and release section:** visibility per RL-1; CodeQL per RL-7; `workflow_call`.
* **D-6, D-8, D-13:** decided here.
* **After step 17:** the KMS-backed key store (DP-1, DP-8); step 18 (samples).

## 8. What step 16 left for 17 (data-protection.md §8)

* The public API baseline includes the codec overloads and the new ports: they are in the generated files
  (`Tarol.Persistence`: `IKeyStore`, `IKeyEncryptionKey`, the codec's sealing overloads; `Tarol.DataProtection`: 33 lines)
  **[verified]**, since the baseline is every public symbol.
* The release notes name schema version 3 and DP-10, and carry the guide's backup note: the `[0.1.0]` CHANGELOG section gets
  "Upgrade notes" (17D).

## 9. Owner answers

Round 1 (2026-11-18), after the inventory and the probes: RL-1 … RL-4. The prefix RL- was free (taken: C-, CA-, D-, DP-, J-, M-,
O-, P-, Q-, QR-, R-, S-, T-).

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| RL-1 | The repository is public, the plan keeps it private (D-1). Private: packages private by inheritance, CI billed (15.7 runner-minutes per run, 233 runs in 6 days), CodeQL only with Code Security. Public: CodeQL and minutes free, source readable, 24 packages to set private by hand | **Stay public, and change the licence** (owner's words: "Stay Public and we change the license to something that can be used but not commercialised and only in free open source, and if [you] want to use [it] in business [earning] money we need written allowance. Explain the options"). The licence options are §9.1; D-1 is reopened by the owner. |
| RL-2 | Symbols: `.snupkg` on GitHub Packages unverified; PDB embedded 1.95 MB vs 1.39 + 0.50 MB | **Embed PDBs** (recommended): `DebugType=embedded`, no `.snupkg`. |
| RL-3 | The first version | **`0.1.0`, first tagged `v0.1.0-preview.1`** (recommended). |
| RL-4 | Meta-package (generators flow already) | **`Tarol` = everything except the specific implementations** (owner's words: "Make Tarol everything but not the specific impl like rabbitmq and so"). Which packages count as specific: §9.2, asked in round 2. |

Round 2 (2026-11-18):

| # | Question | Answer |
|---|---|---|
| RL-1 (licence) | Which licence replaces `LICENSE`: PolyForm Noncommercial 1.0.0, PolyForm + an "open-source projects only" clause, BUSL-1.1, AGPL-3.0 + a commercial licence | **Open.** The owner asked first: can PolyForm be switched to AGPL-3.0 later; can AGPL-3.0 be used without a commercial offer yet; what support each requires; how liability for an incident caused by the library is covered. Answered in the chat (§9.1), asked again in round 3. |
| RL-4 (members) | Which packages does `Tarol` hold (the brokers, the Npgsql bus, SignalR and Zitadel are out in every option)? | **17 packages, ASP.NET Core included, Npgsql not** (recommended): Options, Results, StronglyTypedId, Kernel, Validations, Primitives, Persistence, Changes, Changes.AspNetCore, Queries, Caching, Sagas, Messaging, Jobs, Security, Operations, DataProtection. `Tarol.Persistence.Npgsql`, `Tarol.Queries.Npgsql` and the transports are referenced separately. |
| RL-9 | Who installs the packages, now that the repository stays public? | **Public on GitHub Packages** (recommended): the packages follow the repository; nuget.org stays excluded (a decision of its own). |
| RL-5 | RS0026/RS0027 (12 overload pairs), RS0041 (30 oblivious symbols) | **Configure off with reasons** (recommended): `.editorconfig` severity `none` for the three rules, each with its reason; RS0016/RS0017 stay errors. |

Round 3 (2026-11-18):

| # | Question | Answer |
|---|---|---|
| RL-1 (licence) | With the answers of §9.1: PolyForm Noncommercial, PolyForm + OSS-only clause, AGPL-3.0, or keep proprietary | **AGPL-3.0 now** (owner's choice; the recommendation was PolyForm Noncommercial). Commercial use stays allowed under AGPL's terms; a paid licence for others can be offered later. D-1 changes accordingly (§5.1). |
| RL-6 | The release gate | **Re-run CI on the tag + environment `release`** (recommended): `ci.yml` as `workflow_call`; the publish job in the environment `release`; the owner adds a required reviewer and a `v*` tag ruleset in the settings. |
| RL-7 | Code security with a public repository | **CodeQL + .NET security rules** (recommended): `codeql.yml` (C#, manual build, push to `main`, PRs, weekly, `security-extended`) and the .NET security rules, asked as `AnalysisModeSecurity=All` with "0 findings". That number was wrong (§14); how to handle the 80 findings is RL-10. |
| RL-8 | The split, and branches beyond `ccr-23c21594-m0xy0g` | **Six PRs on branches `ccr-23c21594-m0xy0g-17a` … `-17f`** (recommended), stacked; the order is §11. |

### 9.1 The licence questions (answered in the chat, 2026-11-18; not legal advice)

* **PolyForm now, AGPL-3.0 later?** Yes. As the only copyright holder the owner can license later versions under any licence.
  Versions already released stay usable under the licence they came with. Once others contribute code, relicensing needs their
  consent, so contributions need a contributor licence agreement (CLA) from the start.
* **AGPL-3.0 with no commercial offer yet?** Yes; AGPL-3.0 stands on its own, and a commercial licence can be offered at any time.
  But AGPL-3.0 allows commercial use by anyone who publishes their whole application's source, so it does not forbid business use.
* **Support**: neither licence obliges the owner to support, maintain, fix or answer. Support exists only where a contract
  (e.g. a paid commercial licence) promises it.
* **Liability**: both disclaim warranty and liability "as far as the law allows". Under German and EU law a gratuitous licence
  still cannot exclude liability for intent and gross negligence. The new EU Product Liability Directive (2024/2853) treats
  software as a product but excludes free and open-source software supplied outside a commercial activity. The Cyber
  Resilience Act likewise applies to products made available in the course of a commercial activity. Selling commercial
  licences can therefore bring those versions into scope. Cover in practice: the licence's disclaimer, a liability cap in every
  commercial contract, and IT liability insurance; a lawyer for the commercial terms.

## 10. Changes against steps 1–16

None in this PR. 17B adds two files per package and three `.editorconfig` entries (RL-5); 17D may change the symbol format
(RL-2). Listed in their sections when built.

## 11. What each PR must do

The six PRs are stacked: 17A on `main`, each next one on the previous. Each is green on its own, and each is retargeted to
`main` before its base merges.

### PR 17A `ci: SHA-pinned actions, reusable CI, CodeQL, security rules`
§5.5, plus conventions §7 and the plan's CI section. Verification: the PR's CI and CodeQL runs; a local build.

### PR 17B `build: the public API baseline`
§5.2. Verification: full (build, format, tests with Docker, AOT, pack); a deliberate public change fails the build (recorded).

### PR 17C `meta: the Tarol meta-package`
§5.3. Verification: full; the consumer test.

### PR 17D `licence: AGPL-3.0`
§5.1. Verification: the pack shows the licence expression; `verify-packages.sh` checks it.

### PR 17E `release: the release workflow`
§5.4 without the shipping. Verification: the PR's dry run (everything up to `publish`); the script's tests.

### PR 17F `release: ship 0.1.0`
* The diagnostics move to `## Release 0.1.0`.
* `CHANGELOG` gets `[0.1.0]` with the upgrade notes (§8).
* PDBs are embedded.
* Plan row 17 becomes "done".

Then the owner creates `v0.1.0-preview.1` (asked before).

## 12. Verification of this PR

* `main` at `45da502` in a detached worktree with Docker (PostgreSQL 17, NATS 2.11, RabbitMQ 4) **[verified]**:
  * `dotnet build Tarol.slnx`: 0 warnings, 0 errors;
  * `dotnet format Tarol.slnx --verify-no-changes`: clean;
  * `dotnet test --solution Tarol.slnx`: **1863 of 1863** (the first run, beside the probe builds, failed the SignalR reconnect
    test once; §4 item 5);
  * `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **24 apps, 0 IL warnings**;
  * `eng/verify-packages.sh`: OK.
* `main`'s CI run 44243854282: `build`, `integration`, `aot-smoke` green.
* This PR changes docs only.

## 13. Open items

1. Whether GitHub Packages accepts `.snupkg` and what visibility a new package gets: only a real push answers it (RL-1, RL-2).
2. Dependabot and SHA pins with version comments: confirmed by its first PR after 17A.

## 14. Correction: the .NET security rules (found in 17A)

RL-7 (b) was asked with "`AnalysisModeSecurity=All` finds 0 diagnostics". That property switches nothing on **[verified]**:
* a deliberate `new System.Random().Next()` in `Tarol.Kernel` gives no CA5394 with it;
* it gives CA5394 with `AnalysisLevelSecurity=latest-all`.

With the working property the solution has **80 findings** **[verified]** (output §11):

| Where | Rule | Count | What it is **[verified by reading]** |
|---|---|---|---|
| `Tarol.Persistence.Npgsql`, `Tarol.Queries.Npgsql`, `Tarol.Messaging.Npgsql` | CA2100 "review SQL for user input" | 30 | SQL text built from the schema and projection names, checked against `^[a-z_][a-z0-9_]{0,62}$`, plus constants; every value is a parameter. CA2100 flags any non-constant command text; it does no data-flow analysis |
| `Tarol.Messaging.SignalR` | CA5394 insecure `Random` | 1 | the reconnect delay's jitter (`Random.Shared`), not a security use |
| tests, AOT smoke apps | CA5394, CA2100, CA5399 | 49 | random test data, SQL from test constants, an `HttpClient` without revocation check in a smoke app |

How to handle them was asked of the owner again (2026-11-18):

| # | Question | Answer |
|---|---|---|
| RL-10 | The .NET security rules with 80 findings: all on with CA2100/CA5394 off, all on with a justified suppression per site, or CodeQL only | **All on; CA2100 and CA5394 off** (recommended): `AnalysisLevelSecurity=latest-all` in `Directory.Build.props`, so every security rule is an error; `.editorconfig` sets CA2100 (no data flow; SQL from validated identifiers; CodeQL's SQL-injection query covers it) and CA5394 (`Random` only for jitter and test data) to `none`, for tests and smoke apps too. No code changes. |

## 15. Implementation 17A: SHA-pinned actions, reusable CI, CodeQL, security rules

### 15.1 What changed

* `ci.yml`:
  * the 12 `uses:` are pinned to commit SHAs with the tag as a comment (§3.5: checkout v5.1.0, setup-dotnet v5.4.0, cache v4.3.0,
    upload-artifact v4.6.2);
  * `workflow_call` added, so the release can run the whole CI on the tag;
  * the concurrency group is `ci-${{ github.workflow }}-${{ github.ref }}`. When the release calls the file, `github.workflow`
    is the caller's name, so a dry run on a PR does not cancel that PR's own CI (both have the same `github.ref`).
* `codeql.yml` (new): C#, `build-mode: manual` (`dotnet build -c Release -p:UseSharedCompilation=false`, so CodeQL traces every
  compiler call), `security-extended`; push to `main`, PRs, Mondays 04:17 UTC, `workflow_dispatch`; `security-events: write`
  on the job only.
* `Directory.Build.props`: `AnalysisLevelSecurity=latest-all` (RL-10).
* `.editorconfig`: CA2100 and CA5394 `none`, with the reasons of §14.
* `tests/Operations/Tarol.Operations.AotSmoke`: its `HttpClientHandler` checks certificate revocation, fixing CA5399 instead of
  configuring it.
* Conventions §7 and the plan's CI section.
* Dependabot unchanged: its `github-actions` ecosystem updates SHA pins **[unverified]** until its first PR (§13 item 2).

### 15.2 Public API and behaviour

None: no package code changed. The security rules found nothing beyond CA2100/CA5394 in the packages (§14).

### 15.3 Suppressions

* `.editorconfig`: CA2100, CA5394 `none` (RL-10, reasons in the file and §14).


### 15.4 Verification (SDK 11.0.100, 2026-11-18) **[verified]**

In the branch's worktree with Docker (PostgreSQL 17, NATS 2.11, RabbitMQ 4):
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors, with every security rule on;
* `dotnet format Tarol.slnx --verify-no-changes`: clean;
* `dotnet test --solution Tarol.slnx`: **1863 of 1863**;
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **24 apps, 0 IL warnings**;
* `eng/verify-packages.sh`: OK;
* both workflows parse as YAML with the expected triggers and jobs. `actionlint` was not run here (downloading a binary was
  not allowed in this session), so the PR's own CI and CodeQL runs prove the workflows.

## 16. Implementation 17B: the public API baseline

### 16.1 What changed

* `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0 (`Directory.Packages.props`) on every runtime package
  (`src/Directory.Build.props`, not the generators). Each package's `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` are
  `AdditionalFiles`.
* `eng/public-api.py`, the probe's script moved to `eng/`:
  * `update` builds once with `ErrorLog=<project>.sarif` (property `TarolPublicApiLog`) and writes the analyzer's own lines;
  * `ship` moves Unshipped to Shipped;
  * `check-shipped` fails while any Unshipped has a line.
* **The baseline**: 48 files with 3,694 lines; `update`, then `ship`. Every `PublicAPI.Shipped.txt` holds the package's API,
  and every `PublicAPI.Unshipped.txt` holds only `#nullable enable`.
* `src/PublicApi.globalconfig` (added by `src/Directory.Build.props`): RS0026, RS0027, RS0041 `none`, with reasons (RL-5).
* Conventions §4.1 and §8, `AGENTS.md` and the repository skill `tarol-migrate-package` say how a package keeps its files.
* `CHANGELOG.md` (Unreleased).

### 16.2 Deliberate differences from §5.2

1. **A global config, not `.editorconfig`**: `.editorconfig` sections do not reach generated sources, and RS0041 fired as an
   error in the members System.Text.Json generates **[verified]**. A global analyzer config applies to every file.
2. **No `check-shipped` in CI**: every PR that adds API puts lines into `Unshipped`. So the release (17E) runs `check-shipped`,
   not CI. RS0016/RS0017 are the CI check.

### 16.3 The check works **[verified]**

* A new public type in `Tarol.Options` (`ApiProbe.One()`) fails the build: `RS0016: Symbol 'Tarol.Options.ApiProbe' is not part
  of the declared public API` (and for `One`).
* A line for a symbol that does not exist (`static Tarol.Options.Gone.Away() -> int`) fails it with `RS0017`.
* Both were reverted; the build is then clean again.

### 16.4 Public API and behaviour

No API or behaviour changed. Every public change from now on is an error until `eng/public-api.py update` records it.

### 16.5 Suppressions

* `src/PublicApi.globalconfig`: RS0026, RS0027, RS0041 `none` (RL-5; reasons in the file and §2.5).


### 16.6 Verification (SDK 11.0.100, 2026-11-18) **[verified]**

In the branch's worktree with Docker (PostgreSQL 17, NATS 2.11, RabbitMQ 4):
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors (RS0016/RS0017 errors on every runtime package);
* `dotnet format Tarol.slnx --verify-no-changes`: clean;
* `dotnet test --solution Tarol.slnx`: **1863 of 1863**;
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **24 apps, 0 IL warnings**;
* `eng/verify-packages.sh`: OK;
* `eng/public-api.py check-shipped`: OK (every Unshipped empty).

## 17. Implementation 17C: the meta-package `Tarol`

### 17.1 What changed

* `src/Meta/Tarol` (new):
  * `ProjectReference`s to the 17 packages of RL-4;
  * `IncludeBuildOutput=false`, `IncludeSymbols=false`, and `lib/net11.0/_._` (an empty file) as the framework marker, so NuGet
    raises no NU5128;
  * `IsAotCompatible=false` with a comment: no code, so no AOT claim and no smoke app;
  * empty `PublicAPI.*.txt`; README; skill `tarol-overview`.
* `tests/Meta/Tarol.PackageTests` (new):
  * references only `Tarol`;
  * an MSBuild check fails unless all 10 generators reach the compiler (Changes, Changes.AspNetCore, Kernel, Messaging,
    Options, Primitives, Queries, Results, StronglyTypedId, Validations);
  * it runs a consumer and a change, a publication and an effect, and a projection search;
  * its `nuget.config` maps `Tarol` as well as `Tarol.*` to the local feed.
* `eng/verify-packages.sh`: a package whose csproj sets `IncludeBuildOutput` false must contain `lib/net11.0/_._` instead of
  its DLL.
* `Tarol.slnx` (folder `/src/Meta/`), the root README's package table, conventions §2 (the naming exception), `CHANGELOG.md`.

### 17.2 Verified **[verified]**

* The `.nupkg` holds `LICENSE`, `README.md`, `lib/net11.0/_._` and `skills/tarol-overview/SKILL.md`, plus 17 dependencies
  (each `exclude="Build,Analyzers"`, the default).
* The consumer's compiler received all 10 generators, and the app printed `ALL CHECKS PASSED`.

### 17.3 Deliberate differences from §5.3

* The consumer checks a consumer, a change, a publication, an effect and a projection. Its aggregate is the Messaging consumer
  test's `Board`, plus the Queries consumer test's `Note`.

### 17.4 Skill checklist (`tarol-overview`)

| Task | Expected | Mistake to avoid |
|---|---|---|
| start a web app with PostgreSQL | `Tarol` + `Tarol.Persistence.Npgsql`, then `tarol-primitives` and `tarol-changes` | expecting `Tarol` to bring the store (`UseNpgsqlStore` missing) |
| a worker service without HTTP | the used packages instead of `Tarol` (no ASP.NET Core framework) | referencing `Tarol` and pulling in ASP.NET Core |
| a generator does not run | nothing to add: generators are inside the runtime packages | adding a `*.Generators` reference |
| consume from NATS | `Tarol` + `Tarol.Messaging.Nats`, skill `tarol-messaging-nats` | looking for NATS inside `Tarol` |

### 17.5 Public API and behaviour

A new package, `Tarol`. It has no API of its own, and no other package changed.

