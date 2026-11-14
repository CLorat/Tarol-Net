# Step 6: Primitives (domain core)

Migration note for `Tarol.Primitives` (aggregate root, child entities, events, routing, rehydration, invariants,
snapshots, and their generator). Concept note: [domain-core.md](../architecture/domain-core.md) (first principles,
new insights, scope decisions R-1 … R-4). Old code: `CLorat/Tarol@6a62f81` (after PR #24).

Labels as in the [conventions](../architecture/conventions.md): **[verified]** = reproduced here, probe and output under
[`probes/primitives/`](probes/primitives/) (`*.csproj.txt` files are renamed so the repository build ignores them;
build from a scratch copy with `-p:TarolNetRepo=<clone>`); **[verified by reading]**; **[unverified]**.

**Status: stage 2 (evaluation).** The decisions in §9 go to the owner. Nothing in `src/` has changed yet.

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
| `AggregateRoot<TSelf>` (+ nested `Event`) | `Tarol.Primitives` | `Id`, `Version`, `UncommittedChanges : ReadOnlySpan<StreamEvent>`, `protected Success Raise(Event)`, `protected TChild Raise<TChild>(Entity<TSelf,TChild>.Created)` (+ explicit-id overload), `implicit operator Id<TSelf>` |
| `Entity<TRoot, TSelf>` (+ nested `Event`, `Created`, `Deleted`) | `Tarol.Primitives` | `Id`, `protected TRoot Root`, `Raise(Event)`, `Raise(Deleted)`, `Raise<TChild>(Created)` |
| `DomainEvent` | `Tarol.Primitives` | empty abstract record |
| `StreamEvent` | `Tarol.Primitives` | `readonly record struct (long Version, Guid EntityId, Guid ParentId, DomainEvent Event)` |
| `RecordedEvent` (R-3) | `Tarol.Primitives` | `StreamEvent` + `Id<IEvent>` event id, `DateTimeOffset RecordedAt`, `Actor`, `Option<Id<ITenant>>`, `Id<ICorrelation>`, `Option<Id<ICausation>>`; filled by the store (step 8) |
| `IAggregateRoot<TSelf>` | `Tarol.Primitives` | `static abstract TSelf Rehydrate(Id<TSelf>, ReadOnlySpan<StreamEvent>)` |
| `EntitySet<T>`, `EntityRef<T>`, `IOwnedBy<T>`, `IRegistry<T>` | `Tarol.Primitives` | no `ref` lookups |
| `AggregateChanges` | `Tarol.Primitives` | `Result Prepare(aggregate)` (invariants, poison check), `void Committed(aggregate)` |
| `InvariantAttribute`, `EventTypeAttribute`, `SnapshotAttribute`/`SnapshotIgnoreAttribute` (PR B) | `Tarol.Primitives` | |
| `ThrowHelper` | `Tarol.Primitives` | `[EditorBrowsable(Never)]` |

Dependencies: Kernel (R-3; also brings TKRN001 into the user's domain code), Results, Options, StronglyTypedId. Not
Validations: invariants and guards return `Result`; value objects are Validations' business. **`Primitives.Value` is
dropped** (validations.md §7.6). Diagnostics renumbered to `TPRM001…` (conventions §3). Package: one
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
