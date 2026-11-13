# Step 4: Kernel

Origin: `CLorat/Tarol@6a62f81`: `src/Kernel/Kernel` (3 files), `src/Clock/Clock` (1 file), `src/Contexts/Contexts`
(1 file), `src/Security/Security` (1 file); no tests for any of them. Primitives' `DomainClock`
(`src/Primitives/Primitives/DomainEvent.cs:48,114`). Old review: `docs/review/supporting.md` §1.1–§1.4, findings 14
and 26; `docs/review/ROADMAP.md` Phase 1.6 and Phase 5; `persistence.md` (proposed event columns), `messaging.md`
(F3, envelope), `sagas.md` (S15).

**Status: implemented.** §0–§6 are the inventory, the experiments and the first proposal; §7 the owner's decisions;
§8 onwards the implementation. Where §3–§4 and §7/§8 differ (ids as `Id<T>`, no trace field, JSON converters), §7/§8
are what shipped. The concept review (stage 1) is in four notes in `docs/architecture/`; this note references them and
does not repeat them:

| Topic | Note |
|---|---|
| Time: clock, event timestamps, tests, sagas | [time.md](../architecture/time.md) |
| Identity and context: actor, tenant, flow, tests | [identity-context.md](../architecture/identity-context.md) |
| Tracing and correlation | [tracing.md](../architecture/tracing.md) |
| Security and authorization | [security.md](../architecture/security.md) |

Evidence labels follow [conventions](../architecture/conventions.md). Probes are in [`probes/kernel/`](probes/kernel/)
(`*.csproj.txt`: copy a folder **outside** the repository, rename to `.csproj`, pass `-p:TarolNetRepo=<clone>` where it
references Tarol-Net). Complete output: [`probes/kernel/output.txt`](probes/kernel/output.txt). Environment: SDK
11.0.100, .NET 11.0.0, linux-x64, 4 cores.

| Probe | What it shows | Command |
|---|---|---|
| `isolation/` | finding 14 reproduced under xUnit v3 parallel classes, the `AsyncLocal` fix, where a test can set an ambient value, leaks between tests | `dotnet run` |
| `flow/` | `AsyncLocal` across `await`, `Task.Run`, `ConfigureAwait(false)`, timers, thread pool, hosted services; JIT and Native AOT | `dotnet run`; `dotnet publish -c Release -r linux-x64 -p:Aot=true` |
| `bench/` | ambient read vs injected call vs static (BenchmarkDotNet 0.16.0-preview.2, `ShortRun`) | `dotnet run -c Release -- --filter '*'` |
| `authz/` | resource-based authorization with and without ASP.NET Core, system actor, actor in event metadata (JSON, AOT), denial as `Error.*` through a `Result` pipeline | `dotnet run`; `dotnet publish -c Release -r linux-x64 -p:Aot=true` |
| `web/` | Minimal API: claims → actor → 401/403/204; `Activity.Current` with and without logging; incoming `traceparent`; Native AOT | `dotnet run -- <log level>`; `-p:Aot=true`, `PROBE_PROBLEMDETAILS=1` |

## 0. Summary

* **The old code is four unused seeds.** Only `Tarol.Clock` has consumers (Messaging, Sagas docs); Kernel markers,
  `IUserContext` and `IAuthorizer` have none **[verified]** (§1). They are a starting point for questions, not an API to
  keep.
* **Time: no Tarol clock** (owner, round 1). Inject `TimeProvider`; the store stamps one `RecordedAt` per commit
  (step 8); business time is an argument of the behaviour. Finding 14 reproduced: **100 %** of a parallel class's
  events took the other class's fake time with the static `DomainClock`; **0** with `AsyncLocal`; an injected clock
  cannot leak. Ambient clocks fail for hosted workers (they capture the context at start) and give no timers
  **[verified]** ([time.md](../architecture/time.md) §5).
* **Identity: an immutable `OperationContext`** (actor, optional tenant, correlation, causation, all ids as `Id<T>`),
  captured at the edge and passed on; no `AsyncLocal`, no accessor. The actor is a kind plus the identity provider's
  subject (a string), never personal data.
* **Markers `IUser` and `ITrace` are dropped**: the IdP subject is not a `Guid`, the app's `User` aggregate already
  has `Id<User>`, and a trace id is `ActivityTraceId` (owner, round 1). `ITenant` stays, joined by `ICorrelation` and
  `ICausation`, as phantom types for the context's `Id<T>` ids (owner, round 2).
