# Application layout: one folder per aggregate, role suffixes

How to lay out the code of an application built on Tarol, so that a person or an agent can tell from the file tree which
aggregates a bounded context has, where a change, an event or a child entity is declared, and what each file holds.
The agent skill `tarol-application-layout` (meta-package `Tarol`) carries the rules in short form.

This is a convention for applications, not Tarol API: Tarol's generators do not look at folders or file names. Owner
decisions AL-1 … AL-9 (2026-11-27) and the skill's evaluation checklist: [docs/migration/application-layout.md](../migration/application-layout.md).
First application: Tatalu, pilot context `Tatalu.Dokumentation` (28 files → 97, behaviour unchanged).

Evidence labels: **[verified]** = reproduced by a build, a test or a diff (the command is given); **[verified by reading]** =
read in the code named, not run.

## 1. What Tarol fixes and what it leaves free

| Fact | Source | Evidence |
|---|---|---|
| **One generated module per assembly.** With `[assembly: GenerateTarolModule("…")]` each assembly gets one `GeneratedModule.Module` with its JSON contexts, aggregates, reactions, jobs, projections and HTTP entries. A bounded context is one project; folders inside it change nothing the generators emit. | `src/Changes/Tarol.Changes.Generators/ModuleGenerator.cs` | [verified by reading] |
| **Generated sets take their namespace from the assembly name**, not from the namespaces of your types (`GeneratedReactions`, `GeneratedEndpoints`, `GeneratedOpenApi`). | `src/Changes/Tarol.Changes.Generators/ChangesGenerator.cs`, `src/Changes/Tarol.Changes.AspNetCore.Generators/EndpointGenerator.cs` (`c.AssemblyName`) | [verified by reading] |
| **Aggregates, entities, changes and rows are `partial`**: one type may be split over several files (TPRM002 if a root or entity is not partial). | `src/Primitives/Tarol.Primitives/skills/tarol-primitives/SKILL.md` | [verified]: Tatalu's `X.Aggregate.cs` + `X.Events.cs` |
| **Stored event names come from the nesting, not the namespace or the file**: `snake(root) + "." + snake(nested path) + "." + snake(event)`, the root's own name dropped from the path (`MediaItem.PhotoRecorded` → `media_item.photo_recorded`, `MediaComment.Added` of an entity `MediaComment : Entity<MediaItem, MediaComment>` → `media_item.media_comment.added`). So an alias changes exactly when the chain of types around the event (without the root) changes. `[EventType("…", version)]` pins the name. | `src/Primitives/Tarol.Primitives.Generators/Discovery.cs` (`Alias`) | [verified]: event catalogue diff (§6) and a probe project (the moves in §5) |
| **Stream types** are the snake-cased root name, or `[StreamType("…")]`. | `Discovery.cs` (`StreamTypeOf`) | [verified]: same diff |
| **Reaction and job names are the change's type name** (or the `Name` of `[OnEvent<T>]` / `[Scheduled]`). They are stored in outbox rows, idempotency keys and job rows (TCHG007, TCHG011 on duplicates). An outbox row whose name no handler runs throws ("removed, or renamed without pinning its name"), is retried and then dead-lettered; job rows are keyed by (tenant, job name), so a renamed job starts a new row and the old cursor is left behind. | `src/Changes/Tarol.Changes.Generators/ReactionDiscovery.cs`, `JobDiscovery.cs` (`name = change.Name`); `src/Changes/Tarol.Changes/OutboxRows.cs`, `src/Sagas/Tarol.Sagas/ReactionWorker.cs`; [jobs.md](jobs.md) J-4 | [verified by reading] |
| **Projection names are the string of `[Projection("…")]`**; the read table and its checkpoint are keyed by it. | `src/Queries/Tarol.Queries/Attributes.cs` | [verified by reading] |
| **OpenAPI schema names are type names** (the simple name ASP.NET proposes), unless `[OpenApiSchemaName("…")]` (Tarol.Kernel) pins one; two types with one name throw. The typed Svelte client takes its type names from these schemas. | `src/Changes/Tarol.Changes.AspNetCore/OpenApiSchemaNames.cs` | [verified]: OpenAPI byte diff (§6) |
| **JSON contexts**: TPRM035 (warning) reports a type Tarol serializes that no `JsonSerializerContext` *of the assembly* generates; TPRM036 (info) an entry the context generates anyway. Both have code fixes. Moving entries between `partial` parts of one context changes nothing. | `src/Primitives/Tarol.Primitives.Generators/Diagnostics.cs` | [verified by reading] (from 0.1.0-preview.9) |

