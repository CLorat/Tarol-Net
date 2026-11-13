# Validation

Concept note for step 5 (see [validations.md](../migration/validations.md)). It covers what validation means in an
event-sourced DDD framework, the four kinds of rules, where each one runs, what .NET 11 already provides, how failures
map to `Tarol.Results`, `Option<T>`/`Id<T>`/collections, and replay safety. It starts from first principles; the old
code (`CLorat/Tarol@6a62f81`) is a seed, reviewed in the migration note.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and command in
[`probes/validations/`](../migration/probes/validations/), output in
[`mev/output.txt`](../migration/probes/validations/mev/output.txt)); **[unverified]** means reasoned or read, not run
here. "By reading" means verified by reading the named code, not by running it.

## 1. What a user of Tarol needs it for

* "The name has 2–50 characters, the e-mail looks like an e-mail, the quantity is 1–100" (shape of an input).
* "`Money` is never negative; a `DateRange` never ends before it starts" (a value that cannot exist otherwise).
* "A settled auction has no open bids" (always true for the aggregate, after every change).
* "You can only bid while the auction is open, in its currency, above the current price" (precondition of one change,
  against the current state and the current time).
* "Only the seller may cancel" — **not validation**: a permission ([security.md](security.md)).
* The same rules for a command from HTTP, from a message, from a saga, and in a unit test without a host.
* Old events always load, even after a rule became stricter.

## 2. Four kinds of rules

| Kind | Question | Input | Pure? | Failure | Runs | Never runs |
|---|---|---|---|---|---|---|
| **Value rule** | Is this input well-formed? | the input alone | yes (no I/O, no clock) | `Invalid` (field path + code, 400), **collect all** | at the edge of the domain: when a command / DTO is turned into value objects (endpoint, message handler, saga step, test) | on load, replay, snapshot, deserialization |
| **Value-object invariant** | Can this value exist? (`Amount >= 0`, `Start <= End`) | the value's own fields | yes | `Invalid` with the *input's* field path | in the value object's **factory** (`Money.Create`) — it is a value rule of the factory's input | in the constructor that deserialization uses |
| **Aggregate invariant** | Is the aggregate still consistent after this change? | aggregate state | yes | `Error` (state, not input; see §5) | live path only: after the behaviour has applied its events, before commit | replay, snapshot, projections |
| **Prerequisite** (precondition) | May this change happen *now*, in *this* state? | command + current state + `now` (+ sometimes a read) | yes, if `now` and reads are parameters | first failure wins, usually `Error.Conflict`; `Invalid` when it points at an input field (§5) | live path only: after load and permission, before the events are raised | replay (a closed auction must still load) |
| **Permission** | May *this caller* do it? | principal + command + resource | yes | `Error.Unauthorized/Forbidden/NotFound` | command pipeline, after load (security.md §3) | inside the aggregate |

**[unverified]** as a table; the ArtFlow rows below are verified by reading.

ArtFlow, sorted into these kinds (by reading `examples/ArtFlow/ArtFlow.Domain`):

| ArtFlow rule | Old mechanism | Kind | Note |
|---|---|---|---|
| `Money.Amount` `[Range(0, int.MaxValue)]` **and** `[Invariant] Amount >= 0` | attribute + invariant | value-object invariant | the same rule twice; `new Money(-5, EUR)` still succeeds (review §2.3) |
| `ArtWork.Title` `[Length(3, 10)]`, `Bidder.Name` `[Length(3, 60)]` | attribute on an **aggregate property** | value rule of the *command* that sets the title | see insight 1 |
| `Auction.ArtWork` `[NotEmpty]` | attribute on an aggregate property | value rule of `CreateAuction.ArtworkId` | `Id<T>.Empty` is valid on every inbound path (stronglytypedid.md) |
| `WhenClosedEndtimeMustBeSet`, `WhenSettlesBidsMustBeEmpty` | `[Invariant]` | aggregate invariant | |
| `AuctionIsOpen` (injects `TimeProvider`), `EndtimeMustBeInFuture` (injects `TimeProvider`) | `IPrerequisite(Async)` | prerequisite with `now` | kernel.md §12: `now` becomes a parameter |
| `BidMustBeInOriginalCurrency` (order 1), `BidAmountLargerThanCurrentBid` (order 2) | `IPrerequisite` + `Order` | prerequisites, **ordered** | `Money.CompareTo` throws `InvalidOperationException` for different currencies, so order 2 crashes if it runs first (insight 4) |
| `ArtworkVerified` (`IArtWorkService`), `ExpertIsValid` (`IExpertDomainService`) | `IPrerequisiteAsync` | prerequisite with a **read of another aggregate** | not a domain rule of this aggregate; eventually consistent (insight 5) |
| `NotVerified` returns `IsVerified` | `IPrerequisite` | prerequisite | inverted (finding 17): a `bool` with a separate code/text hides the meaning |

