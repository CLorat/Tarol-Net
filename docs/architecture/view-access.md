# View access: context-aware views and read rights per view

Design note (Tatalu round 2, not built). A view knows only the aggregate, and every view and list of an aggregate is authorized by
the same `Read<T>`; Tatalu's most frequent workaround is an extra aggregate or endpoint policy per visibility level. Proposed: a clock
and the actor for views, a read right per view and list, a diagnostic for missing grants, an observer for denied and sensitive reads.
Decision ids `VA-*` (proposed). Labels as in the [conventions](conventions.md).

## 1. The need (Tatalu)

| Finding | What hurts | Workaround in Tatalu (`origin/integration/welle-2`) |
|---|---|---|
| FB-F01.1-6, FB-F02.1-2 | One `Read<T>` for every view: "opening hours for parents" and "tax and bank details for the childminder only" cannot be told apart; three visibility levels of a child (emergency / profile / confidential, NFR-DS-06) need three rights | an extra aggregate `Caregiver` beside `ChildDayCare` (`backend/src/Tatalu.Einrichtung/Caregiver.cs`), merged again in the UI; endpoint policies per level `KinderPolicies.ChildProfile`/`.Confidential`/`.RestrictionBasis` (`backend/src/Tatalu.Kinder/KinderSecurity.cs`), so the role comes from the token, not from the role map |
| FB-F04.8-2 | A parents' list needs `Read<AttendanceMonth>`, which also opens the month view; only a row rule closes it again | `ReadAttendanceRule` in `Tatalu.Anwesenheit/AnwesenheitSecurity.cs` |
| FB-F02.2-1, FB-F17.2-2, FB-F04.2-1 | `From(T)` has no clock: "restriction valid until 31.12.", measles status today, trial expired, "may pick up today" are computed again in TypeScript | `activeOn` in `screens/kinder/contact.ts`, `checkPickup` in `screens/anwesenheit/handover.ts`, `readOnly()` in `subscription.ts` |
| FB-F15.2-2 | `From(T)` does not know the reader: "mark my own row", "hide the other parent's status" | `FamilySection.svelte` marks the own row; contact data closed by a row rule over `IParentDirectory` |
| FB-F17.4-6 | A caller's own list (`[FromClaim("sub")]`) still needs `Read<T>`; without it a bare 403 | `PlattformSecurity.Map` grants `Read<Notification>`, `Read<Recipient>` to every role |
| FB-F13.3-4, FB-F02.2-4 | Denials (endpoint policy in `UseAuthorization`, row rule in `ChangeHandler.Deny`) and sensitive reads are not observable | middleware `backend/src/Tatalu.Host/AccessLog.cs` (path patterns `SensitiveReads`, every 403 and every 200 on listed paths); requirement `ProtocolledRead` with `ProtocolledReadHandler` (`KinderSecurity.cs`) |

## 2. What Tarol has today

* `IView<TAggregate, TSelf>` with `static abstract TSelf From(TAggregate aggregate)` (`src/Changes/Tarol.Changes.AspNetCore/Attributes.cs:66-73`);
  the generated GET calls `web.Load<T>` / `web.LoadTenant<T>` and then `From(aggregate)`
  (`src/Changes/Tarol.Changes.AspNetCore.Generators/Emitter.cs:144-158`).
* `ChangeEndpoints.Load<T>` authorizes `Read<T>.Instance` against the loaded aggregate, then sets `ETag = "<version>"` and answers 304
  (`src/Changes/Tarol.Changes.AspNetCore/ChangeEndpoints.cs:463-494`). Lists: `ListAccess<T>` → `IChangeAuthorization.CheckList<T>`
  without a resource (`QueryEndpoints.cs:94-115`); TN-6 `IListScope<TQuery>` narrows rows (`ListScopes.cs`).