So: **files and folders are free, namespaces are almost free, type names and nesting are not.**

## 2. The layout

```
Tatalu.Dokumentation/                      one project = one bounded context = one Tarol module
  Dokumentation.Module.cs                  [assembly: GenerateTarolModule("…")] and the context's registration (when it has one)
  Dokumentation.Json.cs                    the JSON context: [JsonSourceGenerationOptions], the class declaration
  Dokumentation.Security.cs                the context's role map and policies (May<…>, MayRead<…>, AddPolicy)
  MediaItems/                              one folder per aggregate, plural of the aggregate's name
    MediaItem.Aggregate.cs                 state, factory, behaviour methods, invariants
    MediaItem.Events.cs                    partial: the nested events and their On handlers
    MediaItem.Lifecycle.cs                 the state enum with [Lifecycle]/[To] (when it has one)
    MediaItem.Values.cs                    the plain enums, records and constants of its state, events and parameters
    MediaItem.Json.cs                      partial part of the context's JSON class: this aggregate's [JsonSerializable] entries
    MediaItem.Security.cs                  this aggregate's role map class (when the context has one per aggregate)
    MediaItem.Rules.cs                     its row rules (IAuthorizationHandler, ChangeHandler<…>)
    MediaItem.Support.cs                   what its changes share: marker interfaces, load helpers, inputs, notice texts
    MediaItem.Endpoints.cs                 hand-written minimal-API endpoints and their request/response records
    MediaComment.Entity.cs                 a child entity: Entity<MediaItem, MediaComment> with its nested events
    UploadMedia.Change.cs                  one change (IChange/ICreation/ILoads) per file, with its [Endpoint]
    NotifyParentsOnUpload.Reaction.cs      a change with [OnEvent<…>]
    PurgeExpiredMedia.Job.cs               a change with [Scheduled]
    SendUploadMail.Effect.cs               an IEffect<…>
    MediaItemView.View.cs                  an IView<MediaItem, …> and the records only it uses
    MediaItemRow.Projection.cs             a [Projection] row, its sort enum and its IQuery/IRowQuery records
    MediaRetention.DomainService.cs        logic across aggregates, no state of its own
    IMediaStore.Port.cs                    an interface to the outside (files, mail, an identity provider) and its records
    InMemoryMediaStore.Adapter.cs          an implementation of a port, or of a Tarol extension point (ErasureParticipant)
    Caption.Value.cs                       a value object with its own rules (a validated record struct)
  Shared/                                  value objects and helpers that no single aggregate of the context owns
  Contracts/                               what other contexts compile against: [Publishes<…>] records, published types
```

## 3. Role suffixes

File names are `Name.Role.cs`, PascalCase (AL-1). `Name` is the declared type for a **single** role and the aggregate for a
**group** role.

