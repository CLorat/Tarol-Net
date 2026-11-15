# Changes

Concept note for step 7 (see [migration/changes.md](../migration/changes.md)): a **change is the entry of an actor into
the domain to change something**. The user writes the change once: its input, the aggregate it targets and the
behaviour method it calls. Tarol runs every change through one pipeline and generates the *entry* from the kind of actor
and channel: an HTTP endpoint for a user or a service, a typed call for a job or a saga, a subscription for a reaction
to an own event, a signed endpoint for a webhook, and a consumer for another system's messages. It starts from first
principles; the old `Tarol.Changes` (`CLorat/Tarol@6a62f81`, marker interfaces only) is reviewed in the migration note.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/changes/`](../migration/probes/changes/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: core implemented** in step 7 (PR 7A `Tarol.Persistence`, PR 7B `Tarol.Changes`; [migration/changes.md](../migration/changes.md) §14; owner decisions C-1 … C-16 in §9). Step 7 builds the
core and the internal entry; the other entries are built in steps 9, 10 and 11 (C-1, C-2).

## 1. What a user of Tarol needs from a change

| Who acts | Channel | Entry | Built in |
|---|---|---|---|
| a user (`Actor.User`) | REST JSON | HTTP endpoint: route, JSON body, authentication, problem details | 10 |
| another service (`Actor.Service`) | REST, client credentials | the same endpoint, another policy | 10 |
| the system: job, timeout, saga (`Actor.System(component, onBehalfOf)`) | in process | a typed call, no HTTP, no serialization | **7** |
| a reaction to an event of this system | in process, after commit | subscription: event → change, outbox, at least once | 9 |
| an external system's webhook | HTTP from outside | endpoint with signature check, delivery-id idempotency, payload → input | 10 |
| another system's domain or integration events | message broker | consumer with an inbox, contract versions, ordering | 11 |

The ArtFlow use cases ([migration/changes.md](../migration/changes.md) §3): **create an auction** and **close it**
(user, REST), **settle after the end time** (background job), **mark the payout due** (reaction to `Bid.Won`),
**record a payment** (webhook from the payment provider).

## 2. One pipeline for every entry

```text
input ─► Validated()            400 Invalid, all fields, before any read            library   (validation.md S-4)
      ─► key seen?              redelivery → the stored outcome, nothing runs         library + store
      ─► [Read] aggregates      422 X.NotFound when a referenced one is missing        generated LoadReads (C-16)
      ─► load (tenant-scoped)   automatic by Target: 404, also for another tenant;    store port (step 8)
                                special changes: their own ILoads<T>.Load (C-16)
      ─► permission             401/403 (404 for another tenant: never reaches here)  hook (step 10)
      ─► Execute(aggregate, context with now)   guards → 409 / 400                    user code: one behaviour call
      ─► Prepare                invariants, forbidden lifecycle step                  Primitives
      ─► append(expected version, key)   409 Stream.VersionConflict                    store port
      ─► Committed              → ChangeOutcome(stream, version, events)              Primitives
