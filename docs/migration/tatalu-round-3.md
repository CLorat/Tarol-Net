# Tatalu feedback, round 3 (TN-69 …)

Round 3 of the feedback from Tatalu ([docs/feedback/tatalu.md](../feedback/tatalu.md#round-3)): the findings of wave 3
(contexts Dokumentation E05, Planung E06, Kommunikation E07, the Aspire AppHost and Windows development; Tatalu
`docs/implementierung/framework-befunde.md` on `main`, sections E05, F06.*, E07, FB-ASPIRE, FB-WIN, FB-INT3, and the
"Nachtrag aus Welle 3" of `docs/implementierung/tarol-prompt-runde-2.md`), plus the items of round 2 that were still open.
The order follows what Tatalu builds next: youth-office billing (E08), parent invoices (E09), finances (E10), documents
and deadlines (E12), dashboard and tasks (E14).

This note starts with the **owner decisions** that gate open items, then lists every public API and behaviour change of the
round, item by item, with the test that was red before. Labels as in the plan: **[verified]** = run here;
**[verified by reading]** = read in the code. The Tarol-Svelte side is in its `docs/migration/tatalu-feedback-3.md`.

## Owner decisions

Each question: the options in at most two sentences each, and a recommendation. Items that need a decision are built after
it; everything else in this round was built without waiting. Ids are the design notes' (`CR-*`, `VA-*`, …).

**Decided by the owner (2026-11-25):** SV-dec **(b)** (number + `[Scale]`), TN-37a/b **(a)** (header `Tarol-Confirm`, string
arguments), CR-1 **(a)** (`[Reads<T>]`), SV-off-1 **(a)** (framework subpaths; device id from the app, SV-off-2 (b)). All
other questions below are open; their items stay open with the recommendation as the proposed next step.

### Change reads ([change-reads.md](../architecture/change-reads.md); TN-40 … TN-42, TN-49)

| # | Question | Options | Recommendation |
|---|---|---|---|
| CR-1 | How does a change read an aggregate it has no `Id<X>` member for (the tenant's singleton, the same Guid as the target)? | **(a)** `[Reads<T>]` on the change type (`[PerTenant]` or `SameIdAs = nameof(Member)`, `Optional`): one declaration, nothing on the wire. **(b)** `[Read]` on a computed property (`[Read] Id<Child> Child => new(CareFile.Value)`): reuses the member attribute, but a computed member must be kept out of JSON and the schema. | **(a)**: declarative, visible to the generator for `x-tarol-errors`, no wire impact. |
| CR-2 | Data from `Load` to `Execute`: what ships in the first step? | **(a)** `ILoads<T, TData>` only (`Load` returns `(T, TData)`, `Execute(T, TData, ChangeContext)`). **(b)** also `ReadsByActor` (a lookup keyed by the actor, e.g. the actor's `Membership`) in the same step. | **(a)** first; `ReadsByActor` once a second application needs it (Tatalu's role lookup fits `TData`). |
| CR-3 | An asynchronous port before `Execute` (`IPrepares<TData>`)? | **(a)** Build it: a port runs after loading, its result reaches `Execute`, and replays are unaffected because only events are stored. **(b)** Only document "port data comes in the input or from a profile aggregate", keeping `Execute` pure. | **(b)** now: the saga pattern documented in round 2 covers payment; revisit with TN-49 when a port answer must be part of the decision. |
| CR-4a | 201 Created for a stream created through `ILoads` (load or start)? | **(a)** Every change that created the stream answers 201 with `{id, version}`. **(b)** Only `IOrStart`/`ICreation` entries. | **(a)**: the client cannot know which path the server took. |
| CR-4b | Scope of `Id<T>.Derive(scope, key)` | **(a)** A free string scope: stable across renames, the caller picks it. **(b)** The aggregate's `StreamType`: no extra argument, but renaming the type changes every id. | **(a)**. |

### Views and access ([view-access.md](../architecture/view-access.md); TN-43, TN-44, TN-59)

| # | Question | Options | Recommendation |
|---|---|---|---|
| VA-1a | What does `ViewContext` carry? | **(a)** The actor id and `Now` only: views stay deterministic per (aggregate, actor, day). **(b)** Also the principal (roles, claims): more power, but a view then depends on token contents and cannot be cached or tested without one. | **(a)**. |
| VA-1b | ETag for a context-dependent view | **(a)** A hash of the written view: 304 still works and is always correct. **(b)** No 304 for such views: simpler, one full body per read. | **(a)**. |
| VA-2a | Restricted views | **(a)** Opt-in `Restricted = true`: existing `Read<T>` grants keep working. **(b)** Every view needs its own grant: fail closed, but every role map grows by one line per view. | **(a)**, with the startup warning of VA-3 for endpoints granted to nobody. |
| VA-2b | Name of the grant | **(a)** `MayRead<T, TView>()`: reads like the existing helpers. **(b)** `May<Read<T, TView>>()`: a second intent type, more general, more ceremony. | **(a)**. |
| VA-3 | An endpoint granted to no role | **(a)** A startup warning (log 1003). **(b)** An error that stops the host. | **(b)** in Development, **(a)** otherwise. |
| VA-4 | Does a 304 on a `Sensitive` view count as a read for `IAccessObserver`? | **(a)** Yes: the person saw the data again. **(b)** No: nothing was transferred. | **(a)**: the access log answers "who looked", not "who downloaded". |

### Tenants and identity ([tenant-membership.md](../architecture/tenant-membership.md); TN-56, TN-62)

| # | Question | Options | Recommendation |
|---|---|---|---|
| TM-1 | A person in several tenants | **(a)** One token per tenant (the organisation scope picks it): no API change, one sign-in per tenant. **(b)** The tenant per request (`Tarol-Tenant` header, checked against the token's grants): one account, switching without a new sign-in. | **(b)**. |
| TM-2 | Tenant ids for per-request tenants | **(a)** A register (organisation id ↔ tenant id) that keeps today's ids. **(b)** A reversible encoding of the organisation id: no table, but today's ids change. | **(a)**: stored data keeps its keys. |
| TM-3 | Second factor | **(a)** `RequireSecondFactor` in `Tarol.Security` with configurable `amr` values (default `mfa`, `otp`, `hwk`, `swk`, `user`). **(b)** Only in `Tarol.Security.Zitadel`. | **(a)**. |
| TM-5 | Tenant-less HTTP calls | **(a)** Refused (behaviour change; single-tenant apps set a fixed tenant). **(b)** Allowed as today (they run as the empty tenant). | **(a)**, listed as a behaviour change. |
| TM-6 | How `UseStoreTenants()` finds tenants | **(a)** From a `[PerTenant]` marker type's streams (`UseStoreTenants<T>()`): an index exists. **(b)** Also a `tenants` table written at the first append of a tenant: works without a marker, one more write path. | **(a)**. |
| TN-62 | Zitadel provisioning (TN-17 a) in the package | **(a)** Optional `IZitadelProvisioning` (organisation, owner, grants). **(b)** Stays in the app. | **(a)**, after TM-1/TM-2. |

### Data protection ([data-protection-round-2.md](../architecture/data-protection-round-2.md); TN-58)

| # | Question | Options | Recommendation |
|---|---|---|---|
| DP2-1 | Keys per category (health data apart from contact data) | **(a)** A category in the key store and the envelope: one more key column and envelope field. **(b)** Derived subjects (`child:health`), no format change, erasure of one category erases one derived subject. | **(b)**. |
| DP2-2 | `ExportTenant` | **(a)** JSON only, admin only. **(b)** A zip with files once `Tarol.Files` exists, also offered to the tenant itself. | **(a)** now, **(b)** with TN-65. |
| DP2-3 | A failed append to the erasure journal | **(a)** The erasure fails (it is retried). **(b)** A warning only. | **(a)**; file implementation first, S3 with Files. |
| DP2-4 | Art. 15 purposes | **(a)** By attribute on the type. **(b)** By configuration only. | **(a)**, overridable by configuration. |
| DP2-5 | `[PersonalSubject]`: where the subject comes from | **(a)** A state member of the aggregate. **(b)** An event member of the same name on every event. | **(a)**. |
| DP2-6 (new, FB-E07-8) | Erasing with exceptions ("everything except messages kept as evidence") and retention from a domain date | **(a)** `EraseValues(subject, predicate)`: a predicate over stored values, powerful, hard to make replay-safe. **(b)** Sub-subjects per list entry (`conversation:{id}/message:{n}`) plus `[Retention(From = nameof(Archived))]`: erasure stays per subject, the app chooses the granularity when it writes. | **(b)**. |

### Audit, files, Web Push ([audit.md](../architecture/audit.md), [files.md](../architecture/files.md), [web-push.md](../architecture/web-push.md); TN-64 … TN-66, TN-63)

| # | Question | Options | Recommendation |
|---|---|---|---|
| AU-1 | Packaging | **(a)** `Tarol.Audit` and `Tarol.Audit.Npgsql`. **(b)** The SQL part inside `Tarol.Queries.Npgsql`. | **(a)**. |
| AU-2 | Which members are audited | **(a)** Every member by default, `[NotAudited]` to leave one out: nothing forgotten. **(b)** Only `[Audited]` members: smaller rows, easy to forget one. | **(a)**. |
| AU-3 | Time of an audit row | **(a)** `RecordedAt` only. **(b)** Also an optional event member `[AuditTime]` (capture time on the device). | **(b)**, together with TN-60. |
| AU-4 | Retention of audit rows | **(a)** Follow the stream's `[Retention]`. **(b)** Their own (`Tarol:Audit:RetentionDays`). | **(b)**: the audit log usually outlives the data. |
| AU-5 | Denials of anonymous callers | **(a)** Recorded (volume, probes). **(b)** Only authenticated callers. | **(b)**, with a counter for anonymous denials. |
| FI-1 | How files enter | **(a)** Only through a change with a `[File]` member (multipart). **(b)** Also a separate upload endpoint in the first version (upload first, reference later). | **(a)**; (b) later for large files. |
| FI-2 | `Tarol.Files.S3` now? | **(a)** Npgsql only (chunks in a table). **(b)** S3 too, with a minimal SigV4 client of our own (no AWS SDK under AOT). | **(a)** for E12; S3 when a deployment needs it. |
| FI-3 | Encryption | **(a)** Every file (key per subject, erasure by key). **(b)** Only files under `[PersonalData]`. | **(a)**. |
| FI-4 | EXIF/metadata of images | **(a)** Stripped on upload by the server. **(b)** Left to the client. | **(b)** in the first version (no image decoder in the BCL); document it. |
| FI-5 | Signed links | **(a)** 5 minutes, not for `Sensitive` views. **(b)** Configurable, allowed for `Sensitive` with one access-log entry per fetch. | **(a)**. |
| WP-1 | Web Push scope | **(a)** Only the sender (VAPID, RFC 8291). **(b)** Sender plus `PushDevice` aggregate and `MapWebPush` endpoints. | **(b)**: every app needs the same device registry. |
| WP-3 | Subject of a device | **(a)** The person's subject derived from the IdP `sub`. **(b)** A `Guid` the app supplies. | **(b)**: Tatalu's person ids are domain ids. |
| WP-5 | Route and policy of `MapWebPush` | **(a)** `/tarol/push`, authenticated users. **(b)** No default; the app passes both. | **(a)**, overridable. |
| TN-63 | Digest of several events | **(a)** A documented pattern (an aggregate per recipient and window). **(b)** `Digest<TKey>` in `Tarol.Messaging` now. | **(a)**; `dueAt` on effects is built without waiting. |

### Confirmations (TN-37)

| # | Question | Options | Recommendation |
|---|---|---|---|
| TN-37a | How the client confirms a warning | **(a)** A header `Tarol-Confirm: Absence.QuotaExceeded` and `context.Confirmed(code)`: no input member, one generic confirmation step in every form, the code is in `x-tarol-errors`. **(b)** A typed input member per change (`bool ConfirmQuota`): explicit in the contract, but every change declares its own and the client cannot build a generic step. | **(a)**; the header is stored with the outbox entry and sent again on replay. |
| TN-37b | What the refusal carries | **(a)** `Error.ConfirmationRequired(code, args)` with string arguments (`{ "office": "…", "days": "3" }`), problem `confirm: { code, args }`, 409. **(b)** A typed record per code (needs a JSON type per code in the document). | **(a)**: enough for translations; (b) only when a client must compute with the data. |

### Tarol-Svelte ([offline.md](https://github.com/CLorat/Tarol-Svelte/blob/main/docs/architecture/offline.md), [files-and-decimals.md](https://github.com/CLorat/Tarol-Svelte/blob/main/docs/architecture/files-and-decimals.md))

| # | Question | Options | Recommendation |
|---|---|---|---|
| SV-off-1 | Offline capture (TS-26, TS-27) | **(a)** In the framework as subpaths `@clorat/tarol-client/outbox` and `/idb` (≈ 1.5–2 KB gzip, **[unverified]**), Tatalu's `queue.svelte.ts`/`idb.ts`/`reads.svelte.ts` as the template. **(b)** An app concern; the framework documents the pattern only. | **(a)**: every field app needs it, and the outbox must keep the whole refusal (`detail`, `reasons`, FB-F05.1-3). |
| SV-off-2 | Device id | **(a)** Generated and kept by the outbox. **(b)** Supplied by the app. | **(b)** (Tatalu has device records). |
| SV-off-4 | Personal data at rest in IndexedDB | **(a)** The app chooses the storage, scoped per person, cleared at `logout()`/`revoke()`; no encryption. **(b)** Encrypted with a key held in memory. | **(a)**. |
| SV-dec | `decimal` on the wire (TN-53, TS-29) | **(a)** A JSON string (`"12.50"`, `type: string, format: decimal`) and a branded `Decimal` in the client: exact, a contract change for every `decimal`. **(b)** A JSON number as today, with `multipleOf` from `[Scale]`: no contract change, exact only up to 15–17 significant digits. | **(b)** for `0.1.0-preview.6` (amounts of a day-care business fit easily; `[Scale(2)]` and `Money` give the rounding rule), **(a)** as an opt-in (`[DecimalString]`) when a client must compute. |
| SV-dl | `client.download` (TS-28) | **(a)** `client.download(request)` returning a `Blob` and the file name, for endpoints the document marks `x-tarol-kind: file`. **(b)** Signed links only. | **(a)**, with TN-65. |

## Changes, item by item

Each section: what Tatalu hit, the change, the test (red → green), and what Tatalu changes or removes after
`0.1.0-preview.6`. Commits are on the round-3 branch.

### TN-69 Fix: reserved column names; a failing projection says why (FB-F05.8-3)

A row member `From` became the column `from`; `CREATE TABLE` failed on PostgreSQL, the host retried with warning 12001 and
the projection stayed building; in-memory tests passed.

* **Behaviour:** every column of a generated read table is a quoted identifier in all SQL (`CREATE TABLE`, indexes, the
  schema upgrade, writes, filters, sorts, cursors). Raw SQL against read tables must quote such names too (skill).
* **New API (`Tarol.Queries`):** `ProjectionHost.LastFailure` (`ProjectionFailure(Message, Failures, At, Prepared)`, cleared
  after a successful batch), `ProjectionHosts.Failing` (`name vN: Type: message (N attempts)`); the readiness text of
  `ReadyWhenCurrent()` appends `; failing: …`. New log event 12004 (Error) when the storage cannot be prepared.
* **Tests [verified]:** `ReservedColumnTests.Columns_named_like_reserved_words_are_created_written_filtered_and_sorted`
  (red before: `42601: syntax error at or near "from"`), `ProjectionHostsTests.A_host_whose_storage_cannot_be_prepared_names_the_failure_and_clears_it_once_it_runs`.
* **Tatalu:** `MedicationOrderRow` may use `From`/`Until` again (`backend/src/Tatalu.Dokumentation/MedicationViews.cs`,
  `StartsOn`/`EndsOn`); a test that waits for projections can print `ProjectionHosts.Failing` on timeout.

### TN-70 Fix: a due time with an offset (FB-F06.4-5)

* **Behaviour:** both stores normalise a reaction's due time to its UTC instant (Npgsql threw "only offset 0 (UTC) is
  supported"). The instant is unchanged; no other API.
* **Test [verified]:** `SagaIntegrationTests.A_due_time_with_an_offset_is_stored_as_its_instant` (red before: `ArgumentException`).
* **Tatalu:** the UTC conversion in `AnnualPlan.Local(…)` and the other `Local` helpers (`backend/src/Tatalu.Planung/
  ChildAbsence.cs`, `Appointment.cs`, `Reminders.cs`) may go.

### TN-71 Fix: background loops do not join a request's trace (FB-ASPIRE-1)

* **Behaviour:** `NpgsqlProjectionPositions`, `NpgsqlFeedSignal`, `NpgsqlOutbox` (Notify) and `NpgsqlKeyStore` start their
  listener with `ExecutionContext.SuppressFlow()` (internal `Detached.Run`). Their spans are roots (or absent with
  `TraceOnlyInActivities`), never children of the first request. FB-ASPIRE-2 (OpenTelemetry with `Tarol.*`): confirmed, nothing to do.
* **Test [verified]:** `DetachedLoopTests.The_poll_loop_of_the_positions_does_not_join_the_trace_of_the_request_that_started_it`
  (red before: the polls' `Npgsql` spans carried the request's trace id).
* **Tatalu:** nothing to remove (it had no workaround); the Aspire dashboard shows the request without the poll spans.

### TN-72 Fix: one session for the gates; the bus on the shared data source (FB-INT3-3)

* **Behaviour change:** `NpgsqlProjectionGate` holds all its advisory locks on **one** pooled connection (was one per
  projection). A projection version is held at most once per gate (session locks are re-entrant, the gate is not); when the
  session breaks, every lease reports `Held = false` and the hosts ask again. The last released lease returns the connection.
* **New API:** `NpgsqlProjectionGate.Locks`, `NpgsqlProjectionGate : IAsyncDisposable`;
  `MessagingBuilder.UseNpgsqlBus(NpgsqlBusOptions? options = null)` (the registered `NpgsqlDataSource`; the connection-string
  overload keeps its own pool and says so). README `Tarol.Queries.Npgsql`: connections per process = pool + about five
  listener/gate sessions.
* **Tests [verified]:** `GateSessionTests.Twelve_projections_hold_their_gates_on_one_connection_and_another_process_gets_none_until_released`,
  `GateSessionTests.When_the_shared_session_ends_every_lease_reports_it_lost_and_the_gate_can_be_entered_again` (both red on the
  old gate: 12 connections; leases survived), `NpgsqlBusTests.UseNpgsqlBus_without_a_connection_string_shares_the_registered_data_source`;
  the existing take-over test `OperationsTests.One_host_runs_a_projection_version_and_another_takes_over_after_its_session_is_terminated` stays green.
* **Tatalu:** `UseNpgsqlBus(connectionString)` in `backend/src/Tatalu.Host/TataluApp.cs` becomes `UseNpgsqlBus(options)`;
  `max_connections=200` (`docker-compose.yml`, `backend/src/Tatalu.AppHost/AppHost.cs`) may go back to the default after
  measuring (42 projections now hold 1 connection).

### TN-73 *Checked:* list answers are wrapped once (FB-F06.3-1)

TN-9 (`bf5b435`, #120, after preview.5) completes each operation once and only the owner's. New regression test
`ModuleTests.With_three_assemblies_a_list_answer_is_one_page_not_a_page_of_pages` (red with both TN-9 guards disabled).
**Tatalu:** `backend/src/Tatalu.Host/OpenApiDeduplication.cs` may go (also for FB-1); `openapi.json` shrinks by the nested pages.

### TN-74 TCHG012: a reaction or job name of two assemblies at build time (FB-INT3-1)

* **Behaviour:** the generated `GeneratedReactions`/`GeneratedJobs` add `[assembly: DeclaredNames("reaction"|"job", …)]`;
  the generator of every assembly that sees a name in two assemblies (its own included; typically the host) reports
  **TCHG012** (error). `ReactionSet.Combine` still refuses at run time.
* **New API:** `Tarol.Changes.DeclaredNamesAttribute` (`EditorBrowsable(Never)`, generated code). New diagnostic TCHG012.
* **Test [verified]:** `DiagnosticTests.TCHG012_a_reaction_name_of_two_assemblies_is_reported_where_both_are_referenced`;
  snapshots `reactions`, `jobs`, `module` show the attribute.
* **Tatalu:** nothing to remove; a host build now fails where `NotifyReminder` collided (`NotifyMessageReminder` in
  `backend/src/Tatalu.Kommunikation/ConversationChanges.cs` stays: it is stored).

### TN-75 `TimeZones.Find` (FB-WIN-1, FB-INT3-2)

* **New API (`Tarol.Kernel`):** `TimeZones.Find(id)`, `TimeZones.TryFind(id, out zone)`: the system id, ICU's conversion,
  then a built-in table of the European zones and a few others (written for Tarol, not copied from CLDR), in both directions.
* **Behaviour:** `JobOptions.TimeZone`, `[Scheduled(TimeZone = …)]` and `Tarol:Jobs:<name>:TimeZone` resolve through it.
* **Tests [verified]:** `TimeZonesTests` (6, with a simulated "Windows without ICU" and "Linux without ICU"); the jobs AOT smoke
  app now runs with `InvariantGlobalization` and checks a Windows and an IANA id. Windows itself **[unverified]** (no Windows here).
* **Tatalu:** `backend/src/Tatalu.Shared/DayCareTime.cs` may resolve with `TimeZones.Find("Europe/Berlin")`;
  `TimeZoneUsageTests` (`backend/tests/Tatalu.Shared.Tests/TimeZoneUsageTests.cs`) may then allow `TimeZones.Find` instead.

### TN-76 `ChangeTestOptions` (FB-F05.3-1, FB-F06.2-4, FB-F06.3-4, FB-F06.3-2, FB-F01.1-7; TN-15)

* **New API (`Tarol.Changes`):** `ChangeTest.At(now, ChangeTestOptions)`; `ChangeTestOptions` (`StateOptions`, `Codec`,
  `Lookups`, `Reactions`, `RunReactions`, `Authorization`); `ChangeTestOutcome.Reactions`; `ReactionRun(Reaction, Cause,
  Result, Recorded)`; `ChangeTest.History<T>(id)`; `ChangeTest.StoredStateMatchesReplay<T>(id, options)`.
* **Behaviour change:** `ChangeTest.When` with `ChangeOptions.Principal` and no authorization throws
  `InvalidOperationException` naming the fix (it answered 403 `Auth.NotConfigured`). The pipeline itself is unchanged.
* **Tests [verified]:** `ChangeTestOptionsTests` (5): without a codec a change that cannot be sealed passes (what Tatalu saw);
  with the codec it fails like the store and `AssertNoPlaintext` passes; `RunReactions`; the principal message; `History`.
  `PipelineTests.A_principal_without_an_authorization_fails_closed` now runs the pipeline directly.
* **Tatalu:** test fixtures may build `ChangeTest.At(now, new ChangeTestOptions { Codec = …, Lookups = …, Reactions = …,
  RunReactions = true })`: the hand-called `MirrorDeliverySettings` (`backend/tests/Tatalu.Planung.Tests/Fixtures.cs`,
  `Tatalu.Dokumentation.Tests/IncidentTests.cs`, `MedicationTests.cs`), the stream filters by declaring type in
  `backend/tests/Tatalu.Planung.Tests/Fixtures.cs` and `Tatalu.Einrichtung.Tests/Fixtures.cs` (`History<T>(id)`), the
  principal switch in `Plan.Opened`, and the repeated lookup definitions (`Tatalu.Datenschutz.Tests/AccessDirectoryTests.cs`).

### TN-77 A view may have a member `From` (FB-F06.4-1)

* **Behaviour:** the generated endpoint builds a view through the `IView<TAggregate, TView>` constraint; a view with a member
  `From` implements the factory explicitly (`static V IView<A, V>.From(A a) => …`). No API change.
* **Test [verified]:** `PerTenantTests.A_view_may_have_a_member_named_From_when_it_implements_the_factory_explicitly` (would not
  compile before: CS0102 / the generated `V.From(aggregate)` call).
* **Tatalu:** `ChildAbsenceView.FirstDay`/`LastDay` (`backend/src/Tatalu.Planung/ChildViews.cs`) may become `From`/`Until`.

### TN-78 *Checked:* TPRM013 and `with` copies (FB-F05.1-2, FB-E07-5, FB-F06.6-2)

Fixed by TN-21; new cases in `DiagnosticTests.TPRM013_Is_Not_Reported_For_A_With_Expression_Or_An_Initializer`.
**Tatalu:** `LogFold.Apply` (`backend/src/Tatalu.Dokumentation/DailyLog.cs`) may move into `DailyLog.Correct`; the constructor copy
in `CommunicationSettings.Set` and `AppointmentDetails.SameAs` (`backend/src/Tatalu.Planung/Appointment.cs`) may use `with`.

### TN-79, TN-80, TN-82 *Checked*

* **TN-79 (FB-F06.6-3):** TPRM034 is about personal data next to a computed property; `ForParents` holds none, and TN-27 lists
  a computed non-nullable member as required, so the generated type is right. `[JsonIgnore]` keeps it out of the payload.
* **TN-80 (FB-F06.4-2):** TN-19 allows any number of personal members per subject in events and state; `AbsenceDetails`
  (`backend/src/Tatalu.Planung/ChildAbsence.cs`) may be split. TN-76 makes such errors visible in unit tests.
* **TN-82 (FB-F06.2-1):** TN-22 fails the document for two types with one name; `CaregiverAbsenceKind` may stay or take its
  natural name with `[OpenApiSchemaName]`.

### TN-81 Upcasting additive members (FB-E07-1)

Skill `tarol-persistence` rule 9; test `EventCodecTests.An_added_member_with_a_default_reads_old_payloads_without_a_new_version`.
**Tatalu:** `NotificationTests.Gespeicherte_Ereignisse_bleiben_lesbar` (`backend/tests/Tatalu.Eltern.Tests/NotificationTests.cs`) may stay
as an app test; it is no longer the only proof.

### TN-37 Warnings the caller confirms (owner decision TN-37a/b (a); FB-F04.2-2, FB-F06.2-2)

* **New API (`Tarol.Results`):** `Error.ConfirmationRequired(code, arguments = null, detail = null)` (a `Conflict`),
  `Error.Confirmable` (JSON `confirm`), `Error.Arguments` (JSON `args`); equality compares both.
* **New API (`Tarol.Changes`):** `ChangeContext.Confirmations`, `ChangeContext.Confirmed(code)`, `ChangeOptions.Confirmed`.
* **New API (`Tarol.Changes.AspNetCore`):** `TarolHeaders.Confirm` (`Tarol-Confirm`, comma-separated codes, at most 16 /
  1024 characters, else 400 `Tarol-Confirm: Length`), in `TarolHeaders.Allowed` (CORS). A confirmable refusal is 409 with
  `confirm: { code, args }`; the problem schema describes `confirm`. The code is in `x-tarol-errors` like any `Error` factory's.
  Refusals store no idempotency key, so the confirmed request may reuse it.
* **Tests [verified]:** `ModuleTests.A_warning_is_409_with_confirm_and_passes_when_the_request_confirms_its_code`,
  `ConfirmationTests.ConfirmationRequired_is_a_confirmable_conflict_with_arguments_compared_by_value`; `EdgeTests` (schema).
* **Tatalu:** `ConfirmQuota` and `Absence.QuotaExceeded` with `Detail` "Jugendamt|Tage" (`backend/src/Tatalu.Planung/Absences.cs`,
  `Changes.cs`) become `Error.ConfirmationRequired("Absence.QuotaExceeded", new Dictionary<string, string> { ["office"] = …, ["days"] = … })`
  and `context.Confirmed(…)`; likewise `WarningConfirmed` (`backend/src/Tatalu.Anwesenheit/Changes.cs`, `Handover.cs`,
  `AttendanceMonth.cs`); `frontend/apps/web/src/screens/planung/quota.ts` (`overflow()`) goes with tarol-svelte TS-33.

### TN-40 `[Reads<T>]`, optional reads (owner decision CR-1 (a))

* **New API (`Tarol.Changes`):** `ReadsAttribute<T>` (`SameIdAs`, `Optional`) on a change; `ReadAttribute.Optional`.
* **Behaviour:** `[Reads<T>]` loads the `[PerTenant]` T of the caller's tenant, or with `SameIdAs` the T under the Guid of an
  `Id<X>`/`Guid` member; a miss is 422 `T.NotFound` unless optional (then `context.TryRead<T>(out t)` is false).
  **TCHG004 change:** a `[Read]` of the target's type is allowed when another member is the target (FB-F02.1-7).
  **New TCHG013:** a `[Reads<T>]` that cannot be loaded, or a hand-written `IHasReads` next to generated reads (was CS8646).
  `x-tarol-errors` lists `T.NotFound` for required reads only.
* **Tests [verified]:** `ReadsTests` (4: tenant singleton and same-Guid reads without input members, an optional read,
  422 for a missing one, a sibling of the target's type); `DiagnosticTests.TCHG004_read_of_the_own_target_only_when_no_other_member_is_the_target`
  (replaces `TCHG004_read_of_the_own_target`, which pinned the old rule), `TCHG013_*` (5).
* **Tatalu:** every hand-written `IHasReads`: `CompleteSetup` with `Tenants.CaregiverRead` (`backend/src/Tatalu.Einrichtung/Changes.cs`,
  `Tenants.cs`), `RecordMeaslesProof` (`backend/src/Tatalu.Kinder/MeaslesChanges.cs`), `RuleProfiles.Read`
  (`backend/src/Tatalu.Plattform/Profile/Changes.cs`), the `IHasReads` part of `ChildAbsenceFacts` (`backend/src/Tatalu.Planung/ChildChanges.cs`),
  `AppointmentChanges.cs`, `ProfileChanges.cs`; `RecordIncident` (`backend/src/Tatalu.Dokumentation/IncidentChanges.cs`) can read the
  child and the profile with `[Reads<…>]`; `LinkSibling` can be an `IChange<Child>` with `[Read] Id<Child> Sibling`; the `catch` in
  `CareFacts.ReadOccupancy` becomes `[Reads<Occupancy>(…, Optional = true)]`. Data from `Load` to `Execute` (`_facts`) waits for TN-41.

### TN-53 `[Scale]` and money (owner decision SV-dec (b))

* **New API (`Tarol.Validations`):** `ScaleAttribute(int digits)`, `ValidationCodes.Scale`, `Rules.Scale(decimal, int)`.
* **Behaviour:** on a `decimal` (value object or input member) more digits than allowed is `Scale` with detail the digits
  (trailing zeros do not count); TVAL004 on another type, TVAL005 for digits outside 0–28. OpenAPI: `multipleOf: 10^-n`, and the
  rule in `x-tarol-rules` with `step`. `decimal` stays a JSON number. `Money`: a value object per currency
  (`Euro([Range(Min = 0), Scale(2)] decimal Value)`) in the skill; no type of its own (currency handling is the app's).
* **Tests [verified]:** `ScaleTests` (7), `ModuleTests.Scale_is_multipleOf_in_the_schema_and_a_400_with_code_Scale`,
  `DiagnosticTests.TVAL004_A_Rule_That_Does_Not_Fit_The_Type` (Scale case). Client: tarol-codegen TS-29 reads `multipleOf`/`step`.
* **Tatalu:** E08–E10 amounts as `[Scale(2)]` value objects; the integer minute × 700 arithmetic of the hours account stays until TN-54.

## Round 3b (after the merge of #125)

The items below were built after round 3 was merged, without waiting for a decision. Each has its test (red before) and the
Tatalu file whose workaround goes.

### TN-54 (part) `Empty(RecordedEvent)`: a start row for any instance `When` (FB-F01.2-2)

* **Behaviour (`Tarol.Queries.Generators`):** a row may declare `public static TRow Empty(RecordedEvent r)`; every instance
  `When` then starts from it on a new stream, so no static `When` is needed for a stream whose first event varies. A wrong
  shape is TQRY003; TQRY002 names `Empty` as the other way.
* **Tests [verified]:** `EmptyRowTests.A_stream_may_start_with_any_event_an_instance_When_handles` (did not compile before:
  TQRY002), `DiagnosticTests.An_Empty_start_row_replaces_the_creating_When_and_a_wrong_one_is_reported`.
* **Tatalu:** `Caregiver.Started` raised first by `Caregiver.Begin` (`backend/src/Tatalu.Einrichtung/Caregiver.cs`) only for the
  projection may go; the row gets `Empty`.

### TN-54 (part) One projection row: `IRowQuery<TRow>` (FB-F01.2-4)

* **New API:** `Tarol.Queries.IRowQuery<TRow>` (marker); `ChangeEndpoints.Row<TRow>` (`EditorBrowsable(Never)`, generated code).
* **Behaviour:** a record with `[Endpoint("GET …")]` implementing `IRowQuery<TRow>` answers one row: with no member the caller's
  tenant's row of a `[PerTenant]` aggregate, else the row of its one route member (`Guid` or `Id<T>`). Access (`Read<T>`),
  `Tarol-Read-After` and 503 while building are a list's; `ETag` is the row's version (304); a missing row is 404 `X.NotFound`.
  Document: `x-tarol-kind: row`, the row's schema, `Tarol-Read-After`, 304, 503. TWEB003 for other shapes. tarol-codegen TS-39.
* **Test [verified]:** `PerTenantTests.One_row_of_a_projection_is_read_for_the_tenant_or_by_id_with_ETag_read_after_and_404`.
* **Tatalu:** `GET /tpp/aenderungen` as a list of one row (`backend/src/Tatalu.Einrichtung/CaregiverHistory.cs`) and `items[0]` in the
  frontend become a row query (`IRowQuery<CaregiverHistoryRow>`) and a plain read.

### TN-63 (part) Effects with a due time (FB-F17.4-3)

* **New API (`Tarol.Messaging`):** `EffectSet.Add<TEvent>(name, handler, dueAt)`, `MessagingBuilder.Effect<TEvent, THandler>(name, dueAt)`.
  `EffectSet.DueAt`/`IsScheduled` answer from it (they were always `null`/`false`).
* **Test [verified]:** `PublicationTests.An_effect_with_a_due_time_runs_once_it_is_due_not_at_the_commit`.
* **Tatalu:** the detour `Notification.Deferred` → `ReleaseNotification` → `Notification.Queued`
  (`backend/src/Tatalu.Plattform/Benachrichtigungen/Notification.cs`, `Changes.cs`) becomes one effect with `dueAt: e => e.NotBefore`.

### TN-34 Enums as route values (FB-F17.4-5)

* **Behaviour:** a route member of a change or view may be an enum: a defined name, case-insensitive; a number or a
  combination is 400 `Format`. The path parameter lists the names. Lists keep refusing enum route members (TWEB003).
* **New API:** `RouteValue.Values`; `ChangeEndpoints.TryEnumName<TEnum>` (`EditorBrowsable(Never)`).
* **Test [verified]:** `ModuleTests.An_enum_route_value_binds_a_name_case_insensitively_and_refuses_numbers_and_unknown_names`
  (did not compile before: TWEB003).
* **Tatalu:** the category in the body of `PUT /ich/benachrichtigungen/kanaele` (`backend/src/Tatalu.Plattform/Benachrichtigungen/Changes.cs`)
  may move to the route (`…/kanaele/{category}`).

### TN-57 `JobTest.BeforeEachRun` (FB-F03.4-6)

* **New API (`Tarol.Jobs`):** `JobTest.BeforeEachRun(Func<Task>)`, e.g. a projection's `CatchUp` over `test.Store`.
* **Test [verified]:** `JobProjectionTests.A_job_reads_a_projection_over_the_test_store_caught_up_before_every_run`.
* **Tatalu:** the port `ICareFiles` with a fixed list in `backend/tests/Tatalu.Vertraege.Tests/ContractRulesTests.cs` may become
  `ProjectionTest<CareFileRow>.Over(test.Store)` with `BeforeEachRun(rows.CatchUp)`; `ReadModelCareFiles` (`backend/src/Tatalu.Vertraege/Daily.cs`) may go.

### Verification (3b)

2026-11-25 **[verified]**: build 0 warnings; `dotnet format --verify-no-changes` clean (after `251b3ce`); `dotnet test --solution
Tarol.slnx` with Docker 2265 of 2265; `eng/verify-packages.sh` exit 0; `eng/aot-smoke.sh` exit 0 (221 checks, 0 IL warnings).
Tarol-Svelte `pnpm verify` exit 0 with TS-39.

### Not built in 3b, and why

* **TN-46 commit and refuse:** a refusal that commits would store the idempotency key, and a replay would answer success. It
  needs a design note: store the refusal with the key (a store schema change), or commit without the key (double counting on retry).
* **TN-48 event id in `On`:** an `On` also runs at `Raise`, before the store assigns the event id. It needs a design note (an id at
  `Raise`, or only for replay).
* **TN-54 per entity / from the state / SQL read endpoint:** a row key per entity and a read endpoint for `ISqlProjection` (the
  join for billing, FB-F04.5-1) need a design note; next for E08/E10.
* **Skills over the 8 KB guide:** `tarol-changes` (12 KB) and `tarol-queries` (12 KB).

## Open after round 3

Not reached or waiting for a decision (next step in the register; see also "Round 3b"): TN-41 (`ILoads<T, TData>`,
decision CR-2), TN-42 (load or start; CR-4a/b), TN-43/44 (views and list rules; VA-*), TN-46, TN-47 with TN-83 (reactions:
needs a design note), TN-48, TN-49 (ports; CR-3), TN-50, TN-51, TN-54 rest (per entity, from the state, SQL read endpoint: next for billing), TN-55, TN-56/62
(TM-*), TN-58 (DP2-*), TN-59/60, TN-63 rest (digest, reads, correlation, recurring dates), TN-64 (AU-*), TN-65 (FI-*), TN-66 (WP-*), TN-67 rest.
The skill `tarol-changes` is 12 KB, over the 8 KB guide: split it (reactions into `tarol-sagas`) in the next round.

## Skill evaluation checklist (round 3)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| a change needs the tenant's facility | `[Reads<ChildDayCare>]`, `context.Read<ChildDayCare>()` | a hand-written `IHasReads` (TCHG013) |
| a change of a care file needs the child under the same Guid | `[Reads<Child>(SameIdAs = nameof(CareFile))]` | an extra `Id<Child>` member the client must send |
| a read that may be missing | `[Read(Optional = true)]` / `Optional = true`, `context.TryRead(out X? x)` | catching the exception of `Read<T>()` |
| a warning the user confirms | `Error.ConfirmationRequired(code, args)`, `context.Confirmed(code)` | a `bool Confirm…` input member, data in `detail` |
| an amount in euro | `readonly partial record struct Euro([Range(Min = 0), Scale(2)] decimal Value)` | a string, or integer cents on the wire |
| a unit test that seals personal data and runs reactions | `ChangeTest.At(now, new ChangeTestOptions { Codec = codec, Reactions = set, RunReactions = true })` | calling reactions by hand |
| a zone by IANA id in an AOT app | `TimeZones.Find("Europe/Berlin")` | `TimeZoneInfo.FindSystemTimeZoneById` (Windows without ICU) |
| a bus in the store's database | `m.UseNpgsqlBus(options)` | `UseNpgsqlBus(connectionString)` (a second pool) |
| a view with a period | `static V IView<A, V>.From(A a)`, members `From`/`Until` | renaming the members |
| an event member added later | `bool AlwaysEmail = false` (no new version) | a version and an upcaster for an optional member |

## Verification

SDK 11.0.100, 2026-11-25 **[verified]**: `dotnet build Tarol.slnx` 0 warnings; `dotnet format Tarol.slnx --verify-no-changes`
clean; `dotnet test --solution Tarol.slnx` with Docker: 2258 of 2259, the one failure (`NpgsqlReadModelTests.The_script_creates_the_table_with_typed_columns_the_key_and_an_index_per_sort`)
pinned the unquoted DDL and was updated with TN-69 (`f3a889b`; the project then 48 of 48); `dotnet pack -c Release` +
`eng/verify-packages.sh`: exit 0; `eng/aot-smoke.sh`: exit 0, 221 checks, 0 IL warnings (the jobs smoke app now runs with
`InvariantGlobalization`). Suppressions: none. New diagnostics: TCHG012, TCHG013 (`AnalyzerReleases.Unshipped.md`). Public API
files regenerated with `eng/public-api.py update`.

## Release notes 0.1.0-preview.6 (round 3, for Tatalu)

These add to the round-2 notes ([tatalu-round-2.md](tatalu-round-2.md#release-notes-010-preview6-for-tatalu)); both ship in
`0.1.0-preview.6`. The owner makes the release (no tag, no package here). The Tarol-Svelte side is its
`docs/migration/tatalu-feedback-3.md`. Paths are Tatalu's.

**Must change (behaviour changes that can break a build or a test)**

1. **TCHG004 (TN-40):** a `[Read]` of the target's type next to the target member is no longer an error; a `[Read]` of
   the target's type without another target member still is. A hand-written `IHasReads` next to `[Read]`/`[Reads<T>]` is
   TCHG013 (was CS8646): move it into `[Reads<T>]`.
2. **TCHG012 (TN-74):** a host that references two contexts with the same reaction or job name no longer builds.
3. **`ChangeTest` (TN-76):** `When` with a principal but no authorization throws; pass `ChangeTestOptions.Authorization` or
   drop the principal (`Plan.Opened` in `backend/tests/Tatalu.Planung.Tests/Fixtures.cs`).
4. **Projection gate (TN-72):** one session per process holds all gate locks; a second host for the same projection in one
   process is not a runner (it was, on its own connection).
5. **Problem details (TN-37):** a new optional member `confirm`; tarol-codegen `0.1.0-preview.6` types it.
6. **`x-tarol-errors` (TN-40):** `X.NotFound` for `[Reads<X>]`, none for optional reads; the catalogue test asks for texts.

**May remove (workarounds)**

| Tatalu workaround | Replace with | Item |
|---|---|---|
| `Caregiver.Started` first event only for the projection (`backend/src/Tatalu.Einrichtung/Caregiver.cs`) | `static Empty(RecordedEvent r)` on the row | TN-54 |
| `GET /tpp/aenderungen` as a one-row list, `items[0]` (`backend/src/Tatalu.Einrichtung/CaregiverHistory.cs`) | `IRowQuery<TRow>` | TN-54 |
| `Deferred` → `ReleaseNotification` → `Queued` (`backend/src/Tatalu.Plattform/Benachrichtigungen/Notification.cs`, `Changes.cs`) | `Effect<…>(name, dueAt: e => e.NotBefore)` | TN-63 |
| category in the body of `PUT /ich/benachrichtigungen/kanaele` | an enum route member | TN-34 |
| port `ICareFiles` and its fixed list in tests (`backend/tests/Tatalu.Vertraege.Tests/ContractRulesTests.cs`, `backend/src/Tatalu.Vertraege/Daily.cs`) | `ProjectionTest.Over(test.Store)` + `JobTest.BeforeEachRun` | TN-57 |
| `StartsOn`/`EndsOn` in `backend/src/Tatalu.Dokumentation/MedicationViews.cs` | `From`/`Until` | TN-69 |
| UTC conversion in `AnnualPlan.Local` and the `Local` helpers (`backend/src/Tatalu.Planung/ChildAbsence.cs`, `Appointment.cs`, `Reminders.cs`) | a local `DateTimeOffset` as `DueAt` | TN-70 |
| `max_connections=200` (`docker-compose.yml`, `backend/src/Tatalu.AppHost/AppHost.cs`), the bus's own connection string in `backend/src/Tatalu.Host/TataluApp.cs` | default `max_connections` after measuring; `UseNpgsqlBus(options)` | TN-72 |
| `backend/src/Tatalu.Host/OpenApiDeduplication.cs` | – (TN-9 already in preview.6) | TN-73 |
| IANA-or-Windows fallback in `backend/src/Tatalu.Shared/DayCareTime.cs` | `TimeZones.Find("Europe/Berlin")` | TN-75 |
| hand-run `MirrorDeliverySettings` in `backend/tests/Tatalu.Planung.Tests/Fixtures.cs`, `Tatalu.Dokumentation.Tests/IncidentTests.cs`, `MedicationTests.cs`; stream filters by declaring type in `Tatalu.Planung.Tests/Fixtures.cs`, `Tatalu.Einrichtung.Tests/Fixtures.cs`; repeated lookups in `Tatalu.Datenschutz.Tests/AccessDirectoryTests.cs` | `ChangeTestOptions { Codec, Lookups, Reactions, RunReactions = true }`, `History<T>(id)`, `StoredStateMatchesReplay<T>(id, …)` | TN-76 |
| `ChildAbsenceView.FirstDay`/`LastDay` (`backend/src/Tatalu.Planung/ChildViews.cs`) | `From`/`Until` with an explicit `IView` factory | TN-77 |
| `LogFold.Apply` (`backend/src/Tatalu.Dokumentation/DailyLog.cs`), constructor copy in `CommunicationSettings.Set`, `AppointmentDetails.SameAs` (`backend/src/Tatalu.Planung/Appointment.cs`) | `with` | TN-78 |
| `AbsenceDetails` bundling (`backend/src/Tatalu.Planung/ChildAbsence.cs`), `LogTexts` bundling (`backend/src/Tatalu.Dokumentation/DailyLog.cs`) | separate `[PersonalData(Subject = …)]` members, sealed in `ChangeTest` too | TN-80 |
| `ConfirmQuota` and `Detail` "Jugendamt\|Tage" (`backend/src/Tatalu.Planung/Absences.cs`, `Changes.cs`), `WarningConfirmed` (`backend/src/Tatalu.Anwesenheit/Changes.cs`, `Handover.cs`, `AttendanceMonth.cs`), `frontend/apps/web/src/screens/planung/quota.ts` | `Error.ConfirmationRequired(code, args)`, `context.Confirmed(code)`, tarol-svelte's confirmation step | TN-37 |
| hand-written `IHasReads` (`backend/src/Tatalu.Einrichtung/Changes.cs` + `Tenants.cs` `CaregiverRead`, `ProfileChanges.cs`, `backend/src/Tatalu.Kinder/MeaslesChanges.cs`, `backend/src/Tatalu.Plattform/Profile/Changes.cs`, `backend/src/Tatalu.Planung/ChildChanges.cs`, `AppointmentChanges.cs`); `RecordIncident` as load-or-start only for its reads (`backend/src/Tatalu.Dokumentation/IncidentChanges.cs`) | `[Reads<T>]`, `[Reads<T>(SameIdAs = …)]`, `Optional` | TN-40 |
| integer arithmetic for amounts (E08–E10, planned) | `[Scale(2)]` value objects | TN-53 |

**Migration steps for Tatalu**

1. Update the Tarol packages and tarol-* npm packages to `0.1.0-preview.6`; build: fix TCHG004/TCHG012/TCHG013 where reported.
2. Replace the hand-written `IHasReads` with `[Reads<T>]` and run the tests; regenerate the client (`pnpm generate`) and let the
   catalogue test list the new `x-tarol-errors` codes.
3. Switch test fixtures to `ChangeTestOptions` with the app's codec, lookups and reactions; drop the hand-run reactions.
4. Switch the bus to `UseNpgsqlBus(options)`; measure connections (`SELECT count(*) FROM pg_stat_activity`) before lowering `max_connections`.
5. Replace confirmation flags with `Error.ConfirmationRequired` and the client confirmation step (TS-33).
6. Rename the workaround members (`StartsOn`, `FirstDay`, …) only with a new projection or view version where they are stored
   (a projection rename is a new `Version`; a view is not stored).
