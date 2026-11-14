# Tarol.Primitives

Event-sourced aggregates with child entities, and short user code. Behaviour methods check their guards and raise
pure-payload events; `On` handlers apply them, live and on replay, through one generated dispatch. Child entities route,
cascade and cannot be changed without an event; invariants run when the events are handed to the store; analyzers keep
decisions out of `On` handlers. The current state is written as queryable JSON with every commit and read back without
replay. Native-AOT ready, no reflection.

## Install

```bash
dotnet add package Tarol.Primitives
```

It brings `Tarol.Kernel`, `Tarol.Results`, `Tarol.Options` and `Tarol.StronglyTypedId` (and their analyzers).

## 30 seconds

```csharp
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

public partial class Gallery : AggregateRoot<Gallery>
{
    public EntitySet<Room> Rooms { get; } = [];
    public string Name { get; private set; } = "";

    public static Gallery Open(string name) { var g = new Gallery(Id<Gallery>.New()); g.Raise(new Opened(name)); return g; }
    public Room AddRoom(string title) => Raise(new Room.Added(title));          // creates the child, returns it

    public sealed record Opened(string Name) : Event;
    private void On(Opened e) => Name = e.Name;
}

public partial class Room : Entity<Gallery, Room>
{
    public string Title { get; private set; } = "";
    public bool IsOpen { get; private set; } = true;

    public Result Close(DateTimeOffset now) => !IsOpen ? Error.Conflict("Room.Closed") : Raise(new Closed(now));   // guard, then raise
    public sealed record Added(string Title) : Created;
    public sealed record Closed(DateTimeOffset At) : Event;

    private void On(Added e) => Title = e.Title;
    private void On(Closed e) => IsOpen = false;
}

// load, decide, hand off
var gallery = Gallery.Rehydrate(id, history);                 // replay: no guard or invariant runs
var result = gallery.Rooms[0].Close(now);
if (result.IsSuccess && AggregateChanges.Prepare(gallery) is { IsSuccess: true })   // invariants
    store.Append(gallery.Id, gallery.UncommittedChanges);      // StreamEvent: version, entity id, parent id, payload

// stored state: written with every commit, read without replay
byte[] state = AggregateState.Write(gallery, options);     // {"id":…,"version":3,"schema":…,"name":"Tate","rooms":[{"id":…,"title":"Turner",…}]}
var loaded = AggregateState.Read(gallery.Id, state, options);   // Option<Gallery>: None when the shape changed → replay
```

## What you get

* **Events** derive from the base of the type they target, written unqualified inside it: `Event`, `Created`,
  `CreatedIn<TParent>` (a child with two owner types), `Deleted`. They are plain records: value equality, no envelope,
  any JSON context (also in the same assembly) can serialize them. `[EventType("alias", version)]` pins the stored name.
* **Generated** in your partial classes: `Rehydrate(id, history)`, `EventTypeOf(event)`, the dispatch (target, then
  owners bottom-up, then the root), the identity maps (`IRegistry<T>.TryFind`), cascade on delete, the invariant check.
* **Strict replay**: a gap, a foreign event, an unknown or duplicate entity or an unknown event type throws.
* **Failure means discard**: a failed behaviour, a broken invariant or an exception; after an `On` handler threw the
  instance is broken and refuses further work.
* **Stored state**: every instance field of the root and its entities, as camelCase JSON with children nested by id
  and `id`/`version`/`schema` on the root (`Gallery.WriteState`/`ReadState`, or `AggregateState.Write`/`Read`).
  Generated writer/reader code, no reflection, nothing runs on read (no handler, invariant or rule). `StateSchema` is a
  stable hash of the shape: an old row reads as `None`, the store replays and rewrites it on the next commit.
  `[StateName("x")]` pins a name, `[Transient]` leaves a cache out, `[LoadByReplay]` turns the state off.
* **Tests** without a host: `AggregateTest.Given(Gallery.Open("Tate")).And(…).When(g => g.Tag("x"))` → `Result`, `Events`;
  `AggregateTest.StoredStateMatchesReplay(gallery, options)` proves state == replay for your own aggregates.
* **Diagnostics** `TPRM001`–`TPRM015` and `TPRM024`–`TPRM027` (all errors): missing handlers, unowned or ambiguous
  children, assignable containers, invariant shape, duplicate aliases, state assigned outside `On`,
  rules/throws/clocks/raises inside `On`, generated plumbing used directly, fields the state cannot hold, state names
  used twice, state attributes without effect, `[Transient]` members filled in `On`.

## Links

* Skill for coding agents: `skills/tarol-primitives/SKILL.md` (in the package).
* Concept and decisions: `docs/architecture/domain-core.md`, `docs/migration/primitives.md` in the repository.

Licence: proprietary, see `LICENSE`.
