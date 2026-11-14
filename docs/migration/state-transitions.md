# State transitions: evaluation (stage 2)

Evaluation of the idea in [state-transitions.md](../architecture/state-transitions.md) within the scope the owner decided
(D-1 … D-4): a declared lifecycle, guards generated from it, and a live-only check of every step that `Prepare` reports
at commit; enum first; `Tarol.Primitives`, step 6 part C. Probes and output:
[`probes/state-transitions/`](probes/state-transitions/), [`output.txt`](probes/state-transitions/output.txt).
Labels as in the [conventions](../architecture/conventions.md).

## 0. Summary

**Implemented** in step 6 part C: §9 (the prototype's ids TPRM020–024 below became TPRM016–023, §7).

* **How a lifecycle is defined**: one attribute on the enum, one `[To(...)]` per state that has outgoing edges.

  ```csharp
  [Lifecycle]
  public enum AuctionStatus
  {
      [To(Closed)] Open,          // initial state: the member with value 0 (or [Lifecycle(Initial = "...")])
      [To(Settled)] Closed,
      Settled,                    // no [To]: terminal
  }
  ```

  The field is an ordinary property, changed only in `On` handlers as before:
  `public AuctionStatus Status { get; private set; }` and `private void On(Closed e) => Status = AuctionStatus.Closed;`.
* **What is generated** (`AuctionStatusLifecycle`, next to the enum): `IsEdge(from, to)`, `Initial`, `IsTerminal(s)`,
  `Edges`, a Mermaid `Diagram` (also in the XML doc), and extension members `status.CanTransitionTo(to) : bool` and
  `status.RequireTransitionTo(to) : Result` (`Error.Conflict("AuctionStatus.NotAllowed", "Closed -> Closed")`).
  For every aggregate or entity with a lifecycle field, the Primitives generator adds a live-only step check:
  a step that is not an edge is remembered and `AggregateChanges.Prepare` returns
  `Error.Internal("Lifecycle.ForbiddenStep", "Auction.Status: Settled -> Open by Reopened")` **[verified]**.
* **Replay**: a stored `Settled → Open` loads, records nothing, and the next live step is checked normally
  **[verified]**. The check is in `Publish`, which `ReplayHistory` never calls.
* **Cost**: **0 bytes** per raised event on the valid path **[verified]**; time within noise for root-only checks and
  about **+5 … 15 %** of raise + apply with an entity lifecycle in the hand-written stand-in, mostly a second identity-map
  lookup that the real generated dispatch avoids (§4). Guards: `CanTransitionTo` 1.5 ns (same as `==`),
  `RequireTransitionTo` 2.9 ns, 0 B.
* **AOT**: 0 IL warnings, native run passes **[verified]**. **JSON**: a bare string (`"Closed"`) with
  `UseStringEnumConverter` in a same-assembly source-generated context **[verified]**.
* **Exhaustiveness**: a small `DiagnosticSuppressor` removes CS8524 for `[Lifecycle]` enums only, so a `switch` that
  names every state needs no `_` arm and a missing state stays CS8509 **[verified]**.
* **Generator**: diagnostics TPRM020–024 (targets, unreachable states, redundant edges, unusable enums, `[To]` without
  `[Lifecycle]`), equatable per-enum model, unrelated edits → `Unchanged` **[verified]**.

## 1. User code per candidate (ArtFlow auction)

Common to all three: `Open(...)`, `PlaceBid(bidder, amount, now)` (a behaviour allowed in a state, not a step),
`Close(now)`, `Settle()` (decides each active bid `Won`/`Lost`), the invariant "a settled auction has no active bid",
and a `Reopened` event that was allowed in 2025 and is forbidden today. Full files:
[`App/A.cs`](probes/state-transitions/App/A.cs), [`B.cs`](probes/state-transitions/App/B.cs),
[`C.cs`](probes/state-transitions/App/C.cs).

### (a) enum + lifecycle (proposed)

```csharp
[Lifecycle]
public enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, Settled }

[Lifecycle]
public enum BidStatus { [To(Withdrawn, Won, Lost)] Active, Withdrawn, Won, Lost }

public partial class Auction : AggregateRoot<Auction>
{
    public AuctionStatus Status { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    // EndTime, CurrentPrice, Winner, Bids …

    public Result Close(DateTimeOffset now)
    {
        var allowed = Status.RequireTransitionTo(AuctionStatus.Closed);   // AuctionStatus.NotAllowed
        if (!allowed.IsSuccess) return allowed;
        if (now < EndTime) return Error.Conflict("Auction.NotEnded");     // a condition of this step: a guard
        return Raise(new Closed(now));
    }

    public Result Settle()
    {
        if (!Status.CanTransitionTo(AuctionStatus.Settled)) return Error.Conflict("Auction.NotClosed");   // own code
        // … decide the bids (each Active -> Won/Lost is checked as a Bid step) …
        return Raise(new Settled(winner?.BidderId, winner?.Amount ?? CurrentPrice));
    }

    private void On(Closed e) { Status = AuctionStatus.Closed; ClosedAt = e.At; }
    private void On(Settled e) { Status = AuctionStatus.Settled; Winner = e.Winner; CurrentPrice = e.Price; }
    private void On(Reopened e) { Status = AuctionStatus.Open; ClosedAt = null; }   // history must still load

    [Invariant]
    private Result ClosedHasClosedAt()
        => Status == AuctionStatus.Open || ClosedAt is not null ? Result.Success() : Error.Conflict("Auction.ClosedWithoutTime");
}

public partial class Bid : Entity<Auction, Bid>
{
    public BidStatus Status { get; private set; }
    public Result Withdraw()
        => Status.CanTransitionTo(BidStatus.Withdrawn) ? Raise(new Withdrawn()) : Error.Conflict("Bid.NotActive");
}
```

Test (probe `Program.cs`, the same with `AggregateTest`):

```csharp
auction.ReopenWithoutGuard();                         // a behaviour that forgot its guard
AggregateChanges.Prepare(auction)
    .ShouldBe(Error.Internal("Lifecycle.ForbiddenStep", "Auction.Status: Settled -> Open by Reopened"));

var old = Auction.Rehydrate(id, [opened, closed, settled, reopened]);   // 2025 history
old.Status.ShouldBe(AuctionStatus.Open);              // loads
AggregateChanges.Prepare(old).IsSuccess.ShouldBeTrue();
```

### (b) `closed` hierarchy with data-carrying states (comparison; D-3: later)

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(Open), "open")] [JsonDerivedType(typeof(Closed), "closed")] [JsonDerivedType(typeof(Settled), "settled")]
public closed record AuctionState
{
    public sealed record Open : AuctionState;
    public sealed record Closed(DateTimeOffset At) : AuctionState;
    public sealed record Settled(DateTimeOffset ClosedAt, Id<Bidder>? Winner, decimal Price) : AuctionState;
}