* **Tracing: Tarol owns correlation and causation ids** (`Id<ICorrelation>`, `Id<ICausation>`, UUIDv7); the Kernel
  carries no trace field (owner, round 2).
  `Activity.Current` is `null` in a Minimal API when logging is off and no OpenTelemetry listens, and an incoming
  `traceparent` is then dropped **[verified]** (`web/`).
* **Security: designed now, `Tarol.Security` built in step 10** (owner, round 1). ASP.NET Core authorization runs
  without a web host and under AOT with 0 IL warnings **[verified]**, so Tarol adds a mapping to
  `Error.Unauthorized`/`Forbidden`/`NotFound`, not a policy engine. Cross-tenant access is `NotFound`. `IAuthorizer` is
  dropped.
* **Package**: one `Tarol.Kernel` (namespaces `Tarol.Kernel`, `Tarol.Kernel.Json`) with the context data types, their
  JSON converters and the time analyzer TKRN001 (Warning); it depends on `Tarol.Options` and `Tarol.StronglyTypedId`
  (owner, rounds 1–2). Nothing replaces Primitives' `global using Tarol.Kernel;`: files write `using Tarol.Kernel;`.

## 1. Inventory

### 1.1 Old code

| Project | Content | Tests |
|---|---|---|
| `src/Kernel/Kernel` | `IUser`, `ITenant` (`;` form), `ITrace` (empty block body): phantom markers for `Id<T>`; references StronglyTypedId | none |
| `src/Clock/Clock` | `static class Clock` in namespace **`Tarol`** (not `Tarol.Clock`): `AsyncLocal<TimeProvider>`, `Provider`, `UtcNow`, `SetProvider(p)` returning a restore scope | none |
| `src/Contexts/Contexts` | `IUserContext { Id<IUser> UserId; Id<ITenant> TenantId; Id<ITrace> TraceId; IEnumerable<string> Roles; bool IsAuthenticated; ContextUserType UserType; }`, `enum ContextUserType { Anonymous, User, Internal }` | none |
| `src/Security/Security` | `IAuthorizer.IsAuthorizedAsync(CancellationToken)` in namespace **`Security`** | none |

### 1.2 Consumers **[verified]** (grep over `src/`, `tests/`, `examples/`, `templates/`)

| Namespace / type | Used by |
|---|---|
| `Tarol.Kernel` | `global using` in Primitives (no type used); `using` in `Persistence/IUnitOfWork.cs` (no type used); project references from Contexts, Changes, Persistence, Primitives (none used) |
| `Tarol.Clock` (`Clock.UtcNow`) | `MessageBackplaneRunner.cs:21,26` (30 s reset window); recommended in `SagaOptions.cs:11-12`, `SagaServiceCollectionExtensions.cs:95`, `SagaOutboxProcessor.cs:17`, `SagaTestClock.cs:9` (Sagas does not reference the project) |
| `DomainClock` (Primitives) | `DomainEvent.Timestamp` initializer; `AggregateRootTests.EventTimestamp_ShouldUseDomainClock` swaps it |
| `IUserContext`, `ContextUserType`, `IAuthorizer` | nothing |
| `Messaging.Security.ITopicAuthorizer` (similar seed) | nothing; the SignalR hub is open |
| templates `$product$.Kernel` | an assembly marker and global usings only; no user/tenant types |
| ArtFlow | no users or tenants; injects `TimeProvider` into two prerequisites; the bidder is a payload field |

Time is handled three ways (`DomainClock`, ambient `Clock`, `SagaOptions.UtcNow`); `MartenSagaStore` also uses
`DateTimeOffset.UtcNow` directly (S15). Correlation/causation exist only in Sagas (`SagaEnvelope`,
`SagaOutboxMessage`, `ChangeContext`). **No actor, user or tenant is recorded anywhere** (event columns, saga events,
outbox rows, messaging wire).

### 1.3 What the later steps need from step 4

| Step | Needs | From Kernel |
|---|---|---|
| 5 Validations | prerequisites that compare with "now" | nothing: `now` is a parameter (time.md §4) |
| 6 Primitives | event envelope without a clock read; metadata slots | `Actor`, `TenantId`, `OperationContext` (types of the metadata) |
| 8 Persistence | stamp `RecordedAt`, actor, tenant, correlation, causation, trace link per commit; tenant in stream key and queries | `OperationContext`, JSON metadata |
| 9 Sagas | context into outbox rows and messages; system actor on behalf of; `TimeProvider` instead of `SagaOptions.UtcNow` | `OperationContext.ForMessage`, `Actor.System` |
| 10 CQRS | context per command; permission check | `OperationContext`; `Tarol.Security` built then |
| 11 Messaging | envelope headers | the same types and JSON |
| web package | context from `ClaimsPrincipal`; `Error` → problem details (+ `AddProblemDetails()` for AOT, security.md insight 6) | `Actor`, `TenantId` |

