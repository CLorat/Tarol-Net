---
name: tarol-overview
description: >-
  Use when starting a Tarol app or deciding which Tarol package or skill covers a task: the meta-package Tarol, the package
  map (aggregates, changes, queries, reactions, messaging, jobs, security, operations, data protection) and which skill to
  load next. Do not decompile packages or web-search for Tarol's API; load the named package's skill.
---
# Tarol: which package, which skill

## Rules

* Reference `Tarol` for the in-process framework, then add the implementations you run on: `Tarol.Persistence.Npgsql` (the
  event store), `Tarol.Queries.Npgsql` (read tables), one transport (`Tarol.Messaging.Npgsql`, `.Nats`, `.RabbitMq`),
  `Tarol.Messaging.SignalR` (UI push) and `Tarol.Security.Zitadel` as needed.
* Every included package's generator runs in your project. Do not add a package's `*.Generators` assembly yourself: generators
  ship inside their runtime package.
* `Tarol` brings the ASP.NET Core shared framework (through `Tarol.Changes.AspNetCore`, `Tarol.Security`, `Tarol.Operations`).
  A worker without HTTP references the packages it uses instead.

## Which skill for what

| Task | Package | Skill |
|---|---|---|
| ids, `Option<T>`, `Result<T>`, value objects with rules | Tarol.StronglyTypedId, Tarol.Options, Tarol.Results, Tarol.Validations | `tarol-strongly-typed-id`, `tarol-options`, `tarol-results`, `tarol-validations` |
| who acts, tenant, correlation, the clock | Tarol.Kernel | `tarol-kernel` |
| aggregates, events, entities, invariants, lifecycles | Tarol.Primitives | `tarol-primitives` |
| storing and loading aggregates, upcasters, `[PersonalData]` sealing | Tarol.Persistence, Tarol.Persistence.Npgsql | `tarol-persistence`, `tarol-persistence-npgsql` |
| a change (command) and its pipeline; HTTP entries | Tarol.Changes, Tarol.Changes.AspNetCore | `tarol-changes`, `tarol-endpoints` |
| projections, lists, the page cache | Tarol.Queries, Tarol.Queries.Npgsql, Tarol.Caching | `tarol-queries`, `tarol-queries-npgsql`, `tarol-caching` |
| global search over aggregates and entities | Tarol.Search (+ Tarol.Search.Npgsql) | `tarol-search` |
| reacting to own events, sagas | Tarol.Sagas | `tarol-sagas` |
| consuming and publishing messages, effects | Tarol.Messaging (+ a transport) | `tarol-messaging` |
| scheduled jobs | Tarol.Jobs | `tarol-jobs` |
| authorization of changes and queries | Tarol.Security | `tarol-security` |
| health, metrics, the admin entry | Tarol.Operations | `tarol-operations` |
| erasure, export, retention | Tarol.DataProtection | `tarol-data-protection` |

## Minimal start

```xml
<PackageReference Include="Tarol" Version="0.1.0" />
<PackageReference Include="Tarol.Persistence.Npgsql" Version="0.1.0" />
```

Then load `tarol-primitives` for the first aggregate and `tarol-changes` for the first change.

## Common mistakes

* A `*.Generators` package reference: there is none; the generator is in the runtime package.
* Expecting `Tarol` to bring PostgreSQL or a broker: it brings no implementation. `UseNpgsqlStore` needs
  `Tarol.Persistence.Npgsql`.

## AOT

`Tarol` has no code. Each included package carries its own AOT claim; see its skill.
