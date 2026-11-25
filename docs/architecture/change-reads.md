# Change reads: further aggregates, load data, load-or-start

Design note (Tatalu round 2, not built). A change decides on its target plus the `[Read]` aggregates named by `Id<X>` members of its
input. Tatalu needed more: the tenant's singleton, an aggregate under the same Guid as the target, an optional read, data from `Load`
in `Execute`, the actor's domain role, a port before `Execute`, and "load or start". Decision ids `CR-*` (proposed). Labels as in the
[conventions](conventions.md).

## 1. The need (Tatalu)

| Finding | What hurts | Workaround (`origin/integration/welle-2`) |
|---|---|---|
| FB-F01.1-2, FB-F13.4-3, FB-F17.7-1, FB-F02.6-1 | `[Read]` needs an `Id<X>` member; the tenant's singleton (day-care facility, consent catalogue, rule profile) has none | hand-written `IHasReads` (marked "do not use"): `CompleteSetup` via `Tenants.CaregiverRead` (`Tatalu.Einrichtung/Changes.cs:106`, `Tenants.cs`), `RecordMeaslesProof` (`Tatalu.Kinder/MeaslesChanges.cs:17`), `RuleProfiles.Read` (`Tatalu.Plattform/Profile/Changes.cs:107`), check in `RecordConsent.Load` |
| FB-F03.4-1 | Decision on `CareFile` (id = child) needs `Child` under the **same** Guid and the facility | `ICareFileDecision : IHasReads` with `CareFacts.Load` (`Tatalu.Vertraege/Facts.cs:101-112`, `Changes.cs:24`) |
| FB-F03.3-2 | `context.Read<T>()` throws when an optional read is missing | `CareFacts.ReadOccupancy` catches the exception (`Facts.cs`) |
| FB-F02.1-7 | `[Read] Id<Child>` on a change of `Child` is TCHG004 even for another stream (the sibling) | `LinkSibling` as `ILoads<Child>` loading both (`Tatalu.Kinder/ChildChanges.cs`) |
| FB-F13.3-6, FB-F15.1-5, FB-F17.4-4, FB-F04.7-1 | `Load` may read more, but only the target reaches `Execute`; the actor's role is domain data (a `Membership`), not a claim | private fields set in `Load`: `_role` via `EntryRoles.Of` in `CheckIn`, `CheckOut`, … (`Tatalu.Anwesenheit/Changes.cs:22-63`), `_facts` in `InviteParent` (`Tatalu.Eltern/InvitationChanges.cs:57`); `UnsubscribeEndedDevice` changes the aggregate inside `Load` (`Tatalu.Plattform/Benachrichtigungen/Changes.cs:133-150`) |
| FB-F17.2-1 | No async port before `Execute` (payment authorization) | event → effect `AuthorizeSubscription` → system change (`Tatalu.Host/AuthorizeSubscription.cs`) and polling in the UI |
| FB-F02.5-2, FB-F15.5-2, FB-F04.1-3, FB-F13.4-2 | "load or start" repeated per change; such a creation answers 204 without id; a stream keyed by business data (child × month) needs a deterministic id; a singleton's view is 404 until its first event | `HealthLoad.OrStart` (`Tatalu.Kinder/HealthChanges.cs:19`), `MonthLoad.OrBegin` and `AttendanceMonth.IdOf` (SHA-256 → UUIDv8, `AttendanceMonth.cs:127`), the same hash in `AuditTrail.IdOf`, `Recipient.IdOf`; reaction `BeginConsentCatalog` so the catalogue view exists |
| FB-F01.9-1 | No local "today" for the tenant's zone | `Days.Today(now)` with a fixed Europe/Berlin |

## 2. What Tarol has today

* `[Read]` = `ReadAttribute` on `Id<X>` members (`src/Changes/Tarol.Changes/Reads.cs:14`); the generator emits `IHasReads.LoadReads`
  (`Reads.cs:63`, `Tarol.Changes.Generators/Emitter.cs:41-59`); a miss is 422 `X.NotFound`. TCHG004 refuses a `[Read]` of the target's
  own type for every non-creation (`Tarol.Changes.Generators/Discovery.cs:83`).
