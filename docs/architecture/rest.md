# REST entries and authorization

Concept note for step 10 (evaluation: [migration/rest.md](../migration/rest.md); probes and output:
[`probes/rest/`](../migration/probes/rest/), [`output.txt`](../migration/probes/rest/output.txt)). **An HTTP endpoint is
an entry of a change** ([changes.md](changes.md) §1, §4): the user writes the change record once and puts
`[Endpoint("POST /auctions/{auction}/close")]` on it; Tarol generates a `RequestDelegate` that builds the context from the
`ClaimsPrincipal`, binds the input from route and body, runs `ChangePipeline` and maps the outcome to HTTP. **Authorization
is ASP.NET Core's**: the app's roles are mapped onto the changes and reads they may run, row rules on the loaded aggregate
grant or deny, and everything else (authentication, schemes, coarse policies) stays the app's. Webhooks are entries too.
This note updates [security.md](security.md) (§5 "what Tarol must add" is now specified here) and answers its open
questions.

Labels as in the [conventions](conventions.md): **[verified]** = reproduced in the probe (JIT and Native AOT, in-memory
and Npgsql store unless a row says otherwise); **[verified by reading]** = read in the named code; **[unverified]** =
reasoned, not run. Owner decisions R-1 … R-17 are in [migration/rest.md](../migration/rest.md) §9.

**Status: designed** (2026-11-15). Built in two PRs (R-15): **10A** core changes + `Tarol.Security`, **10B**
`Tarol.Changes.AspNetCore` (endpoints, reads, OpenAPI, webhooks, sample).

## 1. What a user of Tarol needs

| ArtFlow use case | Entry | HTTP |
|---|---|---|
| a seller creates an auction | `[Endpoint("POST /auctions")]` on `CreateAuction` | 201, `Location`, `{"id","version"}`, `ETag` |
| a bidder bids | `[Endpoint("POST /auctions/{auction}/bids")]` on `PlaceBid` | 204, `ETag`; `If-Match` → 409 when stale |
| the seller (or an admin of the tenant) closes it | `[Endpoint("POST /auctions/{auction}/close")]` on `CloseAuction` | 204; another user 403; another tenant 404 |
| the job settles it | no attribute: `pipeline.Run(new SettleAuction(id), systemContext)` | — |
| a client reads it | `[Endpoint("GET /auctions/{auction}")]` on a view record `IView<Auction, AuctionSummary>` (R-1, R-8) | 200, `ETag`, 304 on `If-None-Match` |
| the payment provider reports a payment | `[Webhook("payments", "payment.succeeded", Version = "2")]` on `RecordPayment.From(PaymentSucceededV2)` | 200 / 202 / 401 / 422 / 503 |

## 2. One request, end to end **[verified]**

```text
request ─► authentication (the app's scheme, R-3)                       401 + WWW-Authenticate
        ─► endpoint policy: [Endpoint(Policy = …)], else the default policy  403, no body  (fail closed, C-11)
        ─► route values → input members (Id<T>, Guid, string)              400 errors.<member>: Format
        ─► body → input: generated member binder (B2, R-7)                  400 Json / Required for every member; 415; 413
        ─► edge context: actor (sub / client_id), tenant (claim = route, R-11), new correlation,
           Idempotency-Key (scope tenant + caller + method + path, fingerprint R-9), If-Match   400 for a malformed header
        ─► ChangePipeline.Run (changes.md §2), with the 10A order:
              Validate 400 → key seen (replay) → [Read] 422 → load 404 (also another tenant)
              → permission 401/403 (R-2: now before If-Match) → If-Match 409 → Execute 409/400 → Prepare → append 409
        ─► outcome → HTTP (changes.md §6, unchanged)
```

The generated code is a `RequestDelegate` per change (C-12) plus one `MapChanges(this IEndpointRouteBuilder, string?
names = null)` per assembly, in a namespace named after the assembly. It reads only what the user wrote
(`[Endpoint]`, `IChange<T>`/`ICreation<T>`, the `AggregateRoot<T>` base, `IView<,>`), never another generator's output.
`MapChanges` accepts a route group (`app.MapGroup("/v1").MapChanges(names: "V1")`): versioning is route groups, and
endpoint names must be unique, so a second mapping needs a name prefix **[verified]** (without one the first request fails
with "Duplicate endpoint name").

