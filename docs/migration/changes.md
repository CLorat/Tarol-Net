# Changes: evaluation (step 7 design)

Evaluation of [changes.md](../architecture/changes.md): inventory of the old `Tarol.Changes`, three candidates for how a
change and its entries are declared, the pipeline, the context and outcome per channel, delivery guarantees, AOT, and
the plan impact. **Design only**: this PR changes no package code. Probes and output:
[`probes/changes/`](probes/changes/), [`output.txt`](probes/changes/output.txt). Labels as in the
[conventions](../architecture/conventions.md). Source of the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old `Tarol.Changes` is ≈ 5 % of a feature** **[verified by reading]**: eight marker types, no generator
  (`gen/Changes` does not exist), no tests, two dead sibling projects. Nothing in it is ported; every name is replaced.
* **Decided shape** (C-4, C-5): an input record implements `IChange<T>` or `ICreation<T>` and calls one behaviour in
  `Execute`; entries are attributes on the record (`[Endpoint]`) or on a static `From` factory (`[OnEvent<T>]`,
  `[Webhook]`, `[Consumes]`). A change without an attribute has the typed in-process entry.
* **Loading is automatic** (C-16): the pipeline loads the target named by the record's `Id<T>`; special changes load it
  themselves (`ILoads<T>`), and read-only other aggregates are `[Read] Id<X>` members **[verified]**.
* **The pipeline is library code** (generic, no reflection); the generator adds `Target`, a typed `Dispatch(object)`, the
  reaction table and the entries. Under Native AOT: **0 IL warnings**, all scenarios pass natively **[verified]**.
* **Cost**: 252 ns / 920 B hand-written vs 395 ns / 976 B through the pipeline (+≈ 145 ns, +56 B per change, both
  including a one-event replay) **[verified]** (`Bench/`, short run). Against one database round trip this is noise.
* **HTTP entries must be generated `RequestDelegate`s** (C-12): the Request Delegate Generator does not see generated
  `MapPost` calls; those publish with IL2026/IL3050 and the native app aborts at startup **[verified]**.
* **Candidate B (`[Change]` on the behaviour method) is blocked**: its generated input record gets neither `Validate`
  (CS0535) nor JSON metadata (SYSLIB1030) **[verified]**.
* **Plan** (C-1, C-2, C-15): step 7 = `Tarol.Persistence` (store port + in-memory store) and `Tarol.Changes` (core +
  internal entry), two PRs; reactions with sagas (9); REST and webhooks with Security (10); consumers (11). Old CQRS
  is not migrated (C-3).

## 1. Inventory of the old code

### 1.1 Public API **[verified by reading]** (`src/Changes/*` at `6a62f81`)

| Old type | File | What it was meant for | New (decided) |
|---|---|---|---|
| `IChange` | `Changes/Change.cs:5` | marker, unrelated to the generic one (finding 15) | — (not needed) |
| `IChange<TAggregateRoot> where TAggregateRoot : IAggregateRoot` | `Change.cs:7` | a change of an existing aggregate; non-generic constraint, so no `T.Rehydrate` (finding 15) | `IChange<T> where T : AggregateRoot<T>, IAggregateRoot<T>` with `Target` and `Execute` |
| `IChangeCreate<TAggregateRoot>` | `Change.cs:9` | a creation; not an `IChange<T>` (finding 15) | `ICreation<T>` with `Execute(ChangeContext) : Result<T>` |
| `IApplysEvent<TDomainEvent> { TDomainEvent Event { get; } }` | `IApplysEvent.cs:5` | the event a change raises; typo (finding 26); `Event => new(…)` re-creates on every read (finding 14) | **removed**: the change calls a behaviour method, which raises (domain-core.md §9). Finding 1 ("no path to `Raise`") and finding 14 disappear |
| `IApplysEventWhen<T> { bool When(); }` | `IApplysEvent.cs:10` | a gate | **removed**: guards return `Result` inside the behaviour |
| `IHttpInvoke` | `Triggers/IHttpInvoke.cs` | "expose over HTTP" | `[Endpoint("POST …")]` |
| `IReactsTo<TDomainEvent>` | `Triggers/IReactsTo.cs` | "run when this event happens" | `[OnEvent<T>]` on a `From` factory |
| `ChangeAttribute(displayName, asApi, createsEntity)` | `CreateAttribute.cs` | commented out | — |
| `Changes.Aggregates.Change<TAggregate>(Id)` | `Changes.Aggregates/Class1.cs` | leftover, no `Tarol.` namespace (finding 27) | not migrated (plan) |
| `Changes.DomainEvent.RaisesEvent<T>`, `RaisesEvent` | `Changes.DomainEvent/RaisesEvent.cs` | unused attributes (finding 27) | not migrated (plan) |

`Changes.csproj` references Kernel, Primitives and Results, of which only Primitives is used (finding 30).

### 1.2 Generator and tests **[verified]**

