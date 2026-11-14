---
name: tarol-primitives
description: >-
  Use when writing event-sourced aggregates with Tarol.Primitives: AggregateRoot<T>, child entities (Entity<TRoot,T>,
  EntitySet, EntityRef), events (Event, Created, CreatedIn<TParent>, Deleted, [EventType]), On handlers, behaviour
  methods that return Result and take now, [Invariant], Rehydrate/StreamEvent, AggregateChanges.Prepare/Committed, the
  AggregateTest given/when/then helper, and fixing TPRM001-TPRM015. Do not decompile or web-search, do not hand-write a
  dispatch switch, do not use the old DomainEvent<TRoot,TEntity>/CreateDomainEvent/DomainClock/ToSnapshot API.
---
# Tarol.Primitives

## Rules

1. **Partial classes.** Root: `public partial class Order : AggregateRoot<Order>`. Entity:
   `public partial class Line : Entity<Order, Line>`. The generator writes the rest (TPRM002 if not partial).
2. **Events are records nested in the type they target**, deriving from its unqualified base:
   `public sealed record Renamed(string Name) : Event;` (root or entity), `: Created` (creates this entity),
   `: Deleted` (removes it and everything it owns), `: CreatedIn<Order>` (a child with several owner types). Top level:
   `: Line.Event`. Payload only: business time is a field (`At`), never an id or version.
3. **Behaviour methods decide, `On` handlers apply.** A behaviour checks guards and returns `Result`/`Result<T>`;
   `Raise` returns `Success`, so `return Raise(new X(…));` works. Pass time in: `Close(DateTimeOffset now)`.
   An `On(TEvent e)` handler only assigns the event's data: no `Result`-returning call, no `throw`, no clock, no `Raise`
   (TPRM014). State is assigned **only** in `On` handlers (or private helpers called only from them, or constructors);
   anything else is TPRM013. Caches: `[Transient]`.
4. **Children.** The owner declares `public EntitySet<Line> Lines { get; } = [];` or
   `public EntityRef<Shipping> Shipping { get; } = new();` (get-only; TPRM008). The owner creates:
   `public Line Add(string sku) => Raise(new Line.Added(sku));` (returns the child; a non-owner is CS0311).
   The entity raises its own events and deletes itself: `public Success Remove() => Raise(new Removed());`.
   Ancestors may handle a child's event (`private void On(Line.Added e)` on `Order`): target first, then owners, root last.
5. **Invariants**: `[Invariant] private Result NotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Line.Negative");`
   on the root or an entity (TPRM009 for another shape). They run in `AggregateChanges.Prepare(aggregate)`, never on replay.
6. **Load and commit**: `Order.Rehydrate(id, history)` (gapless `StreamEvent`s from version 1), then the behaviour, then
   `AggregateChanges.Prepare` → store `UncommittedChanges` → `AggregateChanges.Committed`. Any failure or exception:
   discard the instance; after a throwing `On` it is `IsBroken` and refuses work.
7. **New aggregate**: `var o = new Order(Id<Order>.New()); o.Raise(new Opened(...));` inside a static factory (the
   generated constructor is private).

## Example

```csharp
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

public partial class Order : AggregateRoot<Order>
{
    public EntitySet<Line> Lines { get; } = [];
    public bool IsOpen { get; private set; }
    public bool Submitted { get; private set; }

    public static Order Start() { var o = new Order(Id<Order>.New()); o.Raise(new Started()); return o; }
    public Result<Line> Add(string sku, int quantity)
        => Submitted ? Error.Conflict("Order.Submitted") : Raise(new Line.Added(sku, quantity));
    public Result Submit(DateTimeOffset now) => Lines.Count == 0 ? Error.Conflict("Order.Empty") : Raise(new SubmittedAt(now));

    public sealed record Started : Event;
    public sealed record SubmittedAt(DateTimeOffset At) : Event;
    private void On(Started e) => IsOpen = true;
    private void On(SubmittedAt e) => Submitted = true;
}

public partial class Line : Entity<Order, Line>
{
    public string Sku { get; private set; } = "";
    public int Quantity { get; private set; }
    public Success Remove() => Raise(new Removed());

    public sealed record Added(string Sku, int Quantity) : Created;
    public sealed record Removed : Deleted;
    private void On(Added e) => (Sku, Quantity) = (e.Sku, e.Quantity);
    [Invariant] private Result Positive() => Quantity > 0 ? Result.Success() : Error.Conflict("Line.Quantity");
}

// test
var outcome = AggregateTest.Given(Order.Start()).When(o => o.Submit(DateTimeOffset.UnixEpoch));
// outcome.Result == Result.Failure(Error.Conflict("Order.Empty")); outcome.Events is empty
```

## Common mistakes

| Mistake | Result |
|---|---|
| `public void Rename(string n) => Name = n;` | TPRM013: raise an event, assign in `On` |
| `private void On(Placed e) { if (e.Amount <= 0) throw …; }` or `Money.Create(…)` inside `On` | TPRM014: guards belong in the behaviour; replay must accept history |
| event without `On` on its target | TPRM004 (creation/deletion events are exempt) |
| `On(Line.Added)` on a type that does not own `Line` | TPRM005 |
| entity with no `EntitySet`/`EntityRef` owner | TPRM006; two owner types with `: Created` → TPRM007, use `CreatedIn<T>` |
| `EntitySet<T> Lines { get; set; }` | TPRM008 |
| `[Invariant] bool …` | TPRM009 |
| two events with one `[EventType]` alias | TPRM010 |
| calling `Dispatch`, `CheckInvariants`, `__map0` | TPRM015 |
| `DateTime.UtcNow` in the domain | TKRN001 (Tarol.Kernel): take `now` as a parameter |
| replaying out of order / another stream / unknown entity | `InvalidOperationException` from `Rehydrate` |

## AOT

No reflection: dispatch is a generated `switch`, private constructors and container mutators are reached through
`[UnsafeAccessor]`. Events are plain records; list them in your own `JsonSerializerContext` (with value objects, use
`ValueObjectJson.Options(context)` from Tarol.Validations).
