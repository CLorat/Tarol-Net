# State transitions

Concept note for an idea from the owner (not a migration step): a **state value type** whose allowed transitions are
declared once, next to the states, and an aggregate that **keeps the transition it went through until commit**, so that
invariants can evaluate the transition and not only the end state. It touches `Tarol.Validations` (step 5, value rules),
`Tarol.Primitives` (step 6: apply, invariants at the hand-off, commit, stored state) and `Tarol.Changes` (step 7,
guards). It starts from first principles; the old ArtFlow auction (`CLorat/Tarol@6a62f81`,
`examples/ArtFlow/ArtFlow.Domain/ArtFlow/Auctions`) is the use case.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/state-transitions/`](../migration/probes/state-transitions/)); **[verified by reading]** means read in the named
code, not run; **[unverified]** means reasoned or read elsewhere, not run here.

**Status: decided** (D-1 … D-4 below, L-1 … L-8 in the evaluation note); implementation in step 6 part C. API and measurements in
[migration/state-transitions.md](../migration/state-transitions.md).

## 1. What a user needs it for

* "An auction is `Open`, `Closed` or `Settled`; it goes `Open → Closed → Settled` and never back" — **one place** that
  says this, readable by a newcomer and by an agent, instead of `Status == Open` checks spread over behaviours.
* "You can close only an open auction; you can settle only a closed one" — the same fact, seen from each behaviour.
* "A closed auction has an end time; a settled auction has a winner and a price" — data that exists **because of** the
  state.
* "Closing is allowed only after the end time; settling needs no open bids" — conditions of one particular step.
* Old events load even when today's graph forbids what they record (an auction that was re-opened by a bug in 2025,
  a `Cancelled` state that was later removed).
* Not: "only the seller may close" — a permission ([security.md](security.md) §4), never part of the graph.

The ArtFlow auction today **[verified by reading]** (`Model/`, `Changes/` at `6a62f81`):

| Rule | Mechanism | Observation |
|---|---|---|
| `enum AuctionStatus { Open, Closed, Settled }`, default `Open` | property initializer | the graph exists only in the author's head |
| `AuctionMustBeOpenToClose` (`Status == Open`) | `IPrerequisite<Close>` | one edge (`Open → Closed`), written as a check of the from-state |
| `AuctionIsOpen` (`Status == Open && StartTime <= now <= EndTime`) | `IPrerequisiteAsync<Bid>` + `TimeProvider` | not a transition: "which behaviours are allowed in which state" |
| `WhenClosedEndtimeMustBeSet` (`Status != Closed \|\| EndTime.HasValue`) | `[Invariant]` static `bool` | always true: `EndTime` is set by `AuctionCreated`; the nullable exists only for the type |
| `WhenSettlesBidsMustBeEmpty` (`Status != Settled \|\| Bids.Count == 0`) | `[Invariant]` | **no event and no behaviour ever sets `Settled`**; `Winner` is never assigned either. The state is unreachable and nothing reports it |
| `On(AuctionClosed) => Status = Closed` | apply | correct: apply takes the event as fact, no check |

So the example already shows the two costs the idea targets: the lifecycle is implicit (an unreachable state and a dead
`Winner` went unnoticed), and "state ⇒ data" rules (`Closed ⇒ EndTime`, `Settled ⇒ Winner`) are invariants over
nullable fields.

## 2. What is a transition rule?

Three different things hide behind "transition rule":

| Kind | Example | Input | Belongs | Why |
|---|---|---|---|---|
| **Graph** | `Open → Closed`, `Closed → Settled`; never `Settled → Open` | `from`, `to` | **on the state type** (declared once), checked **before the event** | pure, needs nothing but the two values; it is a property of the *type*, like `Money ≥ 0` is a property of `Money` |
| **Transition condition** | "close only after `EndTime`", "settle only without open bids" | pre-state + command + `now` | **guard** in the behaviour | it needs `now` or the command (S-1); after apply the pre-state is gone |
| **State condition** | "a closed auction has an end time", "a settled auction has a winner" | post-state | **invariant** (V-3), or **by construction** with data-carrying states (§6) | it holds in the state, whichever way it was reached |

**[unverified]** as a classification; the examples are ArtFlow's.

What is left that needs `from` **and** `to` after apply? Very little, and not what the idea first suggests:

* **Monotonic rules on values that are not states**: "the current price never drops", "the end time is never moved
  earlier". They compare the value at the last commit with the value now. They are not about a state type at all.
* **The graph as a safety net**: a behaviour that forgot its guard (`Reopen()` writes `Status = Open` through a new
  event) is caught at commit instead of being stored.

Everything else is a guard (it needs the pre-state and the input) or an ordinary invariant (it needs only the
post-state). **In event sourcing the event already is the transition**: `AuctionClosed` *means* `Open → Closed`. The
state field is a projection of the events the aggregate applied; the graph says which events may follow which state.

## 3. Guard or invariant?

| | Guard (before the event) | Invariant (after apply, at the hand-off) |
|---|---|---|
| when | in the behaviour, before `Raise` | `AggregateChanges.Prepare`, before the store reads the events (domain-core.md §6) |
| sees | the current (from) state, the command, `now` | the state after all of this commit's events |
| failure | `Error.Conflict(…)` returned by the behaviour; no event, nothing to discard | `Error` from `Prepare`; the instance is discarded (no rollback) |
| user can forget it | yes (a new behaviour without the check) | no (runs for every commit) |
| needs "kept until commit" | **no** | yes, for the graph (§4) |
| replay | never runs (behaviours are live only) | never runs (replay never hands off) |

* **The graph belongs in the guard** for the normal case: the from-state is the current state and the to-state is the
  behaviour's own decision, so `if (!Status.CanTransitionTo(AuctionStatus.Closed)) return …;` is all it takes, it fails
  before anything is raised, and it needs no tracking.
* **Invariant-side graph checks are a safety net**, worth it only if they are free on the valid path and need no user
  code (generated). Their failure is then a developer bug (a missing guard), so `Error.Internal` would be honest; but
  V-3 keeps "the rule's `Error`", and the graph has no user rule to choose one. Open question 4.
* **The cleanest user code** is one line per behaviour: a guard generated from the declaration, returning a `Result`
  with a stable code, so the behaviour reads
  `return Status.Allows(AuctionStatus.Closed).Bind(() => Raise(new Closed(now)));` or an `if`. **[unverified]**; the
  candidates are written out in stage 2.

## 4. What exactly is kept until commit?

Three tracking models, by what they can check:

| | T1 baseline (first `from`, last `to`) | T2 per-step check on the live raise path | T3 step log |
|---|---|---|---|
| stores | the value at the last commit/load, one copy per state field | nothing on the valid path; the **first forbidden step** if one happens | every `(from, to)` per commit |
| checks | **reachability** of `to` from `from` (transitive closure), transition invariants (`From`/`To` in user rules) | every **edge**, including `Open → Closed → Settled` in one behaviour | every edge |
| cost per raised event | 0 | read the target entity's state fields before and after apply (no allocation) **[unverified]** | an append (allocation once per commit, list growth) |
| cleared | baseline := current at `Committed` and at the end of `Rehydrate`/state load | at `Committed` | at `Committed` |

* **First/last cannot check edges.** A commit that goes `Open → Closed → Settled` has the net transition
  `Open → Settled`, which is not an edge; a net check must use the closure, and then a behaviour that jumps straight from
  `Open` to `Settled` passes too. Edges need every step (T2/T3) or the guard.
* **"Every step" is already stored**: the uncommitted events *are* the steps. T3 duplicates `UncommittedChanges` in
  another shape; it is not needed.
* **T2 is structurally replay-safe**: the check sits in `Publish` (the live raise path), and `ReplayHistory` calls
  `Apply` directly, never `Publish` **[verified by reading]** `AggregateRoot.cs`. The state is read around `Apply`, not
  inside an `On` handler. A forbidden step cannot be reported by `Raise` (it returns `Success`), so it is remembered and
  returned by `Prepare` — this is the only thing that is literally "kept until commit".
* **T1 lives per state field, per entity instance**: an `Auction.Status` baseline, and one per `Bid` if bids have a
  status. A child created in this commit has no baseline (`From` is absent); a deleted child is gone and is not checked.
  `Prepare` already visits every entity on every commit (decision 5, primitives.md §9), so resetting baselines at
  `Committed` costs one copy per state field per commit, nothing per event.
* **Discard**: nothing to undo; the instance (with its baselines and any remembered violation) is thrown away (V-3,
  domain-core.md insight 4).
* **Stored state (part B of step 6)**: a baseline and a remembered violation are bookkeeping, not state. They must be
  excluded from the captured fields (generated fields are excluded; a library field type would need the same rule as
  `[Transient]`), and a load from stored state sets baseline := current. Stored-state JSON therefore contains only the
  state value.

## 5. Replay

Structural guarantee, not a test-only one:

* **No graph code is reachable from `On`.** The declaration produces a guard (called by behaviours) and, optionally, a
  T2 check in `Publish` or a T1 comparison in `Prepare`. `Dispatch`/`On` contain only the user's assignment
  (`Status = AuctionStatus.Closed;`). The existing TPRM analyzer (rule/throw/clock/`Raise` inside `On`, Error) already
  reports a `Result`-returning guard call inside `On`, so `CanTransitionTo` must return `bool` and `Allows` a `Result`
  for that rule to cover the latter **[unverified]**.
* **Historical forbidden transitions load**: replay applies the events; a T1 baseline is set to the replayed state
  after replay; a T2 check never runs. A test with a stored `Settled → Open` belongs to stage 2.
* **Data-carrying states must not derive the target from the source in `On`.** With typed transition methods
  (`Open.Close(at) : Closed`), the tempting apply is `State = ((AuctionState.Open)State).Close(e.At);` — a cast that
  throws on a historical `Settled → Closed`. Apply must build the target from the event alone
  (`State = new AuctionState.Closed(e.At);`); transition methods are usable in behaviours only.
* **Deserializing the state itself** is part of replay (events) and of every load (stored state): an enum loads an
  undefined number (`7`), but an **unknown name throws `JsonException`**, and pinning a name with
  `[JsonStringEnumMemberName("settled")]` makes the old CLR name `"Settled"` unreadable **[verified]**
  (`probes/state-transitions/facts`). A state that was renamed or removed breaks old events and old state rows; the rule
  "a stored state name is forever" (pin names, never delete members, mark them obsolete) is part of the feature.

## 6. Shapes of a state

| Shape | Exhaustive `switch` | JSON (event payload, stored state) | Data per state | Least code |
|---|---|---|---|---|
| **`enum`** | CS8509 for a missing member, but a *complete* switch is still CS8524 (unnamed values); with CS8524 set to `none`, CS8509 keeps working **[verified]**; `closed enum` does not exist in C# 15 (CS0106) **[verified]** | a bare string with `JsonStringEnumConverter<T>` (AOT-safe, source-generated), queryable; or a number | none: data stays in nullable aggregate fields + state invariants | **yes**: one line per state |
| **`closed` record hierarchy** | complete switch is warning-free; a missing case is CS8509 **[verified]** | polymorphic: needs `[JsonPolymorphic]` + `[JsonDerivedType]` per case (a discriminator such as `{"$type":"closed","endedAt":…}`); **without them it serializes as `{}`, data lost** **[verified]**; a same-assembly STJ context sees them (real types) | **yes**: `Closed(DateTimeOffset EndedAt)`, `Settled(Id<Bidder> Winner, Money Price)` — "closed ⇒ end time" disappears by construction | one record per state, plus JSON attributes |
| **`union`** | warning-free complete switch **[verified]** | writes the case's payload with **no discriminator** **[verified]**, so cases with the same shape cannot be told apart on read; rejected for events in the old review | yes, the case types | short, but JSON is unusable without a custom converter |
| struct value object over an enum/string (`readonly record struct AuctionStatus(string Value)`) | none (strings) | bare value through `ValueObjectJson` (V-9) | no | most code; validation of a state from input is almost never needed (states are not command input: a behaviour *names* the transition) |

Observations:

* **A state is rarely input.** "Set status to Closed" is CRUD; event-sourced behaviours are `Close(now)`, `Settle()`.
  The value-rule side of a state (Validations: `Create` validates) is therefore almost empty. What a state needs is a
  stable stored name and a graph.
* **Allocation**: an enum costs nothing. A data-carrying record is one allocation per transition, in `On` (live and
  replay), next to the event's own allocation; dataless cases can be singletons. That is the price of "by construction",
  not of tracking.
* **Both shapes can share one graph declaration** if it names states by type or by member: the generator reads
  `[To(Closed)] Open` on an enum member, or `[To<Closed>]` on a case record **[unverified for the record form]**.

## 7. Declaring the graph

| Form | Example | The generator/compiler can check |
|---|---|---|
| attributes on the states | `enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, Settled }` (sibling members resolve unqualified **[verified]**) | every target is a declared state (by construction: constants); unreachable states (no path from the initial state = member `0` or `[Initial]`); states with no outgoing edge = terminal (report only if none exists); duplicate edges; self-loops |
| attributes on the events (edges labelled by events) | `[Transition<AuctionStatus>(Open, Closed)] public sealed record Closed : Event;` | the same, plus **"which event performs which step"**; an analyzer can compare the `On` handler's assignment (`Status = AuctionStatus.Closed`) with the declared `To` |
| hand-written `static bool CanTransition(from, to)` | a `switch` on the tuple | nothing (opaque code); no diagram |
| the type of the transition method | `AuctionState.Open.Close(at) : AuctionState.Closed` | the compiler: a `Settled` has no `Close`. No table to generate, but no reachability check and no diagram either |
| a small DSL in a string/`AdditionalFiles` | `"Open -> Closed -> Settled"` | as attributes, but outside C#: no navigation, no rename |

* **What an analyzer can add from the code** (`IOperation`, as TPRM900/901): in each `On(event)`, the constant assigned
  to a state field gives the edge's *target* and the event that reaches it. It cannot know the *from* state statically.
  "A declared state no `On` handler ever assigns" would have reported ArtFlow's `Settled` **[unverified]**.
* **Generated surface** per state type: `CanTransitionTo(to) : bool`, `Allows(to) : Result` (with a stable code),
  `Transitions` (a static table for tests and docs), and the diagram. With C# 14 extension members these attach to an
  enum (`status.CanTransitionTo(…)`) without a wrapper type; generated code must call the real static members
  (lesson from steps 4–5).

## 8. Does .NET 11 already have something?

Nothing in the BCL **[unverified]**. Libraries, briefly (all **[unverified here]**, from their documentation):

| Library | Model | Why not |
|---|---|---|
| Stateless | a runtime-configured state machine object with triggers, guards and entry/exit actions; can export DOT/Mermaid | it *owns* the state and mutates it on `Fire`, which is exactly what an event-sourced aggregate must not do (state changes only in `On`); configuration is built at run time per instance (allocations); guards are delegates without `Result` |
| MassTransit / Automatonymous state machines | saga state machines over messages | step 9 (Sagas) territory; a process manager, not an aggregate's value |
| Workflow engines (Elsa, Durable Task) | long-running orchestration | a different problem (I/O, timers, persistence of the workflow) |

What Tarol needs is far smaller: a graph table (a few bytes, compile time), a `bool`/`Result` check, and a diagram. A
dependency would bring a runtime machine whose central feature (`Fire` mutates the state) contradicts the core rule.

## 9. Documentation value

* The declared graph is data the generator already holds, so it can emit a **Mermaid `stateDiagram-v2`**:
  `[*] --> Open`, `Open --> Closed : Closed`, `Closed --> Settled : Settled`. Where to put it: into the XML doc
  `<remarks><code>` of the state type (visible in IntelliSense as text; IDEs do not render Mermaid), and/or a generated
  `const string Diagram` that a doc test writes into `docs/` (a generator cannot write files outside the compilation).
  **[unverified]**
* For agents, the declaration itself is the documentation: `[To(Closed)] Open` is shorter than any prose.

## 10. Where it lives

| Package | Fits | Does not fit |
|---|---|---|
| `Tarol.Validations` (step 5) | "a rule declared once on a value type" (V-1, V-2) | there is almost no value rule: states are not input; the graph is checked by behaviours and at the hand-off, which Validations does not know; Primitives does not depend on Validations (decision 8) |
| **`Tarol.Primitives` (step 6)** | the aggregate, `Publish` (T2), `Prepare` and `Committed` (T1), the stored-state capture rules (part B, not yet built), the TPRM analyzer that already inspects `On` | — |
| `Tarol.Changes` (step 7) | guards are called from behaviours that Changes invokes | Changes calls behaviour methods and has no access to `Raise` or state; nothing of the graph belongs there |

So the candidate home is `Tarol.Primitives`: the declaration attribute and the generated guard are tiny, and any
tracking needs `Publish`/`Prepare`/`Committed`. It could fold into step 6 part B (stored state, which must know the
bookkeeping fields anyway) or be a part C.

## 11. ArtFlow, sketched (the full rewrite per candidate is stage 2)

```csharp
// (a) enum + declared graph; data stays on the aggregate
public enum AuctionStatus { [To(Closed)] Open, [To(Settled)] Closed, Settled }