**Cost** **[verified]**: the generated endpoint and a hand-written minimal-API endpoint (RDG) for the same change serve the
same load: JIT in-memory 34.2k vs 34.5k req/s (p99 2.5 vs 2.5 ms), Npgsql 2.7k vs 2.8k; Native AOT in-memory 30.1k vs
28.7k, Npgsql 2.7k vs 2.5k (16 clients on the same 4 cores). Startup to the first 200: 34 ms native vs 249 ms JIT
(in-memory), 55 vs 401 ms (Npgsql). Binary 26.4 MB with JwtBearer, OpenAPI and OpenTelemetry, **0 IL warnings**
([migration/rest.md](../migration/rest.md) §4).

## 3. Authorization (R-5 = AZ-D, R-6)

**Model.** The requirement is the intent itself: a `ChangeRequirement(change)` for a change, `Read<T>` for a GET (security.md
§2: action = the change type). The resource is the loaded aggregate; for a creation it is the new one. Two kinds of
handlers decide, with ASP.NET Core's own semantics: **allowed = at least one grant and no deny**.

1. **The app's roles, mapped onto changes and reads** (Tarol.Security). The app names its roles; Tarol checks
   `User.IsInRole(role)` against the map. Nothing granted = denied (fail closed).
2. **Row rules** on the aggregate: `ChangeHandler<TChange, TAggregate>` sees the input and the aggregate; `Deny(context,
   code)` wins over every grant (403 with that code), `context.Succeed(requirement)` is an ownership grant ("the seller may
   read their own auction without the reader role").

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>())
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
        roles.Role("admin").May<CloseAuction>().May<Read<Auction>>();
        roles.Authenticated().May<WatchAuction>();
    });
builder.Services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();

public sealed class CloseAuctionRule : ChangeHandler<CloseAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CloseAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller && !context.User.IsInRole("admin"))
            Deny(context, "Auction.NotSeller");
        return Task.CompletedTask;
    }
}
```

**[verified]**: no role → 403 `Auth.Forbidden`; seller role + another seller's auction → 403 `Auction.NotSeller`; admin of
the tenant → 204; a creation denied on the new aggregate (reserve above a limit without the "dealer" role) → 403
`Auction.ReserveLimit`; seller reads own auction without the reader role → 200, a stranger → 403; an endpoint whose change
no role grants → 403; another tenant → 404 (never loaded, never authorized).

| Outcome | `Result` | HTTP |
|---|---|---|
| not authenticated (endpoint policy) | — (the scheme's challenge) | 401 + `WWW-Authenticate` |
| endpoint policy fails (scope) | — | 403, no body |
| anonymous principal and the change is not granted | `Error.Unauthorized("Auth.Required")` | 401 |
| denied by a row rule | `Error.Forbidden(<the rule's code>)` | 403 problem details |
| nothing granted | `Error.Forbidden("Auth.Forbidden")` | 403 problem details |
| another tenant | `Error.NotFound` (the store filters) | 404 |

**Internal runs (R-6).** The check runs only when `ChangeOptions.Principal` is set. HTTP entries always pass one
(anonymous included), so they fail closed; reactions, jobs, sagas and tests pass none and are not checked (security.md
insight 4: the intent was authorized where it entered). A system action that is a *new* privilege opts in by passing a
system principal. Today the pipeline calls the hook for every run with `principal: null` **[verified by reading
`ChangePipeline.cs`]**; 10A makes "no principal = no check" the pipeline's rule, not the hook's.

**Order (R-2).** Today `If-Match` is compared before the permission, so a forbidden caller with a stale `If-Match` gets
409 `"expected version 1, found 4"` (the current version) **[verified]**. 10A checks the permission first.

**`[Read]` aggregates in the decision.** `IChangeAuthorization.Check(change, aggregate, context, principal, ct)` has no
access to the reads **[verified by reading]**; 10A passes the `ChangeContext` (it holds them), so a rule can look at the
artwork a creation refers to.

**Audit.** Every denial is a warning log (`EventId 1001`, change, aggregate, id, actor kind and id, reason; no personal data,
identity-context.md insight 1) and a counter `tarol.authorization.denied` (tags `change`, `reason`, `actor.kind`) on the
meter `Tarol.Security` **[verified]**. Not an event: nothing changed (security.md open question, answered).

**Field-level authorization of reads** (security.md open question): a view record decides what it shows (R-8); a view that
depends on the caller is a second view with its own `Read` grant, not a filter inside one.

## 4. Binding (R-7 = B2) **[verified]**

The generator writes a member binder per input: route members parsed from route values (`Id<T>.TryParse`, `Guid`,
`string`; a bad one → `errors.<member>: Format`), body members read one by one with `Utf8JsonReader`, each through the
app's JSON context (value objects through `ValueObjectJson.Resolver`), every error collected (`Json` = not valid for the
member type, `Required` = absent and not optional), then the record's constructor. Route members are not read from the
body. The body is buffered with a limit (64 KiB default, 413). The generator must follow the app's naming policy, case
sensitivity and `[JsonPropertyName]` on the parameters. Comparison with B1 (whole record through STJ): B1 reports one error
per request for JSON-shape problems and lets missing members default (`{"title":"Sunflowers"}` → only `reserve: Range`); B2
reports `reserve: Required, endsAt: Required`.