```

* **Library, not generated** **[verified]**: `ChangePipeline.Run<T>(IChange<T>, OperationContext, ChangeOptions, ct)`
  is generic over the aggregate and calls `IChange<T>.Execute` through the interface; `T.Rehydrate`/`T.ReadState` are
  static abstracts the store calls. No reflection, so it publishes with 0 IL warnings and runs natively.
* **Generated**: only what needs a list of the user's types: `Target` per change, the typed `Dispatch(object)` switch
  for outbox rows, the reaction table, and the entries (endpoints, webhook and consumer switches).
* **Discard on any failure** (domain-core.md §2): a failed guard, a failed `Prepare` or a throw leaves the loaded
  instance unreferenced; nothing is appended **[verified]**. The pipeline never repairs an instance.
* **One behaviour per change**: `Execute` calls one public behaviour method; `Raise` stays protected
  (domain-core.md §9). A change that touches two aggregates is not a change: it is a saga (step 9) or a reaction.
* **`now` is read once**, from the injected `TimeProvider`, and passed in `ChangeContext.Now` (time.md) **[verified]**.
* **Version conflicts are not retried** (C-7): the pipeline returns `Error.Conflict("Stream.VersionConflict")`; the
  caller (HTTP client, outbox worker, consumer) retries. The probe shows that a reload-and-decide retry would be safe;
  the owner chose the explicit answer.
* **Creation** (`ICreation<T>`): the factory runs without a load, generates the id (C-10), and the store appends with
  expected version 0.

**Loading is automatic** (C-16) **[verified]**: a change names its target by its `Id<T>` member and the pipeline loads
it; the user writes no load code. Only special changes load themselves (§3.1).

**No dependency on ASP.NET Core in the core**: the principal is a BCL `ClaimsPrincipal` in `ChangeOptions`, the
permission hook is an interface, the store is a port in `Tarol.Persistence` (C-6) **[verified]** (Core has only
Primitives and Validations references).

## 3. Declaring a change (C-4: an input record)

```csharp
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

public sealed partial record CreateAuction(Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Title, Reserve, EndsAt, context.Now);
}
```

The record is the input: the Validations generator writes its `Validate`, the app's JSON context serializes it, a saga
outbox row stores it, and a test builds it. The generator implements `IChange<T>.Target` when the record has exactly one
`Id<T>` member (TCHG001 otherwise) **[verified]**. Why not `[Change]` on the behaviour method: a record emitted by the
Changes generator is invisible to the Validations and JSON source generators (CS0535, SYSLIB1030) **[verified]**, so
the generator would have to re-implement both.

### 3.1 Special changes: their own load, extra reads (C-16) **[verified]**

```csharp
// another aggregate, read-only: [Read] → loaded before Execute, 422 "Artwork.NotFound" when missing, never written
public sealed partial record CreateAuctionOfArtwork([Read] Id<Artwork> Artwork, Title Title, Amount Reserve, DateTimeOffset EndsAt)
    : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.CreateFor(context.Read<Artwork>(), context.Operation.Actor.Id, Title, Reserve, EndsAt, context.Now);
}

// the target is found by another key: ILoads<T>
public sealed partial record CloseByLot(string Lot) : ILoads<Auction>
{
    public async ValueTask<Result<Auction>> Load(IAggregateReader reader, ChangeContext context, CancellationToken ct)
        => (await reader.Find<Auction>("lot", Lot, context.Operation, ct)).TryGetValue(out Id<Auction> id)
           && (await reader.Load(id, context.Operation, ct)).TryGetValue(out Auction? auction)
            ? auction : Error.NotFound("Auction.NotFound", $"lot {Lot}");
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

// load or create: Load may return a new aggregate with uncommitted events; the pipeline appends with expected version 0
public sealed partial record ApprovePayout(Id<Payout> Payout, Guid Auction, decimal Amount) : ILoads<Payout> { … }
```

* `ILoads<T> : IChange<T>`; its `Target` is not used, so the generator writes none and TCHG001 does not apply.
* Everything else stays the pipeline's: validation before any read, permission after the load, `Execute`, `Prepare`,
  append with expected version = loaded version − uncommitted events, discard on failure.
* `[Read]` aggregates are for the decision only: they are never appended, and their version is not checked (the read
  is as fresh as the load; a rule that must hold across both aggregates is a saga, not a change).
* `IAggregateReader` = `Load<T>(id)` + `Find<T>(lookup, key)` for unique lookups; how the Npgsql store backs a lookup
  (a column or an index on the stored state JSON) is step 8 **[unverified]**.
* A special change that needs a *service* (not an aggregate) still gets no injection: the edge resolves the data and
  puts it in the input, or the check becomes a guard parameter (validation.md §2).

## 4. Entries (C-5: attributes on the change)

```csharp
[Endpoint("POST /auctions/{auction}/close", Policy = "auctions:write")]       // REST (step 10)
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable { … }

public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]                                                         // reaction (step 9)
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);
    …
}

