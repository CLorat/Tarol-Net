# Step 6: Primitives (domain core)

Migration note for `Tarol.Primitives` (aggregate root, child entities, events, routing, rehydration, invariants,
snapshots, and their generator). Concept note: [domain-core.md](../architecture/domain-core.md) (first principles,
new insights, scope decisions R-1 … R-4). Old code: `CLorat/Tarol@6a62f81` (after PR #24).

Labels as in the [conventions](../architecture/conventions.md): **[verified]** = reproduced here, probe and output under
[`probes/primitives/`](probes/primitives/) (`*.csproj.txt` files are renamed so the repository build ignores them;
build from a scratch copy with `-p:TarolNetRepo=<clone>`); **[verified by reading]**; **[unverified]**.

**Status: part A implemented** (events, routing, children, invariants, failure rules, analyzers, test helper; §10–§13). Part B
(stored state, round 3 of §9) **implemented**: evaluation, answers and changes in §16.

## 0. Summary

* The old code is better than its review: PR #24 already fixed cascade, strict replay, nested types, the codec and most
  diagnostics. On SDK 11.0.100 all of its **64 unit tests and 28 generator tests pass with no build fix**, and its
  AOT smoke app has 0 IL warnings **[verified]** (§2).
* What is still wrong **[verified]**: four bypass routes (F-1 a–d), F-9 (after a throwing apply the aggregate stays
  usable and diverges from replay), `DomainClock` (F-12), and event equality that includes the envelope and a
  clock-stamped `Timestamp` (`new Repriced(5) == new Repriced(5)` is **false**).
* **Candidate c** (owner R-1: library-nested event bases, pure-payload events, routing envelope beside the event)
  keeps the PR #24 user syntax, passes every check on JIT and Native AOT (0 IL warnings), turns "wrong owner raises a
  create event" into a **compile error** (CS0311), and raises a child event **2.3× faster with 32 B instead of 88 B**;
  replay of 10,000 events costs the same (621 µs vs 605 µs) **[verified]** (§3).
* The in-class bypass ("a behaviour assigns `Price = x` directly") and replay safety need an **analyzer**; a prototype
  finds 6/6 seeded cases, is silent on the real model, and has 2 false positives and 1 false negative **[verified]**
  (§4, §7).
* Proposed package: one `Tarol.Primitives` with its generator and analyzer (`TPRM001…`), depending on Kernel (R-3),
  Results, Options, StronglyTypedId; not on Validations; `Value` is dropped (§8).

## 1. Inventory

### 1.1 Old code **[verified by reading]**

`src/Primitives/Primitives` (18 files, ≈ 900 lines): `AggregateRoot`, `Entity`, `EntitySet`, `EntityRef`, `DomainEvent`
(+ `DomainClock`), `EventEnvolope.cs` (internal `EventEnvelope`), `EventCodec.cs` (`EventTypeAttribute`, `EventType`,
`EventColumns`, `IEventUpcaster`, `IEventCodec<T>`), `AggregateSnapshort.cs`, `ISnapshottable.cs`, `IRegistry`,
`SnapshotIgnoreAttribute`, `TarolPrimitivesJson`, `ThrowHelper`, `Value` (`[GenerateValidation]`),
`ChangeAccessor/AggregateRootChangeAccessor`, `GlobalUsings.cs` (Kernel, Options, StronglyTypedId).
`gen/Primitives/Primitives.Generators` (≈ 2,500 lines, TAROL001–TAROL019 without 010, and a forbidden
`Tarol.Primitives.targets`).

### 1.2 Consumers **[verified by reading]** (agent inventory over `src/`, `gen/`, `examples/`, `templates/`)

| Consumer | Uses | Step |
|---|---|---|
| `src/Changes` | `IAggregateRoot`, `IDomainEvent` as constraints only | 7 |
| `src/DomainEvent` | `IDomainEvent` in `IDomainEventDispatcher.Dispatch` | 8 |
| `src/Persistence`, `Persistence.Marten` | `AggregateRoot<T>`/`IAggregateRoot<T>` constraints, `Rehydrate`, `AggregateRootChangeAccessor.GetChanges/ClearChanges`, `UncommittedChanges`, `Version`, `FromSnapshot`/`ToSnapshot`, internal `EventEnvelope.Restore` (via `InternalsVisibleTo`) | 8 |
| `gen/Sagas` | name match on a base called `DomainEvent` (not namespace-qualified), `TypeArguments[0]` as the root, only to break correlation ties | 9 |
| `examples/ArtFlow` | `AggregateRoot<T>`, `Entity<,>`, `EntitySet`, `Value`, generated `.Event`/`.Created` | 13 |
| CQRS, Messaging, `src/Sagas`, templates | nothing | — |

Notes: ArtFlow has **no wrong event generic arguments any more** (validations finding 13 was fixed by the PR #24
shorthand); `AuctionCreated.Id` and `ArtworkCreated.ArtworkId` repeat the aggregate id in the payload.
`MartenSnapshotRepository.RebuildSnapshotFromEvents` restores only version and timestamp, so the `ForeignEvent` check
should reject its own events **[unverified]** (step 8 replaces it).

### 1.3 What later steps need

| Step | Needs from step 6 |
|---|---|
| 7 Changes | behaviour methods as the contract (`Result`/`Result<T>`, `now` as a parameter), a discard rule, a hand-off that runs invariants |
| 8 Persistence | routing columns (`StreamEvent`), `Rehydrate`, the hand-off and commit acknowledgement, aliases (`[EventType]`), the stored-event type (R-3); snapshots after PR B |
| 9 Sagas | real event types (visible to a saga generator in the same project), the stored-event type |
| 13 Samples | the user syntax |

## 2. Review: old tests and findings on SDK 11.0.100 **[verified]**

Tests (copy of `6a62f81`, `dotnet test`): `Primitives.UnitTests` 64/64, `Primitives.Generators.UnitTests` 28/28 (5
snapshot domains, 19 diagnostic cases, 3 incrementality cases, 1 model purity), AOT smoke 0 IL warnings, exit 0.

| Finding | Status at `6a62f81` | Evidence |
|---|---|---|
| F-1 / I-1 / I-7 bypasses | **partly open** | closed: `EntitySet` mutators, child constructors, envelope setters, `SetVersion`, `IRegistry.Register` (CS0122/CS0200). **Open:** (a) a behaviour assigns a `private set` property (`live Name=Sneaky, replay Name=Tate`); (b) public `ref` returns `IRegistry<T>.FindRef` and `EntitySet.FindRef` let code swap in a foreign entity; (c) generated `private` members (`_roomMap.Clear()`, `__NewRoom(…)`) are callable from the user's half of the partial; (d) an event forged with the public `ReadEvent` and applied through `AggregateSnapshot.Replay` or `ReplayHistory` (`protected internal`) mutates state unrecorded or leaves a version gap |
| F-2 / I-6 cascade | fixed | recursive `__Cascade`; `Probe03_*` |
| F-3 / F-23 snapshot drops | mostly fixed | TAROL011 (Warning) for fields, get-only collections, entity references |
| F-4 user entity ctor | fixed | `Probe08_*` |
| F-5 `EntitySet` fields | fixed | TAROL012 for assignable members |
| F-6 nested/generic | fixed with TAROL013 | CS0534 still appears next to TAROL013 (noise) |
| F-7 / I-5 unknown delete | fixed | `Probe10_*` |
| F-8 / I-8 handlerless event | fixed (TAROL008 + CS8509 witness) | possible false positive: TAROL008 for `OtherEv : BaseEv` although `On(BaseEv)` exists |
| **F-9 throwing apply** | **open** | `_dispatching` is reset in `finally`: `after throw: Name=half-applied`, the next raise succeeds, live ≠ replay; a throwing create leaves the child in the set and the map. No test |
| F-10 envelope JSON | fixed by `[JsonIgnore]` + codec | a user context silently yields `Version=0` |
| F-11 snapshot records | mitigated | a user `[JsonSerializable(typeof(GallerySnapshot))]` still gives SYSLIB1030 |
| **F-12 `DomainClock`** | **open** | `DomainEvent.cs:116`; tests swap it |
| F-14 / I-9 foreign events | fixed on the public path | hole: (d) above |
| F-15 generator pipeline | partly | no `Location` in models; one `Collect()` of all per-type models (`TarolGenerator.cs:35`), then per-aggregate rendering (cached, incrementality tests) |
| F-16, F-17, F-19, F-20, F-22, I-2, I-3, I-4 | fixed | — |
| F-18 record aggregates | n/a | `partial record X : AggregateRoot<X>` is CS8864 (a record cannot derive from a class) |
| F-21 file-name typos | open | disappear with the new names |
| equality | **open (new)** | `new Repriced(5) == new Repriced(5)` is false: equality includes the envelope and the `Timestamp` initializer |

Probe lines that become regression tests: `[F1-privset]`, `[F1-findref]`, `[F1-forge-public]`, `[F1-forge-protected]`,
`[F9-root]`, `[F9-create]` (agent probe, reproduced against `6a62f81`).

## 3. Event model candidates **[verified]**

Same model in each (Gallery → Room → Artwork, ancestor handler `Room.On(Artwork.Repriced)`, cascade on `Room.Closed`).

### 3.1 User code

```csharp
// (a) PR #24 and (c): identical declarations
public partial class Artwork : Entity<Gallery, Artwork>
{
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }

    public Success Reprice(decimal price) => Raise(new Repriced(price));     // (a): void

    public sealed record Hung(string Name) : Created;
    public sealed record Repriced(decimal Price) : Event;

    private void On(Hung e) => Name = e.Name;
    private void On(Repriced e) => Price = e.Price;
}

// delete: (a) the parent raises with the id in the payload; (c) the entity raises, no id
public sealed record Closed(Id<Room> RoomId) : Deleted(RoomId);              // (a)
public sealed record Closed : Deleted;  public Success Close() => Raise(new Closed());   // (c)

// (b) hand-written closed families: one extra line per entity, explicit lifecycle bases
public closed record ArtworkEvent : DomainEvent<Gallery, Artwork>;
public sealed record ArtworkRepriced(decimal Price) : ArtworkEvent;
public sealed record ArtworkHung(string Name) : CreateDomainEvent<Gallery, Gallery, Artwork>;
```

### 3.2 Generated code (c, abridged; full file `probes/primitives/candidate-c/App/Domain.g.cs`)

```csharp
partial class Gallery : IRegistry<Room>, IRegistry<Artwork>
{
    private readonly Dictionary<Id<Room>, Room> _roomMap = new();
    private readonly Dictionary<Id<Artwork>, Artwork> _artworkMap = new();
    public static Gallery Rehydrate(Id<Gallery> id, ReadOnlySpan<StreamEvent> history) { … }
    protected override void Dispatch(in StreamEvent se)
    {
        switch (se.Event)
        {
            case Opened e: On(e); return;
            case Artwork.Hung e: { owner = _roomMap[(Id<Room>)se.ParentId]; artwork = __NewArtwork(…); … __Apply(artwork, e); return; }
            case Room.Closed e: { __Apply(room, e); /* cascade */ … return; }
            case Entity<Gallery, Artwork>.Event e: { __Apply(artwork, e); __Apply(__ParentOfArtwork(artwork), e); return; }
            default: Throw.UnknownEvent(…); return;
        }
    }
    protected override Result CheckInvariants() { /* touched artworks only */ }
}
partial class Artwork : IOwnedBy<Room> { private Room __Parent; private Artwork(Id<Artwork> id, Gallery root) : base(id, root) { } … }
```

### 3.3 JSON of one event

| | `Artwork.Repriced(1000)` payload | Envelope | Same-assembly STJ context |
|---|---|---|---|
| a | `{"Price":1000}` (envelope `[JsonIgnore]`) | 5 properties on the object, restored by the codec through `[UnsafeAccessor]` | **works for the payload** (it is a user type; only its base is generated), but round-trip equality fails: `back=False` (envelope and `Timestamp` differ) |
| b | same as a | same as a | works |
| c | `{"Price":1000}` | `StreamEvent(Version: 4, EntityId: <artwork>, ParentId: ∅, Event)`, i.e. the columns | works, round-trip **equal** |

Correction to the concept note: a user context *can* list a PR #24 event (it is a user type whose base is a generated
type); what it cannot see is generated types themselves (snapshot records, SYSLIB1030).

