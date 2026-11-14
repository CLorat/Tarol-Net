---
name: tarol-changes
description: >-
  Use when writing a Tarol change (the input record an actor sends to change an aggregate) or running one: IChange<T>,
  ICreation<T>, ILoads<T>, [Read], ChangeContext (Operation, Now, Read<T>), ChangePipeline.Run, ChangeOptions
  (idempotency key, expected version), ChangeOutcome, IChangeAuthorization, the generated GeneratedChanges.Dispatch, the
  ChangeTest helper, and fixing TCHG001-TCHG005. Do not decompile or web-search, do not hand-write a load/commit
  handler, do not call Raise from a change, do not retry inside Execute.
---
# Tarol.Changes

## Rules

1. **One change = one input record + one behaviour call.** `public sealed partial record X(Id<T> Target-ish, …) :
   IChange<T>, IValidatable { public Result Execute(T aggregate, ChangeContext context) => aggregate.Behaviour(…, context.Now); }`.
   Members are the input (value objects, ids), never the aggregate instance. `Execute` must not load, save or `Raise`.
2. **The pipeline loads the target.** The generator implements `Target` from the record's only `Id<T>` member. With two,
   implement `Id<T> IChange<T>.Target => Second;` (TCHG001). Keep the record `partial` (TCHG002).
3. **Creation** = `ICreation<T>` whose `Execute(context)` returns the static factory's `Result<T>`. The factory makes the
   id (`Id<T>.New(now)`). No load. `ChangeOutcome.StreamId` is the new id.
4. **Special changes**: another key or load-or-create → `ILoads<T>` with `Load(IAggregateReader reader, ChangeContext
   context, CancellationToken ct)`. Another aggregate, read-only, for the decision → a `[Read] Id<X>` member and
   `context.Read<X>()` (missing → 422 `X.NotFound`). A rule that must *change* two aggregates is a saga, not a change.
5. **Time and actor come from the context**: `context.Now` (read once from the pipeline's `TimeProvider`),
   `context.Operation.Actor`. Never `DateTimeOffset.UtcNow` (TKRN001).
6. **Inputs are `IValidatable`**, so value rules run before any read and return every field as `Invalid` (TCHG005 warns).
7. **Run** with an explicit context: `await pipeline.Run(change, OperationContext.Start(actor, tenant, time), options, ct)`.
   `ChangeOptions(Key: new IdempotencyKey(tenant, scope, key))` for redeliverable work. `ExpectedVersion:` for If-Match.
8. **A version conflict is returned** (`StoreErrors.IsVersionConflict`), never retried by the pipeline. The caller retries.

## Example

```csharp
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

public sealed partial record CreateAuction(string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

public sealed partial record CreateAuctionOfArtwork([Read] Id<Artwork> Artwork, string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt)
    : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.CreateFor(context.Read<Artwork>(), context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

// test: the real pipeline on an in-memory store with a clock that stands still
var test = ChangeTest.At(start).Given(auction);
var then = await test.When(new CloseAuction(auction.Id), Actor.User("seller"));
then.Result.IsSuccess.ShouldBeTrue();
then.Events.ShouldBe([new Auction.Closed(start)]);
then.Recorded[0].Context.Actor.ShouldBe(Actor.User("seller"));
var twice = await test.When(new CloseAuction(auction.Id));
twice.Result.TryGetValue(out Violation? conflict).ShouldBeTrue();      // Error.Conflict("AuctionStatus.NotAllowed", …)

// a job
var result = await pipeline.Run(new SettleAuction(id), OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, time), cancellationToken: ct);
```

## Common mistakes

| Mistake | Result |
|---|---|
| `record Close(Auction Auction)` (the instance as input) | not serializable, not reloadable; take `Id<Auction>` |
| two `Id<Auction>` members, no explicit `Target` | TCHG001 |
| `record Close(…) : IChange<…>` not `partial` | TCHG002 |
| `[Read] string Customer`, or `[Read]` on the own target id | TCHG004 |
| value-object members but no `IValidatable` | TCHG005: rules do not run before the load |
| loading or saving inside `Execute` | not possible: `Execute` gets no store; use `ILoads<T>` or `[Read]` |
| `context.Read<Artwork>()` without a `[Read] Id<Artwork>` member | `InvalidOperationException` ("no [Read] members") |
| a factory that returns a loaded aggregate from `ICreation<T>.Execute` | `InvalidOperationException` |
| expecting the pipeline to retry a `Stream.VersionConflict` | it returns it: retry in the caller with a new run |
| an id member in a creation input | the factory chooses ids; send an `IdempotencyKey` to make retries safe |

## AOT

No reflection: `Run<T>` is generic over the aggregate, `GeneratedChanges.Dispatch` is a generated `switch`, `[Read]`
loads are generated. List your aggregates' state member types in your `JsonSerializerContext` for the stored state.