## 5. Context at the edge (R-11)

| Part | Source | Notes |
|---|---|---|
| actor | `sub` → `Actor.User(sub)`; `client_id` (and no other `sub`) → `Actor.Service(client_id)`; else `Anonymous`; **an app rule** `ChangeEndpointOptions.Actor` replaces it (R-16) | claim names in `ChangeEndpointOptions`; no names or e-mails recorded **[verified]**; an IdP's machine users can have `sub ≠ client_id` (Zitadel), so the default rule records them as users **[verified]** |
| tenant | a claim (default `tenant`, an `Id<ITenant>`; malformed → 401); **an app mapping** `ChangeEndpointOptions.Tenant` turns another value into an `Id<ITenant>` (R-16); a `{tenant}` route value must equal it (else 404); no claim = `Option.None` | no header; webhooks: the sender entry's tenant; Zitadel organisation ids are numeric **[verified]** |
| correlation | new per request | a caller's id is not accepted (tracing.md insight 2) **[verified]** |
| trace | ASP.NET Core continues `traceparent` in `Activity.Current`; the store writes it **only if something listens** to `Microsoft.AspNetCore` (OpenTelemetry) | **[verified]**: no listener → NULL column; with a listener the caller's trace id is stored |
| time | the pipeline's `TimeProvider` | — |

## 6. Keys and versions

* **`Idempotency-Key`** → `IdempotencyKey(tenant, "http:<actor kind>:<actor id>:<METHOD> <path>", key)`; at most 255
  characters. Replay: 200 (creation) / 204 (change) with the first outcome's `ETag` and `Idempotent-Replayed: true`
  **[verified]**. Another caller with the same key gets its own outcome **[verified]**. **The same key with another body
  replays silently today** **[verified]**; 10A stores a fingerprint (SHA-256 of method, path and body) with the key and
  answers 422 `Idempotency.KeyReused` on a mismatch (R-9).
