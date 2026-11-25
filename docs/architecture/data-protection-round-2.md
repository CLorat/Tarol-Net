# Data protection, round 2: categories, tenant export, erasure journal, Art. 15

Design note (Tatalu round 2, not built). Extends [data-protection.md](data-protection.md) (DP-1 … DP-10, built in step 16). Decision ids
`DP2-*` (proposed). Labels as in the [conventions](conventions.md).

## 1. The need (Tatalu)

| Finding | What hurts | Workaround (`origin/integration/welle-2`) |
|---|---|---|
| FB-F02.5-1 | One key per subject: a child's master data, health (Art. 9) and habits can only be erased together; F02.9-R5 needs erasure by category (health at the end of care, master data longer because of the contract) | one stream type per category (`ChildHealth`, `ChildHabits`: `backend/src/Tatalu.Kinder/ChildHealth.cs:77`, `ChildHabits.cs:36`) with retention per type (`Tarol:Retention:<type>:Days`); `EraseSubject(child)` still erases all |
| FB-F17.5-1 | `Export` is per subject; Art. 20 for the childminder needs the whole tenant; `EraseTenant` exists, `ExportTenant` does not | `TenantSubjects` enumerates subjects from read models and `BuildDataExport` stitches the exports (`backend/src/Tatalu.Host/AccountEffects.cs:19-100`) |
| FB-F17.5-2 | Erasures are tombstones in the same database: restoring an older backup brings erased keys back; no journal outside the database to replay | manual runbook `docs/implementierung/betrieb-backup.md` |
| FB-F13.6-2 | `Export` has no Art. 15 frame (purposes, recipients, retention) and no way to withhold parts | `CompileRequestEffect` with hand-written sections (`backend/src/Tatalu.Host/RequestEffects.cs:18-55`) |
| FB-F04.2-3 | Personal fields whose subject is not the stream (stream = month, subject = child) need `Subject = nameof(…)` on every event and on the state; with FB-F13.3-2 only one such field per type | `AttendanceMonth.Notes`, `CheckedIn.Note`, `CheckedOut.Note` (`Tatalu.Anwesenheit/AttendanceMonth.cs:123`) |

## 2. What Tarol has today

* `[PersonalData(Subject = …)]` (`src/Primitives/Tarol.Primitives/PersonalDataAttribute.cs`), `[Retention(Days)]`
  (`RetentionAttribute.cs`), generated `PersonalShape` per event and state in `EventCatalog` (`EventCatalog.cs:6-43`).
* Keys: one data key per (tenant, subject, version) under the tenant key (`subject_keys`, `tenant_keys`, `NpgsqlSchema.cs:183-191`);
  `IKeyStore.Keys/EraseSubject/EraseTenant` (`src/Persistence/Tarol.Persistence/DataProtection/IKeyStore.cs`); the envelope head is
  `subject 16 | key version 4 | nonce | tag` (`Envelope.cs:9-27`), so opening needs no subject member.
* `IDataSubjects.EraseSubject(tenant, subject, actor)`, `EraseTenant`, `Export(tenant, subject, actor)` → one JSON document
  (`Persistence/DataProtection/DataSubjects.cs`, implementation `src/DataProtection/Tarol.DataProtection/DataSubjects.cs:44-121`);
  `RetentionPolicy` per stream type; the subject's streams found through `events.subjects` (GIN).
* Known bug FB-F13.3-2: `PersonalJson.SubjectValues` reads the value after the first match, so a second field with the same subject fails
  (`Persistence/DataProtection/PersonalJson.cs:56` ff.). A prerequisite for DP2-5.

## 3. Options and recommendation

### 3.1 Keys per category (DP2-1)

| | Option | For | Against |
|---|---|---|---|
| A | A `category` column in `subject_keys`, a new envelope `tdp2.` with a category byte | explicit | schema and format change; every reader of the envelope changes |
| B | **A derived subject per category**: `[PersonalData(Category = "health")]` seals under the subject `Derive(subject, "health")` (UUIDv8, as [change-reads.md](change-reads.md) CR-4) | no store, envelope or index change; erasure and export by category are erasure and export of the derived subject | the mapping must be known at erasure and export (it is: the categories are in the generated shapes) |
| C | One stream type per category (Tatalu today) | works now | forces the model to follow the erasure rules; one aggregate per category |

**Recommend B.**

```csharp
public sealed record AllergyAdded(Guid Child, [property: PersonalData(Subject = nameof(Child), Category = "health")] string? Name) : Event;

await subjects.EraseSubject(tenant, child, actor, category: "health");     // only health
await subjects.EraseSubject(tenant, child, actor);                          // the subject and every category
```

* The generator collects the categories into `EventCatalog` (`Categories`); `EraseSubject(…)` without a category erases the base subject and
  every derived one; `Export` merges them, each part labelled with its category. Category names: lowercase, ≤ 32 characters (diagnostic).
* Retention per category is a reaction or job that calls `EraseSubject(…, category)` (e.g. on `Care.Ended` + N days); `[Retention]` stays per
  stream type.

