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
| `Tarol.Results` | `Result<T>` / `Result` as non-boxing C# 15 unions of a value and a `Violation` (`Invalid` field errors or one `Error`), and `Option<T>` (`T` \| `None`, the same for value and reference types); AOT-safe JSON, analyzers included | migrated |
| `Tarol.Kernel`, `Tarol.Validations`, `Tarol.Primitives`, … | | planned (see plan) |

Packages are private: they are published only to the owner's GitHub Packages feed, never to nuget.org.

## Quick start

```csharp
using Tarol.StronglyTypedId;

public sealed class Customer { public Id<Customer> Id { get; } = Id<Customer>.New(); }

app.MapGet("/customers/{id}", (Id<Customer> id) => …);   // binds, serializes, no configuration
```

```csharp
using Tarol.Results;

Result<Customer> Find(Id<Customer> id) => store.TryGet(id, out var c) ? c : Error.NotFound<Customer, Id<Customer>>(id);

var response = Find(id) switch
{
    Customer c => TypedResults.Ok(c),
    Invalid i  => TypedResults.ValidationProblem(…),
    Error e    => TypedResults.Problem(statusCode: e.ToHttpStatusCode(), title: e.Code),
};
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

Proprietary, for the copyright holder's use only. All rights reserved; see [LICENSE](LICENSE).
