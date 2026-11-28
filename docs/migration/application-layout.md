# Application layout (decisions, verification, skill checklist)

The convention: [docs/architecture/application-layout.md](../architecture/application-layout.md). This note records the
owner's decisions, how the facts were checked, and the evaluation checklist of the skill `tarol-application-layout`.

Public API and behaviour: **none changed.** The PR adds two documents, one skill and one row in `tarol-overview`.

## 1. Owner decisions (2026-11-27)

| Id | Question | Decision | Rejected |
|---|---|---|---|
| AL-1 | Spelling of role suffixes | PascalCase `Name.Role.cs` (like Tatalu's existing `AttendanceMonth.Lists.cs`) | lowercase `.aggregate.cs`; folders only without suffix |
| AL-2 | Where an aggregate's events and `On` handlers go | a separate `partial` part `X.Events.cs` | in `X.Aggregate.cs` next to the behaviour |
| AL-3 | One change per file | always, reactions and jobs included | an `X.Changes.cs` below 4 changes |
| AL-4 | Folder = namespace? | no: one namespace per context | `Tatalu.Dokumentation.MediaItems` per folder |
| AL-5 | Cross-context contracts | a `Contracts/` folder, `Name.Contract.cs` | next to the publishing aggregate |
| AL-6 | A feature without its own aggregate (Tatalu's daily report) | the folder of the aggregate its changes change; a reaction whose target is in another context lives with the aggregate whose event starts it | a feature folder `Reports/` |
| AL-7 | Checks | documented now; a layout analyzer (TLAY001 … TLAY005, convention §7) is proposed for a later decision | a check script in the application; documentation only without proposal |
| AL-8 | Which package ships the skill | the meta-package `Tarol`: applications reference it, `tarol-overview` (the entry point) is there, the layout spans every package; `tarol-primitives` is already 13.9 KB | `Tarol.Primitives` |
| AL-9 | Role list refinements from the pilot | `.Values.cs` (group of plain enums/records), `.Support.cs` (what the changes share), `.Endpoints.cs`, `.Security.cs` per aggregate where the context has one role map class per aggregate; `.Rules.cs` means authorization row rules only (Tatalu's `PhotoRules`/`IncidentRules` are parameters and helpers, so they go to `.Values.cs`/`.Support.cs`) | — (follows from AL-1 … AL-3 on real code) |
| AL-10 | Large roots (from moving all Tatalu contexts) | topic parts `X.Aggregate.<Topic>.cs` and `X.Values.<Topic>.cs`, the topic after the role, keeping the relative file order of parts with state | `X.<Topic>.Aggregate.cs` (changed the generated state writer of `AttendanceMonth`) |
| AL-11 | Existing sub-namespaces | stay; each file keeps the namespace of its declarations, folders follow the aggregates | collapsing them (an API change for every importer) |
| AL-12 | What `Contracts/` holds | what exists *for* other contexts: published topics and messages, ports others implement or call, helpers no aggregate owns that others call; an aggregate and its values stay in their folder | every type another context reads (would move most aggregates) |
| AL-13 | Rows and features without an aggregate of their own | `Projections/` for rows built only from other contexts' events; a feature with neither aggregate nor changes keeps its own folder (Tatalu's `Konto/`); context-wide rules and helpers in `<Context>.Rules.cs`/`<Context>.Support.cs` | `Shared/` (that is for values) |

AL-9 is the author's refinement on Tatalu's code under AL-1 … AL-3; AL-10 … AL-13 came from moving the other nine contexts
after the owner asked for all of them. All five are listed for review.

## 2. Verification

| Claim | How | Result |
|---|---|---|
| Folders and file names change no generated code | Tatalu (Tarol 0.1.0-preview.5 packed from `9315626`, because this environment cannot reach the GitHub Packages feed): `dotnet build -p:EmitCompilerGeneratedFiles=true` before and after the move of `Tatalu.Dokumentation`, `diff -r` of every project's `obj/Debug/net11.0/generated` | **[verified]** 2,394 files in 26 projects, identical |
| Event aliases and stream types unchanged | the alias and stream-type lines of the generated event catalogue of `Tatalu.Dokumentation` | **[verified]** 85 lines identical |
| OpenAPI document byte-identical | Tatalu's `OpenApiSnapshotTests` rewritten with `TATALU_ACCEPT_OPENAPI=1`, `git diff --exit-code` on `openapi.json` | **[verified]** no diff |
| No type or member renamed | System.Reflection.Metadata list of every type, method, field and property of `Tatalu.Dokumentation.dll` | **[verified]** 13,894 entries identical |
| All ten Tatalu contexts | the same checks after each context; types and members by decoded signatures without compiler-generated names | **[verified]** generated files identical after every context, 908 files, 750 of 750 tests each time (two of about twenty full runs had sporadic failures, three tests in all, each with the real clock and under the suite's parallel load; none reproduced alone, on `main` or in the next full run) |
| File order and state order | `AttendanceMonth` with `.Corrections.Aggregate.cs` vs `.Aggregate.Corrections.cs`; `Discovery.cs` (`GetMembers`), `Emitter.State.cs` (`StateShape` sorted by name) | **[verified]** the first reordered the generated writer, schema unchanged; the second identical |
| Behaviour unchanged | `dotnet test --solution backend/Tatalu.slnx` (Docker running) before and after | **[verified]** 750 of 750 both times, 25 integration tests included |
| The skill's example compiles; entity aliases | a probe project with the skill's `Gallery/MediaItems` folder against the packages of `0bf961d` (`dotnet pack -p:MinVerVersionOverride=0.1.0-preview.9-dev`) | **[verified]** 0 warnings; `media_item.uploaded`, `media_item.media_comment.added`, `media_item.media_comment.removed`, stream type `media_item` |
| Entity nested in the root or top-level: same alias | the probe with `MediaComment` nested in `MediaItem` | **[verified]** same three aliases |
| An event moved out of its entity changes its alias; a root event moved to the namespace does not | the probe with `Added` moved into the root and `Uploaded` declared at namespace level (`: AggregateRoot<MediaItem>.Event`) | **[verified]** `media_item.added` (changed), `media_item.uploaded` (unchanged) |
| Reaction and job names are the type name; projection names the attribute string; schema names the type name | `ReactionDiscovery.cs`, `JobDiscovery.cs`, `Attributes.cs`, `OpenApiSchemaNames.cs` | **[verified by reading]** |

The task as given said "moving an event out of its aggregate or entity changes the stored name". The probe refines this:
the alias follows the chain of types around the event without the root, so a root event at namespace level keeps its
name, an entity event moved anywhere does not. The convention states the precise rule and still keeps events nested.

## 3. Skill evaluation checklist (`tarol-application-layout`)

Run with a frontier model, with and without the skill, on an application of two contexts (conventions §6).

| Task | Expected | Mistakes to avoid |
|---|---|---|
| "Add a change that archives a media item, with an HTTP entry." | `MediaItems/ArchiveMediaItem.Change.cs`, one record, `[Endpoint]`, `IChange<MediaItem>`, the JSON entry in `MediaItems/MediaItem.Json.cs`; namespace of the context | a second change in the same file; a new namespace `…MediaItems`; the entry in another project |
| "Add an event `Archived` and its handler." | nested in `MediaItem`, in `MediaItem.Events.cs` | a top-level event; an event inside a helper class (alias changes) |
| "Where is the reaction that writes a photo into the daily log?" | `DailyLogs/NotePhotoInLog.Reaction.cs` (folder of the changed aggregate) | searching `MediaItems/` only; grepping `.Change.cs` only |
| "Add a child entity `MediaComment`." | `MediaItems/MediaComment.Entity.cs`, `Entity<MediaItem, MediaComment>`, events nested in it, `EntitySet<MediaComment>` on the root | events of the entity in `MediaItem.Events.cs` (alias `media_item.added`) |
| "Rename `RemindDose` to `SendDoseReminder` to fit the files." | refuses, or keeps the old name with `[OnEvent<…>(Name = "RemindDose")]` and asks the owner | a plain rename: waiting outbox rows dead-letter |
| "Split the 800-line `ChildDayCare` root by topic; add the opening hours part." | `ChildDayCares/ChildDayCare.Aggregate.Hours.cs` (topic after the role), its values in `ChildDayCare.Values.Hours.cs`, generated code checked | `ChildDayCare.Hours.Aggregate.cs` (another file order, another generated state writer); a nested helper class around events |
| "Move `Tatalu.Kinder` to the layout." | baseline of generated files, OpenAPI and client; `git mv` commit; split commit; diff proof | rewriting code while moving it; renaming types; moving code into another project |

## 4. Not in this PR

* The layout analyzer (AL-7): new API, waits for the owner.
* The moves in applications: Tatalu does them one context per PR (pilot `Tatalu.Dokumentation`).
* A `dotnet new` template with the layout (plan step 20).