* `ChangeContext(Operation, Now, Reads)` with `Read<T>()` / `Read<T>(member)` that throw (`ChangeContext.cs:15-23`, `Reads.cs:34-48`).
* `ILoads<T>.Load(reader, context, ct)` returns only `Result<T>` (`IChange.cs:51-62`); the pipeline: validation → key → reads → load
  (`ILoads`, `[PerTenant]` by tenant id, or `Target`) → permission → version → `Execute` (`ChangePipeline.cs:158-202`).
* TN-2 `[PerTenant]`: `T.PerTenant`, `TenantAggregates.IdOf<T>` (`TenantAggregates.cs:17-35`); only for the **target** (and the view).
* HTTP: `Write(…, created)` answers 201 with `{id, version}` only for `ICreation` entries, 204 otherwise (`ChangeEndpoints.cs:306-335`).
* `Id<T>.New()` is UUIDv7 (`StronglyTypedId/IdExtensions.cs:51`); no name-based id.

## 3. Options and recommendation per topic

### 3.1 Reads without an input member (CR-1)

Options: (a) `[Read(FromTenant = true)]` on a dummy member — the client would send it; (b) `context.Read<T>()` loading lazily — `Execute`
is synchronous; (c) **a declaration on the change type** that the generator turns into loads. Recommend (c):

```csharp
[Reads<ChildDayCare>]                                   // [PerTenant]: the caller's tenant's one
[Reads<Child>(SameIdAs = nameof(CareFile))]             // the Guid of another member, typed as Id<Child>
[Reads<Occupancy>(Optional = true)]                     // a miss is no 422
public sealed partial record ConfirmContract(Id<CareFile> CareFile, …) : IChange<CareFile> { … }
```

* `[Reads<T>]` without `SameIdAs` requires `T` to be `[PerTenant]` (new diagnostic otherwise); `SameIdAs` names an `Id<_>`/`Guid` member.
* `[Read(Optional = true)]` on members too. `ChangeContext.TryRead<T>(out T? value)` and `TryRead<T>(string member, out T? value)`:
  `false` for a declared optional read that is missing; an undeclared type still throws (a programming error, not data).
* **TCHG004 only for the target** (FB-F02.1-7): a `[Read] Id<Child> Sibling` on a change of `Child` is allowed when another `Id<Child>`
  member is the target; `context.Read<Child>(nameof(Sibling))`.
* `IHasReads` stays generated-only; with CR-1 no Tatalu change implements it by hand.

### 3.2 Data from `Load` to `Execute` (CR-2)

Options: (a) the actor's roles/claims in `OperationContext`; (b) the principal in `ChangeContext`; (c) **`ILoads<T, TData>`**; (d) reads
by lookup. (a) puts authorization data into every event's metadata and is wrong for Tatalu (the role is a `Membership`, domain data);
(b) differs between HTTP and internal runs (no principal, R-6). Recommend (c) plus (d):

```csharp
public sealed partial record CheckIn([Read] Id<Child> Child, …) : ILoads<AttendanceMonth, EntryRole>
{
    public async ValueTask<Result<Loaded<AttendanceMonth, EntryRole>>> Load(IAggregateReader reader, ChangeContext context, CancellationToken ct)
        => new Loaded<AttendanceMonth, EntryRole>(await MonthLoad.OrBegin(…), await EntryRoles.Of(reader, context, ct));
    public Result Execute(AttendanceMonth month, EntryRole role, ChangeContext context) => month.CheckIn(…, role);
}

[ReadsByActor<Membership>("subject", Optional = true)]  // reader.Find<Membership>("subject", actor) then Load
```

* AOT: a generated, editor-hidden bridge (`ILoadsData<T>`) boxes `TData` once and calls the typed `Execute`; the pipeline never sees
  `TData`; no mutable fields on the change record.

### 3.3 A port before `Execute` (CR-3)

`IPrepares<TData>.Prepare(IServiceProvider services, ChangeContext context, CancellationToken ct) → ValueTask<Result<TData>>`, run after
the reads and before the load, outside any transaction, its data passed like CR-2. **Only for reads of ports** (configuration, a tariff
table, a price): a payment authorization is a side effect that may succeed while the commit fails, so it stays an effect plus a system
change (FB-F17.2-1 is answered with a documented pattern, and an effect result the client can wait for is a messaging topic).
Versioned legal parameters (FB-F02.6-1) remain a `[PerTenant]` or global profile aggregate read by CR-1 (see
[tenant-membership.md](tenant-membership.md) §3.4 for global data).

