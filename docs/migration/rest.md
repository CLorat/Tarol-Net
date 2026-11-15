# Security and REST: evaluation (step 10 design)

Evaluation of [architecture/rest.md](../architecture/rest.md) (which updates [security.md](../architecture/security.md)):
inventory of the old code, candidates for declaring authorization, binding and generating endpoints and the read side,
the scenarios as probed over HTTP, measurements, and the decided split. **Design only**: this PR changes no package code.
Probes: [`probes/rest/`](probes/rest/) ([README](probes/rest/README.md)), output: [`output.txt`](probes/rest/output.txt).
Labels as in the [conventions](../architecture/conventions.md). Old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **Little to port** **[verified by reading]**: the old Security, Contexts and `IHttpInvoke` are five marker files with no
  consumers; ArtFlow has no web project. Nothing is ported; the findings that still matter are kept as rules (§1).
* **Everything changes.md §6 promises works end to end over HTTP, natively** **[verified]**: 60 checks per run of the
  probe app (JIT and Native AOT, in-memory and Npgsql, two binding styles, two problem-details writers) pass: 201/204 with
  `ETag`, 400 with JSON-cased paths, 401/403/404/409/413/415, key replay, `If-Match`, GET with 304, webhooks
  (valid, forged, tampered, stale, rotated secret, unknown type, unknown version, per-tenant URL).