public Result Settle()
{
    if (State is not AuctionState.Closed closed) return Error.Conflict("Auction.NotClosed");
    …
    return Raise(new Settled(closed.At, winner?.BidderId, winner?.Amount ?? CurrentPrice));
}
private void On(Closed e) => State = new AuctionState.Closed(e.At);   // from the event, never from State
```

"A closed auction has a time" disappears (no `ClosedAt?`, no invariant); the price is three JSON attributes per type
and one allocation per state change (+80 B per flow, §4). The graph is the `is` patterns plus a hand-written
`IsEdge`; a later `[To<Closed>]` on the cases could generate it.

### (c) baseline: no new type

```csharp
public enum AuctionStatus { Open, Closed, Settled }

public Result Close(DateTimeOffset now)
{
    if (Status != AuctionStatus.Open) return Error.Conflict("Auction.NotOpen");
    if (now < EndTime) return Error.Conflict("Auction.NotEnded");
    return Raise(new Closed(now));
}
```

A forgotten guard (`Reopened` raised from `Settled`) **commits** silently **[verified]**: the end state does not show
the path, so no invariant over it can catch the step.

### Comparison

| | (a) | (b) | (c) |
|---|---|---|---|
| code lines, `A.cs`/`B.cs`/`C.cs` (no blanks or comments) | 90 | 96 | 78 |
| … of which the auction state declaration | 6 (one line per state) | 12 + 4 JSON attributes | 1 |
| guard line per behaviour | same length as `==` | `is` pattern | `==` |
| the lifecycle is written down once | yes, with diagram | in the types, no diagram yet | no |
| forgotten guard caught at commit | **yes** | yes (stand-in) | **no** |
| state invariants for state data | needed (`ClosedHasClosedAt`) | gone by construction | needed |
| dead state reported (ArtFlow's `Settled`) | TPRM021 if no edge reaches it; the "never assigned in `On`" check is §5 | — | no |
| JSON in stored state | `"Closed"` | `{"state":"settled",…}` | `"Closed"` |

(a)'s extra lines against (c) are the five lines of the expanded enum, the two-line `RequireTransitionTo` form and the
demo-only `CloseAndSettle`; the behaviours are otherwise identical.

## 2. What is defined, and what is generated

User code is the enum only. The generator emits per `[Lifecycle]` enum (verbatim output for an order lifecycle in
[`output.txt`](probes/state-transitions/output.txt), "Generator driver"):

```csharp
/// <remarks><code>
/// stateDiagram-v2
///     [*] --> Open
///     Open --> Closed
///     Closed --> Settled
///     Settled --> [*]
/// </code></remarks>
public static class AuctionStatusLifecycle
{
    public const AuctionStatus Initial = AuctionStatus.Open;
    public const string Diagram = "stateDiagram-v2\n    [*] --> Open\n …";
    public static bool IsEdge(AuctionStatus from, AuctionStatus to) => from switch
    {
        AuctionStatus.Open => to is AuctionStatus.Closed,
        AuctionStatus.Closed => to is AuctionStatus.Settled,
        _ => false,
    };
    public static bool IsTerminal(AuctionStatus state) => state is AuctionStatus.Settled;
    public static ReadOnlySpan<(AuctionStatus From, AuctionStatus To)> Edges => s_edges;

