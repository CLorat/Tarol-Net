# Domain core

Concept note for step 6 (see [primitives.md](../migration/primitives.md), written in stage 2): what an event-sourced
aggregate in Tarol is, decide vs apply, what an event is and where its data lives, child entities, snapshots,
invariants and replay safety, and what .NET 11 / C# 15 give. It starts from first principles; the old code
(`CLorat/Tarol@6a62f81`, after PR #24) is a seed, reviewed in the migration note.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output under
[`probes/primitives/`](../migration/probes/primitives/)); **[verified by reading]** means read in the named code, not
run; **[unverified]** means reasoned, or taken from the old reviews without re-running them. Old findings keep their
ids: F-*n* and I-*n* from `CLorat/Tarol` `docs/review/domain-core.md`, D-*n*/Q*n* from
`docs/review/net11-event-generation.md`, K-*n* from [kernel.md](../migration/kernel.md), V-*n*/S-*n* from
[validations.md](../migration/validations.md).

**Status: stage 2 (evaluation).** Scope decided (R-1 … R-4 at the end); the design decisions follow in the migration note.

## 1. What a user of Tarol needs from an aggregate

* **Behaviour methods that decide and raise**: `auction.PlaceBid(bidder, amount, now)` checks its guards against the
  current state and `now`, and either returns a failure or raises one or more events. It returns `Result` /
  `Result<T>` (validation.md §3, S-1).
* **State that changes only by applying events**: the same `On(event)` code runs live and on replay, so "what happened"
  and "what the aggregate looks like" cannot drift apart (old I-2).
* **Child entities with their own events** (owner decision before stage 1): `Auction` → `EntitySet<Bid>`, a single
  `EntityRef<ShippingAddress>`; a child raises its own events; the root routes them; deleting a child takes its
  children with it.
* **One consistency boundary per aggregate**: one stream, one version, one optimistic-concurrency check per commit.
* **Loading** by full replay, or snapshot + tail, with the same result (ROADMAP Phase 1 exit criterion).
* **Old data always loads**: an event recorded under last year's rules replays today (validation.md §7).
* **A behaviour test without a host**: given events → when behaviour → then events (or a failure), no DI, no clock, no
  store.
* **Short user code**: the aggregate, entity and event declarations at least as short as the PR #24 syntax
  (`sealed record Repriced(decimal Price) : Event;`). The complexity of children, routing, cascade and snapshots stays in
  `Tarol.Primitives` and its generator.

What the aggregate must **not** need: the caller (permissions are not invariants, [security.md](security.md) §4; replay
has no caller), a clock ([time.md](time.md)), a service provider, I/O, or the store's metadata (recorded-at, actor,
tenant, correlation).

## 2. Decide vs apply

```
behaviour(command values, now)        decide: guards → Result failure, or Raise(event)   (live only)
  Raise(event) → Publish → Dispatch   apply:  On(event) mutates state                     (live and replay)
drain changes                         invariants on the new state → hand the events out   (live only)
```

| Rule | Why | What enforces it in the old code | Gap |
|---|---|---|---|
| apply never raises | a raise inside apply would record an event on replay that is already in the stream | runtime `_dispatching` flag → `RaiseDuringDispatch` **[verified by reading]** `AggregateRoot.cs:61` | none; keep |
| apply never fails for business reasons | replay must accept every stored event | nothing | an analyzer can report a call to a `Result`-returning method or a `throw` inside `On` (§6) |
| apply never reads a clock | replay at a later time must give the same state | TKRN001 reports `DateTime(Offset).Now/UtcNow` anywhere **[verified by reading]** kernel.md | `TimeProvider.GetUtcNow()` inside `On` is not reported; the replay analyzer can add it |
| apply never validates | stricter rules must not reject old events | design only | analyzer (§6) |
| **state changes only inside apply** | the core promise of event sourcing (I-1) | `private set` + `[UnsafeAccessor]` closes the *library* paths (`EntitySet` mutators, child constructors, envelope setters, `RestoreVersion`) **[verified by reading]** `EntitySet.cs:466`, generated `Gallery.g.cs` | **a behaviour method in the same class can still assign `Name = x;` directly.** `private set` is private to the class, not to `On`. No access modifier can express "only in `On`"; only an analyzer can (insight 2) |