* **Authorization = AZ-D** (R-5, the owner's requirement): the app's roles are mapped onto the changes and reads they may
  run, row rules on the loaded (or new) aggregate grant or deny; the change itself is the ASP.NET Core requirement, so
  "allowed = a grant and no deny" is ASP.NET Core's own semantics **[verified]**.
* **Two defects in today's pipeline** **[verified]**: a forbidden caller with a stale `If-Match` learns the current version
  (409 before 403), and the hook is called for every reaction with no principal. 10A fixes both (R-2, R-6).
* **Binding B2** (R-7): a generated member binder reports every missing or mistyped member; B1 (STJ on the whole record)
  lets missing members default **[verified]**.
* **Cost: none measurable**: the generated endpoint and a hand-written minimal-API endpoint serve the same load (JIT
  in-memory 34.2k vs 34.5k req/s; Native AOT Npgsql 2.7k vs 2.5k). Native: 26.4 MB, **0 IL warnings**, 34 ms to the first
  200 (JIT 249 ms) **[verified]**.
* **OpenAPI under AOT works only with the delegate's `MethodInfo` as metadata** **[verified]**; 10B generates complete
  metadata (R-12).
* **Aspire**: API and worker as two resources of one app, HTTP span → reaction span linked, natively too **[verified]**.
* **A real IdP** (owner's addition): Zitadel v4.20.3 in a container, standalone and as an AppHost resource; the app validates
  its RS256 tokens through discovery + JWKS, JIT and natively (0 IL warnings), 11 of 11 checks **[verified]**. It needs two
  edge hooks (actor rule, tenant mapping) and a claims transformation; R-16 adds `Tarol.Security.Zitadel` as optional sugar.
* **Plan** (R-15): two PRs, **10A** core changes + `Tarol.Security`, **10B** `Tarol.Changes.AspNetCore` (endpoints,
  reads, OpenAPI, webhooks, sample).

## 1. Inventory of the old code **[verified by reading]** (`CLorat/Tarol@6a62f81`)

| Old | file:line | Content | Fate |
|---|---|---|---|
| `src/Security/Security` | `IAuthorizer.cs:1-6`, `Security.csproj:1-4` | `IAuthorizer.IsAuthorizedAsync(CancellationToken)`; namespace `Security`; no consumers | dropped (security.md §7); replaced by `Tarol.Security` |
| `src/Contexts/Contexts` | `IUserContext.cs:6-22` | `IUserContext { Id<IUser> UserId; Id<ITenant> TenantId; Id<ITrace> TraceId; Roles; IsAuthenticated; ContextUserType }`; no implementation | replaced in step 4 (`OperationContext`, `Actor`); roles stay in the principal (identity-context.md §4.4) |
| `src/Changes/Changes` | `Triggers/IHttpInvoke.cs:3-5`, `Triggers/IReactsTo.cs:5`, `Change.cs:5-9` | empty markers | replaced in step 7 (C-5 attributes) |
| `src/Messaging/Messaging.Security` | `ITopicAuthorizer.cs:3-6` | `IsAuthorized(topic, ct)`, no subject | step 11 (tenant-scoped topics) |
| `src/Results/Results` | `Violation.cs:330-340` (doc example), `:344-350` | `Results.Problem(statusCode: violation.ToHttpStatusCode(), …)` in the XML docs | the example is the AOT trap of security.md insight 6; Tarol writes problem details itself or through `IProblemDetailsService` after `AddProblemDetails` **[verified]** |
| `examples/ArtFlow` | `Auctions/Changes/Bid/Auction.Change.Bid.cs:8`, `Close/Auction.Change.Close.cs:7` | `abstract record … : IChange<Auction>, IHttpInvoke` | no web project, no endpoint, no authorization: the HTTP needs of §2 come from the plan and ROADMAP Phase 3 exit criteria (`docs/review/ROADMAP.md:147`: "create-auction, place-bid, close … run end-to-end: HTTP or event in") |
| `templates/TarolSolution/src/$product$.Api/Program.cs` | `:7-8`, `:38-44`, `:50-56` | `AddCqrs(…)` with assembly scanning and middleware types that do not exist; reflection-based converter factories in `JsonOptions` | replaced in step 19; the rule kept: one generated or explicit registration, no scanning (R-14) |
| `templates/Entity/…/$component$.MinimalApis.cs` | `:8-16` | hand-written `MapGroup` + `MapPost` per command | the shape Tarol generates (route groups kept, §2.2) |

Old review findings that touch endpoints or authorization, re-verified:

| Finding | file:line | Still relevant as |
|---|---|---|
| supporting.md §1.3 / #26: `IAuthorizer` has no subject, resource or action | `IAuthorizer.cs:1,5` | the requirement must carry the intent and the resource (AZ-D: change + aggregate) |
| supporting.md §1.2: `IUserContext` has no implementation, no DI | `IUserContext.cs:6-15` | the context is built at the edge from the principal (§2.2 `Edge`) |
| cqrs.md C2: nothing registers handlers or the dispatcher | `CqrsDispatcherGenerator.cs:241-301` | R-14: one builder; authorization handlers are registered explicitly |
| cqrs.md C3: a singleton dispatcher resolves scoped handlers from the root | `CqrsDispatcherGenerator.cs:241-253` | `ChangeAuthorization` is a singleton over `IAuthorizationService`: handlers must be singletons (or the pipeline scoped); stated in 10A (§5.6) |
| cqrs.md C12: a fixed public generated type collides across assemblies | `CqrsDispatcherGenerator.cs:163-167` | `GeneratedEndpoints` lives in a namespace named after the assembly |
| cqrs.md C14: `build/*.targets` loads the analyzer again | `CQRS.Generators.csproj:29` | the endpoint generator ships inside `Tarol.Changes.AspNetCore` (AGENTS.md) |
| cqrs.md C15: no `IsAotCompatible` | `CQRS.csproj` | both packages set it; the AOT smoke app serves HTTP natively |

## 2. Candidates, as ArtFlow user code

### 2.1 Declaring authorization (question 1)

All four run with ASP.NET Core's `IAuthorizationService`; they differ in what the requirement is.

**AZ-A: resource policy names.** `[Permission("auction:close")]` names a policy; handlers `AuthorizationHandler<SellerRequirement,
Auction>` see the aggregate, not the input; a generated change → policy table. **[unverified]** (policies with resource
handlers verified in step 4).

**AZ-B: the change is the requirement.** `ChangeHandler<CloseAuction, Auction>` sees input and aggregate; a change without
a succeeding handler is denied. **[verified]** (first probe round: 403 `Auction.NotSeller`, admin 204, no handler 403).

**AZ-C: operation names.** `OperationAuthorizationRequirement { Name = "CloseAuction" }`, one handler per aggregate
switching on the name; no input. **[verified in step 4]** (`probes/kernel/authz`).

**AZ-D (decided, R-5): AZ-B + the app's roles mapped onto changes and reads.** **[verified]**

```csharp
builder.Services.AddTarolSecurity(roles =>
{
    roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
    roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
    roles.Role("reader").May<Read<Auction>>();
    roles.Role("admin").May<CloseAuction>().May<Read<Auction>>();
    roles.Authenticated().May<WatchAuction>();             // TouchAuction: granted to nobody → 403
});
builder.Services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();   // Deny unless seller or admin
builder.Services.AddSingleton<IAuthorizationHandler, CreateAuctionRule>();  // Deny a reserve > 100 000 without "dealer" (on the NEW aggregate)
builder.Services.AddSingleton<IAuthorizationHandler, ReadOwnAuction>();     // Succeed for the seller (ownership grant)
```

```http
POST /auctions/0193…/close            Authorization: Bearer <bob: roles seller, bidder>
HTTP/1.1 403 Forbidden
Content-Type: application/problem+json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Auction.NotSeller","status":403,"code":"Auction.NotSeller","traceId":"…"}
```

| | AZ-A | AZ-B | AZ-C | **AZ-D** |
|---|---|---|---|---|
| per-change requirement | policy name | type | string | type + role map |
| row rule sees the input | no | yes | no | yes |
| app roles → changes | by hand in policies | in every handler | in every handler | one map |
| fail closed | if the table lists the change | yes | yes | yes |
| generated code | change → policy table | none | none | none |

### 2.2 Binding and generation of endpoints (question 2)

Generation is decided (C-12: generated `RequestDelegate`s; `MapPost(pattern, lambda)` in generated code is not seen by
the RDG). The candidates are about binding.

```csharp
[Endpoint("POST /auctions/{auction}/bids", Policy = "auctions:bid")]
public sealed partial record PlaceBid(Id<Auction> Auction, Amount Amount) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.PlaceBid(context.Operation.Actor.Id, Amount, context.Now).ToResult();
}
```

**B1: the record through STJ** **[verified]**: `JsonSerializer.DeserializeAsync(body, JsonTypeInfo<PlaceBid>)` then
`with { Auction = route_Auction }`. **B2 (decided, R-7): a generated member binder** **[verified]**, generated code
(probe output, shortened):

```csharp
app.MapMethods("/auctions/{auction}/bids", new[] { "POST" }, (RequestDelegate)Run_PlaceBid)
   .WithName(names + "PlaceBid")
   .WithMetadata(new AcceptsMetadata(new[] { "application/json" }, typeof(PlaceBid), isOptional: false))
   .WithMetadata(new ProducesResponseTypeMetadata(204))
   .RequireAuthorization("auctions:bid");

private static async Task Run_PlaceBid(HttpContext http)
{
    var web = ChangeEndpoints.Of(http);
    if (!Id<Auction>.TryParse(http.Request.RouteValues["auction"] as string, null, out var route_Auction))
    { await web.Problem(http, Invalid.Field("Auction", "Format")); return; }
    var (bytes, status) = await web.ReadBytes(http);                      // 413 above MaxBody
    if (bytes is null) { http.Response.StatusCode = status; return; }
    var bad = Bind_PlaceBid(web, bytes, route_Auction, out var input);    // Utf8JsonReader, every member, Json/Required
    if (bad is not null) { await web.Problem(http, bad); return; }
    var (context, options, refused) = web.Edge(http);                     // actor, tenant, correlation, key, If-Match
    if (refused is not null) { await web.Problem(http, refused); return; }
    await web.Write(http, await web.Pipeline.Run(input!, context, options, http.RequestAborted), created: false);
}
```

**B3: a separate body record** (`[Endpoint(…, Body = typeof(PlaceBid.Body))]`, route members only in the change): exact
OpenAPI body schemas, but a second record per change to keep in sync. **[unverified]**, not probed.

```http
POST /auctions        {"title":"Sunflowers"}
B1 → 400 {"errors":{"reserve":["Range"]}}                          (endsAt defaulted, the factory never ran)
B2 → 400 {"errors":{"reserve":["Required"],"endsAt":["Required"]}}
```

| | B1 | **B2** | B3 |
|---|---|---|---|
| errors for JSON shape | first only; missing members default | all members | as B1 |
| STJ semantics | exact | mirrored by the generator (naming policy, case, `[JsonPropertyName]`) | exact |
| route member in the body | read, then overwritten | ignored | absent |
| record in the JSON context | needed | its member types needed (listing the record brings them) | the body record |
| native, 0 IL warnings | yes | yes | — |

### 2.3 The read side in this step (question 4)

R-1 decided "one GET by id in step 10". Candidates for its shape:

**G1: the stored state** **[verified]**: `[Endpoint("GET /auctions/{auction}")]` on the aggregate, generated
`T.WriteState`. Response: `{"id":…,"version":5,"state":{"id":…,"version":5,"schema":99743012,"endsAt":…,"paid":0,
"reserve":100,"seller":"alice",…,"bids":[…]}}`: the persistence format becomes the API.

**G2 (decided, R-8): a view record** **[verified]**: `[Endpoint("GET /auctions/{auction}/summary")]` on
`AuctionSummary : IView<Auction, AuctionSummary>` (shape in rest.md §7). Response:
`{"id":…,"title":"Sunflowers","seller":"alice","status":"Closed","highestBid":210,"bids":3,"endsAt":…}` with `ETag: "5"`;
`If-None-Match: "5"` → 304; another tenant → 404; no `Read<Auction>` grant → 403; the seller without the reader role →
200 (row grant).

**G0: nothing in step 10** (rejected by R-1): the `ETag` would only come from write responses, so a client that did not
write cannot send `If-Match`.

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/rest/output.txt) §1–§6)