public Result Close(DateTimeOffset now)
{
    if (!Status.CanTransitionTo(AuctionStatus.Closed)) return Error.Conflict("Auction.NotOpen");
    if (now < EndTime) return Error.Conflict("Auction.NotEnded");
    return Raise(new Closed(now));
}
private void On(Closed e) { Status = AuctionStatus.Closed; ClosedAt = e.At; }

// (b) closed hierarchy, data by construction (needs the JSON attributes from §6)
public closed record AuctionState
{
    public sealed record Open : AuctionState;
    public sealed record Closed(DateTimeOffset At) : AuctionState;
    public sealed record Settled(Id<Bidder> Winner, Money Price) : AuctionState;
}
private void On(Closed e) => State = new AuctionState.Closed(e.At);

// (c) today: no new type
public Result Close(DateTimeOffset now)
{
    if (Status != AuctionStatus.Open) return Error.Conflict("Auction.NotOpen");
    if (now < EndTime) return Error.Conflict("Auction.NotEnded");
    return Raise(new Closed(now));
}
```

The difference between (a) and (c) at the call site is one expression; the value of (a) is the declaration (one place,
reachability check, diagram) and the T1/T2 safety net, not the guard line. (b) changes what invariants are needed at all.

## New insights

1. **The event is the transition.** In event sourcing every state change already has a name and a record (`Closed`);
   "keep the switch until commit" is mostly already true: the uncommitted events are the steps. What is *not* kept is
   the value before the commit, and that is all T1 adds.
2. **Most "transition invariants" are guards or state invariants in disguise.** Conditions of a step need the pre-state
   and the input (guards); conditions of a state need only the post-state (invariants). Genuine from-and-to rules after
   apply are monotonic rules on ordinary values ("price never drops") and the graph safety net.
3. **First/last can check reachability, not edges.** `Open → Closed → Settled` in one commit nets to `Open → Settled`.
   An edge check needs every step, and the only place that sees every step on the live path only is `Publish` (T2) —
   or the guard.
4. **The live raise path is the structural replay guard.** `Publish` is live-only and `ReplayHistory` bypasses it, so a
   check placed there (not in `On`) cannot run on replay by construction.
5. **Typed transition methods are a replay trap.** `Open.Close()` is safe in a behaviour and dangerous in `On`; apply
   must build the target state from the event, never from the current state.
6. **A stored state name is forever.** Unknown enum names throw on read and a pinned name hides the old one
   **[verified]**; renaming a state breaks old events and stored state rows unless the old name stays readable.
7. **A state is almost never input**, so the Validations side (value rules, `Create`) does not apply; the feature
   belongs where events and commits are (Primitives).
8. **Declaring the graph reveals dead states.** ArtFlow's `Settled` (and `Winner`) are unreachable today and nothing
   says so; reachability from the initial state is the cheapest useful generator check.

## Scope decisions (owner, round 1, 2026-11-14)

| # | Question | Answer |
|---|---|---|
| D-1 | Ambition (open question 1) | **Declare, guard and safety net**: the graph is declared once; generated `CanTransitionTo`/`Allows`, reachability check and diagram; behaviours enforce it with guards; the framework also catches a forgotten guard at commit, with no user code |
| D-2 | Kept until commit (open question 2) | **Per-step check only (T2)**: the edge check runs in the live raise path (`Publish`), stores nothing on the valid path, remembers the first forbidden step and `Prepare` returns it. **No baseline (T1)**, so invariants get no `Transition<T>`; monotonic rules stay ordinary guards |
| D-3 | Shapes (open question 3) | **Enum first**; the declaration is designed so that a `closed` hierarchy can use it later. Stage 2 still measures the hierarchy as a comparison |
| D-4 | Placement (open question 6) | **Step 6 part C**: its own PR in `Tarol.Primitives`, after part B (stored state), built on its capture rules |

Still open for stage 2 (asked with the measurements): failure type and code of a violation found at commit (open
question 4), edges labelled by events (open question 5), generator diagnostics and their severities, the CS8524
recommendation for enum switches.

## Open questions

Questions that change the scope (answers decide what stage 2 measures):

1. **Ambition**: is the value the *declaration* (one place, generated `CanTransitionTo`/`Allows`, reachability check,
   diagram) with guards doing the enforcement, or must the framework also *enforce* the graph at commit without user
   code (T1 reachability or T2 edges)? Or is the baseline (c: plain guards and invariants) enough, and the note ends
   here?
2. **Kept until commit, for what**: only the graph safety net (T2, stores nothing on the valid path), or also a
   baseline (T1) that user invariants can read as `Transition<T>` (`From`, `To`) — which then also serves monotonic
   rules on non-state values ("price never drops")? If T1, for state types only, or for any field marked as tracked?
3. **Shapes in scope**: enum only (smallest, bare JSON), the `closed` hierarchy with data-carrying states (removes state
   invariants, costs polymorphic JSON and an allocation per transition), or both with one graph declaration?
4. **Graph violation found at commit** (if 1 includes enforcement): a bug (`Error.Internal`, 500) or a conflict
   (`Error.Conflict`, 409)? Guards keep their own codes either way.
5. **Edges labelled by events** (`[Transition<AuctionStatus>(Open, Closed)]` on the event) instead of, or besides,
   edges on the states: worth the second declaration form for "which event performs which step" in the diagram and the
   analyzer check against `On`?
6. **Placement**: fold into step 6 part B (stored state must know the bookkeeping fields anyway), a separate part C of
   step 6 after part B, or later?