**When an apply throws (F-9).** `Publish` stamps the version, runs `Dispatch` and records the event only if it returns
**[verified by reading]** `AggregateRoot.cs:59-77`. A throw after the first handler leaves the aggregate half-applied,
with the old version and without the event. There is no cheap rollback for mutable children (copy-on-write of the whole
graph would cost every event). Two complementary answers:

* **Discard** (the contract): an exception from `Raise` means "this instance is garbage"; the pipeline (step 7) drops it
  with its uncommitted events. This is the same rule as an invariant failure (V-3), so the pipeline has one discard path.
* **Poison** (the guard): the aggregate remembers that a dispatch threw; every later `Raise`, drain or snapshot throws
  `AggregateBroken`. One `bool` that is already there (`_dispatching` stays set when the `finally` is replaced by a
  catch) **[unverified]**. It turns "someone caught the exception and carried on" from silent corruption into a loud
  error. On replay a throwing apply is a corrupt stream or a bug, and loading fails with the exception; it is not a
  `Result` (nothing the caller can fix).

**A failed behaviour that already raised.** A behaviour can raise one event and then return a failure from a later
guard. If the pipeline discards the instance on *every* failure (not only on invariant failures), this is harmless, and
the user does not have to order guards before all raises. That is one rule ("failure ⇒ discard") instead of two.

## 3. Events

### 3.1 What an event is

An event is a **fact of the domain, in the past tense, as data**: `Bid.Placed(BidderId, Amount, At)`. Its type is its
meaning; its payload is the facts the apply needs; its identity in the stream is `(aggregate id, version)`.

Everything else is about the event, not in it:

| Datum | Who knows it | Who needs it | Where it lives | Old code |
|---|---|---|---|---|
| payload (`Amount`, `At`) | the behaviour | apply, projections, sagas | the event record, JSON payload | same |
| business time (`At`) | the behaviour's `now` | apply (`EndsAt <= at`), projections | **payload field** (time.md decision 1) | `Timestamp` envelope field from `DomainClock` (F-12, K-2) |
| aggregate id | the aggregate | replay check (F-14), store stream key | stream (column) | `AggregateRootId` property with `internal set` |
| target entity id, parent id | `Raise` (the raising entity, the created child) | `Dispatch` routing | **routing envelope** (column) | `EntityId`, `ParentEntityId` properties with `internal set` |
| version | `Publish` | replay order, optimistic concurrency | routing envelope (column) | `Version` property with `internal set` |
| event type alias + schema version | the generator / `[EventType]` | the codec (step 8) | column | `EventColumns.Type/TypeVersion` |
| event id | the store at commit (UUIDv7 from its `TimeProvider`) | idempotency, causation of follow-ups | column | none |
| recorded-at | the store, **one per commit** | audit, "last changed" | column | `Timestamp` per `new` (time.md insight 2) |
| actor, tenant, correlation, causation | the `OperationContext` of the commit | audit, tenant filter, tracing | columns (step 8, kernel.md §12) | none |

**[unverified]** as a table; the "old code" column is verified by reading `DomainEvent.cs`, `EventCodec.cs`.

Consequences:

* **The domain needs none of the store metadata.** Nothing in `On` may read actor, tenant or recorded-at (replay has no
  caller, and a projection must not depend on who caused a fact unless it is in the payload). So the Kernel-typed slots
  (`Actor`, `Option<Id<ITenant>>`, `Id<ICorrelation>`, `Option<Id<ICausation>>`) belong to the **stored** event that
  the store returns to readers (projections, sagas, step 8/9), not to the domain event record. Putting mutable slots on
  the record would bring back F-10 (internal setters silently skipped by a same-assembly STJ context **[unverified
  here; verified in the old review, row 7]**) and would make a record's equality depend on store metadata.
* **Routing data does not need to be on the event object either.** `Dispatch` needs `(version, entity id, parent id,
  event)`. If `Raise` passes those as a small struct next to the event, the event record can be pure payload (§3.3 c).

### 3.2 What PR #24 built (candidate a) **[verified by reading]**

Generated per aggregate/entity: `public closed record Event : DomainEvent<Gallery>;` (and `Created`/`Deleted`) nested in
the user's partial type; a never-called `__Exhaustive` switch so a missing `On` is CS8509; the envelope as `[JsonIgnore]`
properties with `internal set`, restored by generated `[UnsafeAccessor]` code; a generated codec (`EventTypeOf`,
`GetColumns`, `WritePayload`, `ReadEvent`, `JsonResolver` built with `JsonMetadataServices`).