ArtFlow over HTTP on today's packages, JwtBearer with a test key, the would-be packages and generator in `probes/rest/`.
Each row ran JIT and Native AOT, on the in-memory store and on PostgreSQL 17.12:

| Area | Checks |
|---|---|
| creation | anonymous 401 + `WWW-Authenticate: Bearer`; missing scope 403 (no body); 201 + `Location` (also under `/v1`) + `ETag "1"` + `{id, version}`; all-field 400; factory guard 400 `endsAt`; wrong JSON type 400 `reserve: Json`; missing members (B1 vs B2); broken JSON 400; `text/plain` 415 |
| keys | replay 200 + `Idempotent-Replayed` (creation) and 204 (change, same `ETag`, nothing appended); another caller: own outcome; **another body: replays the first** (R-9) |
| changes | bid 204 `ETag "2"`; guard 409 with the guard's code; caller rule as a guard 409 `Bid.OwnAuction`; `If-Match` stale 409, malformed 400 `If-Match`, current 204; lifecycle 409 `AuctionStatus.NotAllowed`; bad route id 400; unknown id 404 |
| authorization (AZ-D) | no role 403; row deny on a change (`Auction.NotSeller`) and on a creation (`Auction.ReserveLimit`); admin 204; nothing granted 403; another tenant 404; **forbidden + stale `If-Match` → 409 with the current version (today)**; denials counted on `tarol.authorization.denied` |
| reads | G1 200 + `ETag`; 304; G2 view without `reserve`; other tenant 404; anonymous 401; ownership grant 200 / stranger 403 |
| context | actor `User:alice`, `User:bob`, `Service:svc-billing` (client credentials); new correlation per request; tenant from the claim; `traceparent` stored only with a listener (Npgsql) |
| webhooks | open auction → 503 `Webhook-Error: Auction.NotClosed`; valid 200, redelivery 200 with nothing written; actor `Service:webhook:payments`; old secret 200; forged / tampered / stale / unsigned 401; unknown type 202; unknown version 422; V1 contract 200; second payment 503; unknown reference 503; invalid mapped input 422; wrong data shape 422; 70 KB body 413; per-tenant URL 200 / shared secret 401 / shared URL 503 |
| problem details | through `IProblemDetailsService` (`AddProblemDetails`, adds `traceId`) and written by hand: same status, `errors`, `code` |

## 4. Measurements **[verified]** (4 cores, SDK 11.0.100, PostgreSQL 17.12 in local Docker; client and server share the cores)

### 4.1 Native AOT (`output.txt` §5, §6)

| | B1 | B2 |
|---|---|---|
| IL warnings at publish (app with JwtBearer, OpenAPI, OpenTelemetry, Npgsql) | 0 | 0 |
| binary | 26 421 504 B | 26 412 960 B |
| self-test natively (in-memory, Npgsql, hand-written problem details) | all pass | all pass |

### 4.2 Startup: process start → first 200 on `/health` (median of 5, §7)

| runtime | in-memory | Npgsql (incl. `EnsureSchema`) |
|---|---:|---:|
| JIT (Release) | 249 ms | 401 ms |
| Native AOT | 34 ms | 55 ms |

### 4.3 Generated vs hand-written endpoint (§8; `POST /auctions/{a}/watch`, JWT, 16 clients, 5 s after two 3 s warm-ups)

| endpoint | runtime | store | req/s | p50 ms | p99 ms |
|---|---|---|---:|---:|---:|
| generated (B1) | JIT | in-memory | 34 186 | 0.37 | 2.48 |
| hand-written (RDG) | JIT | in-memory | 34 522 | 0.36 | 2.51 |
| generated | JIT | Npgsql | 2 741 | 4.98 | 20.53 |
| hand-written | JIT | Npgsql | 2 794 | 4.88 | 22.72 |
| generated | Native AOT | in-memory | 30 090 | 0.44 | 2.24 |
| hand-written | Native AOT | in-memory | 28 693 | 0.46 | 2.40 |
| generated | Native AOT | Npgsql | 2 666 | 5.20 | 21.34 |
| hand-written | Native AOT | Npgsql | 2 487 | 5.63 | 22.58 |

Differences are within run-to-run noise. A first run with a 2 s warm-up showed the generated endpoint at 8.8k vs 22.7k
req/s under JIT; reversing the order reversed the result (whichever endpoint was measured first was still tiering up), so
the warm-up was lengthened. One commit per request dominates on Npgsql.

### 4.4 OpenAPI under Native AOT (§5)

| Metadata | Generated endpoints in `/openapi/v1.json` |
|---|---|
| as C-12 generates them (`RequestDelegate`, `AcceptsMetadata`, `ProducesResponseTypeMetadata`) | **none** (2 paths: `/health` and the hand-written endpoint) |
| + `((RequestDelegate)Run_X).Method` | all 16, schemas `CreateAuction`, `PlaceBid`, `AuctionSummary`, …; but no path parameters, `200` instead of `204`, route members in the body schema, no security, `Amount {}`, `Title {}`, `IdOfAuction` without a type |

### 4.6 Zitadel (§11, §12)

| | Result |
|---|---|
| container start → ready (`/debug/ready`, first instance) | 3–5 s, then provisioning (setup.py retries the first 503s) |
| app validating Zitadel tokens: JIT / Native AOT | 11/11 / 11/11 checks; publish with the OIDC mode 0 IL warnings |
| AppHost: `pg` + `zitadel` + `zitadel-setup` + `api` + `worker` | `api` and `worker` start after the setup completed; create 201, close 204, payout `on behalf of <alice's Zitadel user id>`, linked spans |
| network | ghcr.io serves its layers from `pkg-containers.githubusercontent.com` (added to the session environment for this probe) |

### 4.5 Aspire (§9, §10)

