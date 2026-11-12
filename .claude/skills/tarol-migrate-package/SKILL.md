---
name: tarol-migrate-package
description: >-
  Use when migrating a package family from CLorat/Tarol into Tarol-Net, or when adding a new Tarol
  package. Gives the exact sequence, the files every package needs and the verification commands.
  Do not invent a layout; follow docs/architecture/conventions.md.
---

# Migrate a Tarol package family

1. Read `docs/migration/plan.md` (status, order, decisions) and `docs/architecture/conventions.md`.
2. In `CLorat/Tarol`, read the family's code, tests and its review file in `docs/review/`.
3. Write `docs/migration/<family>.md` **before** porting: origin commit, review (.NET 11 / C# 15,
   AOT, performance, developer experience), planned API and behaviour changes, open questions.
4. Create `src/<Family>/Tarol.<Family>/` (+ `.Generators`, satellites) and `tests/<Family>/`
   (`.Tests`, `.Generators.Tests`, `.AotSmoke`, `.PackageTests`). Names = assembly names.
5. Port, then improve. Keep csproj files minimal; inherit from the layered props.
6. Add `README.md` and `skills/tarol-<topic>/SKILL.md` next to the runtime csproj.
7. Add every project to `Tarol.slnx` (`dotnet sln Tarol.slnx add <csproj> --solution-folder <path>`),
   except `*.PackageTests`, which restore from the local feed and are run by `eng/verify-packages.sh`.
8. Verify, and paste the results into the migration note:

```bash
dotnet format Tarol.slnx --verify-no-changes
dotnet build Tarol.slnx
dotnet test --solution Tarol.slnx
dotnet pack Tarol.slnx -c Release
eng/verify-packages.sh
eng/aot-smoke.sh
```

9. Update the plan's status table, `CHANGELOG.md` (Unreleased) and the package table in `README.md`.
