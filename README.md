# Tarol

Tarol is a .NET 11 framework for event-sourced Domain-Driven Design:

* **Every change is an event**, so state can always be rebuilt from the event stream.
* **User code stays small**: the plumbing is source-generated, and each generator ships inside its runtime package.
* **Native-AOT ready**: no reflection on the hot path; every package that claims AOT has a smoke app in CI.
* **Persistence on Npgsql**: a thin event store instead of a document database (planned).

Tarol-Net is the successor of [CLorat/Tarol](https://github.com/CLorat/Tarol). Packages move over one family at a
time; see the [migration plan](docs/migration/plan.md).

## Packages

| Package | Description | Status |
|---|---|---|
| `Tarol.StronglyTypedId` | `Id<TEntity>` over UUIDv7, JSON/binding/formatting built in, generator included | migrated |
| `Tarol.StronglyTypedId.EntityFrameworkCore` | maps every `Id<T>` property with one line | migrated |
| `Tarol.Results`, `Tarol.Options`, `Tarol.Kernel`, `Tarol.Validations`, `Tarol.Primitives`, … | | planned (see plan) |

Nothing is published to nuget.org yet (the licence is still to be decided).

## Quick start

```csharp
using Tarol.StronglyTypedId;

public sealed class Customer { public Id<Customer> Id { get; } = Id<Customer>.New(); }

app.MapGet("/customers/{id}", (Id<Customer> id) => …);   // binds, serializes, no configuration
```

## Build

Requires the .NET SDK 11.0.x (`global.json`).

```bash
dotnet build Tarol.slnx
```

```bash
dotnet test --solution Tarol.slnx
```

```bash
eng/verify-packages.sh
```

```bash
eng/aot-smoke.sh
```

See [CONTRIBUTING.md](CONTRIBUTING.md) and, for coding agents, [AGENTS.md](AGENTS.md).

## Licence

Not decided yet; until then no licence is granted.
