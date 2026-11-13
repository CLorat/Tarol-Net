# Identity and context

Concept note for step 4 (see [kernel.md](../migration/kernel.md)): who is calling (actor, tenant, correlation), how it
flows (DI scope, `AsyncLocal`, message headers across sagas and messaging), and how a test sets it.
Labels and probes as in [time.md](time.md). Authorization is in [security.md](security.md), ids for tracing in
[tracing.md](tracing.md).

## 1. What a user of Tarol needs it for

* **Audit**: every event records who caused it (actor), for which tenant, and in which conversation (correlation).
* **Authorization inputs**: the subject and tenant of the operation ([security.md](security.md)).
* **Tenant isolation**: the store and queries filter by the operation's tenant.
* **Propagation**: a saga step, a timeout or a message handled hours later in another process still knows the
  original actor, tenant and correlation.

## 2. Where it touches the framework

| Place | Reads | Writes / carries |
|---|---|---|
| Web endpoint (later web package) | `ClaimsPrincipal`, route/host for the tenant, `traceparent` | creates the operation context at the edge |
| Command handler (step 10) | the context, for authorization and to hand it to the unit of work | — |
| Aggregate | **nothing** (an aggregate does not know who calls it; permissions are not invariants, [security.md](security.md) §4) | — |
| Unit of work / store (step 8) | actor, tenant, correlation, causation at commit | event metadata columns; tenant in the stream key |
| Saga engine (step 9) | the context of the triggering message | the context into outbox rows and the messages it sends; a **system actor on behalf of** the original actor |
| Messaging (step 11) | envelope headers | envelope headers (actor, tenant, correlation, causation, traceparent) |
| Tests | — | construct the context directly |

## 3. What .NET 11 / ASP.NET Core already provide

* **`System.Security.Claims.ClaimsPrincipal`** is BCL (no ASP.NET Core dependency): authenticated identity, claims,
  roles. ASP.NET Core binds it into Minimal API handlers **[verified]** (`web/`).
* **`IHttpContextAccessor`** gives the current `HttpContext` through an `AsyncLocal`; ASP.NET Core docs discourage it
  for library code **[unverified here]**. It only exists in web requests.
* **DI scopes**: one scope per request (web) or per message/job (created by the worker).
* **Nothing** for tenant, actor kind (user vs service vs system), or a context that crosses a message boundary.

## 4. What Tarol must add on top

1. **An immutable value captured once per operation**, not a live accessor. Proposed shape (names in kernel.md):

   ```csharp
   public sealed record OperationContext(
       Actor Actor,                 // who: kind + subject id (+ on behalf of)
       Option<TenantId> Tenant,     // None = no tenant (single-tenant app, or a host-level operation)
       Guid CorrelationId,          // the conversation (tracing.md §4)
       Option<Guid> CausationId,    // the command/message that caused this operation; None at a user edge
       Option<string> TraceParent); // optional link to the trace (tracing.md §4)
   ```

   `Actor` is `(ActorKind Kind, string Id, Option<string> OnBehalfOf)` with kinds `Anonymous`, `User`, `Service`,
   `System`. The id is the **identity provider's subject** (`sub`), a string: providers issue non-GUID subjects, and
   `Id<T>` is a `Guid` **[verified by reading `Id.cs`]**.
2. **Provision through DI, explicit at the edges**: the edge (web filter, message consumer, saga engine, test) builds the
   value; handlers receive it (scoped service or parameter); the unit of work receives it at commit. No `AsyncLocal`:
   the same reasons as in [time.md](time.md) §5 (hosted workers capture context at start; async-setup traps in tests)
   apply, and an ambient *identity* that leaks is a security bug, not a flaky test.
3. **A wire format** for headers and metadata: JSON via the source generator; `Option` fields are `null` when absent
   **[verified]** (`authz/`: `{"actor":{"kind":"System","id":"Sagas.AuctionTimeout","onBehalfOf":"alice"},"tenant":…}`).
4. **No roles in the context.** Roles and claims are authorization input and stay in the `ClaimsPrincipal`; the context
   is what gets *recorded*, and roles at the time of the action are not audit data Tarol needs.

## 5. Anonymous, internal and absent callers

| Caller | Actor | Tenant |
|---|---|---|
| unauthenticated HTTP request | `Actor.Anonymous` | from host/route if the app is multi-tenant, else `None` |
| user | `Actor.User(sub)` | from a claim, validated against the route; `None` in a single-tenant app |
| another service (client credentials) | `Actor.Service(clientId)` | from the token or `None` |
| saga, timeout, outbox, background job | `Actor.System(component, onBehalfOf: original user)` | inherited from the triggering message |
| migration, admin script | `Actor.System("migration")` | explicit per run |

The old `ContextUserType { Anonymous, User, Internal }` splits "internal" into `Service` (authenticated, external
process) and `System` (Tarol itself acting later), because audit and policy treat them differently.

## 6. AOT and testability

* All types are records of strings, `Guid`s and `Option`s: JSON through a `JsonSerializerContext`, 0 IL warnings
  **[verified]** (`authz/`, Native AOT).
* A test writes `new OperationContext(Actor.User("alice"), new TenantId("t1"), Guid.CreateVersion7(), Option.None, Option.None)`
  (or a test helper) and passes it. Nothing global, so parallel tests cannot see each other's caller.

## New insights

1. **The actor is recorded data, so it must be minimal and stable**: kind + subject id (+ on-behalf-of). No names, no
   e-mail, no roles in events: events are immutable, so personal data in them cannot be erased later (GDPR); a subject
   id can be resolved (or forgotten) outside the event store.
2. **Phantom markers (`IUser`, `ITenant`, `ITrace`) type the wrong thing**: the user id comes from the identity provider
   (a string, not a `Guid`), the app's own `User` aggregate already has `Id<User>`, and the trace id is
   `ActivityTraceId`. `Id<IUser>` would be a third, incompatible id for the same person.
3. **Capture, don't look up**: a context captured once at the edge and passed on survives `await`, workers and
   process boundaries by construction; an accessor that reads "the current request" is only correct while that request
   is alive.
4. **A saga acts as `System` *on behalf of* a user**: both facts matter (audit wants the human; permissions were checked
   when the user's command started the conversation, [security.md](security.md) §4).
5. **`Option<TenantId>` makes "no tenant" explicit** (`None` = single-tenant or host-level) and separates it from "tenant
   required but missing", which is an authentication error at the edge, not a value.

## Open questions

* `TenantId` over `string` (any key: slug, GUID text) or over `Guid`? Recommendation: `string`, because tenants arrive as
  claim values, subdomains and route segments; see kernel.md decision 3.
* Should `OperationContext` be a parameter of handlers (explicit) or a scoped service (`IOperationContext`)? Step 10
  decides with the pipeline; the Kernel type works for both.
* Accept a client-provided correlation id (e.g. `X-Correlation-Id`)? Off by default (tracing.md insight 2).