What it costs:

* The base types are **generator output**, so every *other* generator in the same compilation sees events deriving from
  an error type (old review, row 12): the user's STJ context cannot list the events, a saga generator in the same
  project falls back, and the Primitives generator itself has to resolve `Event`/`Created` **by name** through the
  error symbol's containing type. That is the "generators do not see each other" rule (V-9, F-11) applied to the core.
* Every event object carries five mutable envelope members (`Version`, `Timestamp`, `AggregateRootId`, `EntityId`,
  `ParentEntityId`, ≈ 64 bytes) that the user cannot set and the JSON must skip; record equality includes them, so two
  `Repriced(5)` are unequal when their versions differ.
* `CreateDomainEvent()` generates the child id **in the event constructor** with `Id<TChild>.New()`
  (`Guid.CreateVersion7()`, which reads the system clock) **[verified by reading]** `DomainEvent.cs:298`,
  `IdExtensions.cs:51`; it runs again on every deserialization and is then overwritten.
* Deletes carry the child id twice: in the payload (`Closed(Id<Room> RoomId)`) and in `EntityId`.

### 3.3 Candidates for the event model

| | Candidate | User code (an Artwork event) | Exhaustiveness | Other generators see the base | Envelope on the object |
|---|---|---|---|---|---|
| a | PR #24: generated nested `closed` bases | `sealed record Repriced(decimal Price) : Event;` | CS8509 witness + generator diagnostic | **no** (error type) | yes, `internal set`, `[JsonIgnore]` |
| b | hand-written `closed` family per entity | `public closed record ArtworkEvent : DomainEvent<Artwork>;` once, then `: ArtworkEvent` | CS8509 (user's own switch or a generated witness) | yes | either |
| c | **library-declared nested bases, pure payload, routing envelope beside the event** | `sealed record Repriced(decimal Price) : Event;` (same as a) | generator diagnostic (events are real types, so discovery cannot miss one); `closed` optional | **yes** | **no** |

Candidate c in detail (proposed here, to be measured in stage 2):

* `Tarol.Primitives` declares the bases as **nested types of its own generic base classes**:
  `AggregateRoot<TSelf>.Event`, `Entity<TRoot, TSelf>.Event`, `.Created`, `.Deleted`. Inside
  `class Artwork : Entity<Gallery, Artwork>` the unqualified `Event` resolves to the inherited
  `Entity<Gallery, Artwork>.Event`, so the user syntax is **identical to PR #24**, and the top-level form `: Artwork.Event`
  works too. These are real types from a referenced assembly: the user's STJ context serializes the events, a saga
  generator in the same project sees them, and the Primitives generator reads target and root straight from the base
  type's type arguments **[verified]** ([`probes/primitives/nested-bases`](../migration/probes/primitives/nested-bases/):
  same-assembly `JsonSerializerContext` round-trip, `typeof(Artwork.Hung).BaseType` =
  `Entity`2+Created[Gallery, Artwork]`, a user-declared `closed` sub-family still gets CS8509-exhaustive switches, Native
  AOT publish with 0 IL warnings).
* Events are **pure payload** records: no envelope members, value equality of the facts, safe to `with`-copy, nothing to
  `[JsonIgnore]`, nothing to restore through `[UnsafeAccessor]`. F-10 disappears by construction.
* `Raise` records a `readonly record struct` beside the event (working name `StreamEvent(long Version, Guid EntityId,
  Guid? ParentId, DomainEvent Event)`); `UncommittedChanges` and replay are spans of it. That struct **is** the routing
  columns, so the store maps it 1:1 (step 8).
* Children: `Raise(new Artwork.Hung(name))` on the owner assigns the new id (`Id<Artwork>.New()`, or an overload taking
  an explicit id for deterministic tests) and returns the child. A delete is raised **by the entity itself**
  (`public void Remove() => Raise(new Removed());`), so the id is the entity's own and is never repeated in a payload.
* Lost against a: compiler exhaustiveness for free (a library base cannot be `closed` for subtypes in the user's
  assembly, CS9382 in the old review). The generator still reports an event without a handler as an error, and with
  real types its discovery is semantic, not by name. Another assembly could in theory derive from `Artwork.Event`;
  `Dispatch` throws `UnknownEvent` for it.

Measured in stage 2 (primitives.md §3) for a, b, c: exhaustiveness diagnostics, raise/apply cost and allocations per event, replay of
10,000 events, AOT, JSON with a source-generated context in the same assembly (V-9, F-10).

### 3.4 The codec: aliases, payload JSON, upcasters

A stored event needs a stable alias (`gallery.artwork.repriced`, never the CLR name; D-7), a payload serializer that
works under AOT, and an upcaster hook for old schema versions.

* With candidate a the codec **must** be generated by the Primitives generator: only it can see the event types (they
  derive from its own output). That is why PR #24 put `IEventCodec`, `EventColumns`, `IEventUpcaster` and
  `JsonMetadataServices` metadata into Primitives.
* With candidates b and c the events are ordinary types, so the codec can be generated **in step 8** by the store's own
  generator, next to the code that uses it, or the store can take the user's STJ context for the payloads. Primitives
  then only needs to expose the alias (`[EventType]` attribute, default alias rule) or nothing at all.
* Value objects inside events: their generated converters are invisible to a same-assembly STJ context (V-9); whoever
  owns the payload JSON puts `ValueObjectJson.Resolver` first (step 8 obligation, validations.md §14).

## 4. Child entities (in scope; how, not whether)

| Topic | PR #24 **[verified by reading]** | Review |
|---|---|---|
| routing | flat identity map per entity type on the root (`Dictionary<Id<T>, T>`), O(1) per event at any depth; generated `switch` | keep; it is the reason deep trees are cheap |
| dispatch order | create: insert → target `On` → ancestors bottom-up → root; update: target → ancestors → root; delete: target → ancestors → root → remove with descendants (Q3) | keep: one rule; ancestors opt in by declaring `On(Child.Event)` |
| parent link | generated `__Parent` field set by `Dispatch`/`FromSnapshot` through `[UnsafeAccessor]` | keep |
| parent inference | the unique owner of an `EntitySet<T>`/`EntityRef<T>`; none or several → TAROL015 | keep; with candidate c the explicit form for a child with two owner types needs a design (`Created` cannot name the parent) |
| ids | child id from `Id<TChild>.New()` in the event constructor | generate in `Raise` (once, live only), store in the routing envelope; explicit-id overload for tests |
| cascade (D-5) | delete removes descendants from the maps, no `On` for them (Q6 = a) | keep; a descendant that must observe its removal raises its own delete first |
| strictness (F-7, F-14, I-4, I-9) | duplicate create, unknown delete, unknown update, foreign aggregate id, unknown event type: all throw, live and replay | keep; add "unknown parent on create" (already `OrphanChild`) to the tests |
| single children | `EntityRef<T>`: `Created` fills (throws if occupied), `Deleted` clears | keep |
| bypasses (F-1) | `EntitySet`/`EntityRef` mutators internal, child constructor private, reached by generated `[UnsafeAccessor]` (generic accessor classes; the closed-generic form crashes ILC, old review row 13) | keep; the remaining bypass is a direct assignment in a behaviour method (§2) |
| user entity constructor | an `(Id, Root)` constructor is called through `[UnsafeAccessor]`, so it may be private (F-4 fixed) | keep; a public one lets code `new` a detached entity, which cannot raise (its id is not in the map → `UnknownEntity`) **[unverified]** |

User code stays at the PR #24 size or shrinks (deletes lose the id parameter):

```csharp
public partial class Room : Entity<Gallery, Room>
{
    public EntitySet<Artwork> Artworks { get; } = new();
    public string Title { get; private set; } = "";

    public Artwork Hang(string name) => Raise(new Artwork.Hung(name));
    public void Close() => Raise(new Closed());

    public sealed record Added(string Title) : Created;
    public sealed record Closed : Deleted;

    private void On(Added e) => Title = e.Title;
}
```

## 5. Snapshots

A snapshot is a **cache of replay**: correct only if `FromSnapshot(ToSnapshot())` + tail equals a full replay, and
disposable whenever its shape changes (the store falls back to replay).

| Question | PR #24 **[verified by reading]** | Review |
|---|---|---|
| opt-in or opt-out | default-on, TAROL011 Warning (owner Q7 = b) | **opt-in** (`[Snapshot]`), completeness as an **Error**: a snapshot that drops state silently is worse than none (F-3, F-23); most aggregates have short streams and need no snapshot (insight 6) |
| what is captured | settable properties, `EntitySet`/`EntityRef` members recursively; diagnostics for what is skipped | capture **every instance field** of the aggregate and its entities (auto-property backing fields included) except generated ones and `[SnapshotIgnore]`; a field whose type is an entity, a root, or a collection of them is an Error. "All fields" is a rule a user can predict; property heuristics are not (insight 5) **[unverified]** |
| schema version | hash of the snapshot record's parameter list, compared on load | keep (hash of field names and types); mismatch → the store discards the snapshot and replays |
| record types | public `XSnapshot` records generated next to the aggregate | invisible to a same-assembly STJ context (F-11, SYSLIB1030); either Tarol emits their `JsonTypeInfo` with `JsonMetadataServices` (PR #24, AOT-clean in the old review) or it emits a writer/reader over `Utf8JsonWriter`/`Utf8JsonReader` and **no record types at all** (less public surface) |
| who owns the JSON | the generated `JsonResolver` of the aggregate | Primitives emits it (it sees the private fields); member types it does not know go through the options' resolver chain (value objects: `ValueObjectJson.Resolver`) |
| version | `RestoreVersion` through `[UnsafeAccessor]` | keep |

Proof: a property-based test (random behaviour sequences over every test model) asserting state(replay) ==
state(snapshot + tail) for every split point, plus a generator test that every field is either captured or diagnosed.

## 6. Invariants and replay safety

**Invariants (V-3)**: `[Invariant] Result SettledHasNoBids() => …;` on the root or an entity, aggregated into a generated
`CheckInvariants()`. Open points the stage-1 review adds:

* **When exactly.** Per event is too early (a behaviour may raise two events that are only consistent together); per
  behaviour needs a hook the core does not have. The natural point is **the hand-off of the uncommitted changes**: the
  only way to take events out of an aggregate for persistence (or for a test's "then") runs `CheckInvariants()` first
  and returns its failure instead of the events. Replay and snapshot loading never hand off, so they never check. The
  step 7 pipeline cannot forget it, and a behaviour test sees the same check (insight 3).
* **Entities.** Root invariants may read the whole tree; entity invariants read their own state. Checking only entities
  touched by this commit's events (and their ancestors) keeps the cost proportional to the change, not to the tree.
* **Cost with no invariants**: the generator emits nothing; the base returns the cached success.
* **Failure**: the rule's own `Error` (usually `Error.Conflict`, a state a guard did not anticipate). `Error.Internal`
  is right when the invariant is an assertion behind complete guards (a violation is a bug, 500); the rule decides by
  what it returns, the framework does not rewrite it. The instance is discarded (no rollback, F-9).

**Replay safety (S-3, V-8).** Repeat `tests/Validations/Tarol.Validations.Tests/ReplayTests.cs` on the real
`AggregateRoot`: events whose value objects violate today's rules replay, snapshot + tail replay, and no rule runs.
An analyzer can make it a build error:

* **Reported**: inside an `On`/apply method, or a `[JsonConstructor]`/primary constructor that deserialization uses: a
  call to any method returning `Result`/`Result<T>` (every Tarol rule, `Create` factory, guard and `CheckInvariants`
  returns one), a `throw`, `TimeProvider.GetUtcNow()`, `Raise`.
* **Why "returns `Result`"**: it is precise (a helper that formats a string is not reported) and needs no list of rule
  names **[unverified]**; false positives are measured in stage 2 (e.g. a `Result`-returning pure helper used for a
  derived value).
* It needs Primitives' knowledge of what an apply method is, so it lives in the Primitives analyzer.

## 7. .NET 11 / C# 15

| Feature | Use in the core | Status |
|---|---|---|
| `closed` hierarchies | exhaustive dispatch (CS8509, also in generated code) | GA **[unverified here; verified in the old review]**; only for types the user's assembly declares (candidates a, b, or an optional user family in c) |
| `union` | not for events: no routing info, every event listed twice, STJ cannot tell object cases apart | rejected in the old review |
| `[UnsafeAccessor]` | generated code reaches internal mutators and private constructors; generic accessor classes only | **[verified by reading]** generated code; old review verified under AOT |
| `JsonMetadataServices` | AOT-clean JSON metadata for types other generators emit | old review K1 |
| interceptors | registration call sites (`AddTarolAggregates()`), not dispatch | not needed in step 6 |
| `field` keyword | nothing (no backing-field workaround left in c) | — |
| extension members | `Id<T>.New()`; must be imported, generated code uses real members | lesson from steps 4–5 |
| generator chaining | still impossible: one generator never sees another's output (SYSLIB1030, V-9) | the reason for candidate c |
| Roslyn 5.9 | unions are preview in the API: generator/analyzer tests need `LanguageVersion.Preview` (results.md §8) | lesson from step 2 |

## 8. AOT, incremental, testable

* **AOT**: no reflection; `switch` dispatch; `typeof(T).Name` only in throw messages; generated `[UnsafeAccessor]` in the
  generic-class form; JSON through generated or source-generated metadata. `IsAotCompatible` on the package and an AOT
  smoke app.
* **Generator** (old F-15, F-6, F-18): per-type equatable models with no `Location`/`ISymbol`; models grouped per
  aggregate (one output per aggregate, so editing one aggregate regenerates only it); full-name matching; hint names
  from the full symbol key; record aggregates emitted as `record`; nested types supported, generic ones diagnosed; ids
  renumbered into the family prefix (`TPRM001…`, conventions: one prefix per generator); **no `build/*.targets`**.
  The old generator still `Collect()`s all per-type models before grouping **[verified by reading]** (net11 review §9);
  stage 2 measures whether a per-aggregate key can avoid it.
* **Behaviour tests** without a host: `var auction = Auction.Rehydrate(id, given);` → `var r = auction.PlaceBid(…, now);`
  → assert `r` and the drained events. "Given" written by hand needs routing data (entity ids, versions); a small helper
  that builds it by running behaviours, or takes `(entity, event)` pairs, keeps tests readable (open question 5).

## 9. What steps 7, 8, 9 need from step 6

| Step | Needs | Consequence for step 6 |
|---|---|---|
| 7 Changes | a way to run a behaviour: load → call → on failure discard → on success drain (with invariants) → commit | **behaviour methods are the contract**: public methods returning `Result`/`Result<T>`; `Raise` stays `protected`. Handed-over finding 1 ("no path from a change to `Raise`") is answered by not needing one; findings 14 (`Event => new(…)` re-creates on read) and 15 (`IChange` interfaces) belong to step 7 |
| 8 Persistence | the routing columns, the stream id and version, a way to drain and to rehydrate (full and snapshot + tail), aliases and payload JSON | `StreamEvent` (or PR #24's `EventColumns`), `Rehydrate`, the drain API, snapshot read/write; the codec here (a) or there (b, c) |
| 9 Sagas | domain events as typed records plus their stored metadata | real event types (b, c) let a saga generator in the same project see them |
| 13 Samples | ArtFlow on the new API | old finding 13 (wrong generic arguments) disappears with the shorthand (a or c) |

## New insights

1. **The library base types can be nested in Tarol's own generic base classes.** `Entity<TRoot, TSelf>.Event` gives the
   PR #24 shorthand (`: Event`) without generating a base type, so no generator in the compilation sees an error type
   **[verified]**. That removes the one structural cost of PR #24's design and makes the codec movable to step 8.
2. **The bypass that matters most is not reachable by `[UnsafeAccessor]`.** PR #24 closed every *library* mutation path,
   but a behaviour method can still write `Status = Closed;` because `private set` is class-private. Only an analyzer
   ("state assigned outside an apply method or constructor") closes I-1 for real; it is also the most common mistake
   newcomers make in event-sourced code.
3. **Invariants belong to the hand-off of events, not to a pipeline step.** If draining uncommitted changes runs
   `CheckInvariants()`, no caller can skip it, replay never runs it, and a behaviour test checks it for free.
4. **"Failure ⇒ discard" is one rule for three cases**: a guard that fails after a raise, an invariant violation, and an
   apply that throws (plus poisoning for the last). The user never has to reason about partial state.
5. **Snapshot completeness is a property of fields, not properties.** "Every instance field is captured or explicitly
   ignored" is predictable and checkable; the old property heuristics silently dropped fields, get-only collections and
   entity references (F-3, F-23).
6. **A snapshot is a cache, so it should be opt-in.** Default-on made every aggregate pay generated code and diagnostics
   for an optimisation that only long streams need, and made every completeness gap a silent divergence.
7. **The domain event needs none of the store metadata.** Actor, tenant, correlation, causation, event id and
   recorded-at are facts about the commit; they belong to the stored-event type readers get from the store, not to the
   record the aggregate raises. That also keeps record equality about the facts.
8. **Child ids are generated data, not business time.** They are created once in `Raise` and stored; replay never
   generates one, so `Id<T>.New()` reading the system clock there does not break replay determinism. The old place (the
   event constructor) generated one on every deserialization too.
9. **"Returns `Result`" is the cheap, precise signature of a rule.** Every guard, factory and invariant returns one, so
   the replay analyzer needs no list of rule names.

## Scope decisions (owner, round 1, 2026-11-14)

| # | Question | Answer |
|---|---|---|
| R-1 | Event model direction | **Propose candidate c** (library-nested bases, pure payload, routing envelope beside the event); measure a and b as comparison |
| R-2 | Codec, aliases, upcasters | **Step 8**, generated with the store. Step 6 keeps only what names an event (alias attribute, default alias rule, duplicate-alias diagnostic) |
| R-3 | Event metadata | **A stored-event type declared in Primitives** (Kernel-typed: actor, tenant, correlation, causation, event id, recorded-at), filled by the store in step 8 and shared by Persistence, Sagas and Messaging. The domain event itself has no metadata slots. Primitives therefore depends on Kernel |
| R-4 | PR split | **Two PRs**: (A) events, routing, child entities, invariants, replay safety, analyzers; (B) opt-in snapshots with the snapshot + tail property test |

## Decisions taken (owner, rounds 2 and 3, 2026-11-14; primitives.md §9)

* **Events** (candidate c): library-nested bases, pure-payload records, `StreamEvent` beside the event; deletes raised
  by the entity; `CreatedIn<TParent>` for a child with two owner types; `Raise` returns `Success`.
* **Children**: cascade, strict replay, `EntityRef<T>`, compile-time owner check (`IOwnedBy<TOwner>`).
* **Apply failure**: discard + poison. **Invariants**: at the hand-off, all entities, the rule's `Error`.
* **Analyzers** (Error): state assigned outside `On`; rule, throw, clock or `Raise` inside `On`.
* **Stored state instead of snapshots** (round 3): every commit stores the current state with the events; loads never
  replay; queryable JSON with property names and nested children; an uncapturable field is an Error; an old shape is
  replayed in memory and rewritten on the next change; step 8 adds a rebuild command. §5 above describes the earlier
  "cache" view and is superseded by this.
* **Package**: one `Tarol.Primitives` (`TPRM*`), depends on Kernel, Results, Options, StronglyTypedId; `Value`
  dropped; a given/when/then test helper ships in the package.

## What later steps must do

| Step | Must |
|---|---|
| 7 Changes | call behaviour methods (no access to `Raise`); discard the instance on any failure or exception; `AggregateChanges.Prepare` before commit; findings 14, 15 |
| 8 Persistence | store events (`StreamEvent` → columns) and the current state in one transaction with the expected version; fill `RecordedEvent` (event id, `RecordedAt` per commit, actor, tenant, correlation, causation); aliases from `[EventType]`; codec and upcasters; load from state, replay in memory on an old `schema`, rewrite on the next change; `RebuildStates<T>()`; `ValueObjectJson.Resolver` first |
| 9 Sagas | consume `RecordedEvent`; real event types are visible to the saga generator by symbol, not by name |
| 13 Samples | ArtFlow on the new syntax; `AuctionCreated.Id` duplicates the stream id: drop it |

## Open questions

Questions that changed the scope of step 6 (answered above, R-1 … R-4):

1. **Event model direction**: stay with PR #24 (generated `closed` bases, envelope on the event, candidate a), or
   evaluate candidate c (library-nested bases, pure payload, routing envelope beside the event) as the proposal, with a
   and b as comparison?
2. **Codec, aliases and upcasters**: in step 6 (as PR #24), or in step 8 with the store (possible with b or c)?
3. **Event metadata** (actor, tenant, correlation, causation, event id, recorded-at): on the domain event, in a
   stored-event type declared in Primitives and filled by the store, or entirely in step 8?
4. **PR split**: one PR, or (A) events, routing, children, invariants, replay safety, then (B) snapshots?

Asked in stage 2 with the measurements: snapshot opt-in and capture rule (§5), apply failure (§2), invariant scope on
entities and hand-off point (§6), bypass and replay analyzers and their severity (§2, §6), the testing helper (§8),
whether `Primitives.Value` stays and whether Primitives depends on Validations and Kernel (Kernel brings TKRN001 into
the user's domain code transitively), diagnostic ids.