### 3.2 `ExportTenant` (DP2-2)

`IDataSubjects.ExportTenant(tenant, actor, TenantExportOptions, Stream output, ct)` writes, streaming (`Utf8JsonWriter`), every stream of the
tenant grouped by stream type (events in order with type, version, time, actor; the current state), each personal field opened with its
subject's key (erased subjects read `null`), plus a **structure section** generated from the catalogues: stream types, event types and
versions, member JSON names, which members are personal and their category. With `Tarol.Files` ([files.md](files.md) FI-5) the output is a
zip with `export.json` and the files. Audited like `EraseTenant`; admin endpoint `GET {prefix}/tenants/{id}/export` under `MapTarolAdmin`'s
policy. Subjects are not enumerated: streams are the unit, which also covers data without a subject.

### 3.3 Erasure journal and replay (DP2-3)

| | Option | For | Against |
|---|---|---|---|
| A | Runbook only (today) | nothing to build | depends on people after an incident |
| B | **`IErasureJournal` port outside the store's database**, written on every erasure; `ReplayErasures(since)` | a restore cannot silently resurrect a subject | a second place to back up (small, ids only) |
| C | Key store in its own database with short backups (data-protection.md §4) | already possible behind `IKeyStore` | does not help when both are restored together |

**Recommend B**, with file and object-storage implementations:

```csharp
public interface IErasureJournal
{
    ValueTask Append(ErasureEntry entry, CancellationToken ct);                         // before the tombstone; failure = no erasure
    IAsyncEnumerable<ErasureEntry> Since(DateTimeOffset at, CancellationToken ct);
}
public sealed record ErasureEntry(Guid Tenant, Guid? Subject, string? Category, DateTimeOffset At, string Actor);
// IDataSubjects.ReplayErasures(DateTimeOffset since, string actor, CancellationToken ct) → count; idempotent (AlreadyErased)
```

* Ids and the actor pseudonym only (DP-9), so the journal is not personal content. `.AddDataProtection(d => d.Journal.File("/var/lib/app/erasures.log"))`.
* At startup the host compares the newest journal entry with the newest tombstone in the database and logs an error (and a health check
  fails) when the database is behind: the sign of a restore. Replay is explicit (admin endpoint or a CLI command), not automatic.

### 3.4 Art. 15 frame (DP2-4)

```csharp
[PersonalDataPurpose("Care contract and billing", Recipients = ["youth office"], Legal = "Art. 6(1)(b), (c) GDPR")]
public partial class Child : AggregateRoot<Child> { … }

await subjects.Export(tenant, subject, actor, new ExportOptions { Exclude = ["case-note"], Reason = "rights of others (Art. 15(4))" });
```

* Per stream type: purposes and recipients from the attribute (or configuration `Tarol:DataProtection:Purposes:<type>`), retention from
  `RetentionPolicy` (days) in a `frame` section of the document. Texts are the app's; Tarol does not ship legal wording.
* `Exclude` stream types (and categories, DP2-1) with a reason: the document lists what was withheld and why; the audit row records it.

### 3.5 One subject for an aggregate (DP2-5)

`[PersonalSubject(nameof(Child))]` on the aggregate names a `Guid`/`Id<T>` **state** member: every `[PersonalData]` member of its events
and state without its own `Subject` is sealed under that subject at append time (the store has the aggregate; the envelope carries the
subject, so reading needs nothing). Events then need no subject member. A personal field before the member is set is the existing
"subject missing" error. Requires the FB-F13.3-2 fix (several fields, one subject).

## 4. Not part of this

* A KMS-backed key store (DP-1, its own package). Encryption of bus contracts. Read audit ([audit.md](audit.md) AU-5).
* Automatic retention per category without a trigger the app names.
* Legal texts for purposes and retention.

## 5. Open questions for the owner

1. DP2-1: derived subjects (B, recommended) or a category in the key store and envelope (A)?
2. DP2-2: JSON only, or a zip with files once `Tarol.Files` exists? Is `ExportTenant` also offered to the tenant itself (not only admin)?
3. DP2-3: is a failed journal append a failed erasure (recommended) or a warning? Which implementations first (file; S3 with Files)?
4. DP2-4: purposes by attribute or by configuration only?
5. DP2-5: subject from a state member (recommended) or from an event member of the same name on every event?

## 6. Steps (one PR each)

1. **DP2-A** fix FB-F13.3-2 (several personal fields with one subject), tests.
2. **DP2-B** `[PersonalSubject]` (generator, codec).
3. **DP2-C** categories: `Category`, derived subjects, `EraseSubject(…, category)`, export merge.
4. **DP2-D** `IErasureJournal` (file implementation), `ReplayErasures`, startup check, admin endpoint.
5. **DP2-E** `ExportTenant` with the structure section; admin endpoint.
6. **DP2-F** Art. 15 frame and `ExportOptions.Exclude`; skill `tarol-data-protection` updated; migration note lists the new overloads.