## 3. Where each kind runs: the change pipeline

The order given for step 10 is authenticate → load → permission → validate → apply → invariants → commit. Split
"validate" into its two kinds and the pipeline becomes:

```
request / message / saga step
  ─► authenticate (401)                     edge (web package) or the message's OperationContext
  ─► coarse policy (403)                    edge
  ─► parse input → value objects (400)      Invalid, collect all; pure, no I/O        ◄ value rules + VO invariants
  ─► load aggregate, tenant-scoped (404)    store (step 8)
  ─► resource permission (403 / 404)        Tarol.Security (step 10)
  ─► behaviour(command values, now)         aggregate method                          ◄ prerequisites (guards)
       guards → Result; raise events
  ─► invariants on the new state            live path only; failure discards the instance ◄ aggregate invariants
  ─► commit (expected version)              store; RecordedAt, actor, tenant (step 8)
```

* **Parsing before loading** (a deviation from the order above, proposed): value rules need no state, so they can run
  before the store is touched — a malformed command costs no read. It reveals nothing about the resource (the answer
  is the same whether the auction exists or not), and it comes after authentication and the coarse policy, so an
  anonymous caller learns nothing. **[unverified]**; open question 3.
* **Inside the value object's factory** for value-object invariants: once a `Money` exists it is valid by construction
  ("parse, don't validate"), so nothing downstream re-validates it.
* **Prerequisites are guard clauses of the behaviour**, not a separate stage, when they read only the aggregate, the
  command and `now`: `auction.PlaceBid(bidder, amount, now) : Result<…>`. That is where ArtFlow's sync prerequisites go
  (insight 3).
* **After apply, live only** for aggregate invariants. `Dispatch` mutates in place and has no rollback (review §2.3,
  by reading `AggregateRoot.cs`), so a violation must make the pipeline discard the instance and its uncommitted
  events.
* **Never** in `Dispatch`/`On` handlers, `ReplayHistory`, `FromSnapshot`, event deserialization, or projections
  (§7).

## 4. What .NET 11 / ASP.NET Core already provide (do not reinvent)

| Facility | What it gives | Verdict for Tarol |
|---|---|---|
| `System.ComponentModel.DataAnnotations` (`Validator.TryValidateObject`) | attributes, `IValidatableObject` | reflection (`RequiresUnreferencedCode`); flat (no nested paths without help); messages, no codes **[unverified here; documented]**. Baseline only (candidate d) |
| **`Microsoft.Extensions.Validation` 11.0.0** (`AddValidation()`, `[ValidatableType]`, `IValidatableTypeInfo.Validate/ValidateAsync`) | a source generator over DataAnnotations attributes; ASP.NET Core 10+ runs it for Minimal API parameters | see below |
| `[GeneratedRegex(pattern, options, matchTimeoutMilliseconds)]` | AOT-compiled regex with a timeout | the only regex form Tarol code or generated code should use |
| `IValidatableObject` | cross-field rule on a DTO | runs **only when every member attribute passed** **[verified]** |
| `JsonSerializerOptions.RespectNullableAnnotations` / `RespectRequiredConstructorParameters` (.NET 9+) | `null` for a non-nullable property / a missing constructor parameter fails deserialization | "required" for non-`Option` members can be the deserializer's job **[unverified here]**; open question 6 |

