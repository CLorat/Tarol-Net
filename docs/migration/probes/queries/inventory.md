# Inventory: query, pagination and read-model code in `CLorat/Tarol@6a62f81` **[verified by reading]**

Searched `src`, `gen`, `tests`, `examples`, `templates` for `IQueryable`, `Expression<`, `IQuery`, `QueryHandler`,
`ReadModel`, `Projection`, `Specification`, `OrderBy`, `.Skip(`, `.Take(`, `Cursor`, `PageSize`, `TotalCount`, `SortBy`,
`AsNoTracking`, `ToListAsync`, `MapGet`, `Snapshot`, `View`. Nothing builds or runs here (no package to migrate; see §4).
All projects target `net11.0` (`Directory.Build.props:3`).

## 1. `src/Paginations/Paginations` (package `Tarol.Paginations`): 17 lines in 3 files

One project reference (`StronglyTypedId`, `Paginations.csproj:3`); no reflection, no `IQueryable`, no EF Core.

| Type | file:line | What |
|---|---|---|
| `interface IPaginationRequest { int PageSize { get; init; } }` | `IPaginationRequest.cs:3-6` | common base; `init` on an interface |
| `record OffsetPagination(int Page = 1, int PageSize = 10)` | `Pagination.cs:5` | page number; no bounds (Page ≤ 0, PageSize ≤ 0 or unbounded accepted) |
| `record TokenPagination<T>(Id<T>? Token = null, int PageSize = 10)` | `Pagination.cs:8` | keyset by the last `Id<T>` only: assumes the sort is the id (v7 ids sort by time) |
| `record PaginationResult<T>(IAsyncEnumerable<T> Items, int TotalCount)` | `PaginationResult.cs:3-6` | a lazy stream + a total count; empty body |

No next cursor, no `HasMore`, no sort or filter specification, no maximum page size, no tenant. Tests:
`tests/Paginations/Paginations.UnitTests/Class1.cs` is an empty class: **0 tests** (REVIEW.md:175 "Zero tests ran").
Used by nothing but the template csprojs (`templates/TarolSolution/src/$product$.Contracts/$product$.Contracts.csproj:5`,
`$product$.Domain/$product$.Domain.csproj:5`) and `Tarol.sln:52,68`.

## 2. `src/CQRS/CQRS` + `gen/CQRS/CQRS.Generators`: the query side of the old CQRS

| Type | file:line | What |
|---|---|---|
| `interface IQuery<TResponse> : IRequest` | `IRequest.cs:9` | query marker |
| `IQueryHandler<in TQuery, TResponse>` (`ValueTask<TResponse> HandleAsync(…)`) | `IRequestHandler.cs:6-9,14` | handler |
| generated `IDispatcher` facade | `CqrsDispatcherGenerator.cs:163-167` | `dispatcher.Module.Component.Request(args, ct)` |

No `IQuery` is implemented anywhere (`tests/CQRS/CQRS.UnitTests/CommandDefinition.cs` has commands only; 0 tests ran).
The generator matches `ICommand`/`IQuery` by simple name (`:68-69`) and its fallback emits the nonexistent
`Tarol.CQRS.Shared.ICommandHandler` for queries too (`:305`). C-3 did not migrate CQRS (Changes is the only write model).

## 3. Read-shaped code elsewhere (not query APIs)

| Item | file:line | Relevance |
|---|---|---|
| `IAggregateRootRepository<T>.GetById` | `src/Persistence/Persistence/IAggregateRootRepository.cs:14-21` | the only read API; no list or find |
| `MartenSnapshotRepository<T,TSnapshot>` | `src/Persistence/Persistence.Marten/MartenSnapshotRepository.cs:31,56,71-90` | snapshot by primary key = a de-facto per-aggregate read model; a commented dual-write plan (`:99-135`) |
| `MartenSagaStore.ClaimDueAsync` | `src/Sagas/Sagas.Persistence.Marten/MartenSagaStore.cs:18-30` | the one ordered, limited query: compound order + `Take` (outbox polling) |
| `UseStronglyTypedIds(params Assembly[])` | `src/StronglyTypedId/StronlgyTypedId.EntityFrameworkCore/IdEntityFrameworkCoreIntegration.cs:29-35` | EF Core converters by `GetTypes()` + `MakeGenericType`, no RUC/RDC (review S-4); no consumer |
| `Id<T> : IComparable<Id<T>>` | `src/StronglyTypedId/StronglyTypedId/Id.cs:27-30,65` | v7 ids sort by time: the implicit basis of `TokenPagination<T>` |

## 4. Review findings that concern this step

| Finding | Claim | Re-verified |
|---|---|---|
| supporting #27 / §1.6 (`docs/review/supporting.md:48-52,291`) | no bounds validation, `init` on the interface, `IAsyncEnumerable` + `TotalCount` is not an AOT-friendly contract and wrong for cursors, placeholder tests | **confirmed** by reading `Pagination.cs:5,8`, `PaginationResult.cs:3`, `Class1.cs` |
| supporting (AOT) `:312` | Paginations contains no reflection | confirmed |
| CQRS C1, C5 (`cqrs.md`) | queries routed to a nonexistent command handler; matched by simple name | confirmed (`CqrsDispatcherGenerator.cs:68-69,305`) |
| persistence §2.4 (`persistence.md:180-245`) | a `global_seq` checkpoint must handle commit gaps (`pg_snapshot_xmin`/`xid8`); a generated alias → handler switch for an in-house projector | built in step 8 (P-3, P-14): `(tx_id, position)` + xmin filter; the switch is candidate D-A here |
| REVIEW goal 4 (`REVIEW.md:31`) | none of Marten's projection, daemon or LINQ features are used | confirmed: no projection exists |
| sagas S12 | framework events in the same event store would reach async projections | moot: Tarol-Net's sagas are changes (S-1); the feed carries domain events only |

## 5. What is kept

* Nothing as code (17 lines, 0 tests, 0 users). As ideas: both pagination shapes (offset and keyset), now with a bounded
  limit, an opaque cursor that carries the sort key and the id, `next` instead of `TotalCount`, and the tenant from the caller.
* `IQuery<TResponse>` as a name for a query record (ROADMAP D-3) — candidate Q-A.