    extension(AuctionStatus state)
    {
        public bool CanTransitionTo(AuctionStatus to) => IsEdge(state, to);
        public Result RequireTransitionTo(AuctionStatus to) => IsEdge(state, to)
            ? Result.Success()
            : Error.Conflict("AuctionStatus.NotAllowed", state.ToString() + " -> " + to.ToString());
    }
}
```

The extension members live in the enum's namespace: code in another namespace needs that `using` (as for any
extension) **[verified]** (CS1929 otherwise).

And per aggregate with lifecycle fields (Primitives generator; the probe's
[`A.Lifecycle.g.cs`](probes/state-transitions/App/A.Lifecycle.g.cs) is a hand-written stand-in): an override of the
live-only `ApplyLive` hook with one case per event **whose `On` handler assigns a lifecycle field**; every other event
pays one type switch:

```csharp
protected override void ApplyLive(in StreamEvent stream)
{
    switch (stream.Event)
    {
        case Auction.Closed or Auction.Settled or Auction.Reopened:
        {
            var from = Status;
            ApplyRaised(in stream);
            if (Status != from && !AuctionStatusLifecycle.IsEdge(from, Status))
                ForbiddenStep(new("Auction.Status", from.ToString(), Status.ToString(), stream.Event.GetType().Name));
            return;
        }
        case Bid.Withdrawn or Bid.Won or Bid.Lost: { /* the same for the targeted Bid */ }
        default: ApplyRaised(in stream); return;
    }
}
```

Runtime additions to `AggregateRoot<TSelf>` ([`Core/AggregateRoot.cs.patch`](probes/state-transitions/Core/AggregateRoot.cs.patch)):
`Publish` calls `ApplyLive` instead of `Apply`; `protected virtual ApplyLive`, `protected ApplyRaised`,
`protected ForbiddenStep(LifecycleViolation)` (keeps the first); `Prepare` returns the violation before the invariants.
All three are `[EditorBrowsable(Never)]` and belong on the TPRM015 "generated, do not call or override" list.

## 3. Replay, stored state, JSON

| Check | Result |
|---|---|
| history `Opened, Closed, Settled, Reopened` (Settled → Open, forbidden today) | `Rehydrate` loads, `Status = Open`, `Prepare` succeeds, next live `Close` passes **[verified]** |
| one behaviour, two steps (`CloseAndSettle`: Open → Closed → Settled) | passes: every step is an edge (a first/last check would have seen the non-edge Open → Settled) **[verified]** |
| entity lifecycle (`Bid`: Won → Withdrawn) | the guard refuses (`Bid.NotActive`); the step check covers a forgotten one **[verified]** |
| stored state (part B) | nothing to exclude: the check keeps no per-field baseline; the only bookkeeping is one `LifecycleViolation?` field on the root, a library field (not captured) |
| JSON of the state | `{"status":"Closed","bids":["Active","Won"]}`, round-trips; an unknown name throws (stage 1: names are forever) **[verified]** |
| JSON of (b) | `{"state":{"state":"settled","closedAt":…,"winner":…,"price":150}}`, round-trips with its data **[verified]**; the inner `"state"` is the discriminator |

## 4. Measurements

| Measurement | Result |
|---|---|
| allocation per raised event, valid path, (a) vs (c), 4 000 events | **332.9 B = 332.9 B**, difference 0 **[verified]** |
| full flow (8 events + `Prepare`), one process per candidate (BDN, InProcess) | (c) 1.98 / 1.90 µs, (a) 1.99 / 1.94 µs, (b) 1.98 µs **[verified]** |
| the same, all candidates in one process | (c) 1.87 µs 1688 B, (a) 2.15 µs 1728 B, (b) 2.34 µs 1808 B; the +40 B (and part of the time) is an order artefact: whichever identical flow runs second allocates 40 B more, in both orders **[verified]** |
| (a) against an identical copy without the check, alternating order | full check (root + `Bid`) +4 … 19 %; root only −5 … +12 % (noise ≈ ±5 %) **[verified]** |
| guards | `==` 1.50 ns, `CanTransitionTo` 1.50 ns, `RequireTransitionTo` 2.85 ns, all 0 B **[verified]** |
| Native AOT | 0 IL warnings, all scenarios pass natively **[verified]** |

The entity part of the stand-in looks the entity up a second time (`IRegistry<T>.TryFind`) after the generated dispatch
already did. In the real generator the capture goes into the dispatch case that already holds the entity (before and
after its `__Apply` call, behind the live flag that `Publish` sets), which removes that lookup **[unverified]**; target:
within noise, 0 B.

## 5. Generator and diagnostics

Prototype ([`Gen/`](probes/state-transitions/Gen/)), `ForAttributeWithMetadataName` on the full attribute name, one
equatable model per enum (no symbols, `LocationInfo`), hint name from the full type name. Driver output **[verified]**:

| Id | Severity (prototype) | Reports | Example |
|---|---|---|---|
| TPRM020 | Error | a `[To]` argument that is not a member of the same enum | `[To(Other.X, 3)]` |
| TPRM021 | Warning | a state unreachable from the initial state (only when the declaration has no error) | `Orphan` |
| TPRM022 | Warning | a self-loop or an edge declared twice | `[To(Closed)] Closed` |
| TPRM023 | Error | an enum that cannot be a lifecycle: `[Flags]`, two members with one value, `Initial` naming no member | `[Lifecycle, Flags]` |
| TPRM024 | Error | `[To]` on a member of an enum without `[Lifecycle]` | `enum Plain { [To(Two)] One, Two }` |
| suppressor | — | CS8524 on a `switch` over a `[Lifecycle]` enum | complete switch without `_` |

Incrementality: a comment or an unrelated class → `Unchanged`; an added edge → `Modified` **[verified]**.

Not prototyped (the real Primitives generator/analyzer, `IOperation` walk already used by "state assigned outside `On`"):

* which `On` handlers assign which lifecycle field (to emit only those cases, §2) **[unverified]**;
* **a declared state that no `On` handler ever assigns** (ArtFlow's `Settled` had no event at all), Warning
  **[unverified]**;
* an `On` handler that assigns a lifecycle field from something other than a constant (a computed state cannot be
  labelled; the step check still runs) **[unverified]**.

## 6. Proposed API (candidate a)

| Member | Kind | Notes |
|---|---|---|
| `LifecycleAttribute` (`Initial`) | enum attribute | marks the enum; `Initial` only when the initial state is not value 0 |
| `ToAttribute(params object[] states)` | enum-member attribute | targets checked by TPRM020; `object[]` because attribute arguments cannot be generic enums |
| `<Enum>Lifecycle` | generated static class | `Initial`, `Diagram`, `IsEdge`, `IsTerminal`, `Edges`, extension `CanTransitionTo`, `RequireTransitionTo` |
| `LifecycleViolation(Field, From, To, Event)` | public record | `ToError()`; what `Prepare` returns |
| `AggregateRoot<T>.ApplyLive` / `ApplyRaised` / `ForbiddenStep` | protected, generated use only | TPRM015 list |

Package `Tarol.Primitives`, step 6 part C (D-4); generator and suppressor in `Tarol.Primitives.Generators`.

## 7. Decisions for the owner

Asked with the question tool, one per decision, recommendation first:

1. Declaration syntax and names (`[Lifecycle]`, `[To]`, `<Enum>Lifecycle`, `CanTransitionTo`, `RequireTransitionTo`).
2. Failure of a forbidden step found at commit: `Error.Internal("Lifecycle.ForbiddenStep")` or `Error.Conflict`.
3. Code of the generated guard failure (`<Enum>.NotAllowed` + detail).
4. Creation: must a created root/entity start in the initial state (checked)?
5. Edges labelled by events: derive the labels from the `On` assignments, or a second declaration on events.
6. Diagnostic severities (TPRM021 unreachable, TPRM022 redundant, "never assigned") and the CS8524 suppressor.

### Owner answers (round 2, 2026-11-14)

| # | Decision | Answer |
|---|---|---|
| L-1 | Syntax and names | **as prototyped**: `[Lifecycle]` (+ `Initial`), `[To(...)]` on members; generated `<Enum>Lifecycle` with `Initial`, `Diagram`, `IsEdge`, `IsTerminal`, `Edges`, extension members `CanTransitionTo(to) : bool`, `RequireTransitionTo(to) : Result` |
| L-2 | Forbidden step found at commit | **`Error.Internal("Lifecycle.ForbiddenStep", "<Type>.<Field>: <From> -> <To> by <Event>")`**: a missing guard is a bug; callers get 409 only from guards |
| L-3 | Generated guard failure | **`Error.Conflict("<Enum>.NotAllowed", "<From> -> <To>")`**; a behaviour that wants its own code uses `CanTransitionTo` |
| L-4 | Creation | **a created root or entity must be in the initial state** after its creation event; otherwise the step `(new) -> <State>` is reported like any forbidden step |
| L-5 | Edge labels | asked twice (the first answer conflicted with L-1): **`[To]` stays the graph; `[Transition<TEnum>(from, to)]` on an event is optional** and labels that edge in the diagram (`Open --> Closed : Closed`); a `[Transition]` that is not a declared `[To]` edge is an **Error** |
| L-6 | `On` analysis | **yes, all three**: report a declared state no `On` assigns; check that an event's `On` assigns the `To` of its `[Transition]` (Warning on mismatch); emit step-check cases only for events whose `On` assigns a lifecycle field (§4: removes the cost for every other event) |
| L-7 | Severities | **Warnings** for unreachable (TPRM021), redundant (TPRM022) and never-assigned states; Errors for bad targets (TPRM020), unusable enums (TPRM023), `[To]` without `[Lifecycle]` (TPRM024) and a `[Transition]` that is not an edge |
| L-8 | CS8524 | **ship the suppressor** (`TPRMSPR01`) for switches over `[Lifecycle]` enums only |
| — | Package and step | unchanged (D-4): `Tarol.Primitives`, step 6 part C, after part B |

Diagnostic ids for part C (continuing after TPRM015 in `Tarol.Primitives.Generators`): TPRM016 bad target, TPRM017
unreachable, TPRM018 redundant edge, TPRM019 unusable enum, TPRM020 `[To]` without `[Lifecycle]`, TPRM021 `[Transition]`
not an edge, TPRM022 state never assigned in `On`, TPRM023 `[Transition]`/`On` mismatch. The prototype's TPRM020–024 are
renumbered accordingly.

## 8. What part C must do

* Runtime: `LifecycleAttribute`, `ToAttribute`, `TransitionAttribute<TEnum>`, `LifecycleViolation`, the `ApplyLive`/
  `ApplyRaised`/`ForbiddenStep` hook in `AggregateRoot<T>` (Core patch), `Prepare` returning the violation before the
  invariants; the three hook members on the TPRM015 list.
* Generator: per-enum lifecycle class (§2) and diagnostics; in the aggregate generator, the step check inside the
  generated dispatch cases (entity already resolved, live flag from `Publish`), only for events whose `On` assigns a
  lifecycle field, plus the creation check (L-4) and labelled edges in the diagram (L-5).
* Suppressor for CS8524 (L-8).
* Stored state (part B) captures the lifecycle field as a bare string; the violation field is not captured.
* Tests: the probe's scenarios on the real package (guards, forgotten guard, two steps in one commit, entity lifecycle,
  creation, replay of a forbidden historical step, stored state), generator snapshots and incrementality with
  `tests/Shared/Tarol.Testing.Generators`, an AOT smoke step, a 0-allocation test; skill section in `tarol-primitives`
  and its evaluation checklist; README; migration-note rows; conventions §8 in full.

## 9. Implementation (step 6 part C, 2026-11-14)

Built from the probe (`Core/`, `Gen/`, `App/`) into `Tarol.Primitives` and `Tarol.Primitives.Generators`, as decided in
L-1 … L-8. One owner question came up while building (round 3, below); nothing else needed a decision.

### 9.1 Owner answer (round 3, 2026-11-14)

| # | Question | Answer |
|---|---|---|
| L-9 | A `[Lifecycle]` enum nested in a type (`Auction.Status`): L-1's `<Enum>Lifecycle` must be a namespace-level class (extension blocks need a top-level static class), so two nested `Status` enums in one namespace would collide | **prefix the containing types**: `Auction.Status` → `AuctionStatusLifecycle`; a top-level enum keeps `<Enum>Lifecycle`. A nested enum must be at least internal and not inside a generic type (TPRM019) |

### 9.2 Public API and behaviour changes

| Before (part B) | Now | Why |
|---|---|---|
| — | `LifecycleAttribute` (`Initial`), `ToAttribute(params object[])`, `TransitionAttribute<TEnum>(from, to)` (`where TEnum : struct, Enum`; a generic attribute with `TEnum` constructor parameters compiles and reads back **[verified]**), `LifecycleViolation(Field, From, To, Event)` with `ToError()`, `Code` and `New` | L-1, L-2, L-5 |
| — | generated `<Enum>Lifecycle` per valid `[Lifecycle]` enum: `Initial`, `Diagram`, `IsEdge`, `IsTerminal`, `Edges`, extension members `CanTransitionTo(to) : bool`, `RequireTransitionTo(to) : Result` (`Error.Conflict("<Enum>.NotAllowed", "<From> -> <To>")`); public when the enum is public, else internal | L-1, L-3, L-9 |
| `Publish` called `Apply` | `Publish` calls `protected virtual ApplyLive` (default: `Apply`); `protected ApplyRaised`, `protected ForbiddenStep(LifecycleViolation)` (keeps the first); all `[EditorBrowsable(Never)]` | the live-only hook (D-2) |
| `Prepare` ran the invariants | `Prepare` returns the remembered violation **before** the invariants; it stays on the instance (the instance is discarded on any failure) | L-2 |
| — | generated per aggregate with a lifecycle field (root or entity): a `__live` flag, an `ApplyLive` override (flag around `ApplyRaised`, then the root's creation check), step-check `case`s in `Dispatch` for the events whose `On` assigns a lifecycle field (L-6), a creation check in every creation case of an entity with lifecycle fields (L-4), the same check around an owner's handler for a child's event while bubbling, and `[UnsafeAccessor]` field accessors `__Life…` | D-2, L-4, L-6 |
| TPRM015: `Dispatch`, `CheckInvariants`, `__` members | **plus** calls of `ApplyLive`, `ApplyRaised`, `ForbiddenStep` and an override of `ApplyLive` | §8 |
| — | TPRM016–TPRM021 (generator), TPRM022–TPRM023 (analyzer, compilation end), suppressor TPRMSPR01 | L-6, L-7, L-8 |

Behaviour of existing aggregates: unchanged. An aggregate without lifecycle fields gets no override (`Publish` →
`ApplyLive` → `Apply`, one virtual call more) and an identical generated file (the part A/B snapshots did not change).

### 9.3 How the step check works

* **Where.** The check sits in the generated dispatch case that already holds the entity: before the apply it reads the
  lifecycle fields the event's handler can change, after it compares, behind the `__live` flag. Only events whose
  effective `On` handler (the most-derived one, as in `__Apply`) assigns a lifecycle field get such a `case`, placed
  before the generic `Entity<R, X>.Event`/`.Deleted`/`AggregateRoot<R>.Event` case; every other event takes the
  unchanged path. "Assigns" is found with an `IOperation` walk over the handler and the same-type methods it calls
  (`LifecycleDiscovery.Assigns`), done in the per-type transform.
* **Live only.** `ApplyLive` is reached from `Publish` only; it raises `__live` around `ApplyRaised` (`try`/`finally`).
  `ReplayHistory` and `ReadState` call `Apply` directly or no handler at all, so a step that today's lifecycle forbids
  loads (test and property test), and the flag is never set on those paths (mutation-checked, §9.6).
* **Creation (L-4).** A root's creation event is its first event: after `ApplyRaised` at version 1, every root
  lifecycle field must hold `Initial`, otherwise `(new) -> <State>` is recorded; the root's step checks skip version 1
  (`Version != 0` before the apply). An entity's creation `case` checks its fields after its `On`. This also catches a
  property initializer or an unassigned field when `Initial` is not member 0.
* **Owner handlers.** An owner's `On` for a child's event that assigns the owner's own lifecycle field (ArtFlow-style
  "a won bid makes the payout due") is checked in the bubbling step (`__Up{i}`), around that owner's `__Apply`.
* **Messages.** `<Type>.<Member>` (owner type's simple name and the property or field name), the states by
  `ToString()`, the event by `GetType().Name`; only on the failure path, so the valid path allocates nothing.
* **Stored state.** The lifecycle field is an ordinary enum field and stored by name (part B). `__live` starts with
  `__` and `_violation` lives on the library base class: neither is captured (test: the stored keys of an auction).
  A state read through `ReadState` has no baseline to restore; the next live step reads the current field, as after
  replay (tests and property test).

### 9.4 Generator and diagnostics

* `LifecycleDiscovery.Model` builds one equatable model per enum (`ForAttributeWithMetadataName` on
  `[Lifecycle]`); `[Transition]` labels come from a second `ForAttributeWithMetadataName` pipeline and are filtered
  per enum, so a new label regenerates only that enum's class (incrementality test). The aggregate transform calls the
  same model for every field type with `[Lifecycle]`: an invalid enum gives no class and no step check (test).
* Edges are not in the aggregate model (the aggregate calls `<Enum>Lifecycle.IsEdge`): a new edge regenerates only the
  class; a new state or another initial state regenerates the class and the aggregate (the enum's names are part of
  `StateSchema`, part B). Moving the enum's text regenerates nothing.
* Ids and severities as decided (L-7): TPRM016 bad target, TPRM019 unusable enum (`[Flags]`, aliased members, no
  members, bad `Initial`, file-local, nested in a generic type, private/protected), TPRM020 `[To]` without
  `[Lifecycle]`, TPRM021 a `[Transition]` that is not a declared edge (also: not a member, enum without `[Lifecycle]`)
  are errors; TPRM017 unreachable (only for a declaration without errors), TPRM018 self-loop or duplicate edge,
  TPRM022 never assigned, TPRM023 `[Transition]`/`On` mismatch are warnings.
* TPRM022 looks at `[Lifecycle]` enums declared in the compilation and used by a field of a root or entity; the initial
  state is exempt (it is the default) and a computed assignment (`State = e.To`) turns the rule off for that enum.
  TPRM023 checks only the handler on the event's target type (an owner's handler for the child's event applies the
  owner's state) and follows same-type helpers.
* TPRMSPR01 (`LifecycleSwitchSuppressor`): CS8524 on a `switch` whose governing expression is a `[Lifecycle]` enum;
  CS8509 for a missing state stays (tests; package consumer).

### 9.5 Implementation notes (found while building)

* Backing fields are implicitly declared and get no symbol action: TPRM022 first missed every auto-property. The
  analyzer now walks the fields of each domain type (`SymbolKind.NamedType`).
* `__live` is never read when no handler assigns a lifecycle field and no entity has one (CS0414 in the generated
  code, found by the mutation run): the field is wrapped in `#pragma warning disable CS0414`, like part A's `__Parent`.