* `Read<TAggregate>` is a sealed singleton intent (`src/Security/Tarol.Security/ChangeRequirement.cs:32`); `ChangeRoles.Grants` matches
  the intent's exact `Type` (`ChangeRoles.cs:35-48`); row rules `ChangeHandler<TChange, TAggregate>` match `requirement.Change is TChange`
  (`ChangeHandler.cs:14-35`).
* Denials: log 1001/1002, counter `tarol.authorization.denied` (`ChangeAuthorization.cs:53-101`); endpoint policy failures never reach
  Tarol. R-8 (rest.md §3) says a caller-specific view is a second view with its own grant, but no such grant exists: it is still `Read<T>`.

## 3. Options

**Read right per view**

| | Option | For | Against |
|---|---|---|---|
| A | `Read<TView>` replaces `Read<T>` as the intent of a view | one type per right, simple map | every existing row rule on `Read<T>` stops applying to views (a silent widening); lists have no view type |
| B | the intent stays `Read<T>`, the requirement gains the view (`ChangeRequirement.View`); a view may declare that it needs its own grant | aggregate row rules keep running for every view (defense in depth); old maps keep working; lists use their `IQuery` record as the "view" | two notions (aggregate grant, view grant) to explain |
| C | an endpoint `Policy` per view (today's workaround) | exists | the role map and row rules cannot see it; the decision lives in the token, not in the map |

**Context for views**

| | Option | For | Against |
|---|---|---|---|
| D | `static abstract TSelf From(T, ViewContext)` replaces `From(T)` | one shape | breaks every view |
| E | the generator accepts either `From(T)` or `From(T, ViewContext)`; `IView<,>` keeps its type parameters, the static member is checked by the generator | no break for existing views; no reflection | the interface no longer states the member (a diagnostic does) |

## 4. Recommendation

**VA-1: Option E, a `ViewContext` with the clock and the actor, not the roles.**

```csharp
public readonly record struct ViewContext(OperationContext Operation, DateTimeOffset Now);   // Tarol.Changes.AspNetCore

[Endpoint("GET /kinder/{child}/karte")]
public sealed record ChildCard(Guid Id, string Name, bool PickupForbiddenToday, bool MeaslesOk) : IView<Child, ChildCard>
{
    public static ChildCard From(Child c, ViewContext v)
        => new(c.Id.Value, c.CallName, c.PickupForbidden(Days.Of(v.Now)), c.MeaslesAllowsCare(Days.Of(v.Now)));
}
```

* The actor (`v.Operation.Actor.Id`) is enough to mark the caller's own row (FB-F15.2-2). Roles stay out: content that depends on a role
  is a second view with its own grant (R-8 kept, now enforceable through VA-2).
* **ETag**: a view that takes `ViewContext` can change without a new version. Its ETag becomes `"<version>-<hash>"`, the first 8 bytes of
  SHA-256 over the serialized view (the bytes are produced anyway); views with `From(T)` keep `"<version>"`. `Vary: Authorization` is
  already set (`CacheHeaders.Apply`).
* Diagnostic (AspNetCore generator): a view with neither or both `From` shapes.

**VA-2: Option B, the view in the requirement and a grant per view or per list.**

```csharp
[Endpoint("GET /kinder/{childHealth}/gesundheit", Restricted = true)]          // needs its own grant
public sealed record HealthView(…) : IView<ChildHealth, HealthView> { … }

roles.Role(Roles.Tpp).May<Read<ChildHealth>>().MayRead<ChildHealth, HealthView>();
roles.Role(Roles.Ak).May<Read<ChildHealth>>();                                   // every view of ChildHealth except HealthView
roles.Role(Roles.El).MayRead<AttendanceMonth, ParentMonths>();                  // only this list (IQuery<…>), not the month view

public sealed class ConfidentialRule : ViewHandler<HealthView, ChildHealth> { … } // row rule for one view
```

* `ChangeRequirement.View` (`Type?`): the view or query record type for reads, `null` for changes. `ChangeRoles.Grants` grants a read when
  the view is not restricted and `Read<T>` is granted, or when `MayRead<T, TView>` is granted. A `Read<T>` row rule still runs for every view
  (a restricted view narrows, it never widens).
* `IChangeAuthorization.Check`/`CheckList` gain an overload with the view type; generated endpoints pass `typeof(TView)`/`typeof(TQuery)`.
* `ViewHandler<TView, TAggregate>` mirrors `ChangeHandler` and matches on `requirement.View == typeof(TView)`.

**VA-3: a missing grant is reported.** At startup `Tarol.Security` walks the generated endpoint metadata (`TarolEndpointMetadata.Kind`,
`Input`) and logs warning 1003 "list MyNotifications needs Read&lt;Notification&gt; (or MayRead&lt;Notification, MyNotifications&gt;); no role
grants it". In Development the 403 problem details carry `requires: "Read<Notification>"`; in Production they do not (no hints to probes).

**VA-4: an access observer.**

```csharp
public interface IAccessObserver                                                 // Tarol.Security
{
    ValueTask Observed(AccessRecord access, CancellationToken cancellationToken);
}
public sealed record AccessRecord(Actor Actor, Option<Id<ITenant>> Tenant, string Intent, string Aggregate, Guid? Id,
    string? View, AccessDecision Decision, string? Code, DateTimeOffset At);
public enum AccessDecision { Allowed, Denied }

[Endpoint("GET /kinder/{child}/einschraenkungen", Restricted = true, Sensitive = true)]
```

* Called by `ChangeAuthorization` for every denial (changes, views, lists), by an `IAuthorizationMiddlewareResultHandler` decorator for
  endpoint policy failures on Tarol endpoints, by the hub for `ITopicAuthorizer` refusals, and after a successful 200 of a `Sensitive`
  view. No personal data: ids, codes, types. Observers run after the decision; an observer failure is logged, never changes the answer.
* Durable storage is not Tarol.Security's: `Tarol.Audit` ([audit.md](audit.md)) ships an observer that writes audit rows.

## 5. Not part of this

* Field filtering inside one view by role (VA-1 keeps R-8). Sharing C# rules with TypeScript (FB-F04.2-1, second half).
* Row filters for lists: TN-6 `IListScope<TQuery>` exists; VA-2 only adds the right to call the list.
* Read audit as events in the event store (DP-8 stays: the observer is the hook, storage is the app's or Tarol.Audit's).
* Services in `ViewContext` (a view stays a pure function; data from another aggregate is a projection).

## 6. Open questions for the owner

1. VA-1: actor only, or also the principal (roles, claims) in `ViewContext`? The note recommends actor only.
2. VA-2: is a restricted view opt-in (`Restricted = true`) or should every view need its own grant (fail closed, more map lines)?
3. VA-2: name of the grant: `MayRead<T, TView>()` or `May<Read<T, TView>>()` (a second intent type)?
4. VA-1: ETag by hash for context views, or no 304 at all for them?
5. VA-4: does a 304 on a `Sensitive` view count as a read (the client shows cached data it already had)?
6. VA-3: warning at startup, or an error that stops the host when an endpoint is granted to no role?

## 7. Steps (one PR each)

1. **VA-A** `ViewContext`, both `From` shapes, the ETag rule, generator diagnostic; tests: clock-dependent view, 304 after the day changes.
2. **VA-B** `ChangeRequirement.View`, `MayRead<T, TView>`, `Restricted`, `ViewHandler<,>`, view and query types passed by the generated
   endpoints; tests: restricted view refused with `Read<T>` only, aggregate row rule still applies, parents' list without the month view.
3. **VA-C** startup diagnostic 1003 and the Development `requires` member.
4. **VA-D** `IAccessObserver`, `Sensitive`, policy-failure decorator, hub refusals; skill `tarol-security` updated; migration note lists the
   public API changes (new overloads on `IChangeAuthorization`, metadata members).