`Microsoft.Extensions.Validation` measured **[verified]** (`probes/validations/mev`, console app, no ASP.NET Core, JIT
and Native AOT):

* **Works without ASP.NET Core** (DI + `ValidationOptions`) and **under Native AOT**. The only IL warning came from
  DataAnnotations itself: `MinLengthAttribute` is `RequiresUnreferencedCode` (IL2026) on a collection.
* **Nested and collection paths are right**: `Lines[1].Sku`, `Shipping.Street`; nested records are discovered from the
  root `[ValidatableType]`. One error per failing attribute, no duplicates.
* **No codes.** `ValidationError` has `Name`, `Path`, `ErrorMessage`, `Container`: the failing attribute is not
  reported. Mapping to `FieldError(Field, Code)` has to derive the code from the message, or every attribute must set
  `ErrorMessage = "<code>"`.
* **`Option<T>` breaks it at run time**: `[StringLength]` on an `Option<string>` property throws
  `InvalidCastException` inside `StringLengthAttribute.IsValid`. No build-time diagnostic. Without an attribute the
  `Option` is ignored (a too-long `Some` passes).
* **ReDoS**: `[RegularExpression(@"^(a+)+$")]` on 30 × `a` + `!` runs **≈ 2.0 s** (the DataAnnotations default timeout)
  and then **throws `RegexMatchTimeoutException`** out of `Validate` instead of reporting a field error.
* **Allocations**: ≈ 5.1 KB per *valid* object (7 properties, 1 list element, 1 nested), ≈ 8.3 KB per invalid one with 7
  errors; first call 53 KB (JIT) / 136 KB (AOT).

So `Microsoft.Extensions.Validation` is the right tool **at the web edge for plain DTOs** (ASP.NET Core already runs it),
and the wrong tool **inside the domain**: no codes, no `Result`, breaks on `Option<T>`, allocates kilobytes per call,
and needs a service provider. Tarol should not compete with it at the edge; the web package maps its output (or its
problem details) to `Invalid` where the two meet (open question 2).

**What must work without ASP.NET Core**: everything in the domain and the command pipeline — value objects, guards,
invariants, and the handlers that run them from messages, sagas and tests. None of it may need a service provider.

## 5. Which failure is `Invalid`, which is `Error`

`Tarol.Results` has exactly two failure kinds (results.md Q-2): `Invalid` (one or more `FieldError(Field, Code,
Detail?)`, 400) and `Error` (`ErrorType` + code, one).

| Situation | Failure | Why |
|---|---|---|
| input malformed (value rule, VO invariant) | `Invalid`, all fields at once | the caller can fix every field in one round trip |
| a precondition that names an input field (`endTime` must be ≥ 1 day after `now`) | `Invalid.Field("endTime", "TooSoon")` | the caller fixes that field |
| a precondition about state (`Auction.NotOpen`, `Bid.CurrencyMismatch`) | `Error.Conflict(code)` (409) | no field to fix; retrying later may succeed |
| a read failed (`Artwork.NotFound` in another aggregate) | `Error.NotFound` / `Error.UnprocessableEntity` | |
| aggregate invariant broken after apply | `Error` (type: open question 4) with the invariant's code | it is state, not input; a field path into the aggregate (finding 32) means nothing to the caller |

Rule of thumb: **`Invalid` when the field path is a path in the caller's input; otherwise `Error`.** Field paths are
therefore *input* paths (command / DTO members), never aggregate members.

## 6. `Option<T>`, `Id<T>`, collections, nested values

* **`Option<T>` means optional by type**: `None` passes; the rules apply to the value when it is `Some`. "Required" is
  expressed by *not* using `Option` — a `T` member is required. No `[Required]` on an `Option<T>`, ever.