### 3.4 Measurements (BenchmarkDotNet 0.16.0-preview.2, ShortRun, same machine; compare ratios)

| | Raise a child event | Raise a create (incl. entity) | Replay 10,000 events (100 rooms, 900 artworks, 8,999 reprices) | AOT IL warnings | Exhaustiveness |
|---|---|---|---|---|---|
| a (PR #24) | 63.9 ns, 88 B | 442 ns, 464 B | 605 µs, 319 KB | 0 (old smoke) | TAROL008 + CS8509 witness |
| b | = a (same runtime) | = a | = a | 0 | TAROL008 + CS8509 in the user's own switches |
| c | **27.2 ns, 32 B** | 514 ns, 547 B | 621 µs, 319 KB | **0** | generator diagnostic (semantic discovery); optional user `closed` family |

* c is faster on raise because the event is smaller (no envelope members, no `Timestamp` clock read) and the envelope
  is a struct in the change list.
* c's create is ≈ 70 ns and 83 B dearer because it records the new entity in the "touched" set for entity invariants
  (§6); with no entity invariant the generator emits no tracking.
* Replay is equal within error.

## 4. Bypass closure

| Path | a (PR #24) | c (proposed) | Enforced by |
|---|---|---|---|
| `EntitySet`/`EntityRef` mutators | internal + `[UnsafeAccessor]` | same | access modifier |
| child constructor | private, `[UnsafeAccessor]` | same | access modifier |
| envelope setters | internal, `[UnsafeAccessor]` | **no envelope on the event** | construction |
| `RestoreVersion`, `ReplayHistory` | internal / **`protected internal`** (F-1 d) | `ReplayHistory` internal, reached by generated code only | access modifier |
| writable `ref` lookups (F-1 b) | public `FindRef` | **removed**: `TryGet`/`Find` return the entity, not a `ref` | API shape |
| forged events through the public codec (F-1 d) | `ReadEvent` + `AggregateSnapshot.Replay` | codec moves to step 8 (R-2); replay entry is `Rehydrate` only | API shape |
| generated members used from the user's half of the partial (F-1 c) | compiles | `[EditorBrowsable(Never)]` + analyzer: use of a generated member outside generated code | analyzer |
| create raised by a non-owner | runtime `OrphanChild` | **compile error CS0311** (generated `IOwnedBy<TOwner>` + constraint) | type system |
| **direct state assignment in a behaviour (F-1 a)** | compiles, live ≠ replay | analyzer TPRM900 (prototype §7) | analyzer |

All `[UnsafeAccessor]` calls use the generic-accessor-class form (the closed-generic form crashes ILC, old review);
c's AOT publish has 0 IL warnings.

## 5. Snapshots (part B, R-4)

Not prototyped in stage 2 beyond the old tests: the four `SnapshotReplayEquivalenceTests` and the cascade case pass on
`6a62f81`. Proposal for PR B (decision 3): opt-in `[Snapshot]`; capture every instance field of the root and its
entities (backing fields included) except generated ones and `[SnapshotIgnore]`; an uncapturable field (an entity,
a root, a collection of them, a delegate) is an **Error**; schema version = hash of field names and types; Tarol emits
the JSON (`JsonMetadataServices`, or a writer/reader without public record types); proof by a property-based test
(random behaviour sequences, every split point) against full replay.

## 6. Invariants **[verified]** (candidate c)

```csharp
[Invariant] private Result PriceNotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Artwork.NegativePrice");
```

* Run at the **hand-off** (`AggregateChanges.Prepare(aggregate)` before the store reads `UncommittedChanges`), on the
  root and on the entities touched by uncommitted events. Probe: `art.Reprice(-1)` → `Failure(Error { Conflict,
  Artwork.NegativePrice })`, events stay uncommitted; the same history **replays without running the rule** (price
  −1 loads).
* Cost: no invariant → nothing generated; with entity invariants ≈ 70 ns / 83 B per created entity, one `HashSet` add
  per child event.
* Failure path: the pipeline discards the instance (no rollback). The rule's `Error` is returned unchanged.

## 7. Replay safety and the analyzer prototype **[verified]**

Replay test on the real aggregate (c probe): a stored `Reprice(-1)` that violates today's invariant replays; strict
replay still throws on gaps and unknown entities. Step 6 ports `ReplayTests` with value objects whose rules got stricter.

Analyzer prototype (`probes/primitives/candidate-c/Analyzer`, Roslyn 5.9, `IOperation`), run on a corpus:

| Rule | Reports | Corpus result |
|---|---|---|
| TPRM900 state outside apply | an assignment, compound assignment or `++`/`--` to an instance member of `this` in a domain type, outside an `On(event)` method, a constructor, generated code, and private helpers reached only from `On` | TP: `RenameDirect`, `Bump`, misnamed handler `On2`; silent: the Gallery model, helper only called from `On`, locals; **FP:** lazy cache `_cache ??= …` in a getter |
| TPRM901 deciding inside apply | in `On`: a call returning `Result`/`Result<T>`, `throw`, `TimeProvider.*`, `Raise` | TP: `Money.Create`, `throw`, `GetUtcNow`, `Raise`; **FP:** pure `Result`-returning arithmetic (`Total.Add(…)`) |
| (gap) | — | **FN:** write through another instance (`other.Name = "x"` in a static method); a throw helper (`ArgumentOutOfRangeException.ThrowIfNegative`) |

Both false positives have a clear escape (a `[SnapshotIgnore]`-style `[Transient]` field for caches; a non-`Result`
helper for arithmetic) and a `#pragma` with justification; the false negatives are rare and covered by the replay test.

## 8. Proposed API and grouping (candidate c)

| Type | Namespace | Notes |
|---|---|---|
| `AggregateRoot<TSelf>` (+ nested `Event`) | `Tarol.Primitives` | `Id`, `Version`, `IsBroken`, `UncommittedChanges : ReadOnlySpan<StreamEvent>`, `protected Success Raise(Event)`, `protected TChild Raise<TChild>(Entity<TSelf,TChild>.Created[, Id<TChild>])`, `implicit operator Id<TSelf>`; `[EditorBrowsable(Never)] protected abstract void Dispatch(in StreamEvent)`, `protected virtual Result CheckInvariants()` (generated) |
| `Entity<TRoot, TSelf>` (+ nested `Event`, `Created`, `CreatedIn<TParent>`, `Deleted`) | `Tarol.Primitives` | `Id`, `protected TRoot Root`, `Raise(Event)`, `Raise(Deleted)`, `Raise<TChild>(Created[, Id<TChild>])` |
| `DomainEvent` | `Tarol.Primitives` | empty abstract record |
| `StreamEvent` | `Tarol.Primitives` | `readonly record struct (long Version, Guid EntityId, Guid ParentId, DomainEvent Event)`; `ParentId` is `Guid.Empty` except for creations |
| `RecordedEvent` (R-3) | `Tarol.Primitives` | `sealed record (Guid StreamId, StreamEvent Event, Guid EventId, DateTimeOffset RecordedAt, OperationContext Context)`; `OperationContext` (Kernel) carries actor, tenant, correlation, causation; filled by the store (step 8) |
| `EventType`, `EventTypeAttribute` | `Tarol.Primitives` | `(Alias, Version)`; default alias `<root>.<containing types>.<event>` in snake case |
| `IAggregateRoot`, `IAggregateRoot<TSelf>` | `Tarol.Primitives` | `static abstract TSelf Rehydrate(Id<TSelf>, ReadOnlySpan<StreamEvent>)`, `static abstract EventType EventTypeOf(DomainEvent)` |
| `IEntity<TSelf>`, `IOwnedBy<TOwner>`, `IRegistry<TEntity>` | `Tarol.Primitives` | `IRegistry<T>.TryFind(Id<T>, out T)`; no `ref` lookups |
| `EntitySet<T>` (+ `EntitySet.Create`), `EntityRef<T>` | `Tarol.Primitives` | read-only; `[]` only; mutators internal |
| `AggregateChanges` | `Tarol.Primitives` | `Result Prepare(aggregate)` (poison check + invariants of the root and every entity), `void Committed(aggregate)` |
| `InvariantAttribute`, `TransientAttribute` | `Tarol.Primitives` | `[Transient]`: a cache, exempt from TPRM013 and (part B) not stored |
| `AggregateTest`, `AggregateTest<TSelf>`, `AggregateOutcome<TSelf>` | `Tarol.Primitives` | given (by behaviours, committed and replayed) / when / outcome (`Result`, `Events`, `Aggregate`, `History`, `Replay()`) |
| `ThrowHelper` | `Tarol.Primitives` | `[EditorBrowsable(Never)]`, used by generated code |

Dependencies: Kernel (R-3; also brings TKRN001 into the user's domain code), Results, Options, StronglyTypedId. Not
Validations: invariants and guards return `Result`; value objects are Validations' business. **`Primitives.Value` is
dropped** (validations.md §7.6). Diagnostics renumbered to `TPRM001…` (conventions §2). Package: one
`Tarol.Primitives` with its generator and analyzer bundled (D-10), no `build/*.targets`.

Handed-over Changes findings: 1 (no path from a change to `Raise`) is answered by the behaviour-method contract
(`Raise` stays `protected`); 13 is already fixed; 14 and 15 belong to step 7.

## 9. Decisions for the owner

Asked with the question tool (one question per decision, recommendation first). Answers are recorded here.

1. Event model and user syntax (c), delete raised by the entity, explicit form for a child with two owner types.
2. Child entities: cascade, strict replay, `EntityRef<T>`, compile-time owner check.
3. Snapshots (PR B): opt-in, field capture, Error diagnostics, schema hash, Tarol-owned JSON.
4. Apply failures (F-9): discard and poison.
5. Invariants: V-3 on roots and entities, checked at the hand-off, touched entities only; failure = the rule's `Error`.
6. Bypass enforcement: access modifiers + `[UnsafeAccessor]` + analyzer (severity).
7. Replay analyzer: scope and severity.
8. Package layout, dependencies, `Value`, diagnostic ids, behaviour-method contract (`Raise` returns `Success`).

### Owner answers (round 2, 2026-11-14)

| # | Decision | Answer |
|---|---|---|
| 1 | Event model | **candidate c**; a delete is raised by the entity itself (no id in the payload); a child with two owner types uses `CreatedIn<TParent>`; `Raise` returns `Success` |
| 2 | Child entities | **keep** cascade (D-5 a), strict replay, `EntityRef<T>`, plus the compile-time owner check (`IOwnedBy<TOwner>`, CS0311) |
| 3 | Snapshots → **stored state** | (round 3) Not a replay cache: **every commit stores the aggregate's current state** with its events, in one transaction (step 8); a normal load reads the state and never replays. Default-on for every aggregate (`[SnapshotIgnore]` on the class = load by replay). Capture **every instance field** except generated and `[SnapshotIgnore]`/`[Transient]`; an uncapturable field is an **Error**. State JSON is **queryable**: property names (backing field → property name, plain field → name without leading `_`) in the store's naming policy, children nested as arrays with their id, `id`/`version`/`schema` on the root, no envelope, `[StateName("x")]` pins a name; no public record types. Shape change: the load replays in memory, the state row is rewritten **on the next change**; rows carry `schema` so queries can filter; step 8 adds `RebuildStates<T>()`. Proof: property-based "stored state == full replay" test |
| 4 | Apply failures (F-9) | **discard + poison** |
| 5 | Invariants | at the hand-off (`AggregateChanges.Prepare`), **all entities** on every commit (no touched-set tracking); the rule's `Error` unchanged |
| 6, 7 | Analyzers | both **Error**: state assigned outside an `On` handler; rule/throw/clock/`Raise` inside `On`; `[Transient]` fields exempt |
| 8 | Package | one `Tarol.Primitives` (generator + analyzer, `TPRM001…`), depends on Kernel, Results, Options, StronglyTypedId, not Validations; `Value` dropped; behaviour methods are the step 7 contract; Changes findings 14/15 → step 7 |
| — | Test helper | **ship a given/when/then helper** in the package |

### Owner answers (round 3, 2026-11-14)

Snapshots become **stored state** (decision 3 above, reopened and answered): yes, every commit stores the current state
and normal loads never replay; the state JSON is queryable; an old shape is replayed in memory and rewritten on the next
change, rows carry their `schema` and step 8 offers `RebuildStates<T>()`; an uncapturable field is an Error. JSON
shape: property names, children nested with their ids, no public record types. Built in part B.

## 10. Changes against Tarol (`6a62f81`)

### 10.1 Projects

| Old | New |
|---|---|
| `src/Primitives/Primitives` (`Tarol.Primitives`) | `src/Primitives/Tarol.Primitives` |
| `gen/Primitives/Primitives.Generators` (+ `Tarol.Primitives.targets`) | `src/Primitives/Tarol.Primitives.Generators`, bundled (D-10), **no targets file** |
| `tests/Primitives/Primitives.UnitTests`, `.Generators.UnitTests`, `.AotSmoke` | `tests/Primitives/Tarol.Primitives.Tests`, `.Generators.Tests`, `.AotSmoke`, `.PackageTests` |

The runtime and generator were rewritten for candidate c, not copied: the old design put the envelope on the event and
generated the event bases, both of which change shape. What carries over in substance: flat identity maps, generated
`switch` dispatch, `[UnsafeAccessor]` to internal mutators and private constructors, cascade, strict replay, `EntityRef`,
uniform dispatch order, the `ThrowHelper` messages, default aliases and `[EventType]`.

### 10.2 Public API and behaviour changes (every one deliberate)

| Old | New | Why |
|---|---|---|
| events derive from generated `closed record Event : DomainEvent<R, X>` (or `DomainEvent<R, X>`, `CreateDomainEvent<R, P, X>`, `DeleteDomainEvent<R, P, X>`) | events derive from the library's nested bases `AggregateRoot<R>.Event`, `Entity<R, X>.Event/Created/CreatedIn<P>/Deleted`; **user syntax unchanged** (`: Event`, `: Created`) | R-1: real types, visible to other generators and the user's JSON context; target and root read from symbols |
| envelope on the event (`Version`, `Timestamp`, `AggregateRootId`, `EntityId`, `ParentEntityId`, `internal set`, `[JsonIgnore]`) | **none**: `StreamEvent(Version, EntityId, ParentId, Event)` beside the event | F-10 gone; value equality of payloads; 32 B instead of 88 B per raised child event |
| `DomainEvent.Timestamp` from `DomainClock` | **removed**, with `DomainClock` (F-12, K-2) | recorded-at belongs to the commit (`RecordedEvent.RecordedAt`); business time is a payload field |
| `IDomainEvent`, `IDomainEvent<R, X>`, `IAggregateEvent<R>` | removed | routing is in `StreamEvent`; consumers constrain on `DomainEvent` |
| `UncommittedChanges : ReadOnlySpan<DomainEvent>`, `Rehydrate(id, ReadOnlySpan<DomainEvent>)` | `ReadOnlySpan<StreamEvent>` both | the envelope travels with the event |
| `AggregateRoot` (non-generic base class) | `IAggregateRoot` (marker interface) | nothing needed a base class |
| `protected AggregateRoot()` (id from `Id.New()`) | only `protected AggregateRoot(Id<TSelf> id)`; the generated constructor is private | one way to create; factories pass `Id<T>.New()` |
| `Raise(...)` returns `void` | returns `Success` | `return Raise(…);` in a `Result` behaviour |
| delete raised by the parent with the child id in the payload (`Closed(Id<Room>) : Deleted(id)`) | raised by the entity itself (`Raise(new Closed())`), no id in the payload | decision 1; the id is in `StreamEvent.EntityId` |
| child id generated in the event constructor (on every deserialization too) | generated once in `Raise` (`Id<T>.New()`), or passed: `Raise(e, id)` | insight 8 |
| a creation raised by a non-owner: runtime `OrphanChild` | **compile error CS0311** (`IOwnedBy<TOwner>` constraint) | decision 2 |
| a child with two owner types: explicit `CreateDomainEvent<R, P, X>` | `CreatedIn<TParent>` | the parent is named, the root inferred |
| `IRegistry<T>.FindRef`, `EntitySet.FindRef` (writable `ref`, F-1 b) | `IRegistry<T>.TryFind`, `EntitySet.TryGet` | no swap of a foreign entity |
| `ReplayHistory` `protected internal`, public `AggregateSnapshot.Replay`, public codec `ReadEvent` (F-1 d) | `ReplayHistory` internal (generated `Rehydrate` only); codec and snapshots not in part A | no forged, unrecorded event |
| generated members callable from the user's partial (F-1 c) | still compiles, **TPRM015** reports `Dispatch`, `CheckInvariants` and `__` members | an access modifier cannot express it |
| a behaviour assigning state directly (F-1 a) | **TPRM013** | decision 6 |
| a throwing `On`: state half-applied, aggregate usable (F-9) | **poisoned**: `IsBroken`, every further `Raise`/`Prepare` throws; replay propagates | decision 4 |
| no invariants (old `[Invariant] bool` in Validations) | `[Invariant] Result M()` on root and entities, run by `AggregateChanges.Prepare` over **all** entities | V-3, decision 5 |
| `AggregateRootChangeAccessor.GetChanges/ClearChanges` | `UncommittedChanges` + `AggregateChanges.Prepare/Committed` | the hand-off runs invariants |
| `IEventCodec<T>`, `EventColumns`, `IEventUpcaster`, generated `JsonResolver`/`TarolJson` | **moved to step 8** (R-2); `EventTypeOf` and `[EventType]` stay | events are ordinary types |
| `ISnapshotable`, `ISnapshotSource`, `AggregateSnapshot`, `SnapshotIgnore`, generated `XSnapshot` records | **stored state** (part B, §16.11): `StateSchema`/`WriteState`/`ReadState` on `IAggregateRoot<T>`, `[LoadByReplay]`, no public records | decision 3, round 4 |
| `Value` (`[GenerateValidation]` record) | **removed** | value objects come from Tarol.Validations (validations.md §7.6) |
| `TarolPrimitivesJson` | removed | no Tarol-owned JSON in part A |
| `GlobalUsings.cs` | removed; files write their usings | options.md, kernel.md |
| foreign events: `AggregateRootId` check | structural: a root event must carry the root id, a creation under the root must name it, any other foreign event targets an unknown entity | no aggregate id on events |
| an unknown event type in the dispatch | throws `UnknownEvent` (unchanged) | |
| new | `AggregateTest` given/when/then helper | decision "test helper" |
| diagnostics TAROL001–TAROL019 | TPRM001–TPRM015, all Error (§11) | conventions: family prefix |

### 10.3 Implementation notes

* **Generator pipeline.** Per-type transform (`CreateSyntaxProvider` over classes and records with a base list) →
  symbol-free `TypeModel`s → one `Collect()` → grouped into an equatable `AggregateModel` per root (an aggregate's code
  needs its owners, handlers and events from other files). Two outputs: diagnostics from the full model, source from a
  copy **without locations**, so moving text regenerates nothing (`IncrementalityTests`: an unrelated edit and a method
  body edit regenerate 0 outputs; a new event in one aggregate regenerates exactly 1). Justified deviation from "no
  `Collect()`": the grouped models are tiny strings, and caching per aggregate is proven by the tests.
* **Generated code** per aggregate in one file `<Root>.Aggregate.g.cs`: maps `__map{i}`, `TryFind`, private constructors
  (unless the user declares `(Id<T>)` / `(Id<T>, TRoot)`), `Rehydrate`, `EventTypeOf`, `Dispatch` (one case per entity
  type and kind, root events last, `default` throws), `__Apply` switches (most-derived event first), bubbling `__Up{i}`
  (only where an owner has ancestor handlers), `__Unlink{i}`, cascade `__Remove{i}`, `CheckInvariants`, accessors.
  Entity partials get `IOwnedBy<…>`, `__Parent` (typed by the unique owner, `object` for several) and `__Apply`.
* **Bugs found by the tests while building**: an empty `switch` (CS1522) when a type's only handler is invalid;
  `EventTypeOf` arms in alphabetical order hid a derived event behind a non-sealed base (CS8510). Both fixed, both tested.
* **CA1822 on an empty `On` handler** (an event that changes no state): the analyzer is right that such a handler does
  nothing; give the event state to apply, or keep the handler non-empty. Documented in the skill example.
* **Analyzers and generator errors**: the compiler does not run analyzers when the compilation has errors, so TPRM013–015
  appear once the generator errors are fixed (seen in the consumer traps run, §12).

## 11. Suppressions and analyzer configuration

| Where | What | Why |
|---|---|---|
| `AggregateRoot<TSelf>.Event`, `Entity<TRoot, TSelf>.Event` | `[SuppressMessage("Naming", "CA1716")]` | the short base name is the user syntax (decision 1); C# only (same precedent as `Tarol.Results.Error`) |
| generated `__Parent` field | `#pragma warning disable CS0414` around one line | written and read only through `[UnsafeAccessor]`, which the compiler cannot see |
| `tests/…/Fixtures.cs` (`Faulty`) | `#pragma warning disable TPRM014` around two handlers | fixtures that throw/raise inside `On` on purpose, to test the poison guard |

No `NoWarn`, no `.editorconfig` change.

## 12. Verification (SDK 11.0.100, 2026-11-14, from a clean tree in CI order)

| Step | Result |
|---|---|
| `rm -rf artifacts`; `dotnet restore Tarol.slnx`; build every `src/*/*.Generators` | OK |
| `dotnet format Tarol.slnx --verify-no-changes --no-restore` | OK (after one `dotnet format` pass of whitespace and braces) |
| `dotnet build Tarol.slnx -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx -c Release --no-build` | **503 passed**, 0 failed (422 before; Primitives: 48 unit, 33 generator) |
| `eng/aot-smoke.sh` | 6 apps, 0 IL warnings each; `Tarol.Primitives.AotSmoke`: 11 checks, ALL CHECKS PASSED |
| `eng/verify-packages.sh` | `Tarol.Primitives` contains `lib/net11.0`, README, LICENSE, `skills/tarol-primitives/SKILL.md`, `analyzers/dotnet/cs/Tarol.Primitives.Generators.dll`, no `build/`; the consumer runs (3 checks) and its `project.assets.json` lists the skills of Primitives, Kernel, Results, Options, StronglyTypedId; OK |
| diagnostics reach a package consumer | temporary traps files in `Tarol.Primitives.PackageTests`: `error TPRM002` … `TPRM012` (generator), then a second file with `error TPRM013`, `TPRM014` (×2), `TPRM015`, `TKRN001` (Kernel dependency); both removed |

Tests that cover the findings: F-1 a TPRM013 tests; F-1 b API (no `ref` lookups); F-1 c TPRM015 test; F-1 d `ReplayHistory`
internal; F-2 `Delete_Should_Cascade_To_Descendants`; F-4 `A_Child_With_Two_Owner_Types…` (user `(Id, Root)` constructor);
F-5 the `Orders` snapshot (field container `_notes`); F-6 the `Nested` snapshot and TPRM002/003 tests; F-7, F-14, I-4,
I-9 `StrictReplayTests`; F-8 TPRM004 tests; F-9 `FailureTests`; F-10 and equality `EventTests`; F-12 no clock in the
package (and `Events_Are_Pure_Payload…`); F-15 `IncrementalityTests`; F-22 `F22_Handlers_…_Most_Derived_First`; S-3
`ReplaySafetyTests`; V-3 `InvariantTests`.

## 13. Skill evaluation checklist (`tarol-primitives`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| "Add an aggregate `Order` with lines" | `partial class Order : AggregateRoot<Order>`, `EntitySet<Line> Lines { get; } = [];`, `partial class Line : Entity<Order, Line>`, `sealed record Added(…) : Created;`, `Raise(new Line.Added(…))` returning the line | `new Line(...)`, `Lines.Add(...)`, a settable `EntitySet`, `DomainEvent<Order, Line>` |
| "Reject a bid after the end time" | `public Result PlaceBid(…, DateTimeOffset now)` with a guard returning `Error.Conflict`, then `return Raise(…)` | `DateTime.UtcNow` (TKRN001), a throw or rule inside `On` (TPRM014) |
| "Remove a line" | `public Success Remove() => Raise(new Removed());` with `sealed record Removed : Deleted;` | a delete event carrying the id; removing from the set |
| "Keep a total on the order when a line changes" | `private void On(Line.Added e)` on `Order` (ancestor handler) | assigning the total in a behaviour (TPRM013) |
| "An order never has more than 50 lines" | `[Invariant] private Result AtMost50() => …;` and `AggregateChanges.Prepare` | an `[Invariant] bool`, checking it inside `On` |
| "Write a behaviour test" | `AggregateTest.Given(Order.Start()).And(…).When(…)` → `Result`, `Events` | building `StreamEvent`s by hand |
| "Load an order from stored events" | `Order.Rehydrate(id, history)` | calling `Dispatch` (TPRM015) |
| "A note can belong to an order or a line" | `CreatedIn<Order>` / `CreatedIn<Line>` events | plain `Created` (TPRM007) |

## 14. What later steps must do

See [domain-core.md](../architecture/domain-core.md#what-later-steps-must-do) and the plan (rows 6–9, 13). Part B
(stored state) is the next pull request of step 6.

## 15. Open items

* `CreatedIn<P>` raised by an owner of another type than `P` passes the compile-time check (the child implements
  `IOwnedBy` for both) and fails at run time with `OrphanChild`; a narrower constraint needs a second generic overload.
* The analyzer's known false positive (pure `Result`-returning arithmetic inside `On`) and false negative (a write
  through another instance) from §7 remain; the replay test covers the second.

## 16. Part B: stored state (stage 1 evaluation, 2026-11-14)

Decided before this evaluation (round 3, §9): every commit stores the current state with its events; a normal load reads
the state and never replays; queryable JSON; capture every instance field; an old shape is replayed in memory and
rewritten on the next change. This section evaluates **how**. Probe: [`probes/primitives/stored-state/`](probes/primitives/stored-state/)
(output in `output.txt`). The prototype is the real generator on this branch (`src/Primitives`), not a copy; names and
diagnostic ids below are **provisional** until the owner answers (§16.9).

**Found at the start:** `main` was at `a46b359`, not `8ead467`: PR #10 (state lifecycles, docs only) merged in between.
It **reserves TPRM016–TPRM023 for part C** and states that stored state captures a lifecycle enum "as a bare string"
([state-transitions.md](state-transitions.md) §7, §8). The prototype therefore numbers part B's diagnostics TPRM024–027
and writes enums as names; both are questions below.

### 16.1 API shape candidates

| | (A) static members on `IAggregateRoot<TSelf>` (**proposed**) | (B) separate `IStoredState<TSelf>` | (C) codec object |
|---|---|---|---|
| contract | `static abstract int StateSchema`, `WriteState(Utf8JsonWriter, TSelf, JsonSerializerOptions)`, `ReadState(Id<TSelf>, ref Utf8JsonReader, JsonSerializerOptions) : Option<TSelf>` | the same three members, only on stored aggregates | `static abstract AggregateState<TSelf>? State { get; }` with instance `Schema`, `Write`, `Read` |
| `[LoadByReplay]` aggregate | `StateSchema == 0`, `WriteState` throws, `ReadState` skips the object and returns `None` | does not implement it | `State` is `null` |
| generic store, no reflection | one constraint, one branch on `T.StateSchema` | **two** store entry points: a generic method cannot test for the interface and then call its static members without `MakeGenericMethod` | one constraint, `if (T.State is { } s)` |
| new public types | none | one interface | one abstract class |

Store (step 8), with (A):

```csharp
// commit, same transaction as the events
if (T.StateSchema != 0)
{
    buffer.ResetWrittenCount(); writer.Reset(buffer);
    T.WriteState(writer, aggregate, options);           // options: ValueObjectJson.Resolver first, then the user's context
    writer.Flush();
    await UpsertState(aggregate.Id, aggregate.Version, T.StateSchema, buffer.WrittenMemory, ct);
}

// load
if (row is { Schema: var s } && s == T.StateSchema && s != 0)
{
    var reader = new Utf8JsonReader(row.Json);
    if (T.ReadState(id, ref reader, options).TryGetValue(out T? loaded))
        return loaded;                                   // no replay
}
var aggregate = T.Rehydrate(id, await ReadEvents(id, ct));   // old or missing shape: replay in memory,
return aggregate;                                            // the next commit rewrites the row (lazy)
```

Test, with (A) and the proposed helper (`AggregateState.Write/Read` are thin wrappers over the static members):

```csharp
var json = AggregateState.Write(order, options);                       // byte[]
var loaded = AggregateState.Read(order.Id, json, options).GetValueOrThrow();
AggregateState.Write(loaded, options).ShouldBe(json);
AggregateTest.StoredStateMatchesReplay(order, options);                // state == replay of order's events (§16.8)
```

`ReadState` takes the id like `Rehydrate(id, history)`: the store has it, the reader can construct the root before its
members (jsonb reorders keys, §16.2), and a state of another aggregate throws (like a foreign event).

### 16.2 JSON without reflection: writer/reader against `JsonMetadataServices`

**Generated writer/reader (proposed).** Per aggregate the generator emits `StateSchema`, `WriteState`, `ReadState`, one
`__WriteState{i}`/`__ReadState{i}` per entity type, one `ref` field accessor per captured field
(`[UnsafeAccessor(Field, Name = "<Price>k__BackingField")]`, also for `readonly` fields), `JsonEncodedText` names and a
name/number switch per enum type. Abridged output for an entity (probe, `Room`):

```csharp
private static void __WriteState3(Utf8JsonWriter writer, Room entity, JsonSerializerOptions options)
{
    writer.WriteStartObject();
    writer.WriteString(__sn0, entity.Id.Value);                                   // "id"
    writer.WritePropertyName(__sn22);                                             // "capacity": Option<int>
    if (__S3_3(entity).IsSome) writer.WriteNumberValue(__S3_3(entity).GetValueOrThrow()); else writer.WriteNullValue();
    writer.WritePropertyName(__sn21); __WriteEnum0(writer, __S3_2(entity));      // "lighting": "Dim"
    ...
    writer.WriteStartArray(__sn23);                                               // "artworks": [ … ]
    foreach (var child0 in __Member3_Artworks(entity)) __WriteState0(writer, child0, options);
    writer.WriteEndArray();
    writer.WriteEndObject();
}

private Room __ReadState3(ref Utf8JsonReader reader, JsonSerializerOptions options)
{
    var id = (Id<Room>)StateJson.FindId(reader, "Room");                          // a copy of the reader scans for "id"
    if (__map3.ContainsKey(id)) ThrowHelper.DuplicateEntity("Room", id.Value);
    var entity = __New3(id, this);                                                // the existing constructor accessor
    __map3.Add(id, entity);
    while (StateJson.Next(ref reader, "Room"))
    {
        if (reader.ValueTextEquals("title"u8)) { StateJson.Value(ref reader, "Room"); __S3_0(entity) = reader.GetString()!; found++; }
        ...
        else if (reader.ValueTextEquals("artworks"u8))
        {
            StateJson.StartArray(ref reader, "Room");
            while (StateJson.NextItem(ref reader, "Room"))
            {
                var child = __ReadState0(ref reader, options);
                __TarolSet<Artwork>.Add(__Member3_Artworks(entity), child);       // the existing set accessor
                __Parent0(child) = entity;                                        // the existing __Parent accessor
            }
            found++;
        }
        else StateJson.Unknown(ref reader, "Room");
    }
    ...
}
```

Member values:

| Member type | Written | Read | Through the options? |
|---|---|---|---|
| `string`, `bool`, integers, `decimal`, `double`, `float`, `Guid`, `DateTime`, `DateTimeOffset` | `Utf8JsonWriter` directly (the format System.Text.Json writes) | `reader.GetX()` | no |
| `Id<T>` | the Guid string (`IdJsonConverter`'s format) | `(Id<T>)reader.GetGuid()` | no |
| enum | its **member name** (`"Dim"`); an undefined value as its number | name or number; an unknown name throws | no |
| `T?` and `Option<T>` of the above | the value, or `null` | `null` → `null`/`None` | no |
| anything else (`List<T>`, records, value objects, `DateOnly`, `TimeSpan`, …) | `JsonSerializer.Serialize(writer, v, StateJson.Info<T>(options, "Gallery.Tags"))` | `Deserialize(ref reader, info)` | **yes**: the options' resolver chain (value objects need `ValueObjectJson.Resolver` first; the type must be in the user's context). The `JsonTypeInfo` is cached per options instance; a missing one throws `NotSupportedException` naming the member and the `[JsonSerializable]` to add |

The probe's JSON (one room, two artworks, ids shortened):

```json
{"id":"…","version":21,"schema":635019038,"address":{"street":"Millbank","city":"London"},"artworkCount":2,
 "budget":250000.50,"label":"Modern","log":["tag:art","tag:london"],"motto":"Look closer","name":"Tate",
 "openedAt":"2026-11-14T09:30:00+01:00","tags":["art","london"],"visitors":1200,
 "curator":{"id":"…","name":"Frances"},
 "rooms":[{"id":"…","capacity":400,"lighting":"Dim","title":"Turbine Hall","totalValue":1150000,
   "artworks":[{"id":"…","firstRoom":"…","name":"Weather","preferred":"Warm","price":1100000,"reserve":null,"notes":[…]}, …],
   "notes":[{"id":"…","text":"busy on Sundays"}]}]}
```

Order: `id`, `version`, `schema`, then fields by name, then child collections by name. **jsonb does not keep it**
(shorter keys first, then bytewise), so the reader is order-independent: `schema` is found by a scan on a copy of the
reader, the root id is a parameter, an entity's `id` by a scan that usually stops at the first key. Strict reading: a
missing or unknown member throws `JsonException` (with the schema hash equal, either means a corrupt row); a duplicated
entity id throws.

**`JsonMetadataServices` type infos.** Written by hand as the generator would emit them (`MetadataVariant.cs`): it
produces the same JSON (checked), but only for writing. Reading cannot be expressed: an entity's creator needs the
root and the identity maps, and STJ offers no way to hand them to nested object creation except ambient (thread-static)
state; the root needs its id before its children, and jsonb's key order defeats a parameterized constructor that needs
`id` first. It also needed a hand converter for `Lighting?` (the nullable converter resolves the enum from the options
again). Not proposed.

Results **[verified]**: 28/28 checks pass on JIT and as a **Native AOT publish with 0 IL warnings** (`output.txt`):
state of live == state of full replay; `ReadState(WriteState(x))` writes the same JSON; jsonb key order reads back;
the same commands on the live and the loaded instance give the same versions and the same state (cascade, `EntityRef`
delete, create with a given id); the closed room's artworks leave the registry of the loaded tree; another or no
`schema` → `None` and the object is skipped; foreign id, unknown member, missing member, unknown enum name, duplicated
entity → exceptions; the `[Transient]` cache is not stored; the value object's rule does not run on read;
`[LoadByReplay]` → schema 0, `WriteState` throws, `ReadState` is `None`.

### 16.3 Measurements **[verified]** (BenchmarkDotNet 0.16.0-preview.2, ShortRun, in process; error bars are wide)

Probe Gallery with N entities (rooms = max(1, N/10), the rest artworks, each hung and repriced once). "Replay" is
`Rehydrate` from events already in memory; "replay + JSON" also deserializes each event payload, which is what a load
by replay costs a store.

| N | events | state | event JSON | write state | metadata write | **read state** | replay | replay + JSON |
|---|---|---|---|---|---|---|---|---|
| 1 | 2 | 400 B | 67 B | 0.48 µs, **0 B** | 0.91 µs, 432 B | 1.67 µs, 1.6 KB | 0.43 µs, 1.5 KB | 0.97 µs, 1.9 KB |
| 10 | 20 | 1.9 KB | 0.9 KB | 1.70 µs, **0 B** | 3.71 µs, 1.4 KB | 6.56 µs, 6.9 KB | 2.90 µs, 6.4 KB | 11.1 µs, 10.6 KB |
| 100 | 191 | 17 KB | 8.5 KB | 13.1 µs, **0 B** | 26.9 µs, 5.2 KB | 54.1 µs, 62 KB | 29.4 µs, 57 KB | 98.7 µs, 98 KB |

* **Write cost per commit**: about 0.13 µs per entity, allocation-free, half of `JsonMetadataServices`.
* **Read** is 1.8× faster than replay + JSON at 10 and 100 entities, although this model has only ~2 events per
  entity; read cost grows with the state, replay with the history, so the gap widens with every event. A brand-new
  aggregate (1 entity, 2 events) reads slower than it replays; 1.7 µs either way.
* Read allocates what the aggregate itself allocates (+8 % over in-memory replay).
* The state is about 2× the event JSON here (every member is written, `null`s included, and the history is short). In
  step 8 the real gain is one row instead of N rows.

### 16.4 Schema hash **[verified]**

`StateSchema` = FNV-1a (32 bit) over the UTF-8 bytes of a canonical shape string, masked positive, never 0. No
`string.GetHashCode`; the string is built from symbol names only, so the value is the same on every build and machine
(the snapshot files pin it). Example (`StateSchemaTests`):

```
tarol-state/1:{id:id,version:long,schema:int,address:global::Shop.Address{City:string,Street:string}?,name:string,
status:global::Shop.Status{Open,Closed},tags:global::System.Collections.Generic.List<string>,lines:{id:id,quantity:int,via:id}[],shipping:{id:id}?}
```

Covers: JSON names, member shapes (framework types by name; enums with their member names; other types with their
public members, `[JsonPropertyName]` and types, recursively, depth ≤ 6), nesting (`[]` set, `?` ref, recursive owners
as `#Type`), and a format prefix (`tarol-state/1`) so a change of Tarol's own format changes every schema. Not covered,
by design: field and file order (sorted), CLR names of the root and entities, `[Transient]` and computed members,
nullable annotations, `T?` against `Option<T>` (the same JSON). Tests: stable across compilations and both orders of
a partial split over two files; unchanged for 5 non-shape edits; changed for 9 shape edits (member type, renamed
property, pinned name, new field, nested record member, enum member, renamed collection, set ↔ ref, `Option<T>` → `T`).
Known limit: a change inside a type behind a custom `JsonConverter` (the JSON is the converter's) is not seen.

### 16.5 Restoring without events **[verified]**

`ReadState` builds the tree with the pieces `Rehydrate` already uses: the root's constructor (the user's `(Id<T>)` one
if declared), `__New{i}` for entities (the user's `(Id<T>, TRoot)` one if declared), the identity maps,
`EntitySet.AddInternal`, `EntityRef.SetInternal`, `__Parent`; fields are written through `ref` accessors (no setter
runs), and the version through a new internal `AggregateRoot.RestoreVersion(long)` reached by `[UnsafeAccessor]` (it
throws on an instance that already has a version or changes). **No `On` handler, invariant, guard or rule runs**: the
probe's value object counts its `Validate` calls and the count does not move on read (S-3 repeated on state); a loaded
aggregate then continues with the same versions and passes `Prepare`.

Caveat found: the identity maps are filled in tree order on read and in creation order on replay, so
`CheckInvariants` may visit entities in another order; when **two** entities break invariants, `Prepare` can report the
other one first. Both failures discard the instance; the order was never specified.

### 16.6 Diagnostics (ids confirmed in §16.10; tested in `StateDiagnosticTests`, 32 cases)

| Id | Severity | Trigger |
|---|---|---|
| TPRM024 | Error | a captured field holds an entity, a root, a collection or map of them, an `EntitySet`/`EntityRef` inside another type, a delegate (field-like events included), `object`/`dynamic` or a pointer; not reported for `[Transient]`, static, computed members, `Id<T>` of an entity, or a `[LoadByReplay]` aggregate |
| TPRM025 | Error | two stored members get the same JSON name (`_name` and `Name`; a `[StateName]` equal to a child collection's name), or a reserved name (`id` everywhere; `version`, `schema` on the root) |
| TPRM026 | Error | `[StateName]` on a member that is not stored (computed, `[Transient]`, static, any member of a `[LoadByReplay]` aggregate); `[LoadByReplay]` on an entity |
| TPRM027 | Error (analyzer) | an `On` handler assigns a `[Transient]` member anything but `null`/`default` (incl. `++`, `+=`, `??=`): it is then state, lost on a load from the stored state. Resetting a cache in `On` is allowed |

### 16.7 Capture rule as built

Every instance field of the root and of each entity type, declared in that type: plain fields under their name without
leading `_`, auto-property backing fields (also `field`-keyword properties) under the property name, both in camelCase
by System.Text.Json's own algorithm (reimplemented, so names are fixed at compile time and part of the schema).
Excluded: static and `const` fields, generated `__` fields, `[Transient]` (on the field, the property, or `[field: ]`),
`EntitySet`/`EntityRef` members (nested as children instead), implicitly declared fields that are not backing fields.
`readonly` fields are restored through their `ref` accessor (verified on JIT and Native AOT).

### 16.8 Property-based test

`Directory.Packages.props` has no property-based testing library. Proposal: a **hand-written seeded random walk**, no
new package. Per test model a small driver lists its behaviours (each picks a random valid target from the current
aggregate: an existing room, line, note…). For each seed (fixed list, e.g. 200 seeds × 40 steps per model): run a random
behaviour; after **every** step compare `WriteState(live)`, `WriteState(Rehydrate(id, all events))` and
`WriteState(ReadState(WriteState(live)))` as JSON strings; at random steps **replace the live instance with the loaded
one** and continue, so later behaviours (creates, cascades, `EntityRef` deletes, multi-owner children) run on a
restored tree. A failure reports the model, seed, step, the behaviour log and both JSON strings. Models: Gallery,
Orders (EntityRef, `CreatedIn`, a user constructor), the nested shape (Station), Board (value object), and the probe's
rich model moved into the tests (every member kind). The same check ships as
`AggregateTest.StoredStateMatchesReplay(aggregate, options)` (proposed) for users' own aggregates. A library (FsCheck,
CsCheck) would add shrinking; with fixed seeds and a step log the failures are reproducible without it.

### 16.9 Questions for the owner

1. API names and shape (§16.1).
2. The replay-only attribute (`[SnapshotIgnore]` → ?) and `[StateName]`.
3. JSON by generated writer/reader or `JsonMetadataServices` (§16.2, §16.3).
4. `null`, `None` and an empty `EntityRef`: written as `null` or left out.
5. Naming policy: fixed camelCase at compile time, or the options' `PropertyNamingPolicy`.
6. Enums: member names or numbers.
7. Diagnostic ids: TPRM024–027 (keep part C's reservation) or TPRM016–019 (renumber part C).
8. The test helper and the property-based test approach (§16.8).

### 16.10 Owner answers (round 4, 2026-11-14)

| # | Decision | Answer |
|---|---|---|
| 1 | API shape | **(A)**: `StateSchema`, `WriteState`, `ReadState(id, ref reader, options) : Option<TSelf>` as static abstract members of `IAggregateRoot<TSelf>`, plus `AggregateState.Write/Read` helpers |
| 2 | Replay-only attribute | **`[LoadByReplay]`** (replaces the old `[SnapshotIgnore]`) |
| 3 | Name pin | **`[StateName("x")]`** |
| 4 | JSON | **generated `Utf8JsonWriter`/`Utf8JsonReader` code** |
| 5 | `null`, `None`, empty `EntityRef` | **always written**, as `null`; every member is required on read |
| 6 | Naming policy | **fixed camelCase at compile time** (part of the schema) |
| 7 | Enums | **member names** (numbers for undefined values) |
| 8 | Diagnostic ids | **TPRM024–027**; TPRM016–023 stay reserved for part C |
| 9 | Test helper | **`AggregateTest.StoredStateMatchesReplay(aggregate, options)`** |
| 10 | Property-based test | **hand-written seeded random walk**, no new package |

### 16.11 Public API and behaviour changes (part B)

| Before (part A / old Tarol) | Now | Why |
|---|---|---|
| `IAggregateRoot<TSelf>`: `Rehydrate`, `EventTypeOf` | **plus** `static abstract int StateSchema`, `WriteState(Utf8JsonWriter, TSelf, JsonSerializerOptions)`, `ReadState(Id<TSelf>, ref Utf8JsonReader, JsonSerializerOptions) : Option<TSelf>`, generated for every root | answer 1; a generic store needs no reflection |
| old `ToSnapshot()`/`FromSnapshot()` with public `XSnapshot` records, opt-out `[SnapshotIgnore]` on classes and properties | no records; `[LoadByReplay]` on the root; fields are left out with `[Transient]` (part A) | round 3, answer 2 |
| old snapshots dropped fields, get-only collections, `init` and entity-typed state silently (F-3, I-10, F-23) | **every instance field** is captured; what cannot be stored is **TPRM024** | round 3 |
| old public generated snapshot records invisible to the user's STJ context (F-11) | generated writer/reader code; member values through the caller's options | answer 4 |
| old `SchemaVersion` constant checked by `FromSnapshot` (throws) | `StateSchema`: FNV-1a of the JSON shape, nested types included; another schema → `None` (no exception) | round 3 |
| — | `[StateName("x")]`, `AggregateState.Write/Read`, `AggregateTest.StoredStateMatchesReplay`, `StateJson` (generated code only, `[EditorBrowsable(Never)]`) | answers 3, 1, 9 |
| — | internal `AggregateRoot.RestoreVersion(long)` (generated `ReadState` only, via `[UnsafeAccessor]`) | restore without events |
| — | `ThrowHelper.ForeignState`, `NoStoredState`, `RestoreOnUsedAggregate`, `NegativeStateVersion` | messages |
| — | TPRM024–TPRM027 (Error); TPRM016–023 stay reserved for part C | answer 8 |

Behaviour: no existing behaviour changed; the generated files only gain members. A `[LoadByReplay]` aggregate's
`WriteState` throws `InvalidOperationException`; writing a broken aggregate throws.

### 16.12 Implementation notes (found while building)

* `Option<T>` has two `TryGetValue` overloads (the union protocol), so `TryGetValue(out var v)` is ambiguous (CS0121)
  and `out T v` warns for reference types (CS8600): the writer uses `IsSome`/`GetValueOrThrow()`.
* Enum aliases (`A = 1, B = 1`) would give duplicate `case` labels (CS0152): one name per value, the first declared.
* The schema hash depends on types in other files (a stored record); the per-type transform reruns on every
  compilation, so the aggregate regenerates when such a record changes (incrementality test) and stays cached otherwise.
* Tests that read a static counter (`BoardName.Checks`, `Leaky.Applied`) and the property test that moves it share one
  xUnit collection; in parallel the counter check raced.
* Mutation check: with the reader's `__Parent` assignment removed, the property test fails for every model with
  children (the round-trip tests alone did not catch it).
* `found` counts members per object; a duplicated key combined with a missing one would pass the count. jsonb removes
  duplicate keys and the writer never writes them, so a count is enough.

### 16.13 Suppressions and analyzer configuration (part B)

| Where | What | Why |
|---|---|---|
| `tests/…/StoredState/Leaky.cs` | `#pragma warning disable TPRM013` around one method | a fixture that bypasses its event on purpose, to prove `StoredStateMatchesReplay` catches it |

No `NoWarn`, no `.editorconfig` change.

### 16.14 Skill evaluation checklist (part B additions to `tarol-primitives`)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| "Store the order's state with each commit / load it" | `Order.WriteState(writer, order, options)` / `Order.ReadState(id, ref reader, options)` or `AggregateState.Write/Read`; replay with `Rehydrate` on `None` | a hand-written DTO or `JsonSerializer.Serialize(order)`; treating `None` as an error |
| "Rename `Title` to `Name` without breaking stored rows" | `[StateName("title")] public string Name { get; private set; }` | nothing (rows replay, fine) or a migration script; it is optional |
| "Cache a computed summary on the aggregate" | `[Transient] private string? _summary;` filled lazily, reset with `_summary = null;` in `On` | filling it in `On` (TPRM027); storing it |
| "Keep the currently selected line" | `public Id<Line>? Selected { get; private set; }` | `private Line? _selected;` (TPRM024) |
| "This aggregate must always load by replay" | `[LoadByReplay]` on the root | `[SnapshotIgnore]`; `[LoadByReplay]` on an entity (TPRM026) |
| "Prove the state is right in a test" | `AggregateTest.StoredStateMatchesReplay(order, options)` | comparing properties by hand |
| "A state member is a `List<Tag>`" | add `[JsonSerializable(typeof(List<Tag>))]` to the context of the state options | ignoring the `NotSupportedException` naming the member |

### 16.15 What step 8 must do (stored state)

* Commit: in the events' transaction, upsert `(stream id, version, schema, state jsonb)` when `T.StateSchema != 0`;
  the expected-version check covers both.
* Load: read the state row; `T.ReadState` when its `schema` column equals `T.StateSchema`; otherwise, on `None` or with
  no row, `T.Rehydrate` from the events; the next commit rewrites the row. Decide whether a `JsonException` on read
  falls back to replay (corrupt row) or fails loudly.
* `RebuildStates<T>()`: replay every stream whose `schema` differs and rewrite its row.
* State options: `ValueObjectJson.Resolver` first, then the user's context; cache them.
* Optional: load from an older state plus the tail of events (`ReadState` then replaying events after its version) if a
  state row can lag the stream; with one transaction it cannot.

### 16.16 Open items (part B)

* `CheckInvariants` visits entities in map order, which differs between a restored and a replayed aggregate (§16.5).
* A change inside a type that has a custom `JsonConverter` is not seen by the schema hash (§16.4).
* `double`/`float` members are written directly (added after the evaluation, so a context need not list them); NaN and
  infinities throw on write, as System.Text.Json does by default.


### 16.17 Verification (SDK 11.0.100, 2026-11-14, from a clean tree in CI order)

| Step | Result |
|---|---|
| `rm -rf artifacts`; `dotnet restore Tarol.slnx`; build every `src/*/*.Generators` | OK |
| `dotnet format Tarol.slnx --verify-no-changes --no-restore` | OK (after one `dotnet format` pass) |
| `dotnet build Tarol.slnx -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx -c Release --no-build` | **592 passed**, 0 failed (503 before; Primitives: 83 unit, 87 generator) |
| `eng/aot-smoke.sh` | 6 apps, 0 IL warnings each; `Tarol.Primitives.AotSmoke`: 18 checks (7 new: state == replay, round trip, every member kind, continuing on a restored tree, another schema → `None`, the test helper, `[LoadByReplay]`), ALL CHECKS PASSED |
| `eng/verify-packages.sh` | `Tarol.Primitives` contains `lib/net11.0`, README, LICENSE, `skills/tarol-primitives/SKILL.md`, the generator under `analyzers/dotnet/cs/`; the consumer runs 5 checks (2 new: stored state from the package, state == replay) and lists the skills of Primitives, Kernel, Results, Options, StronglyTypedId; OK |
| diagnostics reach a package consumer | temporary traps files in `Tarol.Primitives.PackageTests`: `error TPRM024`, `TPRM025`, `TPRM026` (generator), then a second file with `error TPRM027` (analyzer); both removed |

New tests: `StoredStateTests` (28: a round trip per model (Gallery, Orders, nested Station, Board, Showroom), the agreed
JSON shape, `null`/`None`/empty ref, enums and `[StateName]`, jsonb key order, an old schema → `None` → replay → current
schema on the next write, a mismatch skips the object, no rule and no `On` handler runs on read, continuing on a
restored tree, malformed states, a missing JSON type, a broken aggregate, `[LoadByReplay]`, the helper);
`StoredStatePropertyTests` (6: 5 models × 150 seeds × 40 steps, every prefix, continuing on restored instances; a
reachability guard); `SkillSampleTests` (+1); generator: `StateSchemaTests` (18), `StateDiagnosticTests` (32),
`IncrementalityTests` (+1: a stored record in another file regenerates its aggregate), the three snapshots.
