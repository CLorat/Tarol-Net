# Security and authorization

Concept note for step 4 (see [kernel.md](../migration/kernel.md)), designed as a topic of its own. Who is allowed to do
what, where checks run, how a denial is reported, audit, multi-tenancy, testing, and the package boundary.
Labels and probes as in [time.md](time.md); the experiments are in [`probes/kernel/authz`](../migration/probes/kernel/authz)
and [`probes/kernel/web`](../migration/probes/kernel/web).

## 1. What a user of Tarol needs it for

* "Only the seller may cancel an auction; an admin may cancel any auction of their tenant."
* "No user ever sees or changes another tenant's data."
* "Every change records who made it."
* The same rules when a saga, a timeout or a background job acts later, and in tests without a web host.

## 2. The model: subject, action, resource, policy

| Part | In Tarol | Notes |
|---|---|---|
| **Subject** | `ClaimsPrincipal` for the decision (roles, scopes, claims); `Actor` for the record ([identity-context.md](identity-context.md)) | user, service (client credentials), system (saga/job) |
| **Action** | the command type (`CancelAuction`) | one command = one action; no separate permission strings needed |
| **Resource** | the loaded aggregate instance (`Auction`), plus its tenant | resource-based: the rule needs the state (`SellerSubject`) |
| **Policy** | an ASP.NET Core authorization handler / requirement, or a plain Tarol permission returning `Result` (§6) | pure function of subject, command, resource |

## 3. Where checks run

```
request ─► authenticate (who? 401) ─► coarse policy at the endpoint (role/scope, 403)
        ─► handler: load aggregate (tenant-scoped, 404) ─► resource permission (403 / 404)
        ─► aggregate behaviour: invariants (409 / 400) ─► commit: stamp actor, tenant, correlation
```

* **Endpoint** (web package): authentication and coarse policies (`RequireAuthorization("auctions:write")`). ASP.NET
  Core does this already.
* **Command pipeline** (step 10): the resource permission, because it needs the loaded aggregate and must also run for
  commands that do not come from HTTP (messages, sagas, tests). This is the one check Tarol must place.
* **Inside the aggregate: never a permission, only invariants.** "A cancelled auction cannot be cancelled again" is an
  invariant (state); "only the seller may cancel" is a permission (who). Mixing them gives the aggregate a dependency on
  identity and breaks replay (replay has no caller) **[verified]** (`authz/`: permission first, then the invariant as
  `Error.Conflict`).
* **Sagas and jobs**: they act as `Actor.System(component, onBehalfOf)`. The user's permission was checked when the
  user's command started the conversation; later steps are the system keeping its promise. A saga that performs a
  *new* privileged action (e.g. refund above a limit) checks a policy for the system actor explicitly.
* **Replay and projections**: no authorization (they rebuild facts that were already authorized).

## 4. What .NET 11 / ASP.NET Core already provide (do not reinvent)

* `ClaimsPrincipal` (BCL, no ASP.NET Core).
* `IAuthorizationService.AuthorizeAsync(user, resource, requirement | policy)`, `AuthorizationHandler<TRequirement,
  TResource>`, `OperationAuthorizationRequirement`, policies with requirements: **resource-based authorization**.
  It lives in the `Microsoft.AspNetCore.Authorization` package and **works without a web host**
  (`AddAuthorizationCore()` in a console), **under Native AOT with 0 IL warnings** **[verified]** (`authz/`).
* Authentication handlers, `[Authorize]`/`RequireAuthorization`, 401/403 challenge/forbid at the endpoint **[verified]**
  (`web/`: 204 seller, 403 other user, 401 anonymous).

## 5. What Tarol must add on top

1. **Glue, not a policy engine**: run the resource permission in the command pipeline and turn the outcome into a
   `Result` — no exception, no `bool`:

   | Outcome | `Result` | HTTP (via `ToHttpStatusCode`) |
   |---|---|---|
   | not authenticated, policy needs a user | `Error.Unauthorized("…")` | 401 |
   | authenticated, not allowed | `Error.Forbidden("…")` | 403 |
   | resource in another tenant | **`Error.NotFound("…")`** | 404 (do not reveal it exists) |

   **[verified]** (`authz/`, JIT and AOT; `web/` maps them to 204/403/401).
2. **The actor in event metadata**, stamped by the unit of work from the operation context (steps 6, 8):
   `{"actor":{"kind":"System","id":"Sagas.AuctionTimeout","onBehalfOf":"alice"},"tenant":{"value":"t1"},…}`
   **[verified]** (source-generated JSON, AOT).
3. **Tenant isolation in the store**, not in handlers: the tenant is part of the stream key and of every query filter,
   taken from the operation context, never from the command payload (step 8).
4. **A system principal** for sagas and jobs (a `ClaimsPrincipal` with `ActorKind.System`), so the same policies can be
   evaluated for them when needed (step 9/10).

## 6. Two ways to write a permission (compared in `authz/`)

