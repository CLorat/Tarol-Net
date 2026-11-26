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
| TN-41 | P1 | `ILoads<T, TData>`; the actor's roles | FB-F13.3-6, FB-F15.1-5, FB-F17.4-4, FB-F04.7-1 | built `003a5b2` (round 3c) |
| TN-42 | P1 | Load or create, 201 for `ILoads`, `IView.Empty`, `Id<T>.Derive` | FB-F02.5-2, FB-F15.5-2, FB-F13.4-2, FB-F04.1-3 | built `c7e973c`, `63e950a` (round 3c) |
| TN-43 | P1 | Views with context (`Now`, actor) and read rights per view/projection; "list X needs Read<Y>" | FB-F01.1-6, FB-F02.1-2, FB-F02.2-1, FB-F15.2-2, FB-F17.2-2, FB-F04.8-2, FB-F17.4-6 | built `fc3f572`, `d2ff1a4`, `0c17fe9`, `3baa514` (round 3c; the Development `requires` member and the policy-failure decorator open) |
| TN-44 | P1 | List rules with the query; range filters `>=`/`<=` | FB-F04.1-1, FB-F13.3-1 | range filters built `553e1bc` (round 3c); refusing a list on its filters open |
| TN-45 | P1 | A change without `If-Match` retries a version conflict | FB-F17.3-10, FB-F02.2-2 | built `f5225c7` (client: TS-32) |
| TN-46 | P1 | Commit and refuse (`Result.Rejected` after `Raise`) | FB-F15.1-4 | built `b6d3716` (round 3c) |
| TN-47 | P1 | Reactions: fan-out, skip, all events of an aggregate | FB-F15.2-1, FB-F15.9-2, FB-F17.4-2, FB-F13.3-7 | open (audit part in [audit.md](../architecture/audit.md)) |
| TN-48 | P1 | Entity id and path in child events; event id in `On` | FB-F02.3-1, FB-F15.9-1, FB-F01.2-3, FB-F04.4-1 | documentation part built `28db79b`; the rest open |
| TN-49 | P1 | Ports in changes (`Prepare`), versioned parameters, `LocalNow(zone)` | FB-F17.2-1, FB-F02.6-1, FB-F01.9-1 | pattern in `tarol-sagas` built `28db79b`; ports designed (change-reads.md), open |
| TN-50 | P1 | Anonymous capability links with the tenant from the route | FB-F15.1-2 | open |
| TN-51 | P1 | Read your reactions | FB-INT2-8 | documented (`tarol-sagas` rule 9); an option is open |
| TN-52 | P1 | Reservation pattern | FB-F03.3-1 | built `28db79b` (skill) |
| TN-53 | P1 | `Money` / `[Scale(2)]` | FB-F03.6-1 | open |
| TN-54 | P1 | Projections: per entity, from the stored state, generic start, single view, SQL read endpoint | FB-F03.4-4, FB-F04.1-4, FB-F01.2-2, FB-F01.2-4, FB-F04.3-1, FB-F04.5-1 | open |
| TN-55 | P1 | Value equality for collections in events and views | FB-F04.8-1 | open |
| TN-56 | P1 | `UseStoreTenants()`; global master data | FB-F03.4-3, FB-F17.7-2 | `UseStoreTenants<T>()` built `e5db48c` (round 3c); global master data and TN-62 open |
| TN-57 | P1 | `JobTest.WithProjection<TRow>()` | FB-F03.4-6 | open |
| TN-58 | P1 | DataProtection: keys per category, `ExportTenant`, erasure journal, Art. 15 options, `[PersonalSubject]` | FB-F02.5-1, FB-F17.5-1, FB-F17.5-2, FB-F13.6-2, FB-F04.2-3 | designed ([data-protection-round-2.md](../architecture/data-protection-round-2.md)), open |
| TN-59 | P1 | `IAccessObserver`, `[Endpoint(…, Sensitive = true)]` | FB-F13.3-4, FB-F02.2-4 | built `3baa514` (round 3c; hub refusals open) |
| TN-60 | P1 | `Tarol-Device`/`Tarol-Captured-At` → `OperationContext.Capture`, `IDeviceGate` | FB-F17.3-6, FB-F13.2-4, FB-F04.6-1 | built `be7af43` (round 3c); the capture in the PostgreSQL event columns (TN-60b) and `SeenVersion` open |
| TN-61 | P1 | `TopicRules.Subscribe(topic, params roles)` | FB-F17.3-9 | built `c4cd608` |
| TN-62 | P1 | Zitadel: `RequireSecondFactor`, `EndAll`, organisation ↔ tenant, accounts in several tenants | FB-F13.2-1, FB-F13.2-2, FB-F15.1-3 | designed (tenant-membership.md), owner decisions, open |
| TN-63 | P1 | Effects with `dueAt`, `Digest<TKey>`; `AddEffects()` without transport | FB-F15.8-1, FB-F17.4-3, FB-F13.2-5 | designed ([web-push.md](../architecture/web-push.md)), open; skill note built `28db79b` |
| TN-64 | P2 | `Tarol.Audit` | FB-F13.3-3, FB-F13.3-7 | built: `Tarol.Audit`, `Tarol.Audit.Npgsql` (round 3c; [audit.md](../migration/audit.md)) |
| TN-65 | P2 | `Tarol.Files` | FB-F02.1-9, FB-F17.5-3 | designed ([files.md](../architecture/files.md)), open |
| TN-66 | P2 | `Tarol.Messaging.WebPush` | FB-F17.4-1 | designed ([web-push.md](../architecture/web-push.md)), open |
| TN-67 | P2 | Test helpers: `Given(factory).And`, `ChangeTest.History`, camelCase paths, several JSON contexts | FB-F17.3-7 (= TN-13), FB-F01.1-7, FB-F01.1-8, FB-F13.3-7 | open |
| TN-68 | P2 | Skills | FB-F01.2-1/3/5, FB-F03.4-4, FB-F04.4-2, FB-F15.1-7, FB-F13.2-5 | built `28db79b`, `b6c3a07` |