AppHost 13.6.5 (one ASPIRE010 warning, as in step 9): a PostgreSQL resource (Aspire's default image, 18.3), `api` (the app in `serve` mode) and `worker`
(the same app in `worker` mode). Over HTTP with a JWT: `POST /auctions` 201, `POST …/close` 204; the worker writes the
payout (`System Reactions.MarkPayoutDue on behalf of alice`), outbox empty; the dashboard shows both resources with
spans `POST /auctions`, `POST /auctions/{auction}/close`, `reaction MarkPayoutDue` (with an `ActivityLink`). The same with
the Native AOT binary as both resources (`AddExecutable`).

## 5. Found while probing

1. **Permission after `If-Match`** (R-2): `ChangePipeline.Run` compares `ExpectedVersion` before `IChangeAuthorization.Check`,
   and the conflict's detail names the current version **[verified]**.
2. **Every run is checked** (R-6): the pipeline calls the hook for reactions with `principal: null`; the probe's
   implementation skipped those itself **[verified by reading]**.
3. **The check cannot see `[Read]` aggregates**: `Check(change, aggregate, context, principal, ct)` **[verified by reading]**.
4. **The delivery classification lives in the worker package**: the webhook answer needs `DeliveryPolicy.Classify`
   (`Tarol.Sagas`); the probe's web library referenced Sagas for it (R-13).
5. **`AddTarolReactions` rejects a registered `ReactionSet`** as "called twice" (`ReactionHost.cs:119-120`), so a builder that
   registers the set for the store first cannot add the worker **[verified]**; 10A moves the guard to its own marker.
6. **Singleton authorization**: `ChangeAuthorization` (a singleton like `ChangePipeline`) resolves `IAuthorizationService`
   from the root, so a scoped `IAuthorizationHandler` is a captive dependency (cqrs.md C3) **[unverified]**: 10A documents
   singleton handlers and tests it with `ValidateScopes`.
7. **Duplicate endpoint names** when `MapChanges` is mapped twice (route groups) → `MapChanges(names: "V1")` **[verified]**.
8. **Header names in problem paths**: `Invalid.Field("If-Match", …)` was camel-cased to `if-Match`; the path converter leaves
   segments with `-` alone **[verified]**.
9. **No `traceparent` without a listener** **[verified]** (rest.md insight 3).
10. **Idempotency keys without a fingerprint** replay another body's outcome **[verified]** (R-9).
11. **A failed member read leaves the reader where STJ stopped**: the probe binder skips after a `JsonException`; 10B reads
    each value on a copy of the reader and skips on the original **[unverified]** (the probe's malformed cases passed).
12. **Business duplicates** must be successes in the domain (rest.md insight 5) **[verified]**.
13. **Zitadel's roles are one JSON-object claim** per project (`urn:zitadel:iam:org:project:<id>:roles`), not `role` claims
    **[verified]**: without a claims transformation the AZ-D role map sees no role.
14. **Machine users have `sub ≠ client_id`** in Zitadel **[verified]**: the default edge rule ("`client_id` alone →
    service") records them as users; R-16 adds the actor hook.
15. **Organisation ids are numeric**, the tenant is an `Id<ITenant>` (a `Guid`) **[verified]**: a tenant mapping hook; taking
    the tenant from role grants left users without grants tenant-less (404 where 403 was right), the organisation claim
    (`urn:zitadel:iam:user:resourceowner:id`) is the source.
16. **No `scope` claim** in Zitadel access tokens **[verified]**: scope-based endpoint policies are IdP-specific; with AZ-D the
    endpoint policy only authenticates.
17. **The issuer is the external URL**: under the AppHost Zitadel's port is fixed (`isProxied: false`, 8080) so the issuer,
    discovery and the tokens agree **[verified]**.

## 6. Generator and diagnostics (10B; numbers final in the PR)

| Id | When |
|---|---|
| TWEB001 | `[Endpoint]` route is not `"VERB /pattern"` |
| TWEB002 | `[Endpoint]` on a type that is neither a change, a creation nor an `IView<,>` (GET) / a change on GET |
| TWEB003 | a route value that is not a member of the input, or a route member that is not `Id<T>`, `Guid` or `string` |
| TWEB004 | two endpoints with the same verb and pattern |
| TWEB005 | `[Webhook]` on a method that is not a static one-parameter factory returning the change; two factories for one (sender, type, version) |
| TWEB006 | an input without a public primary constructor (the binder needs one) |

Changes.md's reserved TCHG003/TCHG004 for endpoints are not used (TCHG004 became `[Read]` in 7B); endpoint diagnostics get
their own prefix in their own package.

## 7. Decided API (sketch for 10A/10B; names final in the PRs)

* **Tarol.Changes (10A)**: `ChangePipeline` order load → permission → `ExpectedVersion`; `IChangeAuthorization.Check<T>(object
  change, T aggregate, ChangeContext context, ClaimsPrincipal principal, CancellationToken)` called only when
  `ChangeOptions.Principal` is set; `Delivery.Classify(Result<ChangeOutcome>)` → `Verdict` (moved from `Tarol.Sagas`,
  `DeliveryPolicy.Classify` forwards); `TarolBuilder`, `services.AddTarol(JsonSerializerContext, Action<EventCodec>)`.
* **Tarol.Persistence (10A)**: `IdempotencyKey` gains an optional fingerprint; `Seen` returns it; a mismatch →
  `Error.UnprocessableEntity("Idempotency.KeyReused")`; `idempotency_keys.fingerprint bytea` (schema step in `EnsureSchema`);
  `UseInMemoryStore()`, `UseNpgsqlStore(connectionString | NpgsqlDataSource)`.
* **Tarol.Sagas (10A)**: `TarolBuilder.AddReactionWorker(configure)`.
* **Tarol.Security (10A)**: `ChangeRequirement`, `ChangeHandler<TChange, TAggregate>` (`Handle`, `Deny`), `Read<T>`,
  `ChangeRoles` (`Role(name).May<T>()`, `Authenticated()`), `ChangeAuthorization`, `TarolBuilder.AddSecurity(roles)`,
  meter `Tarol.Security` / `tarol.authorization.denied`, log event 1001.
* **Tarol.Changes.AspNetCore (10B)**: `[Endpoint(route, Policy, Anonymous)]`, `[Webhook(sender, type, Version)]`,
  `IView<TAggregate, TSelf>`, `ChangeEndpointOptions` (JSON, claim names, `MaxBody`, `UseProblemDetailsService`),
  `WebhookOptions` (senders, secrets, tenant, window, `UnknownVersionStatus`), `TarolBuilder.AddEndpoints(configure)`;
  generated `MapChanges(this IEndpointRouteBuilder, string? names = null)` with complete OpenAPI metadata (R-12).

## 8. Plan impact (question 10)

* Row 10 → the decided split (two PRs, R-15).
* Row 12 (queries) starts from `IView<,>`, `Read<T>` and the GET entry; projections, query endpoints and pagination stay
  there. Row 13 (caching) gets `ETag`/`If-None-Match` already in place for GET by id.
* Row 15 (operations) takes over the denial counter and the admin entry for dead letters (an `[Endpoint]` with a role).
* Row 17 (release): D-8 unchanged (no meta-package; the graph top is now `Tarol.Changes.AspNetCore` and `Tarol.Sagas`).
* Row 18 (samples): `samples/Sagas` becomes the API + worker AppHost in 10B; `Aspire.Hosting.Testing` end-to-end tests
  stay in 18.

## 9. Owner answers (2026-11-15)

Round 1 (scope, before probing): R-1 … R-4. Round 2 (after the first measurements): R-5 … R-8. Round 3: R-5 confirmed
(AZ-D), R-9, R-10, R-12. Round 4: R-11, R-13 … R-15. Round 5 (the owner's addition after the PR was opened: "maybe an
addition for the start: we could use Zitadel in a container for Aspire as testing", probed in §4.6): R-16, R-17. Not asked (answered by existing decisions): generated
`RequestDelegate`s (C-12), context as a parameter and fail closed (C-11), authorization as ASP.NET Core handlers and
cross-tenant 404 (security.md), Conflict retries for webhooks (C-9, S-7), no caller-provided correlation (tracing.md
insight 2), no trace field in the context (kernel decision 2), 409 for a stale `If-Match` (changes.md §6).

| # | Question | Answer |
|---|---|---|
| R-1 | Read side in step 10 | **One GET by id** (ETag, 304, tenant-scoped 404, same rules); projections, queries, pagination in step 12, caching in 13 |
| R-2 | May step 10 change `Tarol.Changes`? | **Yes, where the edge needs it**, each change listed in the note |
| R-3 | Who owns authentication | **The app** (schemes, JwtBearer); Tarol reads the principal with configurable claim names |
| R-4 | OpenAPI | **Probe, decide after** → R-12 |
| R-5 | Declaring authorization | Owner: "Changes may have different auth requirements, could even be row-based aggregate requirements, so choose the one that is best fitting for any scenario; roles could be defined by the app and then we map the app roles on the changes and reads it requires." → probed as **AZ-D**, confirmed in round 3: **the app's role map onto changes and reads + row rules (`ChangeHandler<TChange, TAggregate>`), allowed = a grant and no deny** |
| R-6 | Internal runs | **The principal decides**: checked only when `ChangeOptions.Principal` is set; HTTP always sets one |
| R-7 | Body binding | **B2: generated member binder** (all members, `Json`/`Required`) |
| R-8 | Shape of GET by id | **G2: view records** `IView<TAggregate, TSelf>` |
| R-9 | Key reused with another body | **Fingerprint + 422 `Idempotency.KeyReused`** |
| R-10 | Webhook: unknown version of a known type | **422** (dead-letter at the sender), option `UnknownVersionStatus` |
| R-11 | Tenant at the edge | **Claim, checked against a `{tenant}` route value**; no header; webhooks: bound to the sender entry |
| R-12 | OpenAPI in step 10 | **Complete metadata**: `MethodInfo`, path parameters, body-member schemas, 2xx + problem responses, policy, value-object and `Id<T>` schemas |
| R-13 | Packages | **Two**: `Tarol.Security` and `Tarol.Changes.AspNetCore` (+ generator, depends on Security); classification moves from Sagas to Changes; no meta-package |
| R-14 | Registration | **One builder**: `AddTarol(…)` → `TarolBuilder`, each package adds its part |
| R-15 | PR split | **Two PRs** (against the recommendation of four): 10A core + `Tarol.Security`, 10B everything web |
| R-16 | Zitadel's place | **IdP-neutral hooks + the sample AppHost, and an optional `Tarol.Security.Zitadel`** (owner: "1 and additionally 2 as sugar"): `ChangeEndpointOptions.Actor`/`Tenant` hooks in `Tarol.Changes.AspNetCore`; `Tarol.Security.Zitadel` (10B) = the claims transformation + `AddZitadel(…)`; Zitadel container + setup in `samples/Sagas`' AppHost and step 18 |
| R-17 | Zitadel in CI | **Test key in CI, Zitadel in the AppHost**: endpoint tests use a symmetric key; one OIDC integration class against a Zitadel Testcontainer in the `integration` job |

Consequence of R-15 against the recommendation: 10B is large (generator, runtime, OpenAPI metadata, webhooks, sample).
It stays one feature ("the HTTP entries") and must build and pass on its own; its commits follow the order of §10 so
review can go commit by commit.

## 10. What the implementation must do (in the decided order)

### PR 10A `security: change authorization, role map and the pipeline order` (core + `Tarol.Security`)

1. `Tarol.Changes`: permission before the `ExpectedVersion` check (change and creation); call the hook only when
   `ChangeOptions.Principal` is set; pass the `ChangeContext` (reads, now) to `Check`; move the delivery classification here
   (`Tarol.Sagas.DeliveryPolicy.Classify` forwards, no behaviour change); `TarolBuilder` + `AddTarol` (DI abstractions from
   the base framework).
2. `Tarol.Persistence` + `.Npgsql`: key fingerprint (in-memory and SQL column, `EnsureSchema` adds it to an existing
   table), 422 `Idempotency.KeyReused`; `UseInMemoryStore`, `UseNpgsqlStore` (+ `EnsureSchema` hosted service).
3. `Tarol.Sagas`: `AddReactionWorker` on the builder; the "called twice" guard on its own marker (§5.5).
4. `src/Security/Tarol.Security` (new family): §7 API, AOT-compatible, singleton handlers documented, denial log + counter;
   no reflection (the role map keys on `Type` of the change instance: `GetType()` only, no member access).
5. Tests: pipeline order (forbidden + stale `If-Match` → 403), no principal = no check, reads visible to a rule, fingerprint
   replay/mismatch on both stores (contract suite), role map × row rules (grant, deny wins, ownership grant, nothing
   granted, anonymous → 401), creation rule on the new aggregate, the counter (filter by own tags, step 9 lesson), builder
   registrations with `ValidateScopes`/`ValidateOnBuild`; skill example verbatim; AOT smoke `Tarol.Security.AotSmoke`.
6. README, skill `tarol-security` (§11.1), `docs/security/README.md`, CHANGELOG, plan status; migration notes of changes,
   persistence and sagas list the behaviour changes (order, "principal decides", fingerprint).

### PR 10B `changes.aspnetcore: HTTP entries, reads, OpenAPI and webhooks`

1. `src/Changes/Tarol.Changes.AspNetCore` (+ `Tarol.Changes.AspNetCore.Generators`, packed inside): attributes, `IView<,>`,
   `ChangeEndpoints` runtime (edge context with claim names and the tenant/route check, B2 helpers, outcome → HTTP,
   problem details by hand or through `IProblemDetailsService`, GET by id with `Read<T>`), `Webhooks`, `AddEndpoints`
   (resolver into ASP.NET Core's `JsonOptions`).
2. Generator: one `RequestDelegate` per endpoint, B2 binder (naming policy, case, `[JsonPropertyName]`, reader copy per
   value), reads, webhook switch per sender with versions, `MapChanges(names)`, complete OpenAPI metadata and schema
   transformers (R-12), TWEB001–TWEB006, deterministic names, equatable models.
3. Tests: `WebApplicationFactory` per row of changes.md §6 and rest.md §3/§8 (both stores for keys and `If-Match`),
   generator snapshots, diagnostics, incrementality; AOT smoke that serves and calls itself (the probe's self-test, 0 IL
   warnings); a new integration project is added to the CI `integration` job.
4. `samples/Sagas` → API + worker under the AppHost (JIT and native), as in `probes/rest/Aspire`, with Zitadel as the
   default identity provider (container, setup resource, `WaitForCompletion`; `probes/rest/Zitadel`) (R-16).
5. Edge hooks `ChangeEndpointOptions.Actor` and `.Tenant` (R-16); `src/Security/Tarol.Security.Zitadel`: an
   `IClaimsTransformation` (project roles → `role`, `urn:zitadel:iam:user:resourceowner:id` → tenant via a stable
   mapping, a configurable service role → `Actor.Service(client_id)`) and `AddZitadel(authority, projectId, …)` that also
   sets `JwtBearer` (`Authority`, `Audience`, `MapInboundClaims = false`); tests with captured Zitadel token payloads
   (unit) and one OIDC integration class against a Zitadel Testcontainer (R-17; the CI job needs ghcr.io); README, skill
   `tarol-zitadel`, AOT smoke.
6. README, skill `tarol-endpoints` (§11.2), `docs/changes/README.md` (HTTP section), CHANGELOG, plan status.

## 11. Skill evaluation checklists

### 11.1 `tarol-security` (10A)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| only the seller closes, admins close any of the tenant | `ChangeHandler<CloseAuction, Auction>` + `Deny(context, "Auction.NotSeller")`, `roles.Role("seller").May<CloseAuction>()` | a guard in the aggregate that reads the caller's roles; `context.Fail()` without a reason (generic 403) |
| bidders may bid and read | `roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>()` | forgetting `Read<T>` (403 on GET) |
| a job settles without a user | no principal (`pipeline.Run(change, systemContext)`) | building a fake user principal for the job |
| a privileged system action | pass a system principal and grant it | relying on "no principal" for a new privilege |
| test a rule | `IAuthorizationService` + a `ClaimsPrincipal` in a unit test | a web host for a pure rule |

### 11.2 `tarol-endpoints` (10B)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| expose a change | `[Endpoint("POST /auctions/{auction}/bids", Policy = …)]`, list the record in the JSON context, `app.MapChanges()` | a hand-written `MapPost` lambda per change; `Policy` as the only check |
| retry-safe creation | send `Idempotency-Key`; the replay is 200 with the first outcome | reusing a key for another body (422) |
| optimistic concurrency | read `ETag`, send `If-Match` | parsing the version from the body |
| GET by id | `IView<Auction, TSelf>` + `[Endpoint("GET …")]` | exposing the aggregate (stored state) |
| a provider webhook | `[Webhook(sender, type, Version)]` on `From(Payload)`, secrets in configuration | throwing on a duplicate payment (503 forever) |
| versioned API | `app.MapGroup("/v1").MapChanges(names: "V1")` | mapping twice without names |

## 12. Verification of this PR (SDK 11.0.100, 2026-11-15) **[verified]**

Docs only (this note, rest.md, updates of security.md, identity-context.md, changes.md, plan.md, the probes). See the PR
body for `dotnet format --verify-no-changes`, build and tests (with Docker); `output.txt` holds the probe runs: sections 1–6
from one full run, 7–8 and 9–10 resumed with `ALL_FROM` on the same builds after session worker restarts (the script keeps
the work directory and appends), 11–12 (Zitadel) added afterwards with `ALL_FROM=11`.

## 13. Open items

* Role maps from configuration (needs a generated name → type table of changes and reads).
* RFC 9110 says 412 for a failed `If-Match`; Tarol answers 409 (changes.md §6). Revisit only if a client needs 412.
* Field-level variants of a view per caller: separate views with their own grant (rest.md §3); no filter mechanism planned.

## 14. Implementation: PR 10A (2026-11-15)

Core changes + `Tarol.Security`, stacked on the design PR (#27). Package guide: `src/Security/Tarol.Security/README.md`;
user guide: [docs/security/README.md](../security/README.md).

### 14.1 Public API

* **Tarol.Changes**: `IChangeAuthorization.Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal
  principal, CancellationToken)`; `ChangePipeline.NotConfiguredCode`; `Verdict`, `Delivery.Classify`;
  `TarolServiceCollectionExtensions.AddTarol(IServiceCollection, JsonSerializerContext, Action<EventCodec>)`.
* **Tarol.Persistence**: `IdempotencyKey.Fingerprint` (`Option<string>`, init), `StoredOutcome.Fingerprint`,
  `StoreErrors.KeyReused`/`KeyReusedCode`; `TarolBuilder` (`Services`, `Json`, `Codec`, `Reactions`, `HasStore`,
  `WithReactions`, `UseInMemoryStore`, `UseStore<TStore>`); `IIdempotencyKeyPurge`.
* **Tarol.Persistence.Npgsql**: `UseNpgsqlStore(string | Func<IServiceProvider, NpgsqlDataSource>, options, ensureSchema)`;
  `NpgsqlAggregateStore : IIdempotencyKeyPurge`; column `idempotency_keys.fingerprint`.
* **Tarol.Sagas**: `AddReactionWorker(this TarolBuilder, configure, reactions)`; `DeliveryPolicy.Classify` forwards to
  `Delivery.Classify`.
* **Tarol.Security** (new): `ChangeRequirement(change, context)` (`Change`, `Context`), `Read<TAggregate>.Instance`,
  `ChangeHandler<TChange, TAggregate>` (`Handle`, `Deny`), `ChangeRoles` (`Role`, `Authenticated`, `Grants`), `RoleGrants.May<T>()`,
  `ChangeAuthorization` (`MeterName`, `RequiredCode`, `ForbiddenCode`), `SystemPrincipal.For`, `AddSecurity` /
  `AddTarolSecurity`.

### 14.2 Changes against step 7–9 behaviour (listed per AGENTS.md)

| Before | Now | Why |
|---|---|---|
| the hook was called for every run, `principal` nullable | called only when `ChangeOptions.Principal` is set; non-null | R-6: internal entries are not checked |
| expected version compared before the permission | permission first | R-2: a refused caller learned the version (409 "found 4") |
| a principal on a pipeline without an authorization was ignored | `Error.Forbidden("Auth.NotConfigured")` | fail closed (C-11) |
| `Check` got the `OperationContext` | the `ChangeContext` (operation, now, `[Read]` aggregates) | rules may need the reads (§5.3) |
| a reused key replayed any request's outcome | another fingerprint → 422 `Idempotency.KeyReused`; keys without one replay as before | R-9 |
| `Tarol.Sagas.Verdict`, `DeliveryPolicy.Classify` | `Tarol.Changes.Verdict`, `Delivery.Classify` (the policy forwards) | R-13: webhooks share it without the worker |
| `AddTarolReactions` refused any registered `ReactionSet` | refuses only a second `AddTarolReactions` | §5.5 |
| the worker purged keys only with `o.PurgeKeys = store.PurgeKeys` | also through a registered `IIdempotencyKeyPurge` | one registration |

### 14.3 Deliberate differences from §7 and §10

* **`TarolBuilder` lives in `Tarol.Persistence`**, not `Tarol.Changes`: `UseNpgsqlStore` would otherwise make the Npgsql
  package depend on Changes. `AddTarol` stays in `Tarol.Changes` (it registers the pipeline).
* **`ChangeRequirement` carries the `ChangeContext`**: an ASP.NET Core handler sees only requirement and resource, so the
  reads passed to `Check` reach a rule only through the requirement (`requirement.Context.Read<Artwork>()`).
* **`Read<T>` is in `Tarol.Security`** (rest.md §13 table: there already), not in the web package.
* **`AddReactionWorker` takes the set optionally**: the worker needs the reactions when it is added, so "order does not
  matter" holds for the stores only (`WithReactions` after `UseNpgsqlStore` works).
* **`AddTarolSecurity` adds logging** (`AddLogging`, TryAdd-based): `AddAuthorizationCore` needs `ILogger<T>`, which a
  bare container (a worker without a host, a test) does not have; found by `ValidateOnBuild`.
* **The fingerprint is an `Option<string>`**, compared ordinally; what it hashes is the entry's choice (10B: method, path,
  body).

### 14.4 Tests **[verified]**

* `Tarol.Changes.Tests` (+15): permission with the principal and the context (time, `[Read]`), no principal = not checked,
  principal without authorization → `Auth.NotConfigured` (change and creation, nothing written), forbidden + stale version →
  403 and the seller's stale version → 409, creation authorized before its version, key fingerprint replay / reuse 422 /
  unmarked replay; `Delivery.Classify` for 14 answers.
* Store contract suite (both stores, +2 each): the fingerprint stored and returned, not part of the identity, a reused key
  with another fingerprint still `DuplicateKey` at append; a key without one stores `None`. Npgsql (+1): `EnsureSchema`
  adds the column to a table without it.
* `Tarol.Sagas.Tests` (+4): `AddTarol` + in-memory store + worker (`ValidateScopes`, `ValidateOnBuild`) runs a change and its
  reaction; `WithReactions` after the store; second store / second `AddTarol` / worker without reactions refused; an
  app's `TimeProvider` kept. `Tarol.Sagas.IntegrationTests` (+2): `UseNpgsqlStore` ensures the schema at host start, the
  worker runs and gets the store's purge; the data-source overload. 24/24 Release reruns of the four timing-dependent
  ones green.
* `Tarol.Security.Tests` (16): role grant, no role, anonymous, row deny over a grant with its code, admin, forbidden + stale
  version, creation rule on the new aggregate, a rule reading a `[Read]` aggregate and the time, `Authenticated()`,
  `Read<T>` by role and by ownership, internal run unchecked and `SystemPrincipal` opt-in, system principal claims, the
  counter (filtered by this test's change), the log line (event 1001, no personal data), `AddSecurity` twice; the skill
  example verbatim.
* `Tarol.Security.AotSmoke`: 10 checks natively, 0 IL warnings, 4.7 MB. `Tarol.Security.PackageTests`: the same checks
  against the packed package (the Changes and Primitives generators flow from its dependencies).

### 14.5 Suppressions and analyzer configuration

* `tests/Security/Tarol.Security.Tests/SkillExampleTests.cs`: `#pragma warning disable xUnit1051` around the verbatim
  skill block only (the skill's code is application code; it has no test cancellation token).

### 14.6 Skill evaluation checklist

As §11.1, plus: "a rule needs the referenced artwork" → `requirement.Context.Read<Artwork>()`; mistake: loading it again in
the handler (a second read, possibly another version).

### 14.7 Verification (SDK 11.0.100, 2026-11-15)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` (Docker) | 981 total, 0 failed (932 before + 49) |
| `dotnet pack Tarol.slnx -c Release` + `eng/verify-packages.sh` | OK: 11 packages; `Tarol.Security` holds `lib/` (+ XML docs), `README.md`, `LICENSE`, `skills/tarol-security/SKILL.md`; its consumer passes 10/10 and lists the skills of `tarol-security` and its dependencies |
| `TAROL_PG=… eng/aot-smoke.sh` | 11 apps, each 0 IL warnings, all checks pass natively (`Tarol.Security.AotSmoke` 10/10) |

## 15. Implementation: PR 10B (2026-11-15)

`Tarol.Changes.AspNetCore` (+ `Tarol.Changes.AspNetCore.Generators`), `Tarol.Security.Zitadel` and the sample, stacked on
PR 10A (#28). Package guides: `src/Changes/Tarol.Changes.AspNetCore/README.md`, `src/Security/Tarol.Security.Zitadel/README.md`;
user guide: [docs/changes/README.md](../changes/README.md) §8.

### 15.1 Public API

* **Tarol.Changes.AspNetCore**:
  * `EndpointAttribute(route) { Policy, Anonymous }`, `WebhookAttribute(sender, type) { Version }`, `IView<TAggregate, TSelf>`;
  * `TarolEndpointMetadata`, `RouteValue`;
  * `ChangeEndpointOptions` (`Json`, `SubjectClaim`, `ClientClaim`, `TenantClaim`, `Actor`, `Tenant`, `MaxBody`,
    `UseProblemDetailsService`);
  * `ChangeEndpoints` (`Edge`, `ReadBytes`, `Write`, `Problem`, `JsonPath`, `Load<T>`, `WriteView`; binder helpers
    hidden from IntelliSense);
  * `WebhookOptions`, `WebhookSender`, `WebhookDelivery`, `Webhooks` (`Read`, `KeyOf`, `Answer`);
  * `TarolBuilder.AddEndpoints(configure)`.
  * Generated per assembly: `GeneratedEndpoints.MapChanges(IEndpointRouteBuilder, string? names)`, and
    `GeneratedOpenApi.AddTarolEndpoints(OpenApiOptions)` when the app references `Microsoft.AspNetCore.OpenApi`.
* **Tarol.Security.Zitadel**: `ZitadelOptions` (`Authority`, `ProjectId`, `RequireHttpsMetadata`, `ServiceRole`),
  `ZitadelClaimsTransformation`, `ZitadelTenants` (`Of`, `Actors`), `TarolBuilder.AddZitadel(configure)`.
* **Tarol.Validations** (analyzer): the TVAL006 exemption of entry factories (C-13) also covers
  `Tarol.Changes.AspNetCore.WebhookAttribute`.

### 15.2 Deliberate differences from §7, §10 and the probe

* **The binder reports `[JsonPropertyName]` names**; value-rule errors (`Validate`) still carry C# member paths, converted
  by the naming policy (the Validations generator knows no JSON names). Stated in the skill.
* **`MapChanges` resolves `ChangeEndpoints` at mapping time**, so a missing `AddEndpoints`/`AddSecurity` fails at startup,
  not at the first request (C-11).
* **OpenAPI without a package dependency**: the transformers are generated into the application only when it references
  `Microsoft.AspNetCore.OpenApi`; route members are removed from the input's body schema there.
* **Webhook payloads are classes** (`Payload<T> where T : class`); records are the contract records.
* **The sample provisions Zitadel with a C# project** (`ZitadelSetup`), not the probe's Python script. Its API adds the
  HTTP entries to the worker's changes as partial declarations, so both share one domain.
* **`AddZitadel` comes after `AddEndpoints`** (it sets the actor rule on the options instance; it throws otherwise).

### 15.3 Found while building

1. **Shared sources across assemblies need the generated namespace**: the generated types live in a namespace named after
   the assembly, so the integration tests, the AOT smoke app and the package consumer that link the endpoint tests'
   domain or program add a project-level `<Using>` for their own namespace.
2. **Microsoft.OpenApi 3.x** types its collections with interfaces (`IOpenApiMediaType`, `IOpenApiHeader`,
   `IOpenApiParameter`); `Minimum`/`Maximum` are strings.
3. **A `WebApplication` keeps its endpoints in `IEndpointRouteBuilder.DataSources`**: the `EndpointDataSource` service is
   empty before the app runs (the skill test).
4. **Zitadel's first instance answers 503 for a few seconds after `/debug/ready`**; provisioning retries.

### 15.4 Tests **[verified]**

* `Tarol.Changes.AspNetCore.Tests` (37, TestHost): endpoints (22), webhooks (8), edge (6: hand-written problem details,
  actor/tenant hooks, a malformed tenant, the OpenAPI document, `JsonPath`, startup without security), the skill example
  verbatim.
* `Tarol.Changes.AspNetCore.Generators.Tests` (16): snapshots with and without OpenAPI; the generated code compiles
  without warnings together with the Primitives, Changes and Validations generators; nothing without entries;
  TWEB001–TWEB006 with their messages; incrementality (an unrelated file, an `Execute` body).
* `Tarol.Changes.AspNetCore.IntegrationTests` (3, PostgreSQL): keys with fingerprints, `If-Match` and the view,
  `traceparent` into the event (CI `integration` job).
* `Tarol.Security.Zitadel.Tests` (9): the transformation on a token shape as Zitadel issued it, idempotence, other
  projects, anonymous, the stable tenant, the actor rule, `AddZitadel` options and its preconditions, the skill example verbatim.
* `Tarol.Security.Zitadel.IntegrationTests` (2, Zitadel v4.20.3 in Testcontainers, R-17): roles, tenant, row rule, no-role
  403, service actor, foreign audience and tampered token 401 (CI `integration` job; the runner pulls from ghcr.io).
* Validations (+1): TVAL006 exempt in a `[Webhook]` factory of the HTTP entries, not in a foreign attribute.
* AOT smoke apps: `Tarol.Changes.AspNetCore.AotSmoke` (Kestrel, 12 checks over HTTP, in memory and on PostgreSQL) and
  `Tarol.Security.Zitadel.AotSmoke` (5 checks); package consumers for both.
* `samples/Sagas` with Zitadel: §4.5's run, JIT and native (README there).

### 15.5 Suppressions and analyzer configuration

None.

### 15.6 Skill evaluation checklists

`tarol-endpoints`: as §11.2. `tarol-zitadel`:

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| authenticate with Zitadel | `.AddEndpoints().AddZitadel(z => { z.Authority = …; z.ProjectId = …; })` | own JWT validation; parsing the role claim by hand |
| roles for the role map | project roles granted in Zitadel, `AddSecurity(roles => roles.Role("seller")…)` | roles of another project |
| a service account | machine user with the `service` role | expecting `Actor.Service` without the role |
| a local container | `RequireHttpsMetadata = false`, a fixed issuer port | a proxied random port (issuer mismatch) |

### 15.7 Verification (SDK 11.0.100, 2026-11-15)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` (Docker) | 1049 total, 0 failed (981 after 10A + 68) |
| Release reruns | endpoint + Npgsql tests 20/20, Zitadel integration 3/3 |
| `dotnet pack Tarol.slnx -c Release` + `eng/verify-packages.sh` | OK: 13 packages; `Tarol.Changes.AspNetCore` holds `lib/` (+ XML docs), `analyzers/dotnet/cs/Tarol.Changes.AspNetCore.Generators.dll`, `README.md`, `LICENSE`, `skills/tarol-endpoints/SKILL.md`; `Tarol.Security.Zitadel` holds `skills/tarol-zitadel/SKILL.md`; both consumers pass and list the skills |
| `TAROL_PG=… eng/aot-smoke.sh` | 13 apps, each 0 IL warnings, all checks pass natively (the HTTP smoke app on PostgreSQL) |
| `samples/Sagas` AppHost with Zitadel | JIT and Native AOT: see the sample's README |