```csharp
// (a) ASP.NET Core authorization (works without a web host, AOT-safe)
sealed class AuctionCancelHandler : AuthorizationHandler<OperationAuthorizationRequirement, Auction>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value == auction.SellerSubject) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

// (b) a plain Tarol permission: synchronous, returns the Result directly
sealed class CancelAuctionPermission : IPermission<CancelAuction, Auction>
{
    public Result Check(Actor actor, TenantId tenant, CancelAuction command, Auction auction) => actor switch
    {
        _ when auction.Tenant != tenant => Error.NotFound("Auction.NotFound"),
        { Kind: ActorKind.Anonymous } => Error.Unauthorized("Auth.Required"),
        { Kind: ActorKind.User } u when u.Id == auction.SellerSubject => Result.Success(),
        { Kind: ActorKind.System } => Result.Success(),
        _ => Error.Forbidden("Auction.Cancel.NotSeller"),
    };
}
```

(a) reuses everything ASP.NET Core users know (policies, roles, DI-registered handlers) but returns only
succeeded/failed, so the 401/403/404 distinction is made by the glue. (b) is shorter and states the error codes, but is a
second authorization system next to ASP.NET Core's. Recommendation: **(a) as the mechanism**, with the Tarol glue mapping
the outcome; (b) only if the owner prefers explicit error codes per rule (kernel.md decision 4).

## 7. The old `IAuthorizer`

`IAuthorizer.IsAuthorizedAsync(CancellationToken)` (namespace `Security`, finding 26) has no subject, no resource and no
action, and no consumers. It cannot express any rule above. **Drop it**; nothing replaces it 1:1. The namespace finding is
fixed by construction (every new type is in `Tarol.*`).

## 8. Package boundary and steps

| Part | Package | Step |
|---|---|---|
| `Actor`, `ActorKind`, `TenantId`, `OperationContext` (the recorded data) | `Tarol.Kernel` | 4 (now) |
| actor/tenant/correlation in event metadata, tenant in stream keys and queries | `Tarol.Persistence*` | 6 (event envelope), 8 (store) |
| system actor on behalf of, context in outbox rows and messages | `Tarol.Sagas` | 9 |
| resource permission in the command pipeline, `AuthorizationResult` → `Error.*`, system principal | **`Tarol.Security`** (depends on Kernel, Results, `Microsoft.AspNetCore.Authorization`) | 10, with CQRS (designed here, built with the pipeline it plugs into) |
| endpoint: context from `ClaimsPrincipal`, problem details for `Error.*`, tenant resolution | later web package | after 10 |
| hub/topic authorization with tenant-prefixed groups | `Tarol.Messaging` | 11 |

Why `Tarol.Security` is not built now: its only job is to run in a pipeline that does not exist yet (step 10). Built now,
it would be an unused abstraction, which is what the old `IAuthorizer` was.

## 9. Testing

* A fake subject per test: `Actor.User("alice")` / a `ClaimsPrincipal` built in the test, passed explicitly. No ambient
  user, so parallel tests are independent.
* Permission tests are pure: subject + command + aggregate → `Result` **[verified]** (`authz/` checks 1–6).

## New insights

1. **Cross-tenant access is a 404, not a 403**: a 403 confirms that the resource exists. The tenant check therefore
   belongs to *loading* (the store filters by tenant), and a permission never sees another tenant's aggregate.
2. **Permissions are not invariants**: keeping "who" out of the aggregate keeps replay caller-free and lets the same
   aggregate be driven by users, sagas and tests.
3. **ASP.NET Core authorization is not web-only**: it runs in a console and under AOT, so Tarol needs no policy engine of
   its own; it needs a 30-line mapping to `Result`.
4. **Authorize the intent, not every step**: a saga continuing a user's command acts as `System` on behalf of that user;
   re-checking the user's permission days later (roles may have changed) would break promises already made.
5. **The actor record must not contain personal data** (identity-context.md insight 1): events are immutable.
6. **AOT trap at the web edge** **[verified]**: with `CreateSlimBuilder` and Native AOT, `Results.Problem(...)` throws
   `NotSupportedException` (no `ProblemDetails` metadata) at run time, with **0 IL warnings at publish**;
   `AddProblemDetails()` fixes it. The later web package must register it when it maps `Error` to problem details.

## Decisions taken (owner, 2026-11-13; kernel.md §7)

* **`Tarol.Security` is designed here and built in step 10** with the command pipeline (S-1).
* **Permissions are ASP.NET Core authorization handlers** (`AuthorizationHandler<TRequirement, TAggregate>` evaluated with
  `IAuthorizationService`), mapped by `Tarol.Security` to `Error.Unauthorized` / `Error.Forbidden`; cross-tenant access is
  `Error.NotFound` because the store filters by tenant (decision 4).
* The recorded data (`Actor`, `OperationContext`) ships now in `Tarol.Kernel`; `IAuthorizer` is dropped.

## Open questions

* ~~Style (a) or (b)?~~ Decided: (a).
* Should denials be audited (log/metric), and where? Not as events (nothing changed). Step 10.
* Field-level authorization of query results (e.g. hide `ReservePrice`)? A read-model concern, step 10 or the web package.