* **No generator**: `gen/` has `CQRS`, `Messaging`, `Primitives`, `Sagas`, `StronglyTypedId`, `Validations`; no
  `Changes` (the session prompt's `gen/Changes/*` does not exist). `docs/review/validations-and-changes.md` §2.2 says the
  same.
* **No tests**: `tests/` has no Changes folder. `Changes.csproj` builds on SDK 11.0.100 with 1 warning (MSB9008: the
  StronglyTypedId generator path `../generator/…` does not exist).

### 1.3 Consumers **[verified by reading]** (grep over `src/`, `tests/`, `examples/`, `templates/`)

| Consumer | Uses | Fate |
|---|---|---|
| ArtFlow `Auctions/Changes/{Bid,Close,Create}` | `abstract record Bid(Auction Auction, …) : IChange<Auction>, IHttpInvoke`; `Close : IApplysEvent<AuctionClosed> { Event { get; } = new(); }`; `Create : IChangeCreate<Auction>, IApplysEvent<AuctionCreated> { Event => new(Auction, …) }` | rewritten in step 13 as §2 (A). The records hold the **aggregate instance**, which a JSON body, an outbox row or a message cannot carry (`Abstractions.cs:126` says so) |
| ArtFlow `ArtWorks/Changes/{Create,Verify}` | `Verify : IChange<ArtWork>, IApplysEvent<ArtworkVerified>, IReactsTo<ArtworkCreated>` | `[OnEvent<ArtWork.Created>]` factory |
| ArtFlow prerequisites (8 classes) | `IPrerequisite(Async)<TChange>` returning `bool`, `TimeProvider` injected, `Order` | guards with `now` in the behaviour (validations.md §5, done in step 6); `NotVerified` is inverted (finding 17) |
| `Tarol.Sagas.Abstractions.IChangeDispatcher.DispatchAsync(object, ChangeContext, ct)` | the saga outbox runs a change; implemented by the application | generated `Dispatch(object)` switch **[verified]**; `ChangeContext(MessageId, CorrelationId, CausationId, SagaType, SagaId)` becomes the `OperationContext` + key (step 9) |
| `SagaStep.Execute(change).CompensateWith(change)` | plans changes | step 9 |
| `SagaOutboxProcessor` | exception → retry with backoff, dead-letter after `MaxAttempts`; a failed `Result` → `ChangeFailed<T>` delivered back to the saga, no retry **[verified by reading]** (`SagaOutboxProcessor.cs:30-120`) | C-9 changes this for reactions: a Conflict retries |
| `FakeChangeDispatcher` (`TestUtility/Sagas.Testing`) | records dispatched changes, scripted results | replaced by `ChangeTest` and the in-memory store |
| `Primitives.UnitTests.csproj:34`, `Persistence.Marten.IntegrationTests.csproj:36` | project reference, no use (finding 30) | — |
| `templates/*` | none | — |

### 1.4 Old tests rerun on SDK 11.0.100 **[verified]**

| Project | Result | Finding |
|---|---|---|
| Changes (no test project) | — | nothing tests Changes |
| `tests/CQRS/CQRS.UnitTests` | builds (1 warning MSB9008); **0 tests**, exit code 8 "No tests ran" | review C-finding "runs 0 tests" still true; the dispatcher is untested |
| `tests/Sagas/Sagas.Tests` | **79/79 passed**; 7 warnings (MSB9008 ×2, NU1510 ×6 for packages the shared framework already provides) | the change bridge is covered only through `FakeChangeDispatcher` and `ChangeFailed<T>` tests (`RegistryAndSerializationTests.cs:164,235`, `Engine/DeliveryTests.cs:186`) |

### 1.5 Old review findings re-verified

| Finding (`docs/review/validations-and-changes.md`) | Now |
|---|---|
| 1 no path from a change to `Raise` | answered by behaviour methods (domain-core.md §9); `Execute` calls one |
| 14 `Event => new(…)` re-creates on read | disappears with `IApplysEvent` |
| 15 unrelated `IChange` interfaces, non-generic constraint | `IChange<T>`/`ICreation<T>` with the `AggregateRoot<T>, IAggregateRoot<T>` constraint; the pipeline is generic over `T` **[verified]** |
| 26 typo `IApplysEvent` | disappears |
| 27, 30 dead projects, unused references | not migrated |
| 31 prerequisites return `bool` | guards return `Result` (step 6) |
| AOT hazard: `IChangeDispatcher(object)` needs runtime dispatch | generated `switch` **[verified]** |
| AOT hazard: HTTP endpoints need reflection-based binding | generated `RequestDelegate`s, 0 IL warnings **[verified]**; RDG is not usable from generated code **[verified]** |
| AOT hazard: `IReactsTo` needs reflection | generated reaction table **[verified]** |
| CQRS C1–C15 (`docs/review/cqrs.md`) | not migrated (C-3) |

## 2. Candidates (questions 1 and 2), as ArtFlow user code

The domain is the same for every candidate (`probes/changes/App/Domain.cs`): `Auction` with `EntitySet<Bid>`,
`[Lifecycle] AuctionStatus { Open → Closed → Settled → Paid }`, `Bid` with `Won`/`Lost`, and `Payout`. Behaviours:
`Auction.Create(…, now)`, `PlaceBid`, `Close(now)`, `Settle(now)`, `RecordPayment(amount, reference)`, `Payout.MarkDue`.

### (A) An input record per change (decided, C-4)

Shown in the decided shape. The probe (`App/Changes.cs`) ran the same code **[verified]**, except that before C-10
`CreateAuction` and `MarkPayoutDue` carried the new id (`Id<Auction> Auction`, `(Id<Payout>)recorded.StreamId`).

```csharp
// create an auction (user, REST)
[Endpoint("POST /auctions", Policy = "auctions:write")]
public sealed partial record CreateAuction(Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Title, Reserve, EndsAt, context.Now);   // C-10: the factory makes the id
}

// close an auction (user, REST): 400 invalid, 404 missing, 409 already closed
[Endpoint("POST /auctions/{auction}/close", Policy = "auctions:write")]
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

// settle after the end time (background job): no attribute, typed call
public sealed partial record SettleAuction(Id<Auction> Auction) : IChange<Auction>
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Settle(context.Now);
}
// in the job:
await pipeline.Run(new SettleAuction(id), OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, time), ct: ct);

// mark the payout due (reaction to Bid.Won)
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);
    public Result<Payout> Execute(ChangeContext context) => Payout.MarkDue(Auction, Amount, context.Now);
}

// record a payment (webhook)
public sealed record PaymentSucceededV2(string PaymentId, Guid Reference, decimal Amount, string Currency);   // their contract
public sealed partial record RecordPayment(Id<Auction> Auction, Amount Amount, string PaymentId) : IChange<Auction>, IValidatable
{
    [Webhook("payments", "payment.succeeded")]
    public static RecordPayment From(PaymentSucceededV2 p) => new((Id<Auction>)p.Reference, new Amount(p.Amount), p.PaymentId);
    public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Amount, PaymentId);
}
```

Generated and library code (prototype output, `output.txt` GenTest section; endpoints from `Web/obj/…/Changes.Endpoints.g.cs`):

```csharp
partial record CloseAuction        // per change
{
    Id<Auction> IChange<Auction>.Target => Auction;
}

public static class GeneratedChanges   // per assembly
{
    public static ValueTask<Result<ChangeOutcome>> Dispatch(this ChangePipeline p, object change, OperationContext c, …)
        => change switch { CloseAuction x => p.Run(x, c, o, ct), …, _ => throw new ArgumentException(…) };
    public static IReadOnlyList<string> ReactionsTo(DomainEvent e) => e switch { Bid.Won => __reactions0, _ => [] };
    public static ValueTask<Result<ChangeOutcome>> React(this ChangePipeline p, string reaction, RecordedEvent r, …)
    {
        var context = r.Context.AsSystem("Reactions." + reaction).CausedBy((Id<ICausation>)r.EventId);
        var key = new IdempotencyKey(r.Context.Tenant, reaction, r.EventId.ToString("N"));
        return (reaction, r.Event.Event) switch { ("MarkPayoutDue", Bid.Won e) => p.Run(MarkPayoutDue.From(e, r), context, new(Key: key), ct), … };
    }
}

// step 10 (Tarol.Changes.AspNetCore): one RequestDelegate per [Endpoint]
app.MapPost("/auctions/{auction}/close", (RequestDelegate)Run_CloseAuction).RequireAuthorization("auctions:write")
   .WithMetadata(new AcceptsMetadata(["application/json"], typeof(CloseAuction), isOptional: true));
static async Task Run_CloseAuction(HttpContext http)
{
    if (!Id<Auction>.TryParse(http.Request.RouteValues["auction"] as string, null, out var auction))
    { await ChangeHttp.Write(http, Invalid.Field("Auction", "Format"), json); return; }
    var (context, options) = ChangeHttp.Context(http);          // actor from ClaimsPrincipal, tenant claim, Idempotency-Key, If-Match
    var result = await pipeline.Run(new CloseAuction(auction), context, options, http.RequestAborted);
    await ChangeHttp.Write(http, result, json);                 // 204 + ETag | problem details
}
```

### (B) `[Change]` on the behaviour method (comparison)

```csharp
public partial class Auction : AggregateRoot<Auction>
{
    [Change, Creates, Endpoint("POST /auctions")]
    public static Result<Auction> Create(string seller, Title title, Amount reserve, DateTimeOffset endsAt, DateTimeOffset now) => …;
    [Change, Endpoint("POST /auctions/{id}/close")]
    public Result Close(DateTimeOffset now) => …;                 // `now` recognized by name and type
    [Change]                                                      // job: await changes.Auction.Settle(id, context)
    public Result Settle(DateTimeOffset now) => …;
    [Change, Webhook("payments", "payment.succeeded", Map = typeof(PaymentMapping))]
    public Result RecordPayment(Amount amount, string reference) => …;
}
// reaction: a method on Payout plus a mapping attribute naming Bid.Won and how its fields become parameters
```

Generated: `record Auction.CloseInput(Id<Auction> Id)` etc., the pipeline per method, entries. **Blocked**
**[verified]** (`GenVisibility/`): a record emitted by this generator is invisible to the Validations generator
(`CS0535: 'RenameAuction' does not implement interface member 'IValidatable.Validate(ref ValidationErrors)'`) and to the
JSON source generator (`SYSLIB1030: Did not generate serialization metadata for type 'RenameAuction'`). The Changes
generator would have to emit both itself. Further costs **[unverified]**: the actor and the reaction's event are no
parameters of a behaviour, so reactions and webhooks need side mappings; business-time parameters (`endsAt`) and `now`
are told apart only by name; the domain class carries transport attributes.

### (C) A central map, no generator in the core (comparison) **[verified]** for the endpoint part

```csharp
changes.For<Auction>()
    .Create<CreateAuction>((c, ctx) => Auction.Create(ctx.Operation.Actor.Id, c.Title, c.Reserve, c.EndsAt, ctx.Now))
    .Change<CloseAuction>(c => c.Auction, (a, c, ctx) => a.Close(ctx.Now))
    .Change<SettleAuction>(c => c.Auction, (a, c, ctx) => a.Settle(ctx.Now))
    .Change<RecordPayment>(c => c.Auction, (a, c, ctx) => a.RecordPayment(c.Amount, c.PaymentId));
changes.For<Payout>().OnEvent<Bid.Won, MarkPayoutDue>((e, r) => new(r.StreamId, e.Amount), (c, ctx) => Payout.MarkDue(c.Auction, c.Amount, ctx.Now));

app.MapChange("/auctions/{auction}/close", http => new CloseAuction(Id<Auction>.Parse((string)http.Request.RouteValues["auction"]!, null)));
app.MapWebhook<PaymentSucceededV2, RecordPayment>("payments", "payment.succeeded", AppJson.Default.PaymentSucceededV2, RecordPayment.From);
```

Library code only: a dictionary from input type to delegates, and `MapChange<T>` building a `RequestDelegate` (0 IL
warnings **[verified]**, `CentralMap.MapChange` in `Web/Program.cs`). Every route binding is a hand-written lambda; an
input with a route value and a body needs both read by hand; the map can drift from the input types (a change
registered twice or never is a run-time error); a generic `MapPost(pattern, Delegate)` instead is RDG011 + IL2026/IL3050
**[verified]**.

### Comparison

| | A input record | B `[Change]` on the method | C central map |
|---|---|---|---|
| user code per change | 4–6 lines, one file | 1 attribute | 1–2 lines in the map + the input record |
| validation and JSON of the input | the existing generators **[verified]** | must be re-implemented **[verified]** | the existing generators |
| reactions, webhooks | a `From` factory next to the input | side mappings | lambdas in the map |
| pipeline | library, generic **[verified]** | generated per method | library |
| what an agent finds | the record, its attribute, its `Execute` | the method | the map file |
| compile-time checks | TCHG diagnostics on the record | on the method | none (run-time registration) |

Entry declaration (question 1, second part): per change (attributes, decided C-5), by convention (routes from names;
breaks on a rename), or in a central map (C above).

## 3. The scenarios as probed (candidate A) **[verified]**

From `output.txt` (App: JIT and native; Web: native self-test):

| Scenario | Result |
|---|---|
| invalid input (`Title "x"`, `Amount 0`) | `Invalid { Title: Length; Reserve: Range }` before any load; HTTP 400 `{"errors":{"title":["Length"],"reserve":["Range"]}}` |
| `Id<T>.Empty` target | `Invalid { Auction: Required }`, no load |
| factory guard on an input field | `Invalid { EndsAt: TooSoon }`; HTTP 400 `endsAt` |
| close twice | `Error { Conflict, AuctionStatus.NotAllowed }` (the generated lifecycle guard); HTTP 409 with `code` |
| missing / another tenant | `NotFound`; HTTP 404 for both |
| settle before / after the end time | `Auction.NotEnded` / success with `Closed`, `Won`, `Settled`; events record `System:Jobs.AuctionSettlement` and `RecordedAt` from the fake clock |
| reaction to `Bid.Won` | `ReactionsTo(won) = [MarkPayoutDue]`; context `System:Reactions.MarkPayoutDue`, causation = the event id, correlation carried; redelivery → `Replayed = true`, nothing written |
| concurrent writer (probe retry, rejected by C-7) | reload + decide again succeeded; a second race ended in `Bid.TooLow`, not in an overwrite; `If-Match` stale → `Stream.VersionConflict` |
| webhook | signed delivery 200; redelivery 200, nothing written; actor `Service:webhook:payments`; wrong signature, tampered body, stale timestamp → 401; unknown type → 202; guard rejection → 200 (the probe's pre-C-9 policy) |
| REST extras | anonymous → 401 (policy); `Idempotency-Key` retry → 200 with the same id; non-JSON body → 415 (routing, from `AcceptsMetadata`); bad route id → 400 `auction` |

### 3.1 Loading (C-16) **[verified]** (`App/`, section "C-16")

| Scenario | Result |
|---|---|
| default `CloseAuction(Id<Auction>)` | loaded by the pipeline; the change has no load code |
| `[Read] Id<Artwork>` of an unverified artwork | the artwork is loaded read-only and passed to `Auction.CreateFor`: `Error.Conflict("Artwork.NotVerified")` |
| `[Read]` of a missing artwork | `Error.UnprocessableEntity("Artwork.NotFound")` (422), before the factory runs |
| verified artwork | auction created; no event is written to the artwork's stream |
| `ILoads<Auction>` by lot number | found through `IAggregateReader.Find`, then permission, `Close`, `Prepare`, append as usual; unknown lot → its own `NotFound` |
| `ILoads<Payout>` load-or-create | first call: created and approved in one commit (2 events, expected version 0); second call loads it: `Payout.AlreadyApproved` |

Generated for `[Read]` (prototype): `partial record X : IHasReads` with `LoadReads`, one `reader.Load` per member.
Under Native AOT the App still publishes with 0 IL warnings (`output.txt`).

## 4. Measurements

### 4.1 Native AOT **[verified]**

| Build | IL warnings | Native run |
|---|---|---|
| App (core pipeline, generated dispatch and reactions) | 0 | all pass |
| Web, generated `RequestDelegate` entries | 0 IL, 0 RDG; 17.2 MB binary | self-test 24/24 |
| Web, generated `MapPost(pattern, (HttpContext, ChangePipeline) => …)` | IL2026 + IL3050 per endpoint (publish-time only, from generated code) | **aborts at startup**: `NotSupportedException: JsonTypeInfo metadata for type 'System.Threading.Tasks.Task'` |
| Web, generic library `MapPost<TChange>(pattern, Delegate)` | RDG011 + IL2026 + IL3050 (also at build) | (same process) |

A generated `(HttpContext) => Task` lambda binds to the `RequestDelegate` overload and causes no warning **[verified]**
(first probe run), so the trap is only for lambdas with bound parameters.

### 4.2 Pipeline cost **[verified]** (`Bench/`, BenchmarkDotNet 0.16.0-preview.2, `--job short --inProcess`)

| Method | Mean | Allocated |
|---|---:|---:|
| hand-written (validate, load, `Close`, `Prepare`, append, `Committed`) | 251.5 ns | 920 B |
| `ChangePipeline.Run` (with the C-16 checks) | 395.4 ns | 976 B |
| generated `Dispatch(object)` → `Run` | 374.2 ns | 976 B |

The store is the same in all three (load = replay of one event, append = nothing). Short runs have wide error bars
(±160 … 350 ns); compare ratios. The overhead (+56 B, ≈ +145 ns) comes from the async state machine of `Run`, the
interface calls and the permission hook **[unverified breakdown]**. The in-process loop in `App/` measured 279 B (JIT) and
−65 B (AOT) for the same difference, i.e. within its noise.

### 4.3 Generator incrementality **[verified]** (`GenTest/`)

Run 1: 7 files (6 `Target` parts, 1 dispatch). Adding an unrelated file: models `Cached ×6`, outputs `Cached ×8`.
Editing `Execute`'s body: the same (the model holds names and types only, no bodies, no locations). Two `Id<Auction>`
members: TCHG001. Found: a generated field named with `string.GetHashCode()` differs per process; use an index.

### 4.4 Idempotency and retry sketch **[verified]** (`App/`, `Core/InMemoryAggregateStore.cs`)

The in-memory store checks the expected version and stores (key → outcome) inside the same lock as the append,
standing in for one transaction. A key seen before returns the stored `ChangeOutcome` with `Replayed = true` before any
load. A key whose append would commit twice is refused (`Change.Duplicate`).

## 5. Generator and diagnostics

**Built in PR 7B** (§14) **[verified]**, renumbered from the proposal (TCHG006→005, TCHG008→004):

| Id | Severity | When |
|---|---|---|
| TCHG001 | Error | an `IChange<T>` without exactly one `Id<T>` member and without its own `Target` (not for `ILoads<T>`) |
| TCHG002 | Error | a change that needs generated code (`Target`, `[Read]`) is not `partial`, or a containing type is not |
| TCHG003 | Error | generic, file-local, or both `IChange<T>` and `ICreation<T>` |
| TCHG004 | Error | `[Read]` on a member that is not an `Id<X>` of an aggregate, or on the change's own target |
| TCHG005 | Warning | members with validation rules, but the change is not `IValidatable` |

Proposed for the entries, numbered when they are built: route parameter without a member or not `IParsable`, two
`[Endpoint]`s with one verb and route (step 10), an entry factory with the wrong shape (steps 9–11). Dropped: "`Execute`
calls more than one behaviour" (best effort only, and `Execute` cannot reach the store or `Raise` anyway).

## 6. Decided API (step 7 part)

```csharp
namespace Tarol.Persistence;          // C-6
public interface IAggregateReader                       // C-16: what special changes and [Read] use
{
    ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken ct) where T : AggregateRoot<T>, IAggregateRoot<T>;
    ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken ct) where …;
}
public interface IAggregateStore : IAggregateReader
{
    ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key, CancellationToken ct) where …;
    ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken ct);
}
public readonly record struct IdempotencyKey(Option<Id<ITenant>> Tenant, string Scope, string Key);
public sealed class InMemoryAggregateStore(TimeProvider time) : IAggregateStore { … }

namespace Tarol.Changes;
public interface IChange<T> where T : AggregateRoot<T>, IAggregateRoot<T> { Id<T> Target { get; } Result Execute(T aggregate, ChangeContext context); }
public interface ICreation<T> where T : AggregateRoot<T>, IAggregateRoot<T> { Result<T> Execute(ChangeContext context); }   // C-10: no Target
public readonly record struct ChangeContext(OperationContext Operation, DateTimeOffset Now, Reads? Reads = null) { public T Read<T>() …; }
public interface ILoads<T> : IChange<T> where …     // C-16: the special change loads its target itself
{ ValueTask<Result<T>> Load(IAggregateReader reader, ChangeContext context, CancellationToken ct); }
[AttributeUsage(Parameter | Property)] public sealed class ReadAttribute : Attribute;   // read-only aggregate for the decision
public interface IHasReads { ValueTask<Result<Reads>> LoadReads(IAggregateReader reader, OperationContext context, CancellationToken ct); }   // generated
public readonly record struct ChangeOutcome(Guid StreamId, long Version, int Events, bool Replayed = false);
public readonly record struct ChangeOptions(Option<IdempotencyKey> Key = default, Option<long> ExpectedVersion = default, ClaimsPrincipal? Principal = null);
public sealed class ChangePipeline(IAggregateStore store, TimeProvider time, IChangePermission? permission = null)
{
    public ValueTask<Result<ChangeOutcome>> Run<T>(IChange<T> change, OperationContext context, ChangeOptions options = default, CancellationToken ct = default) …;
    public ValueTask<Result<ChangeOutcome>> Run<T>(ICreation<T> creation, OperationContext context, ChangeOptions options = default, CancellationToken ct = default) …;
}
public interface IChangePermission { ValueTask<Result> Check<T>(object change, T aggregate, OperationContext context, ClaimsPrincipal? principal, CancellationToken ct) where …; }
// entry attributes (data only): EndpointAttribute, OnEventAttribute<TEvent>, WebhookAttribute, ConsumesAttribute
// test helper (C-14): ChangeTest.Given(aggregate | events).At(time).When(change, actor) → ChangeTestOutcome
```

## 7. Plan impact (question 9)

| Row | Before | Decided (C-1, C-2, C-3, C-6, C-15) |
|---|---|---|
| 7 | Changes | **`Tarol.Persistence`** (store port, in-memory store) + **`Tarol.Changes`** (core, internal entry, generator, `ChangeTest`); two PRs |
| 8 | Persistence | `Tarol.Persistence.Npgsql` implements the port; key table in the append transaction; outbox table (rows from `ReactionsTo`) |
| 9 | Sagas | + reactions to own events (`[OnEvent<T>]`, `React`, delivery policy C-9) on the same outbox worker; `IChangeDispatcher(object)` → generated `Dispatch` |
| 10 | CQRS | renamed **Security and REST**: `Tarol.Security` + `Tarol.Changes.AspNetCore` (endpoints, webhooks, problem details); no CQRS |
| 11 | Messaging | consumers (`[Consumes]`), inbox (= key table), contract versions |

## 8. Decisions asked

Round 1 (scope, before probing): C-1, C-2, C-3. Round 2 (after the measurements): C-4 … C-7. Round 3: C-8 … C-11.
Round 4: C-12 … C-15. Round 5 (the owner's loading requirement): C-16, probed afterwards (§3.1). Questions already answered elsewhere were not asked again: parse before load (validation.md
S-4), permissions as ASP.NET Core authorization handlers (security.md), behaviour methods as the contract (domain-core.md
§9), discard on failure (primitives.md), the context shape (kernel.md).

## 9. Owner answers (2026-11-14)

| # | Question | Answer |
|---|---|---|
| C-1 | What step 7 builds | **Core + internal entry**: declaration, generated pipeline parts, typed in-process entry, store port + in-memory store, permission hook without ASP.NET Core; every other channel designed now, built in the step that owns its infrastructure |
| C-2 | Plan | **Split by infrastructure**: 7 core; 8 store + outbox; 9 sagas + reactions; 10 Security + REST + webhooks (`Tarol.Changes.AspNetCore`); 11 consumers + inbox |
| C-3 | ROADMAP D-3 (command model) | **Changes is the only write model**; old CQRS (`ICommand`/`IQuery`, dispatcher) not migrated; queries are a later decision (plan step 12); row 10 renamed |
| C-4 | Declaring a change | **(A) input record** implementing `IChange<T>`/`ICreation<T>`, `Execute` calls one behaviour; library pipeline |
| C-5 | Declaring entries | **Attributes on the change**: `[Endpoint]` on the record; `[OnEvent<T>]`, `[Webhook]`, `[Consumes]` on a static `From` factory |
| C-6 | Store port | **`Tarol.Persistence` created in step 7** (port + `InMemoryAggregateStore`); Npgsql in 8 |
| C-7 | Version conflicts | **Never retry** in the pipeline (against the recommendation "reload, 3 tries"): `Error.Conflict("Stream.VersionConflict")` goes to the caller |
| C-8 | Idempotency keys | **Successes only, stored with the append in one transaction**; scoped keys per channel; retention in step 8 |
| C-9 | Failures in reactions, webhooks, consumers | **Conflict = retry** (against the recommendation "acknowledge a guard conflict"): every `Error.Conflict` retries with backoff and dead-letters after N; the rest of the table as recommended |
| C-10 | Who chooses a new aggregate's id | **The factory generates it** (against the recommendation "the input carries it"): `ICreation<T>` has no `Target`; redelivered creations are deduplicated only by the key |
| C-11 | Context and permission | **Parameter; no checks until 10**: `Run(change, OperationContext, options)`, principal in options; `IChangePermission` hook without an implementation in 7; REST entries in 10 fail closed without one |
| C-12 | REST entries | **Generated `RequestDelegate`s** with own binding, `AcceptsMetadata`, problem details via `IProblemDetailsService` when registered |
| C-13 | TVAL006 in entry factories | **Exempt** `new` inside a static entry factory of an `IValidatable` change (Validations analyzer change, step 9) |
| C-14 | Test support | **`ChangeTest` helper** on `InMemoryAggregateStore` with a fake `TimeProvider` |
| C-15 | PR split of step 7 | **Two PRs**: (A) `Tarol.Persistence`; (B) `Tarol.Changes` |
| C-16 | Loading (owner requirement after round 4: "Changes autoload the aggregate; only special changes load it themselves") | **Auto-load + escape hatch**: the pipeline loads the target by `Target` (unchanged default); a special change implements `ILoads<T>.Load(IAggregateReader, ChangeContext, ct)` (another key, conditional, load-or-create); read-only other aggregates are `[Read] Id<X>` members, loaded by generated code and read with `context.Read<X>()`; the pipeline keeps validation, permission, `Prepare`, append and discard |

Consequences of the three answers against the recommendation:

* **C-7**: an HTTP client gets 409 `Stream.VersionConflict` under contention and must retry; the outbox worker and
  consumers treat it as transient (C-9 already retries it). The pipeline keeps no loop and no `Attempts` option.
* **C-9**: a reaction whose guard can never pass (e.g. `Auction.NotSettled` forever) is retried until dead-lettered, so
  it becomes visible to a human instead of being silently acknowledged. The retry count and backoff are options of the
  worker (step 9). Webhooks answer 503 for a Conflict, so the sender retries too.
* **C-10**: `CreateAuction` carries no id; a REST `POST /auctions` retried without `Idempotency-Key` creates a second
  auction (documented in the skill: send the header). A reaction that creates (`MarkPayoutDue`) is deduplicated by its
  key (reaction, event id), stored in the creation's transaction (C-8), so redelivery still creates once.

## 10. What the implementation must do (in the decided order)

### PR 7A `persistence: the aggregate store port and an in-memory store` (done: [persistence.md](persistence.md))

1. `src/Persistence/Tarol.Persistence` (depends on Primitives, Kernel, Results, Options, StronglyTypedId):
   `IAggregateReader` (`Load`, `Find` over unique lookups) and `IAggregateStore` (§6), `IdempotencyKey`, `StoredOutcome(Guid StreamId, long Version, int Events)`,
   `InMemoryAggregateStore(TimeProvider)`: tenant-scoped streams (another tenant → `None`), expected version (0 = must
   not exist) → `Error.Conflict("Stream.VersionConflict")`, key stored with the append atomically, `RecordedEvent` log
   with event id (UUIDv7 from the clock), `RecordedAt` per commit and the `OperationContext`; loads via `T.ReadState`
   when `T.StateSchema != 0` (else `T.Rehydrate`), so the in-memory store exercises the stored state like step 8 will.
2. Tests: version conflicts, tenant isolation, key replay, `RecordedEvent` contents, state vs replay load.
3. README, skill `tarol-persistence` (port contract for step 8 implementers and test authors), AOT smoke, package
   verification, conventions §8.

### PR 7B `changes: the change pipeline, typed entry and generator` (done: §14)

1. `src/Changes/Tarol.Changes` (+ `Tarol.Changes.Generators`), depends on Persistence and Validations: the API of §6;
   `Run` = `Validated()` → `Seen(key)` → `[Read]` aggregates (422 when missing) → load: automatic by `Target`, or the
   change's own `ILoads<T>.Load` (C-16) → `IChangePermission` (if registered) → `Execute` with `now` read once →
   `Prepare` → append(expected = loaded version − uncommitted events, key) → `Committed`; no retry (C-7); creation
   appends with expected 0.
   Entry attributes as data only (no ASP.NET Core reference).
2. Generator: `Target` for `IChange<T>` records with one `Id<T>` member (none for `ILoads<T>`); `IHasReads.LoadReads`
   for `[Read]` members; `GeneratedChanges.Dispatch(object)`; TCHG001, 002, 006, 007, 008; equatable models without locations or bodies; deterministic names; `AnalyzerReleases`.
3. `ChangeTest` (C-14).
4. Tests: every row of §3 that belongs to the internal entry (invalid before load, 404, tenant 404, 409, system actor,
   `now`, version conflict returned, key replay, discard on guard / `Prepare` / exception failure, creation twice with a
   key); generator snapshots and incrementality; AOT smoke (pipeline + dispatch, 0 IL warnings).
5. README, skill `tarol-changes`, docs, `CHANGELOG.md`, root `README.md`, plan status.

### Later steps (designed here)

* **8**: the port in Npgsql; key table written in the append transaction, successes only, retention; outbox rows from
  `GeneratedChanges.ReactionsTo(event)` in the same transaction.
* **9**: `[OnEvent<T>]` → generated `ReactionsTo`/`React` (context `AsSystem`, causation = event id, key = (reaction,
  event id)); outbox worker with the C-9 policy (Conflict retries, then dead-letter); saga `Execute(change)` through
  `Dispatch`; TVAL006 exemption for entry factories (C-13); TCHG005. **Designed** in [sagas.md](sagas.md) (S-1 … S-15):
  sagas are aggregates whose steps are reactions, so no saga dispatch through `Dispatch(object)` is needed.
* **10** (designed in [rest.md](rest.md); 10A changed the pipeline: permission before the expected version and only with a
  principal, `ChangeContext` in the check, `Delivery.Classify`, key fingerprints, `AddTarol`; rest.md §14.2): `Tarol.Security` implements `IChangePermission`; `Tarol.Changes.AspNetCore` generates `MapChanges()` and
  `[Webhook]` endpoints as `RequestDelegate`s (§2 A, `Web/` probe), fails closed without a permission, maps outcomes as
  in changes.md §6, `AddProblemDetails`, TCHG003/004.
* **11**: `[Consumes]` consumers with the inbox key, contract versions, ordering.

## 11. Skill evaluation checklist (`tarol-changes`, for PR 7B)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| "add a change that closes an auction" | `sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable` with `Execute` calling `auction.Close(context.Now)` | calling `Raise`; taking the aggregate instance as a record member; `DateTimeOffset.UtcNow` (TKRN001) |
| "create an auction" | `ICreation<Auction>` returning the factory's `Result<Auction>` | an id member on the creation input (C-10); loading first |
| "run it from a background job" | `pipeline.Run(new SettleAuction(id), OperationContext.Start(Actor.System("Jobs.X"), tenant, time), ct: ct)` | an ambient context; `Actor.User` for a job |
| "handle a version conflict" | `Error { Code: "Stream.VersionConflict" }` returned; the caller retries | a retry loop inside `Execute` |
| "a change with two `Id<Auction>` members" | implement `Id<Auction> IChange<Auction>.Target => …` | ignoring TCHG001 |
| "close an auction found by its lot number" | `ILoads<Auction>` with `Load` using `reader.Find<Auction>("lot", …)` | loading in `Execute`; injecting the store into the record |
| "run a change from an outbox row typed as object" | `pipeline.Dispatch(change, context, options, ct)` (generated, namespace = assembly name) | a `switch` by hand; reflection |
| "only auction verified artworks" | `[Read] Id<Artwork> Artwork` + `context.Read<Artwork>()` passed to the factory | changing the artwork in the same change (a saga); loading it by hand when `[Read]` suffices |
| "test the change" | `ChangeTest.At(now).Given(aggregate).When(change, actor)` → `Result`, `Events`, `Recorded` | a hand-rolled store fake |

## 12. Verification of this PR (SDK 11.0.100, 2026-11-14) **[verified]**

Docs and probes only; no project in `Tarol.slnx` changed. `dotnet build Tarol.slnx`: 0 warnings, 0 errors.
`dotnet test --solution Tarol.slnx`: 659 total, 0 failed. `dotnet format Tarol.slnx --verify-no-changes`: exit 0.
Probes: `probes/changes/all.sh` → [`output.txt`](probes/changes/output.txt) (67 PASS, 0 FAIL).

## 13. Open items

* Retention of idempotency keys and outbox rows (step 8): **decided** P-11 (persistence.md §15): `PurgeKeys`, 7 days; outbox rows are deleted when completed.
* Order of one stream's outbox rows across several workers (steps 8–9): **decided** P-10: head per (stream, reaction), probed.
* Unknown version of a known webhook type: dead-letter proposed (step 10).
* Whether `ChangeOutcome` should carry the committed `RecordedEvent`s for the caller (e.g. to return a created child's
  id): not needed by any scenario here.
* The read side after C-3.

## 14. Implementation (PR 7B, 2026-11-14)

`Tarol.Changes` (+ `Tarol.Changes.Generators`), stacked on PR 7A. Package guide: `src/Changes/Tarol.Changes/README.md`;
user guide: [docs/changes/README.md](../changes/README.md).

### 14.1 Public API

`IChange<T>` (`Target`, `Execute`), `ICreation<T>` (`Execute`, no target, C-10), `ILoads<T> : IChange<T>` (`Load`; its
`Target` is a default member returning `Id<T>.Empty`), `ChangeContext(Operation, Now, Reads?)` with `Read<T>()` and
`Read<T>(member)`, `ReadAttribute`, `Reads`, `IHasReads` (generated use), `ChangeOutcome(StreamId, Version, Events,
Replayed)`, `ChangeOptions(Key, ExpectedVersion, Principal)`, `IChangeAuthorization`, `ChangePipeline(store, time,
authorization?)` with `Run<T>(IChange<T>, …)` and `Run<T>(ICreation<T>, …)`, `ChangeTest` (`At`, `Given`, `Later`, `As`,
`InTenant`, `When`, `Load`, `Context`, `Store`, `Pipeline`, `Clock`) and `ChangeTestOutcome(Result, Events, Recorded)`.
Generated: `Target`, `LoadReads`, and per assembly `internal static class GeneratedChanges` with `Dispatch(object, …)`.

### 14.2 Deliberate differences from §6 and §10

* **`IChangePermission` (§6, §10) is `IChangeAuthorization`**: CA1711 reserves the suffix "Permission"; renaming beat a suppression.
* **No entry attributes yet** (`[Endpoint]`, `[OnEvent<T>]`, `[Webhook]`, `[Consumes]`): they ship with the step that
  builds their entry (9–11), so an attribute never compiles and silently does nothing (security.md §7: no unused
  abstraction). C-5 (attributes on the change) is unchanged.
* **The dispatcher is `internal`, in a namespace named after the assembly**: one public fixed type per assembly collided
  across assemblies in the old CQRS generator (review C12). Cross-assembly saga dispatch is step 9's to design.
* **A creation's authorization runs on the new aggregate** (there is no loaded resource), before the invariants. A
  factory that returns a loaded aggregate throws; an `ExpectedVersion` other than 0 is a version conflict; a stream that
  exists (only possible for a factory reusing an id) is `Error.Conflict("T.AlreadyExists")`.
* **`ChangeTest` has its own standing clock** (`At`, `Later`): the package does not depend on
  `Microsoft.Extensions.TimeProvider.Testing`.
* Diagnostics renumbered (§5).

### 14.3 Found while building

* **`[Read]` aggregates are recognised by their `AggregateRoot<X>` base class, not by `IAggregateRoot<X>`**: the
  Primitives generator adds that interface, and one generator never sees another's output, so the interface check
  rejected every aggregate (TCHG004 on valid code). The same rule as candidate B's blocker, inside one solution.
* **The dispatcher depended on the whole change model**, so renaming a target member regenerated it; it now takes
  (name, inheritance depth) only (incrementality test).
* A member named `Target` implements `IChange<T>.Target` itself; the generator then emits nothing (test).

### 14.4 Tests **[verified]**

* `Tarol.Changes.Tests` (37): creation, `Invalid` before any read (store loads = 0), empty target id, guard on an input
  field, auto-load and `Now`, guard failure, 404 and another tenant, actor in behaviour and records, job settlement with
  the system actor and `RecordedAt`, version conflict returned without a reload (one load), `ExpectedVersion`, key replay
  without running and without loading, failure stores no key, invariant discard, exception propagation, no-event change,
  explicit target, creation with `ExpectedVersion`, authorization after load with the principal and on creation;
  C-16: `[Read]` decides, missing read 422, read aggregate never written, `[Read]` on a change, `ILoads` by lookup and its
  `NotFound`, load-or-create; `Reads` lookups; generated `Dispatch` for changes, creations, unknown and `null`;
  `ChangeTest` refusals, actor/tenant/time; the skill example verbatim.
* `Tarol.Changes.Generators.Tests` (22): snapshot of `Target`, `[Read]`, nested change and dispatcher; the generated code
  compiles without warnings with the Primitives generator; no dispatcher without changes; every diagnostic and its
  message; explicit `Target`, `ILoads`, a member named `Target` and a creation without reads need no code; a foreign
  `IChange<T>` is ignored; incrementality: unrelated file, `Execute` body edit, moved text → nothing regenerated; a
  renamed target member → only its change.

### 14.5 Suppressions and analyzer configuration

None in `src/`. Tests: `#pragma warning disable TVAL006` in three test files that build inputs the way JSON would (the
pipeline validates them), as in the Primitives and Validations tests.

### 14.6 Verification (SDK 11.0.100, 2026-11-14)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | exit 0 |
| `dotnet build Tarol.slnx` (Debug and `-c Release`) | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | 743 total, 0 failed (684 before + 37 `Tarol.Changes.Tests` + 22 `Tarol.Changes.Generators.Tests`) |
| `eng/verify-packages.sh` | OK: `Tarol.Changes` holds `lib/` (+ XML docs), `analyzers/dotnet/cs/Tarol.Changes.Generators.dll`, `README.md`, `LICENSE`, `skills/tarol-changes/SKILL.md`; the consumer passes 8/8, its generators (Changes, Primitives, Validations) reach the compiler, `project.assets.json` lists `tarol-changes` and the dependencies' skills |
| `eng/aot-smoke.sh` | 8 apps, each 0 IL warnings; `Tarol.Changes.AotSmoke` 8/8 natively |

## 15. Fix: a concurrent run under the same key replays instead of a 409 (2026-11-18)

**Behaviour change.** When two runs with the same idempotency key raced (a client retrying after a timeout while the first
request was still committing), both passed the key check, and the loser's append was refused: `VersionConflict` on the same
stream (409, or `<Aggregate>.AlreadyExists` for a creation), or `Idempotency.DuplicateKey` when the streams differed (a creation
picks a new id each time). The pipeline returned that refusal, so the client saw 409 although its request had committed.

Now, when a run with a key is refused with a version conflict or a duplicate key, the pipeline checks the key again and returns
the stored outcome (`Replayed: true`, or `KeyReused` when the fingerprint differs, R-9). Without a stored outcome the refusal stands.

Tests: `PipelineTests.A_concurrent_run_under_the_same_key_gets_the_stored_outcome_not_a_conflict` and
`A_concurrent_creation_under_the_same_key_gets_the_stored_outcome_not_a_conflict` (both fail without the fix).

## 16. `IOutboxRowHandler.RunPositioned`: a row with its feed position (Tarol-Svelte BC-23, 2026-11-21)

**Public API addition, behaviour change.** `IOutboxRowHandler.RunPositioned(pipeline, reaction, PositionedEvent, ct)` is a
default interface member that runs `Run(pipeline, reaction, positioned.Event, ct)`, so every existing handler compiles and
behaves unchanged; `OutboxRows.RunPositioned` forwards it to the part that owns the row. The reaction worker (`Tarol.Sagas`)
now calls it with the claimed `OutboxRow.Event`, so a publication can put the event's feed position into its message
(`ce-position`). A handler that wraps another must forward `RunPositioned` as well. Details, reasons and tests:
docs/migration/messaging.md §27.

## 17. TN-8: modules for applications of several assemblies, with TN-9 and TN-10 (2026-11-24)

Feedback from Tatalu ([docs/feedback/tatalu.md](../feedback/tatalu.md)): one assembly per bounded context (13 planned).
Each assembly generates its own `MapChanges`, `AddTarolEndpoints`, `GeneratedReactions.Set`, `GeneratedJobs.Set`; the host
listed every one by hand (`Tatalu.Host/TataluApp.cs`), wrote a forwarding `JsonSerializerContext` (`TataluJson.cs`, TN-10) and
a transformer that removes duplicate OpenAPI parameters (`OpenApiDeduplication.cs`, TN-9).

### 17.1 Public API (additions)

| Package | API | Role |
|---|---|---|
| `Tarol.Persistence` | `TarolModule` (`Name`, `Json`, `Aggregates`, `Reactions`, `With<TPart>`, `Part<TPart>`, `Parts`), `TarolModules` (`All`, `In(services)`), `TarolBuilder.Modules` | what one assembly brings; immutable, one typed part per type |
| `Tarol.Changes` | `[assembly: GenerateTarolModule(name?)]` → generated `GeneratedModule.Module`; `AddTarol(params TarolModule[])`; `AddTarol(IReadOnlyList<IJsonTypeInfoResolver>, Action<EventCodec>)` | the generated module; the registration of modules (JSON combined, aggregates, reactions `ReactionSet.Combine`d); several contexts without modules |
| `Tarol.Validations` | `ValueObjectJson.Options(IReadOnlyList<IJsonTypeInfoResolver>)` | the first context's settings, value objects first, then every context |
| `Tarol.Queries` | `ProjectionModule`, `ProjectionsBuilder.AddModules()`, `.Add(IEnumerable<TarolModule>)` | a module's projections |
| `Tarol.Jobs` | `JobsBuilder.AddModules()`, `.Add(IEnumerable<TarolModule>)` | a module's `GeneratedJobs.Set` (the part type is `JobSet`) |
| `Tarol.Security` | `SecurityModule(Action<ChangeRoles>)` | a module's role map; `AddSecurity` applies every module's after the app's |
| `Tarol.Changes.AspNetCore` | `TarolEndpointMetadata.Owner` | the assembly whose generated `MapChanges` mapped an entry (TN-9) |
| | `EndpointModule`, `OpenApiModule`, `app.MapModules(names?)`, `modules.ConfigureOpenApi(object)` | a module's HTTP entries and OpenAPI transformers (`OpenApiOptions` is typed `object`: the package does not reference the OpenAPI package) |

The generated module holds what the generator can see in the assembly: its `JsonSerializerContext`s (one: its `Default`; several:
`JsonTypeInfoResolver.Combine`), its concrete `AggregateRoot<T>` types, `GeneratedReactions.Set`, `GeneratedJobs.Set`, a
`ProjectionModule` over its `[Projection]` rows, an `EndpointModule` over its `GeneratedEndpoints.MapChanges` and an
`OpenApiModule` over its `GeneratedOpenApi.AddTarolEndpoints` (each only when the assembly references the package and has
such members). The role map is hand-written in Tatalu and stays so: attach it with `.With(new SecurityModule(…))`.

### 17.2 Behaviour changes

* **TN-9:** the generated OpenAPI operation transformer completes only the operations of its own assembly
  (`TarolEndpointMetadata.Owner`, new, set by the generated `MapChanges`) and Tarol's own entries (`GET /me`, search: no
  `Owner`), and every operation only once (an operation with `x-tarol-kind` is done). The first attempt compared the
  input's assembly and left `/me` and search incomplete (`EdgeTests` caught it), hence the explicit `Owner`. With two assemblies,
  `Idempotency-Key`, `If-Match`, the list parameters and the "Policy" paragraph appeared twice; `tarol-codegen` emitted
  duplicate identifiers (TS2300). Red on the TN-7 commit **[verified]**: the composition of `ModuleTests.Without_modules_…`
  answers `Idempotency-Key` twice on `PUT /plots/{plot}/name`.
* **TN-10:** `AddTarol(JsonSerializerContext, …)` is unchanged; the new overload takes several contexts. The fault Tatalu hit
  (a hand-written context subclass answers only for its own options, so a copy of the options fails with "returned a
  JsonTypeInfo instance whose JsonSerializerOptions setting does not match") does not arise: every context is asked with the
  caller's options. Red: one context for two assemblies fails at `codec.Add<Recipe>()` ("no metadata for Scaled, Written").
* `AddSecurity` applies the `SecurityModule` parts of the registered modules (none without modules: unchanged).
* No module is generated without the assembly attribute: existing assemblies get no new code.

### 17.3 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Changes.AspNetCore.Tests` (+ fixtures `….Tests.Garden`, `….Tests.Kitchen`) | +6 (`ModuleTests`) | two assemblies, each `[assembly: GenerateTarolModule]`: one `AddTarol`, `MapModules`, `ConfigureOpenApi`; aggregates, both JSON contexts through the options and a copy; changes, lists and each module's role map over HTTP; every operation's parameters once, `x-tarol-kind` on each; the manual composition without modules also has every parameter once (TN-9); unique names, a module needs JSON; parts replace by type |
| `Tarol.Changes.Generators.Tests` | +2 | the module snapshot (JSON, aggregates, reactions, jobs); no module without the attribute |

### 17.4 Skill evaluation checklist (`tarol-overview`)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| a host for three context assemblies | `[assembly: GenerateTarolModule]` in each, `AddTarol(a, b, c)`, `p.AddModules()`, `MapModules()`, `tarol.Modules.ConfigureOpenApi(o)` | a forwarding `JsonSerializerContext`; a de-duplicating OpenAPI transformer; listing `MapChanges` per assembly |
| a context's role map | `.With(new SecurityModule(Map))` | one central role map that every context edits |

### 17.5 Suppressions

None.

### 17.6 Verification (SDK 11.0.100, 2026-11-24) **[verified]**

`dotnet build` 0 warnings; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx` with Docker:
**2167 of 2167**; `dotnet pack` + `eng/verify-packages.sh`: OK; `eng/aot-smoke.sh` with `TAROL_PG`: 28 apps, 0 IL warnings,
all checks passed. No AOT check of its own: a module is delegates and a part dictionary keyed by `typeof(TPart)`; the
packages that ship it keep `IsAotCompatible` and publish without IL warnings.

## 18. TN-2: aggregates with one instance per tenant (2026-11-24)

Feedback from Tatalu (TN-2): the day-care facility *is* the tenant; settings, invoice number ranges and profile assignments
are one per tenant too. Tatalu wrote `Id<ChildDayCare>.From(tenant.Value)` and a hand-written `ILoads` per change
(`RenameDayCare.Load`), and its view took the tenant id as `{childDayCare}`.

### 18.1 Public API (additions)

| Package | API | Role |
|---|---|---|
| `Tarol.Primitives` | `PerTenantAttribute`; `IAggregateRoot<TSelf>.PerTenant` (static virtual, default `false`; the generator writes `public static bool PerTenant => true` for a `[PerTenant]` root) | the marker, readable without reflection (`T.PerTenant`) |
| `Tarol.Changes` | `TenantAggregates.IdOf<T>(Id<ITenant>)`, `IdOf<T>(OperationContext)`, `TryIdOf<T>`, `TenantRequiredCode` (`Tenant.Required`) | the id is `Id<T>.From(tenant.Value)` |
| `Tarol.Changes.AspNetCore` | `ChangeEndpoints.LoadTenant<T>` (EditorBrowsable Never) | the generated view of a per-tenant aggregate without `{id}` |

### 18.2 Behaviour

* **Changes:** for a `[PerTenant]` aggregate the pipeline ignores `Target` and loads the caller's tenant's id; without a tenant
  it answers 403 `Tenant.Required`; a missing one is `<T>.NotFound`. The Changes generator writes `Target => Id<T>.Empty` for
  such a change without an `Id<T>` member instead of TCHG001. An `ILoads<T>` still loads itself.
* **Creations:** the factory must create it with `TenantAggregates.IdOf<T>(context.Operation)`; another id is an
  `InvalidOperationException` (a programming error, before the append). A second creation is `<T>.AlreadyExists` (409), as
  for any creation whose stream exists.
* **HTTP:** a change entry needs no route value (it had none to bind); a view of a `[PerTenant]` aggregate may have no route
  value (`GET /house`): the delegate calls `LoadTenant<T>`, then the read check, ETag and 304 as for any view. With one route
  value it is the ordinary view by id. The OpenAPI operation has no path parameter, so `tarol-codegen` generates
  `requests.houseView()` without an id (TS-8).
* The stored data does not change: the id was already the tenant's in Tatalu; the stream key stays (tenant, type, id).

### 18.3 Tests **[verified]**

| Project | Tests | Covers |
|---|---:|---|
| `Tarol.Changes.AspNetCore.Tests` | +4 (`PerTenantTests`) | two tenants open, rename and read their one house with no id anywhere (the id is the tenant's, ETag `"2"`); a change before the creation is `House.NotFound`, a second creation `House.AlreadyExists`; no tenant is 403 for change and view; no path parameter in the document |
| `Tarol.Changes.Generators.Tests` | +2 | the generated `Target => Id<Settings>.Empty`; it compiles without warnings with the Primitives generator (`PerTenant`) |

### 18.4 Skill evaluation checklist (`tarol-changes`)

| Task | Expected API | Mistakes to avoid |
|---|---|---|
| tenant settings with `PUT /settings/name` | `[PerTenant]` root, `RenameSettings(string Name) : IChange<Settings>`, `[Endpoint("PUT /settings/name")]` | an `ILoads<Settings>`; an `Id<Settings>` route value filled with the tenant id |
| create them | `new Settings(TenantAggregates.IdOf<Settings>(context.Operation))` | `Id<Settings>.New(now)` (throws: not the tenant's id) |

### 18.5 Suppressions

None.

### 18.6 Verification (SDK 11.0.100, 2026-11-24) **[verified]**

`dotnet build` 0 warnings; `dotnet format --verify-no-changes` clean; `dotnet test --solution Tarol.slnx` with Docker:
**2173 of 2173**; `dotnet pack` + `eng/verify-packages.sh`: OK; `eng/aot-smoke.sh` with `TAROL_PG`: 28 apps, 0 IL warnings
(`T.PerTenant` is a static virtual member: no reflection).

## Tatalu round 3

Public API and behaviour changes of this family in round 3 are listed item by item in [tatalu-round-3.md](tatalu-round-3.md).