## 2. Review: old findings re-verified, new findings

| # | Finding | Status |
|---|---|---|
| 14 | `DomainClock.Provider` is a global mutable static; parallel tests interfere | **reproduced** (`isolation/`: 100 % of a parallel class's events polluted); fixed by removing it (no clock read at event creation) |
| 26 | `IAuthorizer` in namespace `Security`, no subject/resource | **confirmed** by reading; dropped (security.md §7) |
| K-1 | `Tarol.Clock` is in namespace `Tarol`, not `Tarol.Clock` (convention break like 26) | new, by reading; dropped with the clock |
| K-2 | `DomainEvent.Timestamp` is a field initializer, so the clock is read for every **deserialized** event too, and the events of one commit get different times | new, by reading; step 6 removes the initializer, step 8 stamps per commit |
| K-3 | An ambient clock does not reach hosted loops started before the scope, and a test host started inside one test's scope keeps that test's clock | new **[verified]** (`flow/`) |
| K-4 | xUnit v3: an `AsyncLocal` set in an awaiting `InitializeAsync` is lost before the test runs; sync constructor/`InitializeAsync` work; no leak between tests | new **[verified]** (`isolation/`) |
| K-5 | `Id<IUser>` cannot hold an identity provider subject (`Id<T>` wraps a `Guid`) and duplicates `Id<User>` | new, by reading `Id.cs` |
| K-6 | `IUserContext.Roles` puts authorization input into the recorded context; `IEnumerable<string>` is lazy and mutable | new, by reading |
| K-7 | Without logging and OpenTelemetry, ASP.NET Core creates no `Activity`; a correlation id taken from it would be missing | new **[verified]** (`web/`) |
| K-8 | AOT web edge: `Results.Problem` throws at run time without `AddProblemDetails()`, 0 IL warnings at publish | new **[verified]** (`web/`); recorded for the web package |

## 3. Experiments

### 3.1 Clock candidates: user code and test code

(a) **ambient** `AsyncLocal` clock (old `Tarol.Clock`):

```csharp
public sealed partial class Auction
{
    public Result Bid(Money amount) => Clock.UtcNow >= EndsAt ? Error.Conflict("Auction.Ended") : Raise(new BidPlaced(amount));
}
// test
using var _ = Clock.SetProvider(new FakeTimeProvider(start));   // must be in the test, ctor or a sync InitializeAsync (K-4)
auction.Bid(new(10)).IsSuccess.ShouldBeTrue();
// the outbox worker started by the test host does not see this clock (K-3)
```

(b) **injected `TimeProvider` only** (+ `FakeTimeProvider`):

```csharp
public sealed partial class Auction
{
    public Result Bid(Money amount, DateTimeOffset now) => now >= EndsAt ? Error.Conflict("Auction.Ended") : Raise(new BidPlaced(amount, now));
}
public sealed class BidHandler(IAuctionRepository auctions, TimeProvider clock)
{
    public async ValueTask<Result> Handle(PlaceBid cmd, CancellationToken ct)
        => await auctions.GetById(cmd.Auction, ct) switch { Auction a => a.Bid(cmd.Amount, clock.GetUtcNow()), Violation v => v };
}
// test
var clock = new FakeTimeProvider(start);
auction.Bid(new(10), clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
clock.Advance(TimeSpan.FromDays(8));
auction.Bid(new(11), clock.GetUtcNow()).ShouldBe(Result.Failure(Error.Conflict("Auction.Ended")));
```

(c) **both**: (b) everywhere with a container, plus an ambient default where none exists (event timestamps at `Raise`).

| | (a) ambient | (b) injected | (c) both |
|---|---|---|---|
| parallel test isolation | yes (AsyncLocal) **[verified]** | yes, by construction **[verified]** | yes |
| hosted workers / outbox see the test clock | **no** (K-3) **[verified]** | yes (same instance) | only through the injected half |
| timers and delays (`CreateTimer`, `Task.Delay(d, tp)`) | no | yes | injected half only |
| test pitfalls | K-4, forgetting `using`, a scope set in an async helper is lost **[verified]** | none found | both sets |
| cost per "now" | +10 ns (41 vs 31 ns); a scope allocates 72 B **[verified]** | ±0 (31 ns) **[verified]** | |
| AOT | 0 IL warnings **[verified]** | 0 IL warnings **[verified]** | |
| aggregate purity / replay | hidden input | `now` is an explicit input | hidden input at `Raise` |

**Event timestamp** (step 6 depends on it): with (b), Primitives reads no clock; the store stamps `RecordedAt` once per
commit from its injected `TimeProvider` (step 8), and business times are payload fields. Decided: (b), owner round 1.

### 3.2 Markers and `TraceId`

```csharp
// old: a phantom id per concept
Id<IUser> user = ...;        // a Guid, but the IdP says sub = "auth0|5f7c…" or a pairwise string (K-5)
Id<User> sameUser = ...;     // the app's own aggregate id: a second, incompatible type for the same person
Id<ITrace> trace = ...;      // a UUIDv7, not the W3C trace id that APMs show

// proposed
Actor actor = Actor.User(principal.FindFirstValue("sub")!);   // the IdP's subject, a string
Option<string> traceParent = Activity.Current?.Id ?? Option.None;   // W3C traceparent, only as a link
```

Decided: drop the markers (owner round 1). `TraceId` is open (decision 2 below): the W3C `traceparent` string, the
`ActivityTraceId` struct, or nothing in the Kernel type.

### 3.3 `OperationContext` (replaces `IUserContext`)

```csharp
// at the edge (web package, later): built once from the request
var context = OperationContext.Start(Actor.User(sub), tenant: new TenantId("acme"), clock);   // correlation = new UUIDv7

// handler (step 10): received, passed to the unit of work, never looked up
public async ValueTask<Result> Handle(CancelAuction cmd, OperationContext context, CancellationToken ct) { … }

// saga (step 9): a system actor on behalf of the original user, same correlation, causation = the handled message
var next = context.ForMessage(messageId).AsSystem("Sagas.AuctionTimeout");

// test: constructed directly, nothing global
var context = new OperationContext(Actor.User("alice"), new TenantId("t1"), Guid.CreateVersion7(), Option.None, Option.None);
```

| Question | Proposal | Why |
|---|---|---|
| anonymous caller | `Actor.Anonymous` (kind `Anonymous`, id `"anonymous"`) | a valid actor; audit records it |
| internal caller | `Actor.Service(clientId)` (another process) vs `Actor.System(component, onBehalfOf)` (Tarol itself, later) | they are audited and authorized differently; the old `Internal` merged them |
| no tenant | `Option<TenantId>.None` | single-tenant app or host-level operation; "required but missing" is a 401 at the edge |
| roles | not in the context (K-6) | authorization input stays in `ClaimsPrincipal` |
| provision | built at the edge and passed (scoped service or parameter, step 10); no `AsyncLocal`, no accessor | time.md §5 and K-3/K-4; an ambient identity that leaks is a security bug |
| package | `Tarol.Kernel` | steps 6, 8, 9, 10, 11 and the web package all need the same types; none of them is "the web" |

JSON **[verified]** (`authz/`, source generator, Native AOT): `{"eventId":…,"recordedAt":…,"actor":{"kind":"System",
"id":"Sagas.AuctionTimeout","onBehalfOf":"alice"},"tenant":{"value":"t1"},"correlationId":…,"causationId":…,
"traceParent":"00-0f1c…-698734cecb680cf9-03"}`. Two things to fix in the real type: `TenantId` should be a JSON string,
not `{"value":…}`, and an `Option<TenantId>` needs `TenantId` metadata in the caller's context (the documented Option
rule; the first run threw `NotSupportedException` naming the attribute to add). The Kernel therefore ships a converter
on `TenantId` and a `KernelJsonContext` to combine with the app's context.