* `TypedConstant.ToCSharpString` is in `Microsoft.CodeAnalysis.CSharp`; list patterns on `ImmutableArray` need
  `System.Index`, absent in netstandard2.0: the generator uses length checks.
* The first event of a test aggregate rebuilt from an empty history is a creation event: the property test's discard
  path must replay from a history that contains the creation, or it reports `(new) -> Closed` (correctly).
* Within one event the owner's check runs before the target's (the order of `__Up` in the dispatch case); both are
  recorded, the first is returned.
* The skill grew to 10.8 KB with the lifecycle rule, example and diagnostics (target ≈ 8 KB): the lifecycle section is
  as short as it can be while every API name stays compiled in `SkillSampleTests`.

### 9.6 Suppressions and analyzer configuration

| Where | What | Why |
|---|---|---|
| generated `__live` field | `#pragma warning disable CS0414` around one line | read only by the step checks, which an aggregate whose handlers assign no lifecycle field does not have |

No `NoWarn`, no `.editorconfig` change. The test fixtures need no suppression (`_ = e;` keeps CA1822 quiet in handlers
that only assign a constant).

Mutation checks: with the step comparison disabled (`if (false && …)`), 8 tests fail (7 `LifecycleTests`, the lifecycle
property test); with the live flag ignored (`__live || true`), the replay test and the property test fail. Both
restored.

