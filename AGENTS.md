# AGENTS.md

Instructions for coding agents (Claude Code, Copilot, Codex, …) working in **Tarol-Net**.
`CLAUDE.md` and `.github/copilot-instructions.md` point here; this file is the single source.

## What this repository is

Tarol is a .NET 11 framework for event-sourced Domain-Driven Design: every change is an event, user code
stays small because the plumbing is source-generated, everything is Native-AOT ready, persistence is a
thin Npgsql event store. Tarol-Net is the clean successor of `CLorat/Tarol`; packages are migrated one
family at a time ([docs/migration/plan.md](docs/migration/plan.md)).

## Build, test, format

SDK 11.0.x (pinned by `global.json`; test runner is Microsoft.Testing.Platform).

```bash
dotnet build Tarol.slnx
dotnet test --solution Tarol.slnx
dotnet format Tarol.slnx --verify-no-changes
dotnet pack Tarol.slnx -c Release          # -> artifacts/package/release
eng/verify-packages.sh                     # inspects the .nupkg files and runs the package consumer tests
eng/aot-smoke.sh                           # publishes every *.AotSmoke app with Native AOT, 0 IL warnings
```

Generator snapshot tests: on a mismatch a `*.received.*` file is written next to the `*.verified.*` file.
Review the diff, then accept with `TAROL_ACCEPT_SNAPSHOTS=1 dotnet test --solution Tarol.slnx`.

## Rules

* Read [docs/architecture/conventions.md](docs/architecture/conventions.md) before adding or moving a project.
  Its §8 checklist is the definition of done for every package.
* Project name = assembly name = root namespace = package id (`Tarol.<Family>[.<Part>]`).
* Warnings are errors everywhere. Never add `NoWarn`. Fix the code, or configure the rule in `.editorconfig`
  with a reason, or suppress at the narrowest scope with a `Justification`, and list it in the family's
  migration note.
* No reflection on hot or AOT paths. Anything that needs it carries `[RequiresUnreferencedCode]` /
  `[RequiresDynamicCode]` and the package says so in its README.
* Every runtime package ships: `README.md` (NuGet readme), `skills/tarol-<topic>/SKILL.md`, XML docs for all
  public API, tests, and an AOT smoke app if it claims AOT.
* Generators ship inside their runtime package (`TarolGenerator="true"` project reference); never a separate
  package, never a `build/*.targets` that loads the analyzer again.
* Package versions only in `Directory.Packages.props`; never reference a package the shared framework provides.
* The licence is proprietary and owner-only (`LICENSE`, packed into every package). Do not change it, do not add
  open-source licence headers or `PackageLicenseExpression`, and do not set sponsorship or other legal claims
  (e.g. Verify's properties). Never publish packages to nuget.org.
* Do not change behaviour silently: every public API or behaviour change is listed in the migration note.
* Pull requests: one clear feature per PR (one migration step, or one self-contained part of it); body follows
  `.github/pull_request_template.md`. See docs/migration/plan.md#pull-requests.
* Commits: small, imperative subject (`stronglytypedid: register inherited ids`), body explains why.

## Skills: what you must add with a package

For every package users write code against, add `src/<Family>/<Package>/skills/tarol-<topic>/SKILL.md`:

```markdown
---
name: tarol-<topic>            # = folder name; lowercase, digits, hyphens
description: >-
  Use when <task>. Covers <API surface>. Do not <expensive wrong approach, e.g. decompile or web-search>.
---
# <Title>
Rules first, then one minimal correct example, then common mistakes with the diagnostic or exception
they produce, then AOT notes.
```

* Write it from the code and tests, not from memory; every API name in it must compile.
* Keep it short (≈ 2–8 KB). It is loaded into an agent's context.
* No instructions to download, execute, change settings or disable analyzers (supply-chain rule).
* Add an evaluation checklist for the skill to `docs/migration/<family>.md` (tasks, expected API, mistakes to avoid).
* `eng/verify-packages.sh` checks that the skill is in the `.nupkg` and listed in a consumer's `project.assets.json`.

## Where things are

| Path | Content |
|---|---|
| `src/<Family>/` | runtime packages and their generators |
| `tests/<Family>/` | unit, generator, integration, AOT smoke and package consumer tests |
| `eng/` | packaging targets and CI scripts |
| `docs/architecture/` | conventions and design decisions |
| `docs/migration/` | the migration plan and one note per migrated family |
| `.claude/skills/` | skills for agents working on this repository (not shipped) |
