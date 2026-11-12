## What and why

<!-- One clear feature: which step or part of the migration plan this PR delivers, and why. -->

## Changes against Tarol / public API and behaviour

<!-- Every public API or behaviour change, or "none". Details belong in docs/migration/<family>.md. -->

## Suppressions and analyzer configuration

<!-- Every new suppression or .editorconfig severity change with its reason, or "none". -->

## Verification

- [ ] `dotnet format Tarol.slnx --verify-no-changes`
- [ ] `dotnet build Tarol.slnx` (0 warnings)
- [ ] `dotnet test --solution Tarol.slnx` (counts: …)
- [ ] `eng/aot-smoke.sh` (0 IL warnings)
- [ ] `eng/verify-packages.sh`
- [ ] Migration note, plan status, CHANGELOG and README updated

## Not in this PR

<!-- What is deliberately left out, and where it will be done. -->