## Round 3

Round 3 (2026-11-25): wave 3 of Tatalu (contexts Dokumentation E05, Planung E06, Kommunikation E07, the Aspire AppHost and
Windows development; Tatalu `docs/implementierung/framework-befunde.md` on `main`, sections E05, F06.*, E07, FB-ASPIRE,
FB-WIN, FB-INT3, and the "Nachtrag aus Welle 3" of `docs/implementierung/tarol-prompt-runde-2.md`). Every item below was
first reproduced with a failing test here, or closed with a reason (*Checked: …*). Owner decisions, changes, tests and what
Tatalu removes are in [docs/migration/tatalu-round-3.md](../migration/tatalu-round-3.md). Confirmations of round-2 items are
listed with those items (table below). The Tarol-Svelte side is `TS-34 …` in `CLorat/Tarol-Svelte` `docs/feedback/tatalu.md`.

| # | Pri | Change | Tatalu | Status |
|---|---|---|---|---|
| TN-69 | P0 | A projection column named like a reserved word (`From`, `Until`) never became current on PostgreSQL; the error was a warning only: columns quoted, `ProjectionHost.LastFailure`, `ProjectionHosts.Failing`, readiness text, log 12004 | FB-F05.8-3 | built `51a36e0` |
| TN-70 | P0 | `[OnEvent<T>(DueAt = …)]` with an offset ≠ 0 ran in memory and threw on Npgsql: both stores store the UTC instant | FB-F06.4-5 | built `0058e37` |
| TN-71 | P0 | Lazily started listeners (`NpgsqlProjectionPositions`, feed signal, outbox, key store) joined the first request's trace forever: started without the caller's `ExecutionContext` | FB-ASPIRE-1 (FB-ASPIRE-2: confirmation only) | built `a127cbf` |
| TN-72 | P0 | "too many clients": one gate connection per projection and a pool of the bus's own: all gate locks on one session, `UseNpgsqlBus(options)` on the registered `NpgsqlDataSource`, connection budget in the README | FB-INT3-3 | built `6d4e15d` |
| TN-73 | P0 | List answers wrapped once per context assembly | FB-F06.3-1 | *Checked: fixed by TN-9 (`bf5b435`, #120)*; regression test with three assemblies `1a9a7f0` |
| TN-74 | P0 | A reaction (or job) name declared in two assemblies failed only at start: TCHG012 at build time in the assembly that references both | FB-INT3-1 | built `6e46ed6` |
| TN-75 | P0 | IANA ids failed on Windows with `InvariantGlobalization`: `Tarol.Kernel.TimeZones.Find` (IANA ↔ Windows without ICU), used by jobs; skills `tarol-jobs`, `tarol-kernel` | FB-WIN-1, FB-INT3-2 | built `9385433` (Windows itself *[unverified]*: simulated, and the jobs AOT smoke app runs invariant on Linux) |
| TN-76 | P0 | `ChangeTest` did not seal, ran no reactions, answered a principal without authorization with a bare 403, had no history per aggregate type: `ChangeTestOptions` (`Codec`, `Lookups`, `Reactions`, `RunReactions`, `Authorization`), `History<T>(id)`, `StoredStateMatchesReplay<T>(id)` | FB-F05.3-1, FB-F06.2-4, FB-F06.3-4, FB-F06.3-2, FB-F01.1-7 (= TN-15) | built `23ee484` |
| TN-77 | P1 | A view could not have a member `From` (CS0102 with the factory): endpoints call the factory through `IView`, a view implements it explicitly | FB-F06.4-1 | built `0aab7f4` |
| TN-78 | P1 | TPRM013 on `with` copies of state, of list entries and in comparisons | FB-F05.1-2, FB-E07-5, FB-F06.6-2 | *Checked: fixed by TN-21*; regression cases `1556559` |
| TN-79 | P2 | A computed property of a record in an event is stored and in the schema | FB-F06.6-3 | *Checked:* TPRM034 reports computed properties next to `[PersonalData]` only, by design (a leak); a computed member without personal data is required in the schema since TN-27, so the client type is right; `[JsonIgnore]` stays the way to keep it out of the payload |
| TN-80 | P2 | Two personal fields of one subject: allowed in rows, not in events | FB-F06.4-2, FB-F05.3-1 | *Checked: fixed by TN-19* (any number of personal members per subject, events and state); TN-76 makes `ChangeTest` seal, so such errors surface in unit tests |
| TN-81 | P2 | No promise that an added event member with a default needs no upcaster | FB-E07-1 | built `75553d3` (skill `tarol-persistence`, test) |
| TN-82 | P2 | Enum name collision in the document | FB-F06.2-1 | *Checked: fixed by TN-22* (the document fails naming both types) |
| TN-83 | P1 | One live topic fed by several events of one contract (TMSG005) | FB-E07-7 | open: part of TN-47 (needs a `[Publishes<A, B>]` shape) |

**Round-2 items built or confirmed in round 3**

| # | Round 3 | Status |
|---|---|---|
| TN-37 | `Error.ConfirmationRequired(code, args)`, header `Tarol-Confirm`, `ChangeContext.Confirmed`; also FB-F06.2-2 | built `61dfc8b` (owner decision TN-37a/b (a)); client TS-33 |
| TN-40 | `[Reads<T>]` (`[PerTenant]`, `SameIdAs`, `Optional`), `[Read(Optional = true)]`, TCHG004 only for the target, TCHG013 (hand-written `IHasReads` next to reads, was CS8646); also FB-F05.7-1, FB-F06.4-4 (the `IHasReads` part) | built `d1a2057` (owner decision CR-1 (a)) |
| TN-53 | `[Scale(n)]` (code `Scale`), `multipleOf` and the rule's `step` in the schema; `Money` as a documented value object per currency | built `e147531` (owner decision SV-dec (b): JSON number) |
| TN-15 | `ChangeTest` with the app's lookups | built with TN-76 |
| TN-67 | `ChangeTest.History`, several singletons with one stream id (FB-F06.3-2) | part built with TN-76; `Given(factory).And`, camelCase paths, several JSON contexts open |
| TN-41 | `ILoads<T, TData>`; again FB-F05.1-1, FB-F06.3-3, FB-F06.4-4 | built `003a5b2` (round 3c) |
| TN-42 | load or start, 201, `IView.Empty`, `Id<T>.Derive` | built `c7e973c`, `63e950a` (round 3c) |
| TN-43, TN-44 | views with context and rights per view; list rules with the query and range filters; again FB-E07-3, FB-F06.1-1, FB-E07-4 | open: decisions VA-1 … VA-4 pending |
| TN-47 | reactions: fan-out, skip, all events of an aggregate, the triggering event; again FB-F06.3-5, FB-F06.3-6, FB-F06.6-4, FB-F06.5-3, FB-F06.2-3, FB-E07-2, FB-INT3-5, new FB-F06.4-3, FB-E07-7 (TN-83) | open: needs a design note (no owner decision listed yet) |
| TN-54 | projections per entity, from the state, generic start, single view, SQL read endpoint (FB-F04.5-1 billing, hours account) | generic start `Empty(RecordedEvent)` built `3133118` (FB-F01.2-2); one row `IRowQuery<TRow>` built `c6ca39e` (FB-F01.2-4); per entity, from the state and the SQL read endpoint (join over two aggregate types) open: need a design note (row key per entity; a read endpoint for `ISqlProjection`) |
| TN-58 | data protection; new FB-E07-8 (erasure with exceptions, retention from a domain date) | open: decisions DP2-1 … DP2-6 pending |
| TN-62 | Zitadel; new FB-F06.5-2 (time-limited grants), FB-F05.2-2 (row rule over several children) | open: decisions TM-1 … TM-6 pending |
| TN-63 | effects; new FB-F06.5-4 (reads in effects), FB-E07-6 (correlation delivery → cause), FB-F05.8-1 (recurring domain dates) | `dueAt` built `7953ecb` (FB-F17.4-3); digest (decision pending), reads in effects, correlation and recurring dates open |
| TN-65 | `Tarol.Files`; FB-F05.2-1 gives the concrete API need (`IMediaStore`, `tatalu_media`) | open: decisions FI-1 … FI-5 pending; next for E12 |
| TN-34 | enums as route values (FB-F17.4-5) | built `2ab0222` |
| TN-57 | `JobTest` with a projection (FB-F03.4-6) | built `ee5f861` as `JobTest.BeforeEachRun` (no Tarol.Queries dependency in Tarol.Jobs) |
| TN-46 | commit and refuse (FB-F15.1-4) | built `b6d3716` (round 3c) |
| TN-48 | event id in `On`, entity id in `Deleted` (FB-F04.4-1, FB-F02.3-1) | open: an `On` also runs at `Raise`, before the store assigns the event id; needs a design note |
| TN-56, TN-59, TN-60 | as in round 2 | built in round 3c (see the rows above and the round-3 note) |
| TN-49, TN-50, TN-51, TN-55, TN-66 | as in round 2 | open: decided 2026-11-26 (recommendations), not built yet; see the round-3 note |