### 3.4 Security

**[verified]** (`authz/`, JIT and Native AOT, 0 IL warnings; `web/`):

| Check | Result |
|---|---|
| seller cancels (plain permission, no ASP.NET Core) | `Success` |
| other user | `Error.Forbidden` → 403 |
| anonymous | `Error.Unauthorized` → 401 |
| other tenant | `Error.NotFound` → 404 (existence not revealed) |
| permission passes, invariant fails (already cancelled) | `Error.Conflict` (the aggregate's own rule) |
| saga `Actor.System("Sagas.AuctionTimeout", onBehalfOf: "alice")` | `Success` |
| `IAuthorizationService` + `AuthorizationHandler<OperationAuthorizationRequirement, Auction>` in a console (`AddAuthorizationCore`) | seller `Success`, other user `Forbidden`, unauthenticated `Unauthorized` |
| actor in event metadata (source-generated JSON) | round-trips; absent tenant/trace are `null` |

User code for both permission styles is in [security.md](../architecture/security.md) §6. Comparison with the old
`IAuthorizer`: it has no subject, action or resource and cannot express any row above; dropped. Decided: design now,
build `Tarol.Security` in step 10 (owner round 1).

### 3.5 Grouping (D-7)

| Candidate | Content | Verdict |
|---|---|---|
| one `Tarol.Kernel` with clock, markers, context, security | everything from the four old projects | rejected: the clock goes (time.md), the markers go (K-5), security waits for its pipeline (step 10) |
| **`Tarol.Kernel` = context data + time analyzer** | `Actor`, `ActorKind`, `TenantId`, `OperationContext`, `KernelJsonContext`, analyzer TKRN001 | **chosen** (owner round 1) |
| Kernel + `Tarol.Security` now | + permission glue | deferred to step 10 |

D-7's example ("an ambient clock that tests replace belongs with the markers") does not apply any more: neither ships.

## 4. Proposed API (first proposal; what shipped is §7 and §8)

```csharp
namespace Tarol.Kernel;

[JsonConverter(typeof(JsonStringEnumConverter<ActorKind>))]
public enum ActorKind { Anonymous, User, Service, System }

/// Who acts: the kind and the identity provider's subject (or a client id, or a component name). No personal data.
public sealed record Actor
{
    public ActorKind Kind { get; }
    public string Id { get; }                       // not empty
    public Option<string> OnBehalfOf { get; }       // only for System

    public static Actor Anonymous { get; }
    public static Actor User(string subject);
    public static Actor Service(string clientId);
    public static Actor System(string component, Option<string> onBehalfOf = default);
}

/// A tenant key: any non-empty string up to 128 characters (slug, GUID text, number). JSON: a string.
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId : ISpanParsable<TenantId>, ISpanFormattable
{
    public TenantId(string value);
    public string Value { get; }
}

/// What every operation and every event records about its origin. Immutable; built at the edge and passed on.
public sealed record OperationContext(
    Actor Actor,
    Option<TenantId> Tenant,
    Guid CorrelationId,
    Option<Guid> CausationId,
    Option<string> TraceParent)
{
    /// Starts a conversation: new UUIDv7 correlation from the clock, the current W3C traceparent if any.
    public static OperationContext Start(Actor actor, Option<TenantId> tenant, TimeProvider clock);
    /// The same conversation, caused by the message or command with this id (sagas, outbox, consumers).
    public OperationContext ForMessage(Guid messageId);
    /// The system continues on behalf of the current actor (keeps the user's subject in OnBehalfOf).
    public OperationContext AsSystem(string component);
}

/// Source-generated JSON metadata for the Kernel types; combine it with the app's context.
public sealed partial class KernelJsonContext : JsonSerializerContext;
```

Analyzer `TKRN001` (Warning, decision 6): `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`,
`DateTimeOffset.UtcNow` in code that references `Tarol.Kernel` → "inject `TimeProvider` (or take `now` as a
parameter)". `TimeProvider.System` is allowed (composition root).