### 9.7 Verification

SDK 11.0.100, 2026-11-14, from a clean tree (`rm -rf artifacts`) in CI order.

| Step | Result |
|---|---|
| `dotnet restore Tarol.slnx`; build every `src/*/*.Generators` | OK |
| `dotnet format Tarol.slnx --verify-no-changes --no-restore` | OK (after one `dotnet format` pass) |
| `dotnet build Tarol.slnx -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx -c Release --no-build` | **659 passed**, 0 failed (592 before; Primitives: 109 unit (+26), 128 generator (+41)) |
| `eng/aot-smoke.sh` | 6 apps, 0 IL warnings each; `Tarol.Primitives.AotSmoke`: 27 checks (9 new: guards, valid steps, a forgotten guard at commit, an entity lifecycle, replay of a forbidden historical step, the state's name in the stored state, continuing after `ReadState`, the diagram, a switch without `_`), ALL CHECKS PASSED |
| `eng/verify-packages.sh` | `Tarol.Primitives` with README, LICENSE, skill and the generator under `analyzers/dotnet/cs/`; the consumer runs 9 checks (4 new: a lend through the generated guard, the step check, guard and diagram, a switch without `_` under warnings-as-errors); OK |
| diagnostics reach a package consumer | temporary traps files in `Tarol.Primitives.PackageTests`: first `error TPRM016`–`TPRM021` (generator; the warnings TPRM017/018 are errors under warnings-as-errors), then a second file with `error TPRM015` (`ApplyRaised` called), `TPRM022`, `TPRM023` (analyzer) and CS8524 reported only for the plain enum's switch (TPRMSPR01 suppressed the lifecycle one); both removed |

New tests:

* `LifecycleTests` (23): the generated class and its diagram (labels from `[Transition]`), `RequireTransitionTo`'s
  conflict, an own guard code with `CanTransitionTo`, a valid flow, a forgotten guard at commit, two steps in one commit
  (`Open -> Closed -> Settled`), the step check before the invariants, the first violation kept, an entity lifecycle, a
  step applied by an owner's handler for a child's event, creation (L-4) of a root (named `Initial`, a default that is
  not initial) and of an entity, a nested lifecycle enum, replay of a forbidden historical step and the next live step,
  replay records no creation violation, the stored keys (`status` by name, no bookkeeping), a forbidden step is not in
  the stored state, continuing after `ReadState`, and **0 bytes** for 2 000 checked raises plus 2 000 guards
  (`GC.GetAllocatedBytesForCurrentThread`).
* `StoredStatePropertyTests` (+2): the auction as a sixth model of the state == replay walk, and a lifecycle walk
  (150 seeds × 40 steps, guarded and unguarded behaviours, commits on loaded instances, discards on failure): at every
  commit `Prepare` reports a forbidden step exactly when an unguarded behaviour took one, replay and `ReadState` never
  report one, and state == replay == load; a reachability guard on both paths.
* `SkillSampleTests` (+1): the skill's lifecycle example.
* Generator: `LifecycleDiagnosticTests` (33 cases: TPRM015–TPRM023, an invalid enum generates nothing, TPRMSPR01 keeps
  CS8509 and CS8524 for other enums), `IncrementalityTests` (+6: an unrelated edit and moving the enum's text regenerate
  nothing; a new edge regenerates only the class; a new state and another initial state regenerate the class and the
  aggregate; a new `[Transition]` regenerates only its class), the `Lifecycle` snapshot (aggregate with a root, an
  entity, a nested enum, an owner handler, a helper called from `On`, and four lifecycle classes).

### 9.8 Skill evaluation checklist (`tarol-primitives`, lifecycle section)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| "An auction is open, closed or settled and never goes back" | `[Lifecycle] public enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, Settled }`, a `Status` property assigned in `On` | a hand-written `switch` of allowed pairs; `Stateless`; checking the graph inside `On` |
| "Only an open auction can be closed" | `Status.RequireTransitionTo(AuctionStatus.Closed)` (generated conflict) or `Status.CanTransitionTo(…)` with an own code, in the behaviour | `if (Status != AuctionStatus.Open)` spread over behaviours; a check in `On` (TPRM014 for a `Result` call) |
| "Show the lifecycle in the docs" | `AuctionStatusLifecycle.Diagram`, edges labelled with `[Transition<AuctionStatus>(Open, Closed)]` on the event | writing the Mermaid by hand; a `[Transition]` without the `[To]` edge (TPRM021) |
| "Make sure a forgotten guard can never corrupt history" | nothing: the generated step check; test with `AggregateChanges.Prepare(x) == Error.Internal("Lifecycle.ForbiddenStep", "…")` | an invariant comparing old and new state; calling `ForbiddenStep` (TPRM015) |
| "Old events contain a step that is forbidden today" | nothing: replay and `ReadState` never check | removing or renaming the state (old rows and events stop loading); a `throw` in `On` |
| "Start in Draft, which is not the first member" | `[Lifecycle(Initial = nameof(Status.Draft))]`, the creation event's `On` assigns `Draft` | leaving the field at value 0 (reported as `(new) -> X`) |
| "A switch over the states" | a `switch` naming every state, no `_` arm (TPRMSPR01) | `_ => throw …` (hides a new state; CS8509 would have reported it) |
| "Add a state" | a new member with its `[To]` edges and an event whose `On` assigns it | forgetting the event (TPRM022), or the edge (TPRM017) |
