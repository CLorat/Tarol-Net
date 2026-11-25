# Audit: one row per event with before and after

Design note (Tatalu round 2, not built) for a package family `Tarol.Audit`: a change log that answers "who changed which field of which
object when, from what to what", erasure-proof, with a list endpoint, and complete by construction (no event can be forgotten).
Decision ids `AU-*` (proposed). Labels as in the [conventions](conventions.md).

## 1. The need (Tatalu)

* **FB-F13.3-3**: projections keep one row **per stream**; an audit needs one row **per event** with before/after. Reactions see the event,
  not the previous state, and each context assembly needs its own reaction because the factory must name the event types.
* **FB-F13.3-7**: a reaction subscribes to single event types (`[OnEvent<T>]`); the children's audit needs about 40 factories, and nothing
  reports a forgotten event. Entity events carry the entity only in `RecordedEvent.Event.EntityId`. Tests of a context with another
  context's audit reaction need a hand-chained resolver.
* Related: FB-F13.3-2 (two `[PersonalData]` members with one subject fail to seal: before/after packed into one value `AuditValues`),
  FB-F13.3-5 (the row's `Values` and `Role` as strings because of codegen), FB-F13.3-4 (access log, see [view-access.md](view-access.md) VA-4).

The chain to replace (`origin/integration/welle-2`, `backend/src`):

| Step | Code | What it does |
|---|---|---|
| 1 | `Tatalu.Kinder/Audit.cs` (`AuditKinder`), `Tatalu.Einrichtung/Audit.cs`, `Tatalu.Anwesenheit/Audit.cs`, `Tatalu.Vertraege/Audit.cs`, `AuditMembership` in `Tatalu.Datenschutz/Audit/Changes.cs` | one `[OnEvent<E>]` factory per event, mapping members to `AuditField(name, text)` by hand (`AuditChange.Of`, `.OfEntity`, `.Text`) |
| 2 | `Tatalu.Datenschutz/Audit/AuditTrail.cs` | aggregate per object (id = SHA-256 of the object id), `[PersonalData] Values` = last value per field; `Track` raises `Tracked` per changed field |
| 3 | `WriteAuditEntry` (`Audit/Changes.cs`) | reaction on `Tracked` → creation of `AuditEntry` |
| 4 | `AuditRow` projection `audit-log` + `GET /protokoll` (`AuditSearch`) | the list; before/after as sealed JSON text |
| + | `RecordAccess` from `Tatalu.Host/AccessLog.cs` | reads and denials into the same entries |

Per field change: one reaction run, two commits (trail, entry), one projection row, three outbox rows.

## 2. What Tarol has today

* `RecordedEvent(StreamId, Event, EventId, RecordedAt, Context)` with the actor (`Context.Actor`, `OnBehalfOf`) and `Event.EntityId`
  (`src/Primitives/Tarol.Primitives/RecordedEvent.cs:14`).
* `IEventFeed.ReadAll(after, limit)` → `PositionedEvent(Position, StreamType, Event)` (`src/Persistence/Tarol.Persistence/Feed/IEventFeed.cs`).
* `ISqlProjection` + `SqlProjectionHost`: own tables, per-batch transaction together with the checkpoint (exactly once), filtered by
  stream types, versioned rebuild (`src/Queries/Tarol.Queries/Hosting/SqlProjection.cs`).
* `EventCatalog` per aggregate with `PersonalShape` per event and for the state (`src/Primitives/Tarol.Primitives/EventCatalog.cs:6-43`);
  the envelope `tdp1.` sealed under the subject's key, a tombstoned key reads as `null` (`Persistence/DataProtection/Envelope.cs`,
  [data-protection.md](data-protection.md) §3–§5); `ErasureParticipant` for extra stores (`Persistence/DataProtection/DataSubjects.cs`).
* `operations_audit` records operator actions only (data-protection.md §1). Read audit is out of scope (DP-8).

## 3. Options

| | Option | For | Against |
|---|---|---|---|
| A | Generalize Tatalu's chain: a generated reaction per aggregate, a trail aggregate, an entry aggregate, a projection | everything is an event | 2 commits and 3 outbox rows per change; the audit grows the event store it audits; slow catch-up |
| B | **An `ISqlProjection` over the feed**: `audit_rows` (one per event and field) and `audit_values` (the last value per tenant, stream, entity, field) written in the batch transaction | one batch, exactly once, no extra events; a new version rebuilds before/after from the feed; uses the existing host | values derive from event members, not from the state; a field must be named on events |
| C | Fold on read: replay the stream up to each event (`Rehydrate`) when listing | no table | a list across subjects replays everything; paging and filters impossible |

## 4. Recommendation (AU-1 … AU-5)

**AU-1: option B, packages `Tarol.Audit` (attributes, generator, port, list endpoint, in-memory store) and `Tarol.Audit.Npgsql` (the SQL
projection).**

