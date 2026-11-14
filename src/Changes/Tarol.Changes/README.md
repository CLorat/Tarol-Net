# Tarol.Changes

A **change** is the entry of an actor into the domain to change something. You write it once: an input record that names
its target and calls **one behaviour method**. Every caller (a job, a saga, a test, and later REST, reactions, webhooks
and consumers) runs it through **one pipeline**. The pipeline validates, loads the target, checks the permission, passes
the time, runs the invariants, commits with the expected version and an idempotency key, and discards the instance on
any failure. Generated: the target, the read-only loads and a typed dispatcher. Native-AOT ready. Depends on
`Tarol.Persistence` and `Tarol.Validations`.

## Install

```bash
dotnet add package Tarol.Changes
```

## 30 seconds

```csharp
using Tarol.Changes;

// a change: the input names the target (its only Id<Auction>), Execute calls one behaviour
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

// a creation: the factory makes the aggregate (and its id)
public sealed partial record CreateAuction(string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

// run it (a job, a saga step, a test): the context is built at the edge and passed in
var pipeline = new ChangePipeline(store, timeProvider);
Result<ChangeOutcome> result = await pipeline.Run(new CloseAuction(id),
    OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, timeProvider), cancellationToken: ct);
```

## The pipeline

| Step | Failure |
|---|---|
| `Validated()` of an `IValidatable` input, **before any read** | `Invalid`, every field (400) |
| idempotency key seen? (`ChangeOptions.Key`) | the first outcome, `Replayed = true`; nothing runs |
| `[Read]` aggregates | `Error.UnprocessableEntity("X.NotFound")` (422) |
| load the target: by `Target`, or the change's own `ILoads<T>.Load` | `Error.NotFound("T.NotFound")` (404, also for another tenant) |
| expected version from the caller (`ChangeOptions.ExpectedVersion`, HTTP `If-Match`) | `Stream.VersionConflict` (409) |
| `IChangeAuthorization` (when given; built in step 10) | `Error.Forbidden` / `Unauthorized` |
| `Execute` with `context.Now` read once from the `TimeProvider` | the guard's error (409, or 400 for an input field) |
| `AggregateChanges.Prepare`: invariants, lifecycle steps | the rule's error |
| append with the expected version and the key, then `Committed` | `Stream.VersionConflict`, **returned, never retried**: the caller decides |

A failure discards the loaded instance. An exception propagates and discards it too. A behaviour that raises nothing
appends nothing (`Events = 0`).

## Special changes

```csharp
// another aggregate, read-only: loaded before Execute, never written
public sealed partial record CreateAuctionOfArtwork([Read] Id<Artwork> Artwork, string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt)
    : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.CreateFor(context.Read<Artwork>(), context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

// the target is found another way (or loaded-or-created): ILoads<T>
public sealed partial record CloseByLot(string Lot) : ILoads<Auction>
{
    public async ValueTask<Result<Auction>> Load(IAggregateReader reader, ChangeContext context, CancellationToken ct)
        => (await reader.Find<Auction>("lot", Lot, context.Operation, ct)).TryGetValue(out Id<Auction> id)
           && (await reader.Load(id, context.Operation, ct)).TryGetValue(out Auction? auction)
            ? auction : Error.NotFound("Auction.NotFound");
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}
```

## Generated

* `IChange<T>.Target` from the record's only `Id<T>` member (TCHG001 when there is none or several; implement it yourself
  then, or make the change an `ILoads<T>`).
* `IHasReads.LoadReads` for `[Read]` members.
* Per assembly: `internal static class GeneratedChanges` (namespace = assembly name) with
  `pipeline.Dispatch(object change, context, …)`, a typed switch for outbox rows and tests, without reflection.

| Diagnostic | When |
|---|---|
| TCHG001 | no single `Id<T>` target member |
| TCHG002 | the change or a containing type is not `partial` |
| TCHG003 | generic, file-local, or both `IChange<T>` and `ICreation<T>` |
| TCHG004 | `[Read]` on something that is not an `Id<X>` of an aggregate, or on the own target |
| TCHG005 (warning) | members with validation rules, but the change is not `IValidatable` |

## Testing

```csharp
var test = ChangeTest.At(start, MyJson.Default.Options).Given(auction);      // real pipeline, in-memory store, fixed clock
var then = await test.Later(TimeSpan.FromDays(8)).When(new SettleAuction(auction.Id), Actor.System("Jobs.AuctionSettlement"));
// then.Result, then.Events (what this change committed), then.Recorded (with the operation context), test.Load(id)
```

## AOT

No reflection: the pipeline is generic over the aggregate (static abstracts), the dispatcher is a generated `switch`, and
reads are generated loads. The smoke app publishes with 0 IL warnings.