public sealed partial record RecordPayment(Id<Auction> Auction, Amount Amount, string PaymentId) : IChange<Auction>, IValidatable
{
    [Webhook("payments", "payment.succeeded")]                                 // webhook (step 10)
    public static RecordPayment From(PaymentSucceededV2 p) => new((Id<Auction>)p.Reference, new Amount(p.Amount), p.PaymentId);
    …
}
```

A change with no attribute has only the typed entry: `await pipeline.Run(new SettleAuction(id), context, ct: ct)`.
`From` factories are the anti-corruption layer: the foreign payload's names and types stay in its own record
(`PaymentSucceededV2`), mapped to ours in one expression; the pipeline validates the result. TVAL006 is silenced inside
such factories (C-13).

## 5. Context per channel

| Channel | Actor | Tenant | Correlation | Causation | Idempotency key |
|---|---|---|---|---|---|
| REST, user | `Actor.User(sub)` from the `ClaimsPrincipal` | claim, validated against the route; else `None` | new, server-side (tracing.md insight 2) | `None` | `Idempotency-Key` header, scoped to (tenant, caller, route) |
| REST, service | `Actor.Service(client_id)` | from the token or `None` | new | `None` | header |
| job / timeout | `Actor.System("Jobs.AuctionSettlement")` | per run | new (`OperationContext.Start`) | `None` | optional |
| saga step | `Actor.System(saga, onBehalfOf)` (`AsSystem`) | the saga's | the saga's | the handled message | outbox row id (step 9) |
| reaction | `recorded.Context.AsSystem("Reactions." + name)` | the event's | the event's | **the event id** | (reaction, event id) |
| webhook | `Actor.Service("webhook:" + sender)` | mapped from the payload or route | new | `None` | (sender, delivery id) |
| consumer | `Actor.Service(source system)` or the envelope's actor | envelope header | envelope header | the message id | (contract, message id) |

Reaction and webhook rows **[verified]** (`App/`, `Web/`); the others **[unverified]**. The context is a **parameter**
of `Run` (C-11): built at the edge, never ambient (identity-context.md §4).

## 6. Outcomes per channel

**HTTP** (step 10, generated `RequestDelegate`s, C-12) **[verified]** natively:

| Outcome | Status | Body |
|---|---|---|
| change committed | 204, `ETag: "<version>"` | — |
| creation committed / replayed key | 201 / 200 | `{"id":…,"version":…}` |
| `Invalid` (value rules, malformed JSON, bad route id, guard on an input field) | 400 | problem details, `errors` keyed by **JSON-cased input paths** (`reserve`, `endsAt`, `auction`) |
| not authenticated (endpoint policy) | 401 | — |
| `Error.Forbidden` (permission, step 10) | 403 | problem details |
| `Error.NotFound` (missing, or another tenant) | 404 | problem details |
| `Error.Conflict` (guard, lifecycle, `Stream.VersionConflict` for a stale `If-Match`) | 409 | `code` = the guard's code |
| body not JSON | 415 | (routing, from the generated `AcceptsMetadata`) |
| other `Error` types | `ToHttpStatusCode()` | problem details |

**Internal**: the typed `Result<ChangeOutcome>` itself.

**Reactions, webhooks, consumers** (at least once, C-9):

| Pipeline answer | Delivery | Webhook HTTP |
|---|---|---|
| success, or a replayed key | acknowledge | 200 |
| `Invalid` | dead-letter (mapping bug or contract change) | 422 |
| **any `Error.Conflict`** (guard or version) | **retry with backoff, dead-letter after N** | 503 |
| `NotFound` | foreign: retry (ordering); own event: dead-letter. **Reactions: retry, then dead-letter** (S-7, [sagas.md](sagas.md) §3: the target may be created by another reaction) | 503 / 422 |
| `Forbidden`/`Unauthorized` | dead-letter | 422 |
| `Unavailable`/`Timeout`/`TooManyRequests`, an exception | retry, then dead-letter | 503 |
| unhandled webhook type | — | 202 (no retry storm) |

## 7. Delivery guarantees, and which step owns them

| Guarantee | Mechanism | Step |
|---|---|---|
| no lost update | expected version on append; conflict → 409, the caller retries (C-7) | 7 (port), 8 (SQL) |
| a retried request does not commit twice | idempotency key stored **with the append, same transaction**, successes only (C-8) | 7 (port + in-memory), 8 (table, retention) |
| a reaction runs after its event is durable | outbox row per (event, reaction) written in the commit's transaction (`ReactionsTo(event)`) | 8 (table), 9 (worker) |
| a reaction runs at least once, effect once | key (reaction, event id) | 9 |
| foreign messages are deduplicated | inbox = the same key table, key (contract, message id) | 11 |
| order per stream | one aggregate's events are committed in version order; the outbox claim returns only the head row per (stream, reaction) (P-10) **[verified]** in the step 8 probe | 8/9 |
| order across systems | not guaranteed; `NotFound` for a foreign target retries | 11 |

## 8. Webhooks and foreign events

* **Raw body first**: the signature covers the exact bytes, so the entry reads the body into a buffer (size-limited),
  checks `HMAC-SHA256(secret, timestamp + "." + body)` with `CryptographicOperations.FixedTimeEquals`, rejects a
  timestamp outside ±5 minutes (replay window), and only then parses JSON **[verified]**: forged signature, tampered body
  and stale timestamp → 401.
* **Envelope → payload → input**: `{id, type, data}` is read with `Utf8JsonReader`; `type` selects the `[Webhook]`
  factory; `data` is deserialized through the app's context into the provider's own record; `From` maps it.
* **Versions**: one payload record per contract version (`PaymentSucceededV2`), one `From` overload or attribute per
  version; an unknown type is acknowledged (202) **[verified]**; an unknown *version* of a known type is dead-lettered
  (422) so a human sees the contract change **[unverified]**.
* **Consumers** (step 11) reuse the same shape with `[Consumes("billing.invoice-paid", 2)]` and an inbox key.

## 9. AOT and packages

| Package | Contents | Step |
|---|---|---|
| `Tarol.Persistence` | `IAggregateStore` (load, append with expected version and key, seen), `IdempotencyKey`, `InMemoryAggregateStore` | 7 (C-6) |
| `Tarol.Changes` (+ generator) | `IChange<T>`, `ICreation<T>`, `ChangeContext`, `ChangeOutcome`, `ChangeOptions`, `ChangePipeline`, `IChangeAuthorization`, entry attributes, `ChangeTest`; generator: `Target`, `Dispatch`, TCHG diagnostics | 7 |
| `Tarol.Persistence.Npgsql` | the store, the key table, the outbox table | 8 |
| `Tarol.Sagas` | outbox worker, `DeliveryPolicy`, hosting, `ReactionTest`; the reaction table and `React` are generated by the Changes generator (S-6, [sagas.md](sagas.md) §9) | 9 |
| `Tarol.Security`, `Tarol.Changes.AspNetCore` (+ generator) | `IChangeAuthorization` via `IAuthorizationService`; endpoints, webhooks, problem details | 10 |
| `Tarol.Messaging` | consumers, inbox, contract versions | 11 |

* **Endpoints are generated `RequestDelegate`s** (C-12) **[verified]**: 0 IL warnings, native self-test 24/24. The
  Request Delegate Generator does **not** see calls in generated code: a generated `MapPost(pattern, lambda)` gives
  IL2026/IL3050 at publish and the native app aborts at startup (`NotSupportedException` for `Task`) **[verified]**.
  A generic library `MapPost(pattern, Delegate)` gives RDG011 + IL2026/IL3050 **[verified]**.
* A generated `(HttpContext) => Task` lambda binds to the `RequestDelegate` overload and is fine **[verified]**.
* Problem details are written with `Utf8JsonWriter` (no `ProblemDetails` metadata; security.md insight 6), through
  `IProblemDetailsService` when the app registers one (C-12).

## 10. Testing

| Level | Tool | Covers |
|---|---|---|
| behaviour | `AggregateTest.Given(…).When(a => a.Close(now))` (Primitives) | guards, events, invariants, replay |
| change | `ChangeTest.Given(aggregate).At(time).When(change, actor)` (C-14) | the real pipeline on `InMemoryAggregateStore` with a fake `TimeProvider`: outcome, committed events, recorded context |
| endpoint | `WebApplicationFactory` (step 10) | status codes, problem details, headers |
| generator | snapshots | `Target` part, `Dispatch`, endpoint shapes (route only, route + body, creation), webhook switch, every diagnostic; incrementality |

## 11. What Changes owns, and what it does not

Owns: the pipeline order, the input record contract, the entries, the mapping of outcomes per channel. Does not own:
guards and the lifecycle graph (Primitives, state-transitions.md §10), value rules (Validations), the permission policy
(Security), the SQL (Persistence), the outbox worker and saga state (Sagas), brokers (Messaging).

## New insights

1. **The pipeline needs no generator.** C# static abstracts (`T.Rehydrate`) and one interface call (`Execute`) make a
   generic library pipeline AOT-safe; generated code is only needed where a list of the user's types is needed.
2. **Generators do not compose.** An input record generated from a method signature gets neither `Validate` nor JSON
   metadata; the RDG does not intercept generated `MapPost` calls. Whatever the user should get from other generators
   must be written by the user (the record) or generated without them (`RequestDelegate`s).
3. **Post-initialization output is the exception**: other generators do see it, but it cannot depend on user code.
4. **The guard decides retries, not the error type.** A reload-and-retry re-runs the guards, so it can never lose an
   update; the probe's second race ended in `Bid.TooLow`, not in a silent overwrite.
5. **A reaction is a change with a different context**: the same pipeline, `AsSystem(...)`, causation = event id,
   key = (reaction, event id). Redelivery returns the stored outcome without running.
6. **Idempotency keys and the inbox are one mechanism**, stored with the append in one transaction.
7. **Generated names must be deterministic**: `string.GetHashCode` is randomized per process; the probe first named a
   generated field with it.

## Decisions taken (owner, 2026-11-14; migration/changes.md §9)

C-1 core + internal entry in step 7; C-2 reactions 9, REST/webhooks 10 with Security, consumers 11; C-3 Changes is the
only write model, old CQRS not migrated; C-4 input record; C-5 attributes on the change; C-6 `Tarol.Persistence` port
in 7; C-7 never retry a version conflict in the pipeline; C-8 keys store successes with the append; C-9 any Conflict
in a reaction retries, then dead-letters; C-10 factories generate ids; C-11 context as a parameter, no permission
checks until step 10, REST fails closed; C-12 generated `RequestDelegate` endpoints; C-13 TVAL006 exempt in entry
factories; C-14 `ChangeTest`; C-15 two PRs; C-16 the pipeline loads the target automatically, special changes use
`ILoads<T>` (own load) and `[Read]` (read-only aggregates).

## Open questions

* Retention of idempotency keys: decided in step 8 (P-11, [persistence.md](persistence.md) §7): `PurgeKeys`, 7 days default.
* Per-stream ordering of outbox rows across several workers: decided in step 8 (P-10, [persistence.md](persistence.md) §8).
* Unknown versions of a known webhook type: dead-letter (proposed) or acknowledge? Step 10.
* The read side (queries, projections) after C-3: a later decision.