* **`ETag: "<version>"`** on every success and every GET; `If-Match: "<n>"` → `ExpectedVersion` (stale → 409
  `Stream.VersionConflict`, changes.md §6; RFC 9110 would say 412, the 409 is Tarol's decided mapping); a malformed value →
  400 `errors.If-Match`; `If-None-Match` on GET → 304 **[verified]**.

## 7. Reads: GET by id (R-1, R-8 = G2)

```csharp
[Endpoint("GET /auctions/{auction}", Policy = "auctions:read")]
public sealed record AuctionSummary(Guid Id, string Title, string Seller, AuctionStatus Status, decimal HighestBid, int Bids, DateTimeOffset EndsAt)
    : IView<Auction, AuctionSummary>
{
    public static AuctionSummary From(Auction a) => new(a.Id.Value, a.Title, a.Seller, a.Status, a.HighestBid, a.Bids.Count, a.EndsAt);
}
```

Load (tenant-scoped, the stored state when current), authorize `Read<Auction>`, `ETag`, 304 on `If-None-Match`, then the
view through the app's JSON context **[verified]**. The stored state itself (G1) is not an API: it carried `schema`,
`reserve` and every bid **[verified]**. Hooks for steps 12 and 13: route groups, `ETag`/`If-None-Match`, `Read<T>` as the
intent of any query, `IView<,>` as the shape a projection-backed query can return later. Step 12 ([queries.md](queries.md))
adds list queries on `IQuery<TRow>` records over projections (QR-4) and `Tarol-Position` on change responses (QR-5).

## 8. Webhooks (R-10) **[verified]**

```csharp
public sealed partial record RecordPayment(Id<Auction> Auction, Amount Amount, string PaymentId) : IChange<Auction>, IValidatable
{
    [Webhook("payments", "payment.succeeded", Version = "2")]
    public static RecordPayment From(PaymentSucceededV2 p) => new((Id<Auction>)p.Reference, new Amount(p.Amount), p.PaymentId);

    [Webhook("payments", "payment.succeeded", Version = "1")]
    public static RecordPayment From(PaymentSucceededV1 p) => new((Id<Auction>)p.Order, new Amount(p.Cents / 100m), p.Id);
    …
}
```

* `POST /webhooks/<sender>` and `/webhooks/<sender>/{tenant}`, anonymous at the endpoint: the signature is the
  authentication. Raw body first (size limit → 413), `HMAC-SHA256(secret, timestamp + "." + body)` compared with
  `FixedTimeEquals` against every configured secret (rotation: new and old), `Webhook-Timestamp` within ±5 minutes of the
  `TimeProvider` → else 401; then the envelope `{id, type, version?, data}` with `Utf8JsonReader`.
* Secrets and windows from `IOptionsMonitor<WebhookOptions>` (configuration), one entry per sender or per sender/tenant;
  the tenant is bound to the entry, so a sender cannot address another tenant (another tenant's URL with the shared
  secret → 401; the shared URL cannot see tenant B's auction → 404 → 503).
* Context `Actor.Service("webhook:<sender>")`, key `(tenant, "webhook:<sender>", delivery id)`; no permission check
  (no principal, R-6).
* Answer (the classification the reaction worker uses, C-9/S-7; moved into `Tarol.Changes` in 10A): success or replayed
  key → 200; `Invalid` or data that does not match the contract record → 422; any `Conflict` and `NotFound` → 503 (with
  `Webhook-Error: <code>`); unknown type → 202; **known type, unknown version → 422** (R-10, option
  `UnknownVersionStatus`).
* Guidance: a redelivered *business* duplicate (the same payment id under a new delivery id) must be a success in the
  domain, or C-9 makes the sender retry it until it gives up **[verified]** (`RecordPayment` returns success for a known
  payment id).

## 9. OpenAPI (R-4, R-12)

`Microsoft.AspNetCore.OpenApi` describes only endpoints with a `MethodInfo` in their metadata: generated `RequestDelegate`
endpoints are absent; with `((RequestDelegate)Run_X).Method` as metadata all of them appear, natively, with request
schemas from `AcceptsMetadata` **[verified]**. Missing then: route parameters, 204 and problem responses, security, and
value objects (`Amount {}`, `Title {}`, `Id<T>` without a type). 10B generates complete metadata (R-12): route parameters,
`Accepts` for the body members only, `Produces` 2xx + 400/401/403/404/409/415 problem responses, the policy, and schema
transformers for value objects (type and rules from `[Length]`/`[Range]`) and `Id<T>` (`uuid`) **[unverified]**.

## 10. Hosting (R-14)

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>())   // Tarol.Changes: TarolBuilder
    .WithReactions(GeneratedReactions.Set)                                                // outbox rows from the API too
    .UseNpgsqlStore(builder.Configuration.GetConnectionString("tarol")!)                  // Tarol.Persistence.Npgsql; EnsureSchema hosted
    .AddSecurity(roles => …)                                                              // Tarol.Security
    .AddEndpoints();                                                                      // Tarol.Changes.AspNetCore
// the worker process: … .UseNpgsqlStore(…).AddReactionWorker();
app.MapChanges();
```

`WebApplication.CreateSlimBuilder` + Native AOT **[verified]**. Under the Aspire AppHost the API and the worker are two
resources of one app: `POST …/close` in the API, `reaction MarkPayoutDue` in the worker with an `ActivityLink`, recorded as
`System Reactions.MarkPayoutDue on behalf of alice` **[verified]** (JIT projects and the native binary).

## 11. A real identity provider: Zitadel (R-16, R-17) **[verified]**

The test key hides what a real IdP does. The probe runs **Zitadel v4.20.3** in a container on PostgreSQL (standalone, and
as an Aspire resource on the AppHost's PostgreSQL resource), provisions it through its management API (project
`artflow`, the app's roles, machine users with client secrets and role grants, JWT access tokens) and lets the same app
validate its tokens with `JwtBearer` `Authority` (discovery + JWKS, RS256), JIT and Native AOT (0 IL warnings):

| Seen in a real token | Consequence |
|---|---|
| roles in **one JSON-object claim** `urn:zitadel:iam:org:project:<projectId>:roles` → `{"seller":{"<orgId>":"<domain>"}}` | an `IClaimsTransformation` turns them into `role` claims, or the AZ-D role map sees nothing |
| a machine user: `sub` = its user id, `client_id` = its name | the default actor rule says `User`; an app rule (`ChangeEndpointOptions.Actor`, here: role `service` → `Actor.Service(client_id)`) |
| the organisation `urn:zitadel:iam:user:resourceowner:id` (scope `urn:zitadel:iam:user:resourceowner`), numeric | the tenant mapping (`ChangeEndpointOptions.Tenant`) to an `Id<ITenant>` (probe: a stable version-8 Guid from the id); taking it from the role grants instead left users without grants without a tenant (404 instead of 403) |
| no `scope` claim in access tokens | scope-based endpoint policies do not carry over; the role map decides, the endpoint policy authenticates |
| `aud` = the project id (only when the scope `urn:zitadel:iam:org:project:id:<id>:aud` is requested) | `JwtBearer.Audience` = the project id; a token without it → 401 |

Checks **[verified]** (JIT and native): create 201 with keys from discovery, actor `User(<zitadel user id>)`, tenant from
the organisation, bid 204, another seller's close 403 `Auction.NotSeller`, no role 403 (create and GET), reader GET 200,
machine user with role `service` → `Actor.Service(billing)`, foreign `aud` 401, tampered signature 401, own close 204.
Under the AppHost: `zitadel` (container, `/debug/ready` health check, fixed port 8080 because the issuer is
`http://localhost:8080`), `zitadel-setup` (provisioning), `api` and `worker` `WaitForCompletion(setup)`; the reaction is
recorded `on behalf of <alice's Zitadel user id>`.

Decided: Tarol ships IdP-neutral hooks (actor rule, tenant mapping) in `Tarol.Changes.AspNetCore`; **`Tarol.Security.Zitadel`**
(optional sugar, 10B) packages the claims transformation (project roles → `role`, organisation → tenant, a configurable
service role → `Actor.Service`) behind `AddZitadel(…)`; the Zitadel container and setup live in the sample AppHost and
step 18's end-to-end tests. CI keeps the symmetric test key for endpoint tests and runs one OIDC integration class against
a Zitadel Testcontainer (R-17). No package depends on Aspire (D-15).

## 12. Testing

| Level | Tool |
|---|---|
| rules | a `ClaimsPrincipal` + `IAuthorizationService` in a unit test (no host): role map and row rules are pure **[verified in step 4]** |
| change | `ChangeTest` (unchanged; no principal = no check) |
| endpoint | `WebApplicationFactory` with a JwtBearer test key or a test scheme; one test per row of changes.md §6 and §3 here |
| generator | snapshots of the binder, a read, a webhook switch; diagnostics; incrementality |
| native | an AOT smoke app that serves and calls itself over HTTP (the probe's `selftest`) |

## 13. Packages (R-13, R-16)

| Package | Contents | Depends on |
|---|---|---|
| `Tarol.Changes` (10A) | pipeline order, "principal decides", `ChangeContext` in the check, delivery classification (from Sagas), `TarolBuilder`/`AddTarol` | unchanged + DI abstractions (base framework) |
| `Tarol.Persistence(.Npgsql)` (10A) | key fingerprint column, `UseInMemoryStore` / `UseNpgsqlStore` | unchanged |
| `Tarol.Sagas` (10A) | `AddReactionWorker` on the builder; `Classify` forwarded | unchanged |
| **`Tarol.Security`** (10A) | `ChangeRequirement`, `ChangeHandler<,>`, `ChangeRoles`, `ChangeAuthorization`, `Read<T>`, audit | `Tarol.Changes`, ASP.NET Core framework reference |
| **`Tarol.Changes.AspNetCore`** (+ generator, 10B) | `[Endpoint]`, `[Webhook]`, `IView<,>`, edge context (with actor and tenant hooks), binder runtime, problem details (owned here), reads, webhooks, OpenAPI metadata | `Tarol.Security` |
| **`Tarol.Security.Zitadel`** (10B, optional) | Zitadel claims transformation + `AddZitadel(…)` (sets the hooks) | `Tarol.Changes.AspNetCore` |

No meta-package (D-8 stays at step 17).

## New insights

1. **Authorization needs no Tarol engine, only a requirement type.** Making the change itself the requirement turns
   ASP.NET Core's "any succeed, no fail" into "role grant or ownership grant, and no row rule denies" without a new model.
2. **OpenAPI follows `MethodInfo`, not routes.** A `RequestDelegate` endpoint is invisible to the document until its method
   is metadata, even though routing, auth and `AcceptsMetadata` all work.
3. **A trace continues only when someone listens.** ASP.NET Core creates no `Activity` without a listener, so the stored
   `traceparent` is empty in an app without tracing; Tarol does not read the header itself (kernel decision 2).
4. **Endpoint names are global**: generated names collide when the same entries are mapped twice (route groups).
5. **Business duplicates are the domain's job**: delivery ids stop redeliveries, but a provider that sends the same payment
   under a new id is only safe if the behaviour treats it as a success.
6. **A real IdP's token shape is app work, not a Tarol rule**: roles as a JSON-object claim, machine users with their own
   `sub`, numeric organisations, no `scope` claim; Tarol needs two hooks (actor, tenant), the mapping is IdP-specific.
7. **The generated endpoint costs nothing measurable** against a hand-written minimal-API endpoint once both are warm
   (Native AOT: equal throughput and p99).

## Decisions taken (owner, 2026-11-15; migration/rest.md §9)

R-1 GET by id in step 10; R-2 core changes allowed; R-3 authentication stays the app's; R-4 OpenAPI probed, then R-12
complete metadata; R-5 AZ-D (app roles mapped onto changes and reads + row rules); R-6 the principal decides; R-7 B2
member binder; R-8 view records; R-9 key fingerprint + 422; R-10 unknown version 422; R-11 tenant claim checked against
the route; R-13 two packages, classification into Changes; R-14 one builder; R-15 two PRs; R-16 IdP-neutral hooks +
`Tarol.Security.Zitadel` sugar, Zitadel in the sample AppHost; R-17 test key in CI + one Zitadel integration class.

## Open questions

* Role maps from configuration (role → change names) need a generated name → type table; later, if asked.
* Rate limiting and request size per endpoint: ASP.NET Core's own middleware; nothing Tarol-specific planned.