* **Required `T` members from JSON**: a reference type can still arrive as `null` (and a struct as `default`) unless the
  deserializer enforces it. Either the rule helpers report `Required` for `null`/`default`, or the JSON options do
  (§4). Open question 6.
* **`Id<T>`**: `Id<T>.Empty` deserializes and binds everywhere (stronglytypedid.md); "not empty" is a value rule
  (`id.IsEmpty` → `FieldError(field, "Required")`).
* **Collections**: per-element errors carry an index path (`Lines[2].Quantity`); count rules (`1..50 lines`) are on the
  collection path (`Lines`). Without reflection: the helper must take `IReadOnlyList<T>`/span and a per-element
  callback, not `object`.
* **Nested values**: a nested factory reports paths *relative to its own input* (`Amount`); the caller **prefixes**
  (`Price.Amount`, `Lines[2].Price.Amount`). So a value object's factory is reusable in any command, and the prefix is
  the only thing that knows the wire shape.
* **Casing**: C# member names (`Lines[2].Quantity`) or the JSON names (`lines[2].quantity`)? The domain does not know the
  JSON naming policy; the web package does. Open question 5.

## 7. Replay safety

Old events must always load, even when today's rules are stricter: a `Money(-1)` recorded under last year's rules, an
auction that ended last week, a title of 12 characters before the limit became 10. Where validation could sneak into
loading, and how each is closed:

| Path | Risk | Enforced by |
|---|---|---|
| `Dispatch` / `On(event)` handlers, `ReplayHistory` | an invariant or rule call inside apply rejects history | design (rules are called by the pipeline, never by apply) + a **replay test** (step 6 owns the aggregate; see open question 7) |
| `FromSnapshot` | same | the same test, with a snapshot |
| **event deserialization of a value object** | if `Money`'s constructor validates, an old event with `Money(-1)` throws `JsonException` | rule: **constructors trust, factories validate** — the constructor that STJ uses never validates; only `Create(…) : Result<T>` does. Test: deserialize an event whose value is invalid today |
| projections | a read model rebuild rejects old data | projections never call rules |
| prerequisites with time | `EndtimeMustBeInFuture` on a replayed auction is false for every auction ever created | prerequisites are guards of the *live* behaviour; apply handlers take the event's data as fact |

An **analyzer** could report a rule call (a `Create` factory, a `Validate*`/`CheckInvariants` call, `Invalid`
construction) inside an `On`/apply method or a `[JsonConstructor]` constructor. It needs the aggregate and event shape
of `Tarol.Primitives` (step 6) to know what an apply method is **[unverified]**.

## 8. What must stay AOT-safe, incremental and testable

* **AOT**: no `object`-typed rule evaluation, no attribute reading at run time, no `Regex` without `[GeneratedRegex]`, no
  `RegexOptions.Compiled` (interpreted under AOT, finding 16). Rules are plain generic methods on concrete types.
* **Allocation**: the valid path should allocate nothing (a `Result` success is a cached sentinel, results.md §0);
  only failures allocate their `FieldError`s.
* **Generators** (only if one survives stage 2): per-type equatable models, no `Collect()` over all types, no
  `Diagnostic`/`Location` in models, full-name attribute matching, hint names from the full symbol key, one diagnostic
  per misuse, incrementality tests (`TrackIncrementalGeneratorSteps`) with the shared harness.
* **Testable**: every rule is a pure function, so a test is `Money.Create(-1, Eur).ShouldBe(Invalid.Field(…))` with no
  host, no DI, no clock.

## New insights

1. **Value attributes on aggregate properties are a category error.** An aggregate's state changes only through events
   raised by its own behaviour, from inputs that were already parsed. Checking `[Length]` on `ArtWork.Title` after the
   fact either repeats the input rule (redundant) or is an invariant (and then a stricter limit makes old aggregates
   "invalid" on every check). Value rules belong to **inputs** (commands, DTOs) and to **value-object factories**, not
   to entities. This removes most of what the old Value generator did, and the reason `Value` carried
   `[GenerateValidation]` (finding 7).
