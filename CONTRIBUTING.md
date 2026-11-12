# Contributing

## Prerequisites

.NET SDK 11.0.x (`global.json` rolls forward to the latest 11.0 feature band). Tests run on
Microsoft.Testing.Platform (selected in `global.json`).

## Build and test

```bash
dotnet build Tarol.slnx
```

```bash
dotnet test --solution Tarol.slnx
```

```bash
dotnet format Tarol.slnx --verify-no-changes
```

```bash
eng/verify-packages.sh
```

```bash
eng/aot-smoke.sh
```

All five run in CI. Warnings are errors locally and in CI.

## Generator snapshot tests

Generator tests compare generated code with committed `Snapshots/**/*.verified.*` files. On a mismatch the test
writes `*.received.*` next to them. Review the difference; if it is intended, rename the received file or run:

```bash
TAROL_ACCEPT_SNAPSHOTS=1 dotnet test --solution Tarol.slnx
```

Commit the updated `*.verified.*` files with the change that caused them.

## Adding or migrating a package

Follow [docs/architecture/conventions.md](docs/architecture/conventions.md); its §8 checklist is the definition of
done (code, tests, AOT smoke app, README, agent skill, package verification, docs).

## Commits and pull requests

* Small commits; subject `<area>: <imperative summary>` (for example `stronglytypedid: register inherited ids`),
  body explains why.
* A moved or renamed file is moved in its own commit (`git mv`), without content changes.
* Every public API or behaviour change is listed in the family's `docs/migration/<family>.md` and in `CHANGELOG.md`.