### 3.4 Load or start, 201, empty views, derived ids (CR-4)

```csharp
public partial class ChildHealth : AggregateRoot<ChildHealth>, IStartable<ChildHealth>
{
    public static ChildHealth Start(Id<ChildHealth> id, ChangeContext context) { var h = new ChildHealth(id); h.Raise(new Started()); return h; }
}
public sealed partial record AddAllergy(Id<ChildHealth> ChildHealth, …) : IChange<ChildHealth>, IOrStart;   // missing → Start
public static Id<AttendanceMonth> IdOf(Guid child, int year, int month) => Id<AttendanceMonth>.Derive("attendance-month", child, year, month);
```

* `IStartable<T>` on the aggregate (`static abstract T Start(Id<T>, ChangeContext)`), `IOrStart` marker on the change. The pipeline starts
  the target when the load misses (expected version 0; a concurrent start is `Stream.VersionConflict`, the caller retries). Works for
  `[PerTenant]` targets (id = tenant).
* `ChangeOutcome.Created` (`true` when the commit started the stream); the endpoint answers 201 with `{id, version}` for **any** change
  whose outcome is created (FB-F15.5-2). Behaviour change for `ILoads` entries: listed in the migration note.
* A GET of an `IStartable` aggregate that does not exist answers the view of `T.Start(id, …)` uncommitted, ETag `"0"` (FB-F13.4-2);
  no `IView.Empty` member needed.
* `Id<T>.Derive(string scope, params ReadOnlySpan<IdPart> parts)` in `Tarol.StronglyTypedId`: UUIDv8 over SHA-256 of a canonical,
  length-prefixed encoding (`IdPart` from `Guid`, `string`, `int`, `long`, `DateOnly`). Stable across releases (a test vector).

### 3.5 Local time (CR-5)

`ChangeContext.Today(TimeZoneInfo zone)` / `LocalNow(zone)` as pure helpers; the tenant's zone is data (a `[PerTenant]` settings aggregate
read by CR-1), not framework configuration.

## 4. Not part of this

* Writing two aggregates in one change (a saga); checking versions of `[Read]` aggregates (changes.md §3.1).
* Services injected into `Execute`; side-effecting ports in `Prepare`; `GlobalRef<T>` (TN-3); global data (tenant-membership.md).
* Automatic retry of version conflicts without `If-Match` (FB-F17.3-10, a separate item).

## 5. Open questions for the owner

1. CR-1: attribute on the change (`[Reads<T>]`) as recommended, or `[Read]` on a computed property (`[Read] Id<Child> Child => …`)?
2. CR-2: `ILoads<T, TData>` only, or also `ReadsByActor` (a lookup keyed by the actor) in the first PR?
3. CR-3: build `IPrepares<TData>` at all, or only document "port data comes in the input or from a profile aggregate"?
4. CR-4: 201 for every created stream (also through `ILoads`), or only for `IOrStart`/`ICreation`?
5. CR-4: `Id<T>.Derive` scope: a free string (recommended), or the aggregate's `StreamType` (rename-sensitive, needs Primitives)?
   Tatalu's existing `IdOf` hashes stay valid for their streams; `Derive` is not required to reproduce them.

## 6. Steps (one PR each)

1. **CR-A** TCHG004 only for the target; `Optional`, `TryRead`; tests (sibling, missing occupancy).
2. **CR-B** `[Reads<T>]` (`[PerTenant]`, `SameIdAs`), diagnostics, skill `tarol-changes`.
3. **CR-C** `ILoads<T, TData>` with the generated bridge; `ReadsByActor`.
4. **CR-D** `IStartable<T>`, `IOrStart`, `ChangeOutcome.Created`, 201, empty views of startable aggregates.
5. **CR-E** `Id<T>.Derive` (StronglyTypedId) and `Today`/`LocalNow`.
6. **CR-F** (if decided) `IPrepares<TData>`.