```csharp
builder.Services.AddTarol(…).AddAudit(a => a.Add<Child>().Add<Guardian>().Add<Membership>())   // every event of these aggregates
    .AddNpgsqlAudit();
app.MapAudit("/protokoll", policy: Policies.Caregiver);

public sealed record Registered([property: PersonalData, Audited("firstName")] string? FirstName,
                                [property: PersonalData, Audited("lastName")] string? LastName,
                                [property: NotAudited] string Checksum) : Event;
public sealed record BasicDataChanged([property: PersonalData, Audited("firstName")] string? FirstName, …) : Event;
[AuditSubject(nameof(Child))]                                       // the subject of the rows when it is not the stream
public sealed record CheckedIn(Guid Child, …) : Event;
```

* **AU-2: complete by construction.** `Add<T>()` audits every event of `T`; every public member is a field (name = its JSON name unless
  `[Audited("name")]` maps several events onto one field, so before/after spans `Registered` and `BasicDataChanged`). `[NotAudited]` opts a
  member out; `[NotAudited]` on an event opts it out with a reason. A new event is audited without code. The generator (inside
  `Tarol.Audit`) emits per event an `AuditShape` (field names, UTF-8 JSON names, subject member) and an accessor that writes each member as
  JSON through the app's source-generated context: no reflection. Diagnostic: `[Audited]` on a type the generator cannot see as an event.
* **Row:** `(tenant, position, event id, stream type, stream id, entity id, event type, actor kind, actor id, on behalf of, recorded at,
  subject, field, before, after)`. Kind = created (first event of the stream or entity), changed, removed (an event marked
  `[AuditRemoved]`). Entity events use `Event.EntityId` as the object. Same value as before: no row.
* **AU-3: erasure-proof.** A value of a `[PersonalData]` member is stored sealed under its subject's key (the projection seals it again
  through `IKeyStore` and the envelope, whether the feed hands it opened or sealed **[unverified]**), in `before`/`after` and in
  `audit_values`. The list opens it with the subject's key; after `EraseSubject` it reads as `null` without touching
  the table (crypto-shredding, DP-3). `ForgetTenant` deletes the tenant's rows (an `ErasureParticipant`). This avoids FB-F13.3-2 (two
  personal fields with one subject) because nothing is packed.
* **AU-4: the list.** `MapAudit(prefix, policy)`: `GET {prefix}?subject=&object=&actor=&streamType=&from=&to=&cursor=` (tenant-scoped,
  cursor paging as `ListQuery`, newest first), OpenAPI metadata like the generated lists; `IAuditLog.Query(AuditQuery, OperationContext)`
  for app code. Optional `GET {prefix}/objects/{id}` for one object's history.
* **AU-5: reads and denials.** `Tarol.Audit` implements `IAccessObserver` (view-access.md VA-4) and writes `read`/`denied` rows into the same
  table directly (no event, DP-8 kept), so one list shows changes, sensitive reads and denials.

## 5. Not part of this

* A general "subscribe to all events of an aggregate" for reactions (`[OnEvents(typeof(Child))]`): with AU-2 the audit no longer needs it;
  it stays a sagas topic if another use appears.
* `ChangeTest.At(now, params JsonSerializerContext[])` (FB-F13.3-7, last part): a Changes testing item.
* The fix of FB-F13.3-2 itself (`PersonalJson.SubjectValues`): a Persistence bug fix, independent of this note.
* Field values computed from the state (derived fields); capture time of offline entries (Tatalu's `Capture.At`): see question 3.
* Audit of the actor's role (roles are not in the event metadata, [change-reads.md](change-reads.md) CR-2).

## 6. Open questions for the owner

1. AU-1: two packages (`Tarol.Audit`, `Tarol.Audit.Npgsql`) or the SQL part inside `Tarol.Queries.Npgsql`?
2. AU-2: audit every member by default (recommended: nothing forgotten) or only members marked `[Audited]`?
3. Time of a row: `RecordedAt` only, or an optional event member (`[AuditTime]`) for the time on the device?
4. Retention: do audit rows follow the stream's `[Retention]` (deleted with the stream) or have their own (`Tarol:Audit:RetentionDays`)?
5. AU-5: should denials of anonymous callers be recorded (volume, probes), or only authenticated ones as Tatalu does?

## 7. Steps (one PR each)

1. **AU-A** attributes, generator (`AuditShape`), `AddAudit`, in-memory store; generator snapshot tests; field mapping across events.
2. **AU-B** `Tarol.Audit.Npgsql`: tables, the `ISqlProjection`, sealed values, `ForgetTenant`; integration tests: before/after, entity
   events, erasure reads `null`, rebuild with a new version gives the same rows.
3. **AU-C** `IAuditLog`, `MapAudit`, OpenAPI, skill `tarol-audit`, AOT smoke app.
4. **AU-D** `IAccessObserver` implementation (after view-access VA-D).
