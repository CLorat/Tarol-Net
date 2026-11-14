# Session prompt: Tarol-Net, idea: state value types with declared transitions (concept and evaluation first)

## Task

Evaluate an idea from the owner: a **state value type**. A state is a value of the domain (an auction is `Open`,
`Closed` or `Settled`) whose **allowed transitions are declared once**, next to the states. The aggregate **keeps the
transition it went through (`from → to`) until commit**, so that invariants can evaluate the transition itself, not only
the end state ("closing requires an end time", "an auction never goes back to `Open`").

This is not a migration step. It is a feature idea that touches `Tarol.Validations` (step 5, declarative rules on value
objects), `Tarol.Primitives` (step 6, apply, invariants after apply, commit) and `Tarol.Changes` (step 7, guards). Find
where it belongs, whether it is worth it, and what it looks like in user code. Work in three stages:

1. **Concept review.** From first principles, for an event-sourced DDD framework on .NET 11. Write
   `docs/architecture/state-transitions.md` with **[verified]**/**[unverified]** labels, "New insights" and "Open
   questions". Present a short summary plus the questions that change the scope, then **stop and ask**.
2. **Evaluation.** Prototype the candidates in a scratch project (then as probes under
   `docs/migration/probes/state-transitions/`), measure, and write `docs/migration/state-transitions.md` with the
   proposed API and user code per candidate. Then **stop and ask me** the design decisions with the question tool, one
   question per decision, each with your recommendation and call-site code in the previews. If an answer is ambiguous
   or conflicts with another, ask again.
3. **Implementation** only after my answers, in the package and step we agree on (possibly folded into step 6 or 7
   instead of a PR of its own). Conventions §8 in full if it ships.

## The idea, in the owner's words (to refine, not to take literally)

* "It should be possible to define what transition is possible from what state to what."
* "It should keep the state switch until commit, so we can evaluate it in an invariant."
* "It is related to validation somehow, but could be very useful."

## What exists (verify, do not trust)

* `main` of Tarol-Net (fetch first). Read `AGENTS.md`, `docs/architecture/conventions.md`,
  `docs/architecture/validation.md`, `docs/migration/validations.md` (§4 invariants, §5 guards, §9 decisions V-1 … V-9),
  `docs/prompts/step-6-primitives.md` and, if step 6 has started or merged, `docs/architecture/domain-core.md` and
  `docs/migration/primitives.md`.
* Decisions that constrain the design:
  * **Value rules** live once on value objects (V-1, V-2); constructors and JSON reading **trust**, `Create` validates
    (insight 7). A state is a value object candidate.
  * **Invariants** (V-3): `[Invariant]` methods returning `Result`, aggregated into a generated `CheckInvariants()`, run
    **after apply on the live path only**; a violation is the rule's `Error` (usually `Error.Conflict`); the instance is
    discarded.
  * **Prerequisites are guards** in behaviour methods returning `Result`, with `now` as a parameter (S-1).
  * **Replay safety**: nothing validates during apply, replay, snapshots or deserialization. **An old event that records
    a transition that is forbidden today must still load.**
  * **Permissions are not invariants** (security.md): "who may close" is not a transition rule.
* The old ArtFlow example (`CLorat/Tarol@6a62f81`, `examples/ArtFlow/ArtFlow.Domain/ArtFlow/Auctions`) is the use case:
  `enum AuctionStatus { Open, Closed, Settled }`; transition rules hidden in prerequisites (`AuctionMustBeOpenToClose`,
  `AuctionIsOpen`) and invariants (`WhenClosedEndtimeMustBeSet`, `WhenSettlesBidsMustBeEmpty`). Rewrite these with each
  candidate.

## Concept review (stage 1)

Answer these in the note:
* **What is a transition rule?** Separate:
  * the **graph** (which `from → to` pairs exist at all: `Open → Closed`, `Closed → Settled`; never `Settled → Open`);
  * **conditions on a transition** ("closing requires an end time", "settling requires no open bids"): state-dependent,
    so invariant- or guard-like;
  * **conditions on a state** ("a closed auction has an end time"): ordinary invariants.

  Which kind belongs where: in the state type, in a guard, or in an invariant after apply?
* **Guard or invariant?** A forbidden transition can be refused before the event is raised (a guard: no event, no
  discard) or detected after apply (an invariant: the instance is discarded). Is "keep the switch until commit" needed
  for the graph, or only for transition conditions that need the new state? What is the cleanest user code?
* **What exactly is kept until commit?** The first `from` and the last `to` of the change, or every step (one behaviour
  may go `Open → Closed → Settled`)? Per state property, or per aggregate? What is cleared at commit, and what happens on
  discard? It must be invisible to replay and snapshots.
* **Replay**: apply records the transition without checking it, so forbidden historical transitions load. Can the
  mechanism *structurally* guarantee that (no check reachable from `On`/apply)?
* **Shapes of a state**: `enum` with a declaration of transitions; a C# 15 `closed` hierarchy or union, where states carry
  data (`Closed(DateTimeOffset EndTime)`) so that "closed requires an end time" disappears by construction; a struct value
  object over a string or enum. Which gives exhaustive `switch` (CS8509), compact JSON, and the least code?
* **Declaring the graph**: attributes (`[Transition(From = Open, To = Closed)]`), a generated table from a small DSL, a
  hand-written `static bool CanTransition(from, to)`, or the type of the transition method (`Open.Close(...) : Closed`)?
  What can a generator check at compile time (unreachable states, transitions to undeclared states, no terminal state)?
* **Does .NET 11 already have something?** (Stateless-style libraries, workflow engines: evaluate briefly, AOT, and why
  or why not.)
* **Documentation value**: can the declared graph generate a diagram (Mermaid) into the docs or the XML doc comments?
* Where does it live: `Tarol.Validations` (a value-object rule), `Tarol.Primitives` (needs apply and commit), or
  `Tarol.Changes`?

## Evaluation (stage 2)

1. Rewrite the ArtFlow auction with each candidate: user code for the state, the behaviour methods (`Close(now)`,
   `Settle()`), the invariants, and a test. Count lines and compare readability against today.
2. Candidates (at least):
   * (a) `enum` + declared transitions (attributes), generated `CanTransitionTo` and a tracked `Transition<TState>`
     (`From`, `To`) on the aggregate until commit;
   * (b) a `closed` hierarchy / union of states that carry their data, transitions as methods on the states;
   * (c) plain guards and invariants with no new type (the baseline: what does the idea add?).
3. Measure or verify: the cost of tracking per raised event (allocations: must be 0 for the valid path), AOT (0 IL
   warnings), JSON of the state in an event and in a snapshot (bare value, same-assembly context: V-9), exhaustiveness
   diagnostics, and a replay test with a historical transition that is forbidden today.
4. What a generator would add (graph checks, diagram, `CanTransitionTo`) and its diagnostics; equatable models,
   incrementality, snapshot tests with `tests/Shared/Tarol.Testing.Generators`.

## Decisions to ask me (at least)

1. Is the idea worth a feature, or are guards and invariants enough (candidate c)?
2. State shape: enum + declared transitions, union of data-carrying states, or both.
3. Where a forbidden transition is caught: guard (before the event), invariant (after apply), or both; and the failure
   (`Error.Conflict` with which code).
4. What is tracked until commit (first/last vs every step), and where it lives.
5. Generator and diagnostics (graph checks, diagram), and their severities.
6. Package and step: `Tarol.Validations`, `Tarol.Primitives` (step 6), or `Tarol.Changes` (step 7).

## Environment notes

* SDK 11.0.x: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0`, then
  `export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet`. BenchmarkDotNet `0.16.0-preview.2`.
* Generators do not see each other's output (validations.md §3.9): a generated `[JsonConverter]` is invisible to a
  source-generated JSON context in the same assembly; use `ValueObjectJson` or emit metadata.
* CI runs "Build generators" before `dotnet format`; verify from a clean tree.
* Probes go outside the repository first (`*.csproj.txt` when committed). Licence: proprietary, owner-only; publish
  nothing.
