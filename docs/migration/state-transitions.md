# State transitions: evaluation (stage 2)

Evaluation of the idea in [state-transitions.md](../architecture/state-transitions.md) within the scope the owner decided
(D-1 … D-4): a declared lifecycle, guards generated from it, and a live-only check of every step that `Prepare` reports
at commit; enum first; `Tarol.Primitives`, step 6 part C. Probes and output:
[`probes/state-transitions/`](probes/state-transitions/), [`output.txt`](probes/state-transitions/output.txt).
Labels as in the [conventions](../architecture/conventions.md).

## 0. Summary

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
