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
| TN-2 | P1, closed: #123 | **Per-tenant singleton aggregates**, e.g. `[PerTenant]` on a root: the id is derived from the tenant, the generator writes the load (no `ILoads`), creations need no id, endpoints no `{id}` (`GET /settings`, `PUT /settings/name`). | Tatalu's day-care facility *is* the tenant. Today: `Id<ChildDayCare>.From(tenant.Value)` plus a hand-written `ILoads` per change (`Tatalu.Einrichtung/ChildDayCare.cs`, `Changes.cs` `RenameDayCare.Load`). Settings, invoice number ranges and profile assignments need the same pattern. | `Tarol.Primitives` (attribute), `Tarol.Changes.Generators` (load), `Tarol.Changes.AspNetCore.Generators` (routes, view) |
| TN-3 | P2 | **`GlobalRef<T>(Id<ITenant> Tenant, Id<T> Id)`** as an explicit type for the few places where an id leaves its tenant: message contracts between systems, admin endpoints, exports, logs. `Id<T>` stays as it is. | Messages and exports carry the tenant next to the id by convention only. | `Tarol.StronglyTypedId` or `Tarol.Kernel` |
| TN-4 | P2 | **Analyzer: no `Id<ITenant>` in inputs.** A member of type `Id<ITenant>` (or `Option<Id<ITenant>>`) on an `IChange`/`ICreation`/`IQuery` record or an event is a diagnostic (the tenant comes from the context). | `tarol-kernel` already says so as a rule; nothing enforces it. | `Tarol.Changes.Generators` / `Tarol.Kernel` analyzer |
| TN-5 | P2 | **Record cross-tenant misses.** When a load misses because the id exists under another tenant, keep the 404 but write a log event and a counter (`tarol.store.foreign_tenant`), never with values. | Tatalu's access log (F13.1-R5, F13.3) should show probing with ids of other day-care facilities. | `Tarol.Persistence.Npgsql` load path |
| TN-6 | P0, closed: #123 | **Lists scoped by the caller's set of rows.** A server-side hook per query, e.g. `IListScope<TQuery>` returning extra filters for the caller, and a set-valued filter (`column IN (…)`, also over an array column). Row rules do not run on lists, and `[FromClaim]` binds one claim value with equality only (`ListQuery.cs:11`, `ColumnFilter(Column, Value)`). | „Parents see only their own children“ (F13.1-R3, NFR-DS-06): a parent with two children, a substitute assigned to three children for two weeks. The set is domain data (Tatalu's `IChildAccess.VisibleChildren`), not a claim. Without it every child-related list (daily logs, attendance, messages) needs one row per (child, viewer) or its own endpoint. | `Tarol.Queries` (`ListQuery`, filter kinds), `Tarol.Changes.AspNetCore` (`QueryEndpoints`), `Tarol.Queries.Npgsql` (SQL) |
| TN-7 | P1, closed: #123 | **`DateOnly` (and `TimeOnly`) projection columns**, filterable and sortable (`date`, `time`). Today only `Guid, string, bool, short, int, long, decimal, double, DateTimeOffset, enum` get columns (`Queries/Tarol.Queries.Generators/Discovery.cs:99`); a `DateOnly` lives only in the row's JSON. | Attendance by day, the monthly list per child, closing days, validity periods (`MembershipRow.ValidUntil`) are calendar days, not instants. A `DateTimeOffset` at midnight in some zone is the wrong type for them. | `Tarol.Queries.Generators`, `ColumnValues`, `Tarol.Queries.Npgsql` |
| TN-8 | P1, closed: #123 | **Applications made of several assemblies (modules).** Today each assembly generates its own `GeneratedEndpoints.MapChanges`, `GeneratedOpenApi.AddTarolEndpoints`, `GeneratedReactions.Set`, and the app must list its JSON contexts and aggregates by hand. Proposal: one generated `TarolModule` per assembly (aggregates, JSON resolver, reactions, jobs, projections, endpoints, OpenAPI, security map hook) and `AddTarol(json, modules: …)` / `app.MapModules()`. | Tatalu has one assembly per bounded context (13 planned). `Tatalu.Host/TataluApp.cs` repeats every list per context; forgetting one is found only at run time. | `Tarol.Changes`, the generators |
| TN-9 | P1, closed: #123 | **OpenAPI operation transformer is not assembly-scoped.** `GeneratedOpenApi.AddTarolEndpoints` of *each* assembly completes *every* operation carrying `TarolEndpointMetadata`; with two assemblies list parameters, `Idempotency-Key`, `If-Match` and the "Policy" paragraph appear twice, and `tarol-codegen` emits duplicate identifiers (TS2300). Fix: complete only the assembly's own entries (an assembly marker in the metadata), or make the completion idempotent. | `Tatalu.Host/OpenApiDeduplication.cs` removes the duplicates as a last transformer. | `Changes/Tarol.Changes.AspNetCore.Generators/OpenApiEmitter.cs` (`Complete`) |
| TN-10 | P1, closed: #123 | **`AddTarol` with several JSON contexts.** `AddTarol(JsonSerializerContext, …)` takes one context. Combining is not obvious: a hand-written `JsonSerializerContext` subclass answers `IJsonTypeInfoResolver.GetTypeInfo` only for its own options, so the copies made by `ValueObjectJson.Options` fail with "returned a JsonTypeInfo instance whose JsonSerializerOptions setting does not match". Proposal: `AddTarol(params IJsonTypeInfoResolver[] contexts, …)` (part of TN-8). | `Tatalu.Host/TataluJson.cs` re-implements the interface to ask each context with the caller's options. | `TarolServiceCollectionExtensions.AddTarol`, `ValueObjectJson.Options` |
| TN-11 | P1, closed: round 2 (TN-33) | **Enums as strings in the OpenAPI document.** A source-generated context with `UseStringEnumConverter = true` writes names on the wire, but the schema says `integer`; clients get `number`. | Tatalu puts `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` on every contract enum (`Role`, `MembershipState`). | OpenAPI schema transformer (`OpenApiEmitter.cs`): read the converter from the app's options |
| TN-12 | P2, (a) closed: round 2 (TN-31) | **Optional members of inputs.** (a) A nullable value object (`PersonName?`) becomes `oneOf: [{type: null}, {$ref}]`, which `tarol-codegen` rejects ("a schema without a type"). (b) `Option<T>` works, but a missing member is `Required` (it must be sent as `null`). Proposal: emit `type: [null, …]` / an `anyOf` the generator accepts, and treat a missing `Option<T>` member as `None`. | `GrantMembership.DisplayName` is `Option<PersonName>`; clients must send `displayName: null`. | `OpenApiEmitter.cs` (schemas), the binder in `ChangeEndpoints` |
| TN-13 | P2, open: round 2 TN-67 | **`AggregateTest.Given` with a committed aggregate.** Given uses the *uncommitted* events as history; passing an aggregate that went through `Prepare`/`Committed` rehydrates from nothing, and the next `When` fails with `Lifecycle.ForbiddenStep`, far from the cause. Proposal: throw in `Given` when the aggregate has a version but no uncommitted events ("pass the aggregate before committing it"). | Cost Tatalu a debugging round. | `Primitives/Tarol.Primitives/Testing/AggregateTest.cs` |
| TN-14 | P2, closed: round 2 (TN-21) | **TPRM022/TPRM023 and deconstructing assignments.** `private void On(Withdrawn e) => (State, RevokedAt) = (MembershipState.Revoked, e.At);` is reported as "never assigned" / "does not assign"; plain assignments pass. | Style forced by the analyzer, not by a rule. | `Tarol.Primitives.Generators` (lifecycle analysis) |
| TN-15 | P2 | **`ChangeTest` with the app's `StoreLookups`.** `ChangeTest.At(now, stateOptions, authorization)` has no lookups; tests call `test.Store.DefineLookup` and repeat the app's lookup definitions. | `Tatalu.Datenschutz.Tests/AccessDirectoryTests.cs` | `Changes/Tarol.Changes/Testing/ChangeTest.cs` |
| TN-16 | P2 | **Operations snapshot noise at start-up.** The first poll warns `relation "tarol.projections" does not exist` until the projection host has created its tables (`OperationsHost[15005]`). | Every start of a fresh database logs a warning. | `Tarol.Operations` (skip a signal whose table does not exist yet, or wait for schema readiness) |
| TN-17 | P2 | **Zitadel: self-service sign-up and local compose.** (a) A multi-tenant SaaS needs "new organisation + owner + project grant + user grant" when a customer signs up; every app writes this against the management API (Tatalu: `Tatalu.Einrichtung/Konto/ZitadelIdentityProvider.cs`). An optional provisioning client in `Tarol.Security.Zitadel` would help. (b) Docs: with docker compose, `zitadel ready` as health check needs `ZITADEL_TLS_ENABLED=false` when started with `--tlsMode disabled`; the lockout policy cannot be changed through `PUT /admin/v1/policies/lockout` in v4 (405), only by `ZITADEL_DEFAULTINSTANCE_LOCKOUTPOLICY_*`. | | `Tarol.Security.Zitadel` (optional), skill `tarol-zitadel` |
| TN-18 | P2, closed: #123 | **Consuming from GitHub Packages.** With `TreatWarningsAsErrors`, NuGet audit fails with NU1900 because the GitHub feed has no vulnerability data; consumers need `<auditSources>` (nuget.org only) in `nuget.config`. Worth one line in the README / `tarol-overview` next to the `packageSourceMapping` sample. *Checked: there was no such sample; the README now has the whole `nuget.config`.* | `Tatalu/backend/nuget.config` | `README.md`, skill `tarol-overview` |