2. **A field path is a path in the caller's input, not in the domain.** Invariants get no `FieldError` (finding 32 is
   resolved by not needing it), and nested factories report relative paths that the caller prefixes.
3. **Most prerequisites are not a framework type.** With `now` as a parameter (kernel.md §12), a prerequisite that reads
   the aggregate, the command and the time is a guard clause in the behaviour method that returns `Result`. A separate
   `IPrerequisite<TChange>` class existed because the old Changes design put the behaviour outside the aggregate and
   used DI for the clock; both reasons are gone.
4. **Value rules collect all; prerequisites stop at the first failure.** A user fixes all input fields in one round trip,
   but later preconditions depend on earlier ones: ArtFlow's amount comparison throws unless the currency check ran
   first (`Money.CompareTo`). The old `Order` attribute encoded this dependency by number; sequential code in a method
   makes it explicit.
5. **Prerequisites that read another aggregate are not domain rules.** "The artwork is verified" and "the expert exists"
   are reads across consistency boundaries, true at check time and possibly false at commit. They belong to the handler
   (step 7/10) and must be documented as eventually consistent. True set rules ("e-mail unique") cannot be prerequisites
   at all: they need a reservation or a unique index in the store (step 8).
6. **Time-dependent rules can never be value rules.** "End time at least one day in the future" depends on `now`, so it
   is a precondition of the live change, and the only correct failure on replay is none.
7. **Constructors trust, factories validate.** The constructor of a value object is the deserialization and replay path;
   the factory is the live path. This single rule is what makes value objects compatible with event sourcing; validating
   in the constructor (the obvious "always valid" design) breaks loading old events.
8. **`Microsoft.Extensions.Validation` is an edge tool.** It is AOT-ready and gets paths right, but it has no codes,
   throws on `Option<T>` attributes and on regex timeouts, and allocates ≈ 5 KB per valid call **[verified]**. Tarol uses
   it (through ASP.NET Core) at the web edge for DTOs and does not build a competing attribute system for the edge.
9. **A regex timeout is a failure, not an exception.** Both DataAnnotations and any `[GeneratedRegex]` rule throw
   `RegexMatchTimeoutException`; a Tarol pattern rule must catch it and report a field error, or a crafted input turns
   into a 500 after two seconds of CPU **[verified for DataAnnotations]**.
10. **An invariant check after apply is a safety net with a cost**: there is no rollback, so it must run on an instance
    the pipeline is willing to throw away. Whether invariants are the *primary* check ("apply, then check") or an
    assertion behind guards decides whether a violation is a 409 for the user or a 500 for the developer.

## Open questions

These change the scope of step 5; they are asked before stage 2.

1. **What is built in step 5?** Only value validation (rule helpers, path composition, maybe a value-object factory
   generator), with invariants and prerequisites *designed* here and built where the aggregate is (steps 6–7)? Or all
   three as standalone contracts now? (Nothing in the framework can call invariants or prerequisites before step 6/7
   exists.)
2. **Is there a generator at all**, and where does it live? Candidates: none (value objects with hand-written factories
   plus small rule helpers); a generator that writes `Create(…) : Result<T>` from rule attributes on a value object's
   constructor parameters (it needs the Primitives `Value` base, step 6); or the rewritten old property generator.
3. **Pipeline order**: parse input before loading (proposed, §3) instead of validate after the permission?
4. **Aggregate invariants**: primary check ("apply, then check, discard on failure", violation = `Error.Conflict`) or an
   assertion behind guards (violation = `Error.Internal`, a bug)?
5. **Field-path casing**: C# member names, or the JSON naming policy applied by the web package?
6. **Required non-`Option` members**: reported by the rule helpers (`Required` field error) or by the deserializer
   (`RespectNullableAnnotations`, `RespectRequiredConstructorParameters`)?
7. **Replay test and analyzer**: a replay test needs an event-sourced aggregate, which arrives in step 6. Prove it in
   step 5 with a minimal hand-written aggregate in the test project and move the real test to step 6, or move the
   obligation to step 6?