| Suffix | Holds | Kind | Example |
|---|---|---|---|
| `.Module.cs` | `[assembly: GenerateTarolModule]`, the context's DI and module registration | context | `Dokumentation.Module.cs` |
| `.Json.cs` | the JSON context: options and class in `<Context>.Json.cs`, each aggregate's entries in a partial `<Aggregate>.Json.cs` | context / group | `MediaItem.Json.cs` |
| `.Security.cs` | role map classes (`Map(ChangeRoles)`), policies, sensitive-read lists | context / group | `Incident.Security.cs` |
| `.Rules.cs` | row rules: authorization handlers of one aggregate | group | `Incident.Rules.cs` |
| `.Aggregate.cs` | the root: attributes, state, factory, behaviour, invariants | single | `DailyLog.Aggregate.cs` |
| `.Events.cs` | the same `partial` root (or entity): nested events and `On` handlers (AL-2) | group | `DailyLog.Events.cs` |
| `.Lifecycle.cs` | the state enum with `[Lifecycle]`/`[To]` | single | `Order.Lifecycle.cs` |
| `.Entity.cs` | one child entity with its nested events | single | `MediaComment.Entity.cs` |
| `.Values.cs` | plain enums, records and constants of the aggregate's state, events and method inputs | group | `Incident.Values.cs` |
| `.Value.cs` | one value object with rules (`[NotEmpty]`, `[Length]`, `IValidatable`) | single | `LogText.Value.cs` |
| `.Change.cs` | one change with its `[Endpoint]` (AL-3: always one per file) | single | `TagPhoto.Change.cs` |
| `.Reaction.cs` | one change with `[OnEvent<…>]` | single | `RemindDose.Reaction.cs` |
| `.Job.cs` | one change with `[Scheduled]`, or an `IJob` | single | `PurgeExpiredMedia.Job.cs` |
| `.Effect.cs` | one `IEffect<…>` | single | `DeletePhotoData.Effect.cs` |
| `.View.cs` | one `IView<T, TView>` and the records only it uses | single | `IncidentView.View.cs` |
| `.Projection.cs` | one `[Projection]` row, its sort enum and its queries | single | `IncidentRow.Projection.cs` |
| `.Support.cs` | what the aggregate's changes share and is none of the above: marker interfaces for row rules, load helpers, validated inputs, notification texts, folds shared by root and view | group | `DailyLog.Support.cs` |
| `.Endpoints.cs` | hand-written endpoints (`MapPost`, uploads) and their records | group | `MediaItem.Endpoints.cs` |
| `.DomainService.cs` | stateless logic across aggregates (reads other aggregates, decides) | single | `PhotoConsents.DomainService.cs` |
| `.Port.cs` | an interface to the outside and the records in its signature | single | `IMediaStore.Port.cs` |
| `.Adapter.cs` | an implementation of a port or of a Tarol extension point | single | `MediaErasure.Adapter.cs` |
| `.Contract.cs` | a type other contexts compile against, in `Contracts/` (AL-5) | single | `ChildRegistered.Contract.cs` |

A reaction is a change, so it could be `.Change.cs`; `.Reaction.cs` and `.Job.cs` say *who* starts it, which is what a reader
looks for ("what happens when a photo is shared?").

## 4. Where a declaration goes

1. **A change lives in the folder of the aggregate it changes** (the `T` of `IChange<T>`/`ILoads<T>`), even when it reacts to
   another aggregate's event (AL-6). `NotePhotoInLog` reacts to `MediaItem.SharedWith` and changes `DailyLog`: it lives in
   `DailyLogs/`.
2. **A change whose target is in another context** (a notification, an audit trail) lives with the aggregate whose event
   starts it. `NotifyPhotoUntagged` changes `Notification` (Plattform) after `MediaItem.TaggingOverdue`: `MediaItems/`.
3. **A feature without its own aggregate** goes where its changes go. Tatalu's daily report changes `DailyLog`, so its
   changes, view and rows are in `DailyLogs/`; the setting `SetReportRelease` changes `DocumentationSettings`, so it is there.
4. **A type lives with the aggregate whose state or events define it**, even if another aggregate of the context uses it.
   Only a type no aggregate owns goes to `Shared/` (`LogText` is used by three aggregates and owned by none).
5. **Everything another context relies on goes to `Contracts/`** (AL-5). A file there is a promise to other contexts.
6. **One JSON context per context**; its options and class declaration in `<Context>.Json.cs`, each aggregate's entries in a
   partial part `<Aggregate>.Json.cs` next to the types they list.
7. **One namespace per context** (AL-4): folders are not namespaces. Everything in `Tatalu.Dokumentation/` stays in
   `namespace Tatalu.Dokumentation;`.

Why one namespace (AL-4): it is safe for stored names either way (§1), but a namespace per folder changes every `using` in
the host, the tests and the other contexts, and breaks code that writes a namespace out to tell a type from a member
(`Tatalu.Dokumentation.Incident.Record(…)` inside `RecordIncident`, whose property `Incident` hides the type). The plural folder
name keeps a later namespace from colliding with the aggregate's type (`MediaItems` vs `MediaItem`). IDE0130 ("namespace does
not match folder") is silent by default; if a repository raises it, set `dotnet_style_namespace_match_folder = false`.

## 5. Safe and unsafe moves