## Suggested order

TN-6 first (it decides how every child-related list in Tatalu's MVP is built), then TN-7, TN-8 (which absorbs TN-9 and
TN-10), TN-2 and TN-11. The rest are small and independent.

## Round 2

Round 2 (2026-11-25): about 150 findings from 13 contexts and about 60 features of Tatalu on `0.1.0-preview.5`
(Tatalu `docs/implementierung/framework-befunde.md`, branch `integration/welle-2`; ids `FB-*`). Every item below was first
reproduced with a failing test here, or closed with a reason (*Checked: …*). Changes, tests and what Tatalu removes are in
[docs/migration/tatalu-round-2.md](../migration/tatalu-round-2.md); designs for the larger items in `docs/architecture/`.
The Tarol-Svelte side is `TS-9 …` in `CLorat/Tarol-Svelte` `docs/feedback/tatalu.md`. Status "built" names the commit on the
round-2 branch (PR links follow the PRs).

| # | Pri | Change | Tatalu | Status |
|---|---|---|---|---|
| TN-19 | P0 | Several `[PersonalData(Subject = …)]` members with one subject failed on `Append` | FB-F13.3-2 | built `793179c` |
| TN-20 | P0 | Computed public properties leaked personal data unsealed: TPRM034, `InMemoryAggregateStore.AssertNoPlaintext` | FB-F13.3-8, FB-F13.4-1 | built `b6c3a07`; state part *Checked: does not reproduce* |
| TN-21 | P0 | TPRM013 on `with`/initializers; TPRM022/023 and deconstructing assignments (= TN-14) | FB-F04.6-4, FB-F15.5-1, FB-F03.4-2 | built `5778c08` |
| TN-22 | P0 | Same-named contract types overwrote each other's schema: error, `[OpenApiSchemaName]` | FB-INT-1, FB-INT2-1 | built `92dcbf1` |
| TN-23 | P0 | `Required` in every depth; `DateOnly`/`TimeOnly` value objects | FB-F02.1-3, FB-F03.9-1 | built `998d967`, `fc30158` |
| TN-24 | P0 | `x-tarol-topics` missing when only the host references SignalR | FB-F17.3-2 | built `1225870` |
| TN-25 | P0 | `EraseSubject` failed while projections build | FB-F17.5-4, FB-INT2-4 | built `135d657` |
| TN-26 | P0 | Wait until all projections are current; readiness | FB-INT2-3 | built `d229276` |
| TN-27 | P0 | Computed non-nullable members optional in the schema | FB-F04.6-2 | built `92dcbf1`, `3f8f672` |
| TN-28 | P0 | TCHG006 "must be RecordedEvent, not RecordedEvent" | FB-F15.1-1 | built `917405c` |
| TN-29 | P0 | TVAL009 for types without a value object | FB-F15.1-6 | built `8d8211a` |
| TN-30 | P0 | `x-tarol-errors` wrong: `[Read]` targets, `AlreadyExists`, `ILoads` `NotFound` | FB-F04.1-2, FB-F17.6-1 | built `8a2baa4` |
| TN-31 | P0 | Nullable records, enums, value objects and `Option<T>` in the schema | FB-3, FB-F01.1-4, FB-F02.1-5, FB-F13.3-5, FB-F17.3-3, FB-F03.9-1, FB-F04.3-2, FB-F17.2-3 | built `92dcbf1` (client: TS-9); build diagnostic *Checked: not needed once tarol-codegen accepts the shape* |
| TN-32 | P0 | Value objects of referenced assemblies without type and rules | FB-F01.1-3, FB-F02.1-4, FB-INT-3 | built `92dcbf1` |
| TN-33 | P0 | Enums (also BCL) by name in the schema (= TN-11) | FB-4, FB-F02.3-2 | built `92dcbf1` |
| TN-34 | P0 | Enums as route values | FB-F17.4-5 | open: route binding and OpenAPI parameter; round 3 |
| TN-35 | P0 | `[ContractType]`/`x-tarol-types`, `[ContractConstant]`/`x-tarol-constants` | FB-F02.6-2, FB-F13.5-1 | built `622c876` (client: TS-10) |
| TN-36 | P0 | Several reasons: `Error.Reasons`, problem `reasons` | FB-F03.4-5 | built `9e8583b` (client display: TS-33) |
| TN-37 | P0 | `Error.ConfirmationRequired(code, data)` with a `confirm` input member | FB-F04.2-2 | open: needs a design decision (how the input carries the confirmation; typed data in the problem) |
| TN-38 | P0 | `x-tarol-errors` complete: `Invalid.Field` codes; `Lookup.Taken` per lookup; row-rule codes | FB-F01.1-9, FB-F02.1-6, FB-F15.1-8 | `Invalid.Field` built `8a2baa4`; lookup and row-rule codes open (proposal in the note) |
| TN-39 | P0 | Generate the `[JsonSerializable]` list | FB-F01.2-1, FB-F02.2-3 | *Checked: a generator cannot feed the System.Text.Json generator*; skill documents `TypeInfoPropertyName` |
| TN-40 | P1 | Reads in changes: `[Read(FromTenant)]`, `Read<T>(id)`, `TryRead`, `Optional`, TCHG004 for the target only | FB-F01.1-2, FB-F03.4-1, FB-F13.4-3, FB-F17.7-1, FB-F03.3-2, FB-F02.1-7 | `TryRead` built `bbfc048`; rest designed ([change-reads.md](../architecture/change-reads.md)), open |
| TN-41 | P1 | `ILoads<T, TData>`; the actor's roles | FB-F13.3-6, FB-F15.1-5, FB-F17.4-4, FB-F04.7-1 | designed (change-reads.md), open |
| TN-42 | P1 | Load or create, 201 for `ILoads`, `IView.Empty`, `Id<T>.Derive` | FB-F02.5-2, FB-F15.5-2, FB-F13.4-2, FB-F04.1-3 | designed (change-reads.md), open |
| TN-43 | P1 | Views with context (`Now`, actor) and read rights per view/projection; "list X needs Read<Y>" | FB-F01.1-6, FB-F02.1-2, FB-F02.2-1, FB-F15.2-2, FB-F17.2-2, FB-F04.8-2, FB-F17.4-6 | designed ([view-access.md](../architecture/view-access.md)), owner decisions, open |
| TN-44 | P1 | List rules with the query; range filters `>=`/`<=` | FB-F04.1-1, FB-F13.3-1 | open: TN-6 (`IListScope<TQuery>`) covers "rows of the caller" but not refusing on the query's filters or ranges |
| TN-45 | P1 | A change without `If-Match` retries a version conflict | FB-F17.3-10, FB-F02.2-2 | built `f5225c7` (client: TS-32) |
| TN-46 | P1 | Commit and refuse (`Result.Rejected` after `Raise`) | FB-F15.1-4 | open |
| TN-47 | P1 | Reactions: fan-out, skip, all events of an aggregate | FB-F15.2-1, FB-F15.9-2, FB-F17.4-2, FB-F13.3-7 | open (audit part in [audit.md](../architecture/audit.md)) |
| TN-48 | P1 | Entity id and path in child events; event id in `On` | FB-F02.3-1, FB-F15.9-1, FB-F01.2-3, FB-F04.4-1 | documentation part built `28db79b`; the rest open |
| TN-49 | P1 | Ports in changes (`Prepare`), versioned parameters, `LocalNow(zone)` | FB-F17.2-1, FB-F02.6-1, FB-F01.9-1 | pattern in `tarol-sagas` built `28db79b`; ports designed (change-reads.md), open |
| TN-50 | P1 | Anonymous capability links with the tenant from the route | FB-F15.1-2 | open |
| TN-51 | P1 | Read your reactions | FB-INT2-8 | documented (`tarol-sagas` rule 9); an option is open |
| TN-52 | P1 | Reservation pattern | FB-F03.3-1 | built `28db79b` (skill) |
| TN-53 | P1 | `Money` / `[Scale(2)]` | FB-F03.6-1 | open |
| TN-54 | P1 | Projections: per entity, from the stored state, generic start, single view, SQL read endpoint | FB-F03.4-4, FB-F04.1-4, FB-F01.2-2, FB-F01.2-4, FB-F04.3-1, FB-F04.5-1 | open |
| TN-55 | P1 | Value equality for collections in events and views | FB-F04.8-1 | open |
| TN-56 | P1 | `UseStoreTenants()`; global master data | FB-F03.4-3, FB-F17.7-2 | designed ([tenant-membership.md](../architecture/tenant-membership.md)), open |
| TN-57 | P1 | `JobTest.WithProjection<TRow>()` | FB-F03.4-6 | open |
| TN-58 | P1 | DataProtection: keys per category, `ExportTenant`, erasure journal, Art. 15 options, `[PersonalSubject]` | FB-F02.5-1, FB-F17.5-1, FB-F17.5-2, FB-F13.6-2, FB-F04.2-3 | designed ([data-protection-round-2.md](../architecture/data-protection-round-2.md)), open |
| TN-59 | P1 | `IAccessObserver`, `[Endpoint(…, Sensitive = true)]` | FB-F13.3-4, FB-F02.2-4 | designed (view-access.md), open |
| TN-60 | P1 | `Tarol-Device`/`Tarol-Captured-At` → `OperationContext.Capture`, `IDeviceGate` | FB-F17.3-6, FB-F13.2-4, FB-F04.6-1 | open (the client outbox TS-26 waits for it) |
| TN-61 | P1 | `TopicRules.Subscribe(topic, params roles)` | FB-F17.3-9 | built `c4cd608` |
| TN-62 | P1 | Zitadel: `RequireSecondFactor`, `EndAll`, organisation ↔ tenant, accounts in several tenants | FB-F13.2-1, FB-F13.2-2, FB-F15.1-3 | designed (tenant-membership.md), owner decisions, open |
| TN-63 | P1 | Effects with `dueAt`, `Digest<TKey>`; `AddEffects()` without transport | FB-F15.8-1, FB-F17.4-3, FB-F13.2-5 | designed ([web-push.md](../architecture/web-push.md)), open; skill note built `28db79b` |
| TN-64 | P2 | `Tarol.Audit` | FB-F13.3-3, FB-F13.3-7 | designed ([audit.md](../architecture/audit.md)), open |
| TN-65 | P2 | `Tarol.Files` | FB-F02.1-9, FB-F17.5-3 | designed ([files.md](../architecture/files.md)), open |
| TN-66 | P2 | `Tarol.Messaging.WebPush` | FB-F17.4-1 | designed ([web-push.md](../architecture/web-push.md)), open |
| TN-67 | P2 | Test helpers: `Given(factory).And`, `ChangeTest.History`, camelCase paths, several JSON contexts | FB-F17.3-7 (= TN-13), FB-F01.1-7, FB-F01.1-8, FB-F13.3-7 | open |
| TN-68 | P2 | Skills | FB-F01.2-1/3/5, FB-F03.4-4, FB-F04.4-2, FB-F15.1-7, FB-F13.2-5 | built `28db79b`, `b6c3a07` |