## 5. Planned projects

| Project | Content |
|---|---|
| `src/Kernel/Tarol.Kernel` | the types above, README, skill `skills/tarol-kernel/SKILL.md`; references `Tarol.Options` (and, after decision 3, `Tarol.StronglyTypedId`) |
| `src/Kernel/Tarol.Kernel.Generators` | `TimeAnalyzer` (TKRN001), bundled (D-10), `AnalyzerReleases.*.md` |
| `tests/Kernel/Tarol.Kernel.Tests` | types, JSON, findings (14, K-2 by construction, K-3/K-4 as clock-isolation tests that prove the injected design), parallel test classes |
| `tests/Kernel/Tarol.Kernel.Generators.Tests` | one test per diagnostic case |
| `tests/Kernel/Tarol.Kernel.AotSmoke`, `.PackageTests` | AOT checks; package consumer with the analyzer |

## 6. Not decided here

* How handlers receive the context (parameter vs scoped service): step 10.
* Event metadata columns and the tenant in the stream key: steps 6 and 8.
* The web edge (context from claims, tenant resolution, problem details): the later web package.

## 7. Decisions for the owner

Round 1 (scope, after the concept review, 2026-11-13):

| # | Decision | Recommendation | Owner's answer |
|---|---|---|---|
| S-1 | `Tarol.Security` now or with step 10 | design now, build in 10 | **design now, build in 10** |
| S-2 | does Tarol ship a clock | no: inject `TimeProvider`, `RecordedAt` per commit, `now` as an argument, analyzer | **no Tarol clock** |
| S-3 | Kernel scope | context data only; markers and `IAuthorizer` dropped | **context data only** |
| S-4 | where the `Tarol.Options` split goes | new PR to `main` (Step 3 was not on `main`) | **new PR to main** (#6) |

Round 2 (design, with call-site code in the question previews, 2026-11-13):

| # | Decision | Recommendation | Owner's answer |
|---|---|---|---|
| 1 | where an event's time comes from | one `RecordedAt` per commit from the store's injected `TimeProvider`; business time as payload fields, `now` passed into the behaviour | **as recommended** |
| 2 | what the context carries for tracing | `Option<string>` W3C `traceparent` as a link | **nothing in the Kernel** (tracing stays in `Activity`; steps 8/9 may store a link in their own metadata) |
| 3 | context shape: ids and tenant | `Guid` correlation/causation, `TenantId` over `string` | **`Id<T>` for all three**: `Id<ITenant>`, `Id<ICorrelation>`, `Id<ICausation>`; Kernel references `Tarol.StronglyTypedId` (asked twice: the first answer "both typed IDs" was ambiguous, the second conflicted with "Options only", the third settled it) |
| 4 | how a resource permission is written (step 10) | ASP.NET Core `AuthorizationHandler<TRequirement, TAggregate>` + `IAuthorizationService`, mapped to `Error.*` | **as recommended** |
| 5 | package dependencies | `Tarol.Options` only | `Tarol.Options` **and `Tarol.StronglyTypedId`** (follows from 3) |
| 6 | analyzer for the system clock | TKRN001 as Warning | **Warning** |

Applied without a question: the phantom types are **interfaces** (`ITenant`, `ICorrelation`, `ICausation`) so that they
never clash with an application's own `Tenant` aggregate (`Id<Tenant>`), and `using Tarol.Kernel;` next to the app's
namespace stays unambiguous.

## 8. Changes against Tarol

Projects:

| Tarol-Net | Content | From |
|---|---|---|
| `src/Kernel/Tarol.Kernel` | `Actor`, `ActorKind`, `OperationContext`, `ITenant`, `ICorrelation`, `ICausation`; `Json/ActorJsonConverter`, `Json/OperationContextJsonConverter`, `Json/KernelJsonContext`; README; skill `skills/tarol-kernel/SKILL.md` | `src/Kernel/Kernel`, `src/Contexts/Contexts` (redesigned) |
| `src/Kernel/Tarol.Kernel.Generators` | `TimeAnalyzer` (TKRN001), bundled | new |
| `tests/Kernel/Tarol.Kernel.Tests` | 44 tests: `ActorTests`, `OperationContextTests`, `KernelJsonTests`, `ClockIsolationTests` (two parallel classes + a worker started before the clock moves), `FindingTests` | new (the old projects had no tests) |
| `tests/Kernel/Tarol.Kernel.Generators.Tests` | 3 tests for TKRN001 | new |
| `tests/Kernel/Tarol.Kernel.AotSmoke`, `.PackageTests` | 7 native checks; a consumer of the packed package that checks the Kernel, Options and StronglyTypedId analyzers reach the compiler | new |
| — | `src/Clock/Clock`, `src/Security/Security` | dropped (time.md, security.md §7) |

Public API and behaviour (every change is deliberate):

| Tarol (6a62f81) | Tarol-Net | Why |
|---|---|---|
| `Tarol.Clock` (`static class Clock` in namespace `Tarol`: `Provider`, `UtcNow`, `SetProvider`) | **removed**; inject `TimeProvider`, `FakeTimeProvider` in tests; TKRN001 | S-2; K-1, K-3, K-4 |
| `Tarol.Primitives.DomainClock` (global static) and the `Timestamp` field initializer | not in the Kernel; step 6 removes them, step 8 stamps `RecordedAt` per commit | 14, K-2; decision 1 |
| `IUser`, `ITrace` | **removed**; the caller is `Actor`; tracing stays in `Activity` | K-5; decision 2 |
| `ITenant` (`;`-form interface) | kept as the phantom type of `Id<ITenant>`; new `ICorrelation`, `ICausation` | decision 3 |
| `IUserContext { Id<IUser> UserId; Id<ITenant> TenantId; Id<ITrace> TraceId; IEnumerable<string> Roles; bool IsAuthenticated; ContextUserType UserType; }` | `sealed record OperationContext(Actor Actor, Option<Id<ITenant>> Tenant, Id<ICorrelation> CorrelationId, Option<Id<ICausation>> CausationId)` + `Start`, `CausedBy`, `AsSystem`; a value, not an interface | identity-context.md; K-6 |
| `ContextUserType { Anonymous, User, Internal }`, `IsAuthenticated` | `ActorKind { Anonymous, User, Service, System }` in `Actor(Kind, Id, OnBehalfOf)` | "internal" split into Service and System |
| `Roles` | not in the context (authorization input stays in `ClaimsPrincipal`) | K-6 |
| namespace `Tarol.Contexts` | `Tarol.Kernel` | D-7 |
| `Security.IAuthorizer.IsAuthorizedAsync(ct)` | **removed**; `Tarol.Security` designed in security.md, built in step 10 | 26; S-1 |
| no JSON | fixed camelCase contract via converters on `Actor` and `OperationContext`; invalid input is a `JsonException` | event metadata must not depend on the caller's naming policy |
| no analyzer | TKRN001 (Warning) | decision 6 |
| Kernel → StronglyTypedId | Kernel → `Tarol.Options`, `Tarol.StronglyTypedId` | decisions 3, 5 |

Found while implementing (both **[verified]**, both in tests):

* **Nested contexts and generated fast paths.** With `[JsonSourceGenerationOptions]` on an app context, a property of type
  `Option<Id<ITenant>>` is serialized with the app context's *own* options, so the option converter cannot find
  `Id<ITenant>` metadata even when `KernelJsonContext` is combined into the outer options; and an app type that *holds*
  an `OperationContext` inlines it, so no resolver order helps. The first design (generated metadata plus "list the ids
  or combine") was a trap for every user. Hand-written converters on `Actor` and `OperationContext` remove it: the app
  lists only its own type (`KernelJsonTests.Context_InsideAnAppType_Should_NeedNoExtraMetadata`, AOT smoke).
* **TOPT004 in the wild.** The first converter wrote `tenant = isNull ? default : Id<ITenant>.From(…)`, which reads back
  `Some(Empty)`. The Kernel project did not run the Options analyzer (analyzer project references do not flow), so it went
  unnoticed until the round-trip test failed. The Kernel now references the Options analyzer for its own code, and the
  same line produces `error TOPT004` (checked by putting the bug back).

## 9. Suppressions and analyzer configuration

| Where | Rule | Reason |
|---|---|---|
| `ITenant`, `ICorrelation`, `ICausation` | `[SuppressMessage]` CA1040 "Avoid empty interfaces" | phantom type arguments for `Id<T>` (decision 3), never implemented |
| `tests/Kernel/Tarol.Kernel.Tests/AssemblyInfo.cs` | `[assembly: Xunit.v3.Parallelization(MaxThreads = -1)]` | the clock-isolation classes must run at the same time on any machine (xUnit v3 defaults to one thread per core) |
| `Tarol.Kernel.Generators.Tests` | `LanguageVersion.Preview` | the shared test host (Roslyn 5.9 calls unions preview, results.md §8) |

No `NoWarn` and no `.editorconfig` change. New package version: `Microsoft.Extensions.TimeProvider.Testing` 11.0.0
(tests and the AOT smoke app only).

## 10. Verification (SDK 11.0.100, 2026-11-13)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | clean (exit 0) |
| `dotnet build Tarol.slnx -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | **334 passed**, 0 failed: Kernel 44, Kernel analyzer 3, Options 48 + 11, Results 136 + 10, StronglyTypedId 62 + 17, harness 3 |
| clock isolation | `ClockIsolationFakeTimeTests` and `ClockIsolationSystemTimeTests` meet at a rendezvous (the test fails if they do not overlap), then run 250–300 ms side by side: 0 of the system-clock contexts carry the other test's year 2000; 5 consecutive runs green |
| `eng/aot-smoke.sh` | `Tarol.Kernel.AotSmoke`: 0 IL warnings, 7/7 native checks; Options, Results, StronglyTypedId unchanged |
| `eng/verify-packages.sh` | OK; `Tarol.Kernel` contains `analyzers/dotnet/cs/Tarol.Kernel.Generators.dll` and `skills/tarol-kernel/SKILL.md`; the consumer's `project.assets.json` lists `tarol-kernel`, `tarol-options` and `tarol-strongly-typed-id` |
| analyzers reach a package consumer | a temporary traps file in `Tarol.Kernel.PackageTests` → `error TKRN001` (`DateTimeOffset.UtcNow`, `DateTime.Now`) and `error TOPT004` (through the Options dependency); then removed |
| skill sample | the "Minimal correct example" compiled with warnings as errors and the bundled analyzers, and run: prints `True` |

```
Tarol.Kernel 0.0.0-alpha.0.10
    LICENSE
    README.md
    Tarol.Kernel.nuspec
    analyzers/dotnet/cs/Tarol.Kernel.Generators.dll
    lib/net11.0/Tarol.Kernel.dll
    lib/net11.0/Tarol.Kernel.xml
    skills/tarol-kernel/SKILL.md
== tests/Kernel/Tarol.Kernel.PackageTests/Tarol.Kernel.PackageTests.csproj
ok   system actor on behalf of the user
ok   JSON round-trip from the package
ok   TimeProvider is the clock
ALL CHECKS PASSED
```

## 11. Skill evaluation checklist

Run each task with a frontier model in a fresh console project that references the packed package, once with the
skills copied to `.agents/skills/` and once without. Record correctness and token cost.

`tarol-kernel`:

1. "Record who closed the auction, including when a timeout saga does it" → `context.CausedBy(messageId).AsSystem("Sagas.…")`;
   `Actor.System(…, onBehalfOf: user)`; no names or e-mails in the actor.
2. "The auction must end at `EndsAt`; make it testable" → `Bid(amount, DateTimeOffset now)` with `now` from an injected
   `TimeProvider`; `FakeTimeProvider` + `Advance` in the test; no `DateTimeOffset.UtcNow` (TKRN001).
3. "Add the tenant to the context of an incoming request" → `OperationContext.Start(Actor.User(sub), tenantId, clock)` with
   `Option<Id<ITenant>>`; not from the command body.
4. "Serialize an audit record that contains the context in a Native-AOT app" → lists only the audit type in its context.
5. "Write a test for a handler that needs the current user" → constructs `OperationContext` directly; no static, no `AsyncLocal`.
6. Must not: use `IUserContext`, `Id<IUser>`, `Tarol.Clock`/`DomainClock`, `IAuthorizer`; put roles in the actor; set a
   tenant from the payload; use `default` for an absent `Option<Id<ITenant>>`; decompile the assembly or search the web.

Status: checklist written; **not yet run** (needs a model-evaluation harness outside this repository).

## 12. What later steps must do (also in plan.md)

| Step | Must |
|---|---|
| 5 Validations | prerequisites that compare with time take `DateTimeOffset now` (ArtFlow's injected `TimeProvider` in prerequisites goes) |
| 6 Primitives | delete `DomainClock` and the `Timestamp` initializer; no clock read when raising or deserializing; event metadata slots typed with Kernel types (`Actor`, `Option<Id<ITenant>>`, `Id<ICorrelation>`, `Option<Id<ICausation>>`); `using Tarol.Kernel;` where needed, no `global using` |
| 8 Persistence | stamp one `RecordedAt` per commit from the injected `TimeProvider`; store actor, tenant, correlation, causation per event; tenant in the stream key and in every query filter (cross-tenant = not found); decide whether to store a `traceparent` link (tracing.md) |
| 9 Sagas | replace `SagaOptions.UtcNow` by the injected `TimeProvider` (also for `Task.Delay`, polling and leases; fix S15); `Sagas.Testing` uses `FakeTimeProvider`; carry `OperationContext` in outbox rows and messages; system actor via `AsSystem` |
| 10 CQRS | the context per command (parameter or scoped service); build `Tarol.Security` (security.md §5, §8): resource permission with `IAuthorizationService`, outcome → `Error.Unauthorized`/`Forbidden`/`NotFound`, system principal |
| 11 Messaging | envelope headers from `OperationContext`; inject `TimeProvider` into `MessageBackplaneRunner`; topic authorization with tenant-prefixed groups |
| web package | context from `ClaimsPrincipal` at the edge; `Error` → problem details with `AddProblemDetails()` (AOT, K-8) |
