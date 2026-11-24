# Feedback from Tatalu (first consumer application)

Tatalu (`CLorat/Tatalu`, an ERP for childminders) is the first application built on Tarol-Net `0.1.0-preview.5` from the
packages, with several bounded contexts in separate assemblies, Zitadel as identity provider and PostgreSQL. This note
collects what its foundation (Tatalu PR #1) showed: one decision about tenant ids and proposed changes, each with where
it hurts in Tatalu and where it lives in Tarol. Ids `TN-*`; the Tarol-Svelte side is in `CLorat/Tarol-Svelte`
`docs/feedback/tatalu.md` (`TS-*`).

Priority: **P0** blocks Tatalu MVP stories; **P1** Tatalu ships with a workaround; **P2** nice to have.

## TN-1 Decision: the tenant stays out of `Id<T>`

**Question (owner, 2026-11-24):** would it be better if the tenant id were part of the strongly typed id?

**Recommendation: no.** The tenant is already part of every storage key and always comes from the authenticated
`OperationContext`, never from the request:

* `streams` `PRIMARY KEY (tenant, type, id)`, `events` `(tenant, type, stream_id, version)`, `lookups`, `idempotency_keys`,
  `jobs` likewise (`src/Persistence/Tarol.Persistence.Npgsql/NpgsqlSchema.cs:39-140`); a list is keyed (tenant, stream id).
* An id of another tenant is never loaded (404). Tatalu tests this in memory and against PostgreSQL
  (`HostTests.Tagespflegestelle_eroeffnen_und_Mandanten_sind_getrennt`, `StoreTests.Mandanten_sind_im_Store_getrennt…`).

Putting the tenant into the id would make things worse:

1. **A second source of truth.** Every id from a route or a body would carry a tenant claim that must still be checked
   against the token. Nothing gets safer; two values can disagree.
2. **Ids live in immutable events.** A composite id changes the wire format everywhere: JSON (a `"D"` UUID today), route
   binding, UUIDv7 ordering, indexes, the branded ids of `tarol-codegen`. Stored events cannot be rewritten.
3. **Data moves between tenants.** Import from another system, export and account deletion, a childminder joining a group
   setting: every reference in every event would have to be rewritten.
4. **Some data has no tenant.** Centrally maintained rule profiles (Tatalu's youth-office profiles), the identity
   provider's subject.
5. **Almost every reference stays inside one tenant**, so the tenant would be redundant in nearly every event.

What *would* help instead is TN-2 … TN-5.

## Proposed changes

| # | Pri | Change | Why (Tatalu) | Where in Tarol |
|---|---|---|---|---|
| TN-2 | P1 | **Per-tenant singleton aggregates**, e.g. `[PerTenant]` on a root: the id is derived from the tenant, the generator writes the load (no `ILoads`), creations need no id, endpoints no `{id}` (`GET /settings`, `PUT /settings/name`). | Tatalu's day-care facility *is* the tenant. Today: `Id<ChildDayCare>.From(tenant.Value)` plus a hand-written `ILoads` per change (`Tatalu.Einrichtung/ChildDayCare.cs`, `Changes.cs` `RenameDayCare.Load`). Settings, invoice number ranges and profile assignments need the same pattern. | `Tarol.Primitives` (attribute), `Tarol.Changes.Generators` (load), `Tarol.Changes.AspNetCore.Generators` (routes, view) |
| TN-3 | P2 | **`GlobalRef<T>(Id<ITenant> Tenant, Id<T> Id)`** as an explicit type for the few places where an id leaves its tenant: message contracts between systems, admin endpoints, exports, logs. `Id<T>` stays as it is. | Messages and exports carry the tenant next to the id by convention only. | `Tarol.StronglyTypedId` or `Tarol.Kernel` |
| TN-4 | P2 | **Analyzer: no `Id<ITenant>` in inputs.** A member of type `Id<ITenant>` (or `Option<Id<ITenant>>`) on an `IChange`/`ICreation`/`IQuery` record or an event is a diagnostic (the tenant comes from the context). | `tarol-kernel` already says so as a rule; nothing enforces it. | `Tarol.Changes.Generators` / `Tarol.Kernel` analyzer |
| TN-5 | P2 | **Record cross-tenant misses.** When a load misses because the id exists under another tenant, keep the 404 but write a log event and a counter (`tarol.store.foreign_tenant`), never with values. | Tatalu's access log (F13.1-R5, F13.3) should show probing with ids of other day-care facilities. | `Tarol.Persistence.Npgsql` load path |
| TN-6 | P0, closed: #118 | **Lists scoped by the caller's set of rows.** A server-side hook per query, e.g. `IListScope<TQuery>` returning extra filters for the caller, and a set-valued filter (`column IN (…)`, also over an array column). Row rules do not run on lists, and `[FromClaim]` binds one claim value with equality only (`ListQuery.cs:11`, `ColumnFilter(Column, Value)`). | „Parents see only their own children“ (F13.1-R3, NFR-DS-06): a parent with two children, a substitute assigned to three children for two weeks. The set is domain data (Tatalu's `IChildAccess.VisibleChildren`), not a claim. Without it every child-related list (daily logs, attendance, messages) needs one row per (child, viewer) or its own endpoint. | `Tarol.Queries` (`ListQuery`, filter kinds), `Tarol.Changes.AspNetCore` (`QueryEndpoints`), `Tarol.Queries.Npgsql` (SQL) |
| TN-7 | P1, closed: #119 | **`DateOnly` (and `TimeOnly`) projection columns**, filterable and sortable (`date`, `time`). Today only `Guid, string, bool, short, int, long, decimal, double, DateTimeOffset, enum` get columns (`Queries/Tarol.Queries.Generators/Discovery.cs:99`); a `DateOnly` lives only in the row's JSON. | Attendance by day, the monthly list per child, closing days, validity periods (`MembershipRow.ValidUntil`) are calendar days, not instants. A `DateTimeOffset` at midnight in some zone is the wrong type for them. | `Tarol.Queries.Generators`, `ColumnValues`, `Tarol.Queries.Npgsql` |
| TN-8 | P1, closed: #120 | **Applications made of several assemblies (modules).** Today each assembly generates its own `GeneratedEndpoints.MapChanges`, `GeneratedOpenApi.AddTarolEndpoints`, `GeneratedReactions.Set`, and the app must list its JSON contexts and aggregates by hand. Proposal: one generated `TarolModule` per assembly (aggregates, JSON resolver, reactions, jobs, projections, endpoints, OpenAPI, security map hook) and `AddTarol(json, modules: …)` / `app.MapModules()`. | Tatalu has one assembly per bounded context (13 planned). `Tatalu.Host/TataluApp.cs` repeats every list per context; forgetting one is found only at run time. | `Tarol.Changes`, the generators |
| TN-9 | P1, closed: #120 | **OpenAPI operation transformer is not assembly-scoped.** `GeneratedOpenApi.AddTarolEndpoints` of *each* assembly completes *every* operation carrying `TarolEndpointMetadata`; with two assemblies list parameters, `Idempotency-Key`, `If-Match` and the "Policy" paragraph appear twice, and `tarol-codegen` emits duplicate identifiers (TS2300). Fix: complete only the assembly's own entries (an assembly marker in the metadata), or make the completion idempotent. | `Tatalu.Host/OpenApiDeduplication.cs` removes the duplicates as a last transformer. | `Changes/Tarol.Changes.AspNetCore.Generators/OpenApiEmitter.cs` (`Complete`) |
| TN-10 | P1, closed: #120 | **`AddTarol` with several JSON contexts.** `AddTarol(JsonSerializerContext, …)` takes one context. Combining is not obvious: a hand-written `JsonSerializerContext` subclass answers `IJsonTypeInfoResolver.GetTypeInfo` only for its own options, so the copies made by `ValueObjectJson.Options` fail with "returned a JsonTypeInfo instance whose JsonSerializerOptions setting does not match". Proposal: `AddTarol(params IJsonTypeInfoResolver[] contexts, …)` (part of TN-8). | `Tatalu.Host/TataluJson.cs` re-implements the interface to ask each context with the caller's options. | `TarolServiceCollectionExtensions.AddTarol`, `ValueObjectJson.Options` |
| TN-11 | P1 | **Enums as strings in the OpenAPI document.** A source-generated context with `UseStringEnumConverter = true` writes names on the wire, but the schema says `integer`; clients get `number`. | Tatalu puts `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` on every contract enum (`Role`, `MembershipState`). | OpenAPI schema transformer (`OpenApiEmitter.cs`): read the converter from the app's options |
| TN-12 | P2 | **Optional members of inputs.** (a) A nullable value object (`PersonName?`) becomes `oneOf: [{type: null}, {$ref}]`, which `tarol-codegen` rejects ("a schema without a type"). (b) `Option<T>` works, but a missing member is `Required` (it must be sent as `null`). Proposal: emit `type: [null, …]` / an `anyOf` the generator accepts, and treat a missing `Option<T>` member as `None`. | `GrantMembership.DisplayName` is `Option<PersonName>`; clients must send `displayName: null`. | `OpenApiEmitter.cs` (schemas), the binder in `ChangeEndpoints` |
| TN-13 | P2 | **`AggregateTest.Given` with a committed aggregate.** Given uses the *uncommitted* events as history; passing an aggregate that went through `Prepare`/`Committed` rehydrates from nothing, and the next `When` fails with `Lifecycle.ForbiddenStep`, far from the cause. Proposal: throw in `Given` when the aggregate has a version but no uncommitted events ("pass the aggregate before committing it"). | Cost Tatalu a debugging round. | `Primitives/Tarol.Primitives/Testing/AggregateTest.cs` |
| TN-14 | P2 | **TPRM022/TPRM023 and deconstructing assignments.** `private void On(Withdrawn e) => (State, RevokedAt) = (MembershipState.Revoked, e.At);` is reported as "never assigned" / "does not assign"; plain assignments pass. | Style forced by the analyzer, not by a rule. | `Tarol.Primitives.Generators` (lifecycle analysis) |
| TN-15 | P2 | **`ChangeTest` with the app's `StoreLookups`.** `ChangeTest.At(now, stateOptions, authorization)` has no lookups; tests call `test.Store.DefineLookup` and repeat the app's lookup definitions. | `Tatalu.Datenschutz.Tests/AccessDirectoryTests.cs` | `Changes/Tarol.Changes/Testing/ChangeTest.cs` |
| TN-16 | P2 | **Operations snapshot noise at start-up.** The first poll warns `relation "tarol.projections" does not exist` until the projection host has created its tables (`OperationsHost[15005]`). | Every start of a fresh database logs a warning. | `Tarol.Operations` (skip a signal whose table does not exist yet, or wait for schema readiness) |
| TN-17 | P2 | **Zitadel: self-service sign-up and local compose.** (a) A multi-tenant SaaS needs "new organisation + owner + project grant + user grant" when a customer signs up; every app writes this against the management API (Tatalu: `Tatalu.Einrichtung/Konto/ZitadelIdentityProvider.cs`). An optional provisioning client in `Tarol.Security.Zitadel` would help. (b) Docs: with docker compose, `zitadel ready` as health check needs `ZITADEL_TLS_ENABLED=false` when started with `--tlsMode disabled`; the lockout policy cannot be changed through `PUT /admin/v1/policies/lockout` in v4 (405), only by `ZITADEL_DEFAULTINSTANCE_LOCKOUTPOLICY_*`. | | `Tarol.Security.Zitadel` (optional), skill `tarol-zitadel` |
| TN-18 | P2 | **Consuming from GitHub Packages.** With `TreatWarningsAsErrors`, NuGet audit fails with NU1900 because the GitHub feed has no vulnerability data; consumers need `<auditSources>` (nuget.org only) in `nuget.config`. Worth one line in the README / `tarol-overview` next to the `packageSourceMapping` sample. | `Tatalu/backend/nuget.config` | `README.md`, skill `tarol-overview` |

## Suggested order

TN-6 first (it decides how every child-related list in Tatalu's MVP is built), then TN-7, TN-8 (which absorbs TN-9 and
TN-10), TN-2 and TN-11. The rest are small and independent.
