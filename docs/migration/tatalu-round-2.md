# Tatalu feedback, round 2 (TN-19 …)

Round 2 of the feedback from Tatalu ([docs/feedback/tatalu.md](../feedback/tatalu.md#round-2)): about 150 findings
collected while building 13 contexts and about 60 features on `0.1.0-preview.5` (Tatalu
`docs/implementierung/framework-befunde.md` on `integration/welle-2`). This note lists every public API and behaviour
change of the round, item by item, with the test that was red before the change and is green after it. The family notes
point here. Labels as in the plan: **[verified]** = run here; **[verified by reading]** = read in the code.

Each section: what Tatalu hit, the change, the test (red → green), and what Tatalu changes or removes after
`0.1.0-preview.6` (also collected in [Release notes](#release-notes-010-preview6-for-tatalu)).

## TN-19 Fix: several personal members may share one subject member

Tatalu FB-F13.3-2 (and the bundling in FB-F04.2-3). Two `[PersonalData(Subject = nameof(X))]` members of one event or
state failed on `Append` with "The subject member 'x' of a personal field is missing or empty":
`PersonalJson.SubjectValues` read the value after the first match and compared it, not the property name, with the next
member's subject name.

* **Behaviour:** any number of personal members may name the same subject member (or different ones). No API change.
* **Tests [verified]:** `DataProtectionCodecTests.Two_Members_With_The_Same_Subject_Seal_For_That_Subject` (red before:
  `JsonException` from `PersonalJson.Subjects`), `InMemoryDataProtectionTests.Two_State_Members_With_The_Same_Subject_Round_Trip_Sealed`
  (state and events, with `AssertNoPlaintext`).
* **Tatalu:** may split `AuditValues` (before/after as one sealed value, `Tatalu.Datenschutz` audit trail) and
  `HandoverNote` (`AttendanceMonth.Notes`, `CheckedIn.Note`, `CheckedOut.Note`) into separate personal members.
  `[PersonalSubject]` on the aggregate (FB-F04.2-3) stays open (TN-58).

## TN-20 Fix: computed properties no longer leak personal data unsealed (TPRM034, `AssertNoPlaintext`)

Tatalu FB-F13.3-8, FB-F13.4-1. A computed public property of an event (`AuditTrail.Tracked.Record` returned before/after
from a `[PersonalData]` member) was written by System.Text.Json into the stored payload in plaintext, next to the sealed
member, and survived the subject's erasure. Nothing reported it.

Options considered: (a) the codec writes only constructor members — it would need its own serializer contract next to the
application's `JsonSerializerContext` (or reflection) and would silently drop data a type means to store; (b) an analyzer.
Chosen: (b), plus a test helper.

* **New diagnostic TPRM034 (error, `PersonalDataAnalyzer`):** an event or a projection row (`[Projection]`) that has a
  `[PersonalData]` member must not have a public instance property with a getter and no backing field, unless it is
  `[JsonIgnore]` (condition `Always`). Fix: make it a method or mark it `[JsonIgnore]`. Types without personal data are not
  checked (nothing to leak); auto-properties are stored data and pass.
* **Aggregate and entity state (FB-F13.4-1): *Checked: does not reproduce.*** The stored state of a root or entity is
  written by the generated `WriteState` from fields and auto-properties only (computed properties of the aggregate are not
  part of it); a record inside a `[PersonalData]` state member is sealed as a whole, computed members included
  **[verified by reading]** (`Emitter.State.cs`, `PersonalJson.Seal`). Tatalu's `ConsentRecord.ToEntry()` was a precaution;
  it may stay a method or become a property again.
* **New API:** `InMemoryAggregateStore.AssertNoPlaintext(string plaintext)` throws `InvalidOperationException` naming the
  stream type, stream and event (or the state) when the value appears, raw or JSON-escaped, in a stored payload or state
  (values shorter than 4 characters are refused: they occur by chance in ids and nonces). `ChangeTest.Store` is that store.
* **Tests [verified]:** `PersonalDataDiagnosticTests.TPRM034_*` (10; red before: no diagnostic), 
  `InMemoryDataProtectionTests.AssertNoPlaintext_Finds_A_Value_The_Store_Holds_Unsealed`.
* **Tatalu:** `AuditTrail.cs` (`ToRecord()`) and `ChildConsents.cs` (`ToEntry()`) may stay; the integration test
  `Vorher_nachher_steht_versiegelt_im_Event_Store` can use `AssertNoPlaintext` in unit tests too. A new build may report
  TPRM034 where a computed property sits next to personal data: that is the leak.

## TN-21 Fix: TPRM013 for `with` and initializers, TPRM013/022/023 for deconstructing assignments

Tatalu FB-F04.6-4, FB-F15.5-1 (TPRM013 false positive) and FB-F03.4-2 = TN-14 (TPRM022 false positive).

* **TPRM013** reported `input with { Confirmed = true }` and `new Draft { By = 1 }` in a behaviour method as "state
  assigned outside an On handler": inside a `with` or an object initializer the receiver is the new value (an implicit
  receiver), not the aggregate. Now only members of the aggregate or entity itself (`this.X`, `X`) count.
* **TPRM022/TPRM023** did not see `(SignedOn, Status) = (e.On, ContractStatus.Signed)`: a deconstructing assignment now
  counts element by element (a constant element is the assigned state, a computed one disables TPRM022 as a computed plain
  assignment does). **TPRM013** now also reports a deconstructing assignment of state in a behaviour method (it was missed).
* **Tests [verified]:** `DiagnosticTests.TPRM013_Is_Not_Reported_For_A_With_Expression_Or_An_Initializer` (3 cases),
  `DiagnosticTests.TPRM013_A_Deconstructing_Assignment_In_A_Behaviour`, `LifecycleDiagnosticTests.TPRM022_Counts_A_Deconstructing_Assignment`,
  `LifecycleDiagnosticTests.TPRM023_Counts_A_Deconstructing_Assignment` (all 6 red before).
* **Tatalu:** `AttendanceMonth.CheckOut` may use `pickedUpBy with { WarningConfirmed = true }` again,
  `ChangeProposal.Propose` `item with { Accepted = null }`, and `CareContract.cs` a tuple assignment in `On`.

## TN-22 Fix: two types may not share one OpenAPI schema name (`[OpenApiSchemaName]`)

Tatalu FB-INT-1, FB-INT2-1. The document named schemas after the simple type name; `Tatalu.Einrichtung.Label` (1–80)
and `Tatalu.Kinder.Label` (1–120) became one schema `Label`, whichever came last, and the generated client checked the
other context's rules. The same happened to two `NotificationCategory` enums. Nothing reported it.

Options: (a) make the names unique automatically (namespace prefix) — the client type names would depend on which type the
document met first and change when an endpoint is added; (b) fail and let the application choose. Chosen: (b).

* **Behaviour:** the generated `AddTarolEndpoints` wraps `OpenApiOptions.CreateSchemaReferenceId` once per document
  (`OpenApiSchemaNames.Claim`). Two different types with one schema name make the document fail with
  `InvalidOperationException` naming both types and the fix. Same type, same name: as before.
* **New API:** `Tarol.Kernel.OpenApiSchemaNameAttribute(string name)` on a class, struct or enum pins its schema name; a
  generic type over a pinned type uses it (`ListOfPantryLabel`). `Tarol.Changes.AspNetCore.OpenApiSchemaNames` (`Claim`,
  `Name`; `EditorBrowsable(Never)`, called by generated code).
* **Not detected:** two different types with the same name *and* the same content are an error too (renaming one, or
  the attribute, resolves it); comparing contents would need the finished schemas.
* **Tests [verified]:** `ModuleTests.Same_named_contract_types_of_two_assemblies_keep_their_own_schemas` (red before:
  `maxLength` 80 for both), `OpenApiSchemaNameTests` (2).
* **Tatalu:** `ContractNameTests` (host) may go; `Tatalu.Kinder.ShortText` and `ParentNotificationCategory` may take their
  natural names again with `[OpenApiSchemaName("KinderLabel")]` on one of them. A document that fails after the update has
  had a silent collision.

## TN-31 Nullable references and `Option<T>` in the schema

Tatalu FB-3, FB-F01.1-4, FB-F02.1-5, FB-F13.3-5, FB-F17.3-3, FB-F03.9-1, FB-F04.3-2, FB-F17.2-3. Together with TPRM030
(personal = nullable) only nullable scalars were usable in views and rows.

* **Behaviour:** `Option<T>` is no component any more (`OptionOfX` disappears from `components.schemas` and from the
  generated client); it is written in place: `oneOf: [{type: "null"}, {$ref: T}]` when `T` has a component (a record, an
  enum, a value object), `type: [T, "null"]` for a primitive (as before, now with `T`'s extensions such as `x-tarol-rules`).
  `T?` keeps the shape ASP.NET writes, `oneOf: [{type: "null"}, {$ref: T}]`. tarol-codegen (Tarol-Svelte round 2) makes
  both `T | null`.
* **Build diagnostic (FB-F04.3-2): *closed without a diagnostic.*** The diagnostic was asked for because the generator
  rejected the shape at generation time; with tarol-codegen accepting it there is nothing left to report.
* **Tests [verified]:** `ModuleTests.A_value_object_of_an_assembly_without_endpoints_has_its_type_and_rules` (the `note`
  member: `oneOf` with `$ref PantryLabel`; red before: `OptionOfLabel` typed `["null","object"]`).
* **Tatalu:** views and rows may use `T?`/`Option<T>` of records and enums again: `SetupView.Setting`/`FederalState`/
  `LicenseStatus`, `AuditRow.Values`/`Role`, `SyncProbeView`, `AttendanceCapacityRow.Status`, `SubscriptionView.Method`,
  the flat address fields of the child view, `Option<PostalAddress>` in `ChildChanges.cs`; drop the casts in the frontend.
  Generated names `OptionOfX` are gone.

## TN-32 Value objects of referenced assemblies get their type and rules

Tatalu FB-F01.1-3, FB-F02.1-4, FB-INT-3. A value object of an assembly without endpoints (`Tatalu.Shared.PersonName`,
`Email`) was `{}`, `Option<…>` of it `object`: Roslyn does not mark a record struct from metadata as a record, so the
endpoint generator did not take it for a value object.

* **Behaviour:** the generator also takes a struct with the `Check(T, ref ValidationErrors)` method the validation generator
  writes for a value object; its schema gets type, format, bounds, pattern and `x-tarol-rules` like a value object in the
  same assembly. No API change.
* **Tests [verified]:** `ModuleTests.A_value_object_of_an_assembly_without_endpoints_has_its_type_and_rules` (red before:
  no `type`); new fixture `Tarol.Changes.AspNetCore.Tests.Pantry` (contract types without endpoints).
* **Tatalu:** `NamePart`, `EmailAddress` (`Tatalu.Einrichtung/Values.cs`), `Tatalu.Kinder.Name`, the per-context `PostalCode`,
  `Phone`/`PhoneNumber` may become one type each in `Tatalu.Shared`.

## TN-33 Enums by name in the schema, also BCL enums

Tatalu FB-4, FB-F02.3-2 (= TN-11). Entries wrote enums by name (a context with `UseStringEnumConverter`), the document said
`integer`.

* **Cause:** `AddEndpoints` appended the application's contexts to ASP.NET's HTTP `JsonOptions`, after its own resolver (the
  reflection-based default when reflection is on). That one answered first for an enum and wrote numbers; the OpenAPI
  document is built from those options.
* **Behaviour:** the contexts are inserted right after the value objects' resolver, before ASP.NET's. The schema transformer
  asks the options how they write an enum: names give `type: string` with `enum` (the names as the converter writes them,
  naming policy included), numbers keep ASP.NET's schema. This covers enums of other assemblies and BCL enums
  (`DayOfWeek`) without an attribute. ASP.NET's own writing (`Results.Json`, …) now also uses the contexts' metadata first.
* **Tests [verified]:** `ModuleTests.Same_named_contract_types_of_two_assemblies_keep_their_own_schemas` (`Shade`,
  `PantryShade`, `DayOfWeek` by name; red before: `integer`, no `enum`).
* **Tatalu:** the `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` on contract enums (`Role`, `MembershipState`, …)
  may go when the context sets `UseStringEnumConverter`; `Tatalu.Shared.Weekday` may become `DayOfWeek` (the wire format
  is the same names).

## TN-27 Computed members are required in the schema

Tatalu FB-F04.6-2. `AttendanceBlock.Minutes => …` is always written but was `minutes?: number`.

* **Behaviour:** a get-only member without a setter that is not nullable is listed in `required`.
* **Test [verified]:** `ModuleTests.A_computed_member_that_is_not_nullable_is_required` (red without the change).
* **Tatalu:** `minutesBetween` in the frontend may use `block.minutes`.

## TN-23 Fix: `Required` in every depth; `DateOnly`/`TimeOnly` value objects

Tatalu FB-F02.1-3, FB-F03.9-1. Only top-level members of an input were reported as `Required`; in a nested record
System.Text.Json read a missing `DateOnly` as 0001-01-01 and a missing enum as its first value. A value object over
`DateOnly` had no JSON converter (`{"value":…}`, 400 `Json` for a bare date).

* **Behaviour:** the endpoint generator passes the tree of the nested input records (through `Option<T>`, `T?` and lists, to
  a depth of four); the binder adds `first.on: Required`, `steps[1].on: Required` for a missing non-nullable member. A
  missing nullable member or one with a default is fine; a missing `Option<T>` member is `Required` as at the top level.
* **Behaviour:** a value object over `DateOnly` or `TimeOnly` writes and reads the bare value (`"2020-01-02"`, `"07:30:00"`)
  with System.Text.Json's own converters. "A value object without a rule attribute is not recognised": *Checked: does not
  reproduce* — a custom `Validate` makes it a value object; the missing converter was the cause.
* **New API (EditorBrowsable Never, generated code):** `RequiredMember`, `ChangeEndpoints.Missing`.
* **Tests [verified]:** `ModuleTests.A_member_missing_in_a_nested_record_is_required` (red before: 204),
  `JsonTests.DateOnly_And_TimeOnly_Value_Objects_Write_The_Bare_Value` (red before).
* **Tatalu:** `BirthDate` may be a `DateOnly` value object (`Tatalu.Kinder/Values.cs`); `TerminationReason.Unspecified` may go.

## TN-24 Fix: `x-tarol-topics` also when only the host references SignalR

Tatalu FB-F17.3-2. The topics transformer was generated only into an assembly that references `Tarol.Messaging.SignalR`.

* **Behaviour:** every assembly's `AddTarolEndpoints` writes the topics, read from the new `Tarol.Changes.IContractTopics`
  (`ContractTopic`), which `UiPushTopics` implements and `PushToUi` registers. Without UI push: no extension, as before.
* **Tests [verified]:** `OpenApiTopicsTests.The_topics_are_written_when_only_the_host_references_SignalR`; the `openapi`
  generator snapshot shows the transformer that was missing.
* **Tatalu:** the `Tarol.Messaging.SignalR` reference in `Tatalu.Einrichtung.csproj` (only for this) may go.

## TN-25 Fix: an erasure no longer fails while projections build

Tatalu FB-F17.5-4, FB-INT2-4.

* **Behaviour:** `Replace`/`Remove` of the Npgsql read store on a table no host has prepared do nothing (nobody read the feed
  into it; it is built later from the erased streams). The eraser of a projection whose host in this process is still
  building waits until it is current (`ProjectionOptions.ErasureWait`, default 1 min; then `TimeoutException`, so the
  erasure is incomplete and is run again) — a batch read before the key was erased is written first.
* **Tests [verified]:** `NpgsqlReadModelTests.Replace_And_Remove_On_A_Table_No_Host_Prepared_Do_Nothing` (red before: 42P01),
  `ProjectionHostsTests.An_erasure_waits_until_the_hosts_projection_is_current_before_it_refolds` (red without the wait),
  `DataProtectionIntegrationTests.An_Erasure_Before_The_Projections_Prepared_Their_Tables_Completes` (green before too: the
  started hosts prepare their tables at once; it guards the path end to end). The exact exception Tatalu saw was not
  recorded; both causes found in the code are fixed.
* **Tatalu:** the erasure retries in the host's effects may stay (the outbox retries anyway); none is needed in tests.

## TN-26 `ProjectionHosts.WaitUntilCurrent` and `ReadyWhenCurrent`

Tatalu FB-INT2-3.

* **New API:** `ProjectionHosts` (registered with the projections: `All`, `Building`, `WaitUntilCurrent(timeout, ct)`),
  `ProjectionHost.WaitUntilCurrent(timeout, ct)`, `ProjectionsBuilder.ReadyWhenCurrent()` (a `ReadinessCheck` "projections":
  the `ready` health group of `AddOperations` stays down while one builds; opt-in).
* **Tests [verified]:** `ProjectionHostsTests` (3).
* **Tatalu:** `TestApp.Start`'s own wait over every `ProjectionHost` (`backend/tests/Tatalu.Host.Tests/TestApp.cs`) becomes
  `await services.GetRequiredService<ProjectionHosts>().WaitUntilCurrent(TimeSpan.FromSeconds(60))`, failing with `Building`.

## TN-28 Fix: TCHG006 names a type that does not resolve

Tatalu FB-F15.1-1. "the second parameter must be RecordedEvent, not RecordedEvent" without `using Tarol.Primitives;`.
Now: "the type 'RecordedEvent' of the second parameter is not found (missing 'using Tarol.Primitives;'?)"; the other message
names full types. **Test [verified]:** `DiagnosticTests.TCHG006_factory_shape` (2 cases red before).

## TN-29 Fix: TVAL009 looks at the type used

Tatalu FB-F15.1-6. `MyJson.Default.Info` was reported when the context knew any value object. A typed member now checks its
own type's graph. **Test [verified]:** `DiagnosticTests.TVAL009_A_Same_Assembly_Context_Used_Directly` (red before: 4, not 3).
**Tatalu:** `ValueObjectJson.Options(ElternJson.Default)` for `InvitationInfo` may become `ElternJson.Default.InvitationInfo`.

## TN-30 / TN-38 `x-tarol-errors` names what can happen

Tatalu FB-F04.1-2, FB-F17.6-1 (wrong codes), FB-F01.1-9, FB-F02.1-6, FB-F15.1-8 (missing codes).

* **Behaviour:** a `[Read] Id<X>` member declares `X.NotFound` (422); an `ILoads<T>` change declares `T.NotFound` only when
  `Load` returns it with a constant code; a creation declares `T.AlreadyExists` only when its input has an `Id<T>` member or
  `T` is `[PerTenant]`; `Invalid.Field(path, code)` codes in the change and its aggregate (two calls deep) are declared like
  `Error` codes.
* **Open (TN-38, part):** `Lookup.Taken` per lookup (`Define(…, code: "Child.Duplicate")`) and codes of row rules need a
  declaration the generator can see; proposal for round 3: a `code` on `StoreLookups.Define`/`DefinePersonal` and
  `[Codes("Access.Denied")]` on a row rule.
* **Tests [verified]:** `GeneratorTests.The_problem_codes_name_what_can_happen` (red before), the `endpoints` snapshot
  (`Order.AlreadyExists` gone for a creation with `Id<Order>.New`).
* **Tatalu:** catalogue texts for `AttendanceMonth.NotFound`, `SupportRequest.AlreadyExists`, `DataExport.AlreadyExists`,
  `DataSubjectRequest.AlreadyExists` may go; the hand-written codes in `de.ts` (`Window.Overlap`, `Caregiver.ContactRequired`,
  `Invitation.NoEmail` …) are now in the document and checked by the catalogue test.

## TN-35 `[ContractType]` and `[ContractConstant]`

Tatalu FB-F02.6-2, FB-F13.5-1.

* **New API (`Tarol.Kernel`):** `ContractTypeAttribute` (class, struct, enum: schema in the components, name in
  `x-tarol-types`), `ContractConstantAttribute(string? name = null)` (a public `const` field: `x-tarol-constants`
  `{ "<Type><Field>" | name: value }`). Several assemblies merge into one list and one map. The type must be in a JSON context.
* **Tests [verified]:** `ModuleTests.Contract_types_and_constants_of_both_assemblies_are_in_the_document`.
* **Tatalu:** `[ContractType]` on `MeaslesStatus`, `[ContractConstant]` on `PrivacyDocuments.ProcessingAgreementVersion`;
  then `screens/kinder/measles.ts` and the `version` copy in `PrivacyDocuments.svelte` go (tarol-codegen TS-10 exports them).

## TN-36 Several reasons in one refusal

Tatalu FB-F03.4-5.

* **New API (`Tarol.Results`):** `Error.Reasons` (`IReadOnlyList<string>?`, JSON `reasons`), `Error.Conflict(code, reasons,
  detail)`. `Error` equality compares the reasons by value. Problem details carry `reasons`; the OpenAPI problem schema
  describes it.
* **Test [verified]:** `ModuleTests.A_refusal_with_several_reasons_lists_them_in_the_problem`; `EdgeTests` (schema).
* **Tatalu:** `Contract.Prerequisites` with `detail = "License,Measles"` becomes `Error.Conflict("Contract.Prerequisites",
  ["Contract.NoLicense", "Contract.NoMeasles"])`; `gapsOf` in `screens/vertraege/contract.ts` reads `problem.reasons`.

## TN-40 (part) `ChangeContext.TryRead`

Tatalu FB-F03.3-2. **New API:** `ChangeContext.TryRead<T>(out T?)`, `Reads.Find<T>()`. **Test [verified]:**
`SpecialChangeTests.TryRead_answers_without_an_exception_when_nothing_was_loaded`. **Tatalu:** `CareFacts.ReadOccupancy`
(`backend/src/Tatalu.Vertraege/Facts.cs`) drops its `catch`. The rest of TN-40 (`[Read(FromTenant)]`, `Read<T>(id)`,
`Optional`, TCHG004 only for the target) is designed in [change-reads.md](../architecture/change-reads.md) and open.

## TN-45 A change without an expected version runs again when it loses a race

Tatalu FB-F17.3-10, FB-F02.2-2. **Behaviour change:** the pipeline used to return `Stream.VersionConflict` always ("never
retried"). A change without `ExpectedVersion` (no `If-Match`) is now loaded and decided again, up to
`ChangePipeline.ConflictAttempts` (3) times; with `ExpectedVersion` the conflict is returned at once. Reactions run through
the same pipeline and benefit too. Log event 7006 (Debug) per retry.

* **Tests [verified]:** `PipelineTests.A_change_without_an_expected_version_that_loses_a_race_runs_again`, `…_decides_again_and_may_be_refused`,
  `…_keeps_losing_returns_the_conflict_after_three_attempts`, `…with_an_expected_version_gets_the_conflict_and_is_not_retried`;
  they replace `A_version_conflict_is_returned_not_retried`, which pinned the old rule.
* **Tatalu:** the three immediate retries in `frontend/apps/web/src/lib/offline/queue.svelte.ts`, the wait for the reaction in
  `KinderGuardianHostTests` and the two-device retry of `MonthLoad.OrBegin` may go (Tarol-Svelte TS-32).

## TN-61 `TopicRules.Subscribe`/`Publish` with several roles

Tatalu FB-F17.3-9. **New API:** `TopicRules.Subscribe(string topic, params string[] roles)`, `Publish(…)`. **Test [verified]:**
`PushTests.A_topic_rule_may_name_several_roles`. **Tatalu:** the loop over TPP/VTP/AK in `TataluApp.cs`.

## TN-68 Skills

Written from the code: `tarol-queries` (instance `When(e, r)`, `r.Event.EntityId`/`ParentId`), `tarol-messaging` (tenant
and entity in `EffectContext`, effects in `AddMessaging` without a bus), `tarol-sagas` (entity of an event, reactions run
after the commit, the reservation pattern TN-52, versioned parameters TN-49), `tarol-primitives` (`TypeInfoPropertyName`
against SYSLIB1031, TN-39), `tarol-data-protection` (namespace, TPRM034, `AssertNoPlaintext`), `tarol-changes` (TN-45),
`tarol-queries` (TN-26). *Checked:* `tarol-security` already names `requirement.Context` with `Now` (FB-F04.4-2).

### Skill evaluation checklist (round 2)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| a list row per entity event needs the entity's id | instance `When(TEvent e, RecordedEvent r)`, `r.Event.EntityId` | `r.StreamId` (the root's) |
| prove a personal value is never stored in plain text | `test.Store.AssertNoPlaintext("ada@example.com")` | a computed property next to `[PersonalData]` (TPRM034) |
| a test waits until lists answer | `ProjectionHosts.WaitUntilCurrent(TimeSpan.FromSeconds(60))` | retrying on 503 |
| an enum only the UI needs | `[ContractType]` on it, in a JSON context | a copy in TypeScript |
| refuse with every missing prerequisite | `Error.Conflict("X.Prerequisites", ["X.NoLicense", …])` | codes joined into `detail` |
| a change without If-Match races a reaction | nothing: the pipeline retries 3 times | a retry loop around `Run` |
| at most N places over all contracts | a reservation on a counter aggregate | reading a projection in `Execute` |

## TN-39 *Checked:* the JSON context list cannot be generated

Tatalu FB-F01.2-1, FB-F02.2-3. A source generator's output is not seen by another generator: the Primitives generator cannot
add `[JsonSerializable]` attributes the System.Text.Json generator would read, and a context written by Tarol would duplicate
the application's. The modules of TN-8 already combine one context per assembly. Closed with the documented
`TypeInfoPropertyName` (TN-68); SYSLIB1031 names the type.

## Verification

See the end of this note (filled in after the last commit of the round).

## Release notes 0.1.0-preview.6 (for Tatalu)

What Tatalu changes or may remove after updating to `0.1.0-preview.6` (Tarol-Net; the Tarol-Svelte side is in its
`docs/migration/tatalu-feedback-2.md`). Paths are Tatalu's (`backend/…`, `frontend/apps/web/…`).

**Must change (behaviour changes that can break a build or a test)**

1. **Schema names (TN-22):** the OpenAPI document fails when two different types share a simple name. Give one of them
   `[OpenApiSchemaName("…")]` (`Tarol.Kernel`) or rename it. `ContractNameTests` (host) may then go.
2. **`Option<T>` (TN-31):** no `OptionOfX` components any more; generated clients name the member type `X | null`. Replace
   uses of `OptionOfX` types in the frontend; regenerate with tarol-codegen `0.1.0-preview.6`.
3. **Enums (TN-33):** enums are `string` with their names when the JSON context writes names (`UseStringEnumConverter`);
   ASP.NET's own JSON writing (`Results.Json`, Minimal API results) now uses the application's contexts first.
4. **TPRM034 (TN-20):** a computed public property on an event or row with `[PersonalData]` is an error: make it a method or
   `[JsonIgnore]`.
5. **Nested `Required` (TN-23):** a client that left out non-nullable members of nested records now gets 400 `Required`.
6. **Version conflicts (TN-45):** a change without `If-Match` is retried up to three times instead of answering 409.
7. **`x-tarol-errors` (TN-30, TN-38):** codes change (`Customer.NotFound` for `[Read]`, `Invalid.Field` codes added;
   `X.AlreadyExists` and an `ILoads` `X.NotFound` removed where they cannot occur): the catalogue test will ask for texts of
   the new codes and allow removing the others.

**May remove (workarounds)**

| Tatalu workaround | Replace with | Item |
|---|---|---|
| `AuditValues` (before/after as one sealed value), `HandoverNote` bundling | separate `[PersonalData(Subject = …)]` members | TN-19 |
| integration-only check "no plaintext in the event store" | also `test.Store.AssertNoPlaintext(value)` | TN-20 |
| local variable instead of `with` (`AttendanceMonth.CheckOut`), constructor in `ChangeProposal.Propose`, tuple split in `CareContract.cs` | `with`, tuple assignment | TN-21 |
| `Tatalu.Kinder.ShortText`, `ParentNotificationCategory`, `ContractNameTests` | natural names + `[OpenApiSchemaName]` | TN-22 |
| `BirthDate` as `[Pattern] string`, `TerminationReason.Unspecified` | `DateOnly` value object; plain enum | TN-23 |
| `Tarol.Messaging.SignalR` reference in `Tatalu.Einrichtung.csproj` | – | TN-24 |
| retried erasure in `KinderStoreTests` (already removed) | – | TN-25 |
| own wait over `ProjectionHost`s in `backend/tests/Tatalu.Host.Tests/TestApp.cs`; 503 retry loops | `ProjectionHosts.WaitUntilCurrent`, `ReadyWhenCurrent()` | TN-26 |
| `minutesBetween` in the frontend | `block.minutes` (required) | TN-27 |
| `ValueObjectJson.Options(ElternJson.Default)` for `InvitationInfo` | `ElternJson.Default.InvitationInfo` | TN-29 |
| texts for `AttendanceMonth.NotFound`, `*.AlreadyExists` of generated ids; hand-written codes in `de.ts` | the document's codes | TN-30, TN-38 |
| `string?` views (`SetupView`), flat address fields, `AuditRow.Values`/`Role` as text, `SyncProbeView` lists, `AttendanceCapacityRow.Granted`, `SubscriptionView.Method`, three `Option<…>` in `ChildChanges.cs` | `T?` / `Option<T>` of records and enums | TN-31 |
| `NamePart`, `EmailAddress` (`Tatalu.Einrichtung/Values.cs`), `Tatalu.Kinder.Name`, per-context `PostalCode`/`Phone` | one value object each in `Tatalu.Shared` | TN-32 |
| `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` on contract enums, `Tatalu.Shared.Weekday` | `UseStringEnumConverter`, `DayOfWeek` | TN-33 |
| `screens/kinder/measles.ts`, the `version` copy in `PrivacyDocuments.svelte` | `[ContractType]`, `[ContractConstant]` | TN-35 |
| `detail = "License,Measles"`, `gapsOf` in `screens/vertraege/contract.ts` | `Error.Conflict(code, reasons)`, `problem.reasons` | TN-36 |
| `catch` in `CareFacts.ReadOccupancy` (`Tatalu.Vertraege/Facts.cs`) | `context.TryRead<Occupancy>(out var o)` | TN-40 |
| three retries in `lib/offline/queue.svelte.ts`, waiting for the reaction in `KinderGuardianHostTests` | – (the pipeline retries) | TN-45 |
| loop over roles in `TataluApp.cs` | `Subscribe(topic, "tpp", "vtp", "ak")` | TN-61 |