| Move | Effect | Safe? |
|---|---|---|
| A file to another folder of the same project | nothing the generators emit changes | **safe** [verified] |
| A declaration to another file of the same project | as above | **safe** [verified] |
| An event, or an `On` handler, to another `partial` part of **the same** type | the alias keeps the nesting | **safe** [verified] |
| An event **out of** its entity, or into another containing type | the path of containing types changes, so the alias does: `media_item.media_comment.added` → `media_item.added`; stored events of the old name no longer load | **unsafe**, unless `[EventType("old.alias")]` pins it [verified] |
| A root's event from nested to the namespace (`: AggregateRoot<MediaItem>.Event`) | the path stays empty: `media_item.uploaded` either way | safe for the alias [verified], but do not: the layout keeps events nested, and a reader no longer sees whose event it is |
| An entity from nested-in-root to top-level, or back | the root's name is dropped from the path either way: `media_item.media_comment.added` | safe for the alias [verified]; it changes the type's full name, which an OpenAPI schema or another context may show |
| A type to another namespace (in the same project) | aliases, stream types, reaction and job names, schema names do not change | safe for stored names; breaks `using`s and fully qualified references |
| A type to **another project** (another bounded context) | another module; TPRM035 if its JSON entry stays behind; the reaction moves to another `GeneratedReactions.Set` | a separate decision, not a layout change |
| **Rename** a change | the reaction or job name changes: waiting outbox rows of the old name dead-letter, a job starts a new row | **unsafe**, unless `Name = "Old"` on `[OnEvent<T>]`/`[Scheduled]` |
| **Rename** an aggregate | the stream type and every event alias change | **unsafe**, unless `[StreamType]` and `[EventType]` pin them |
| **Rename** an event | its alias changes | **unsafe**, unless `[EventType]` |
| **Rename** a view, row, input or enum | the OpenAPI schema and the generated client type change; a `[Projection]` keeps its table only by its string | **unsafe** for clients, unless `[OpenApiSchemaName]` |

A rename always needs a pinned name and the owner's OK. File names follow the types, never the other way.

## 6. Moving an existing context

One context per pull request, behaviour unchanged.

1. **Baseline.** Build with `-p:EmitCompilerGeneratedFiles=true` and copy every project's `obj/**/generated` folder. This is
   the event catalogue (aliases and stream types), the reaction and job tables, the endpoints, the OpenAPI transformers and
   the JSON contexts in one tree. Save the OpenAPI document and the generated client. Run all tests.
2. **Rename commit.** `git mv` each file, unchanged, to the folder of its aggregate and the name of its main declaration.
   Git follows each file with `git log --follow` because its content is unchanged.
3. **Split commit.** Move each declaration to the file of its role. Copy text; do not rewrite it. The only new text is the
   `partial class X { … }` around the events, the `using`s each file needs, and `[JsonSerializable]` lists split at entry
   boundaries.
4. **Prove it.**
   * `dotnet build` with 0 warnings, `dotnet format --verify-no-changes`, all tests (integration tests included);
   * the generated trees of step 1 and now are identical (`diff -r`);
   * the OpenAPI document is byte-identical (regenerate it, `git diff --exit-code`) and the regenerated client has no diff;
   * optional: a list of the assembly's types and members (System.Reflection.Metadata) is identical: no type was renamed.
5. **Document** in the application where each kind of file lives, with a table old file → new files.

Tatalu's pilot (`Tatalu.Dokumentation`, Tarol 0.1.0-preview.5): 151 declarations and the JSON context from 28 files into 97 files; all 2,394
generated files of the 26 projects identical, 85 event aliases and stream types identical, OpenAPI document and client
unchanged, 750 of 750 tests passed (25 integration tests against PostgreSQL in Docker) **[verified]**.

## 7. Checks

Documented, not enforced (AL-7). A layout analyzer would be new Tarol API and waits for the owner's decision. Proposal:

| Id | Rule | Severity |
|---|---|---|
| TLAY001 | a `.Change.cs`, `.Reaction.cs` or `.Job.cs` file declares exactly one change, and nothing else | warning |
| TLAY002 | a `.Reaction.cs` file's change has `[OnEvent<…>]`; a `.Job.cs` file's has `[Scheduled]` | warning |
| TLAY003 | an `.Aggregate.cs` file declares one `AggregateRoot<T>`; an `.Entity.cs` file one `Entity<TRoot, T>` | warning |
| TLAY004 | an `.Events.cs` file holds only nested events and `On` handlers of one `partial` type | warning |
| TLAY005 | a file's `Name` part names a type it declares (single roles) or an aggregate of the project (group roles) | info |

It would read only syntax trees and file paths (no semantic model beyond the base types), be opt-in per project
(`<TarolLayout>true</TarolLayout>`), and ship in `Tarol.Primitives`' analyzer assembly.
