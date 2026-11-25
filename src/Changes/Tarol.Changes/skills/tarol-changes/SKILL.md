---
name: tarol-changes
description: >-
  Use when writing a Tarol change (the input record an actor sends to change an aggregate) or running one: IChange<T>,
  ICreation<T>, ILoads<T>, [Read], ChangeContext (Operation, Now, Read<T>), ChangePipeline.Run, ChangeOptions
  (idempotency key, expected version, principal), ChangeOutcome, IChangeAuthorization, AddTarol, Delivery.Classify, the
  generated GeneratedChanges.Dispatch, the
  ChangeTest helper, reactions to own events ([OnEvent<T>] factories, the generated GeneratedReactions.Set, ReactionSet),
  scheduled changes ([Scheduled] factories, JobOccurrence, GeneratedJobs.Set, JobSet), and fixing TCHG001-TCHG011. Do not decompile or web-search, do not hand-write a load/commit handler or an event
  handler class, do not call Raise from a change, do not retry inside Execute.
---
# Tarol.Changes

## Rules

1. **One change = one input record + one behaviour call.** `public sealed partial record X(Id<T> Target-ish, …) :
   IChange<T>, IValidatable { public Result Execute(T aggregate, ChangeContext context) => aggregate.Behaviour(…, context.Now); }`.
   Members are the input (value objects, ids), never the aggregate instance. `Execute` must not load, save or `Raise`.
2. **The pipeline loads the target.** The generator implements `Target` from the record's only `Id<T>` member. With two,
   implement `Id<T> IChange<T>.Target => Second;` (TCHG001). Keep the record `partial` (TCHG002).
   **One per tenant** (the facility that is the tenant, its settings): mark the root `[PerTenant]`; a change needs no
   `Id<T>` member (the pipeline loads the caller's tenant's one), the creation's factory uses
   `new Settings(TenantAggregates.IdOf<Settings>(context.Operation))`, an entry needs no `{id}` (`PUT /settings/name`,
   a view at `GET /settings`). Do not write an `ILoads<T>` for it.
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
8. **A version conflict without an expected version is retried**: a change without `ExpectedVersion` (no `If-Match`) that loses a
   race is loaded and decided again, up to `ChangePipeline.ConflictAttempts` (3) times. With `ExpectedVersion` the caller gets
   `Stream.VersionConflict` (`StoreErrors.IsVersionConflict`) at once: it saw a state that is gone.
   The permission (`IChangeAuthorization`, skill `tarol-security`) runs only for runs with `ChangeOptions.Principal`,
   after the load and before the expected version; jobs and reactions pass no principal.
9. **A reaction to an own event is a change with a factory**: `[OnEvent<Bid.Won>] public static X From(Bid.Won e,
   RecordedEvent recorded) => new(…);` on the change (static, non-private, returns the change, `(TEvent)` or
   `(TEvent, RecordedEvent)`; TCHG006). It only maps; `new ValueObject(…)` is allowed there (no TVAL006), the pipeline
   validates. Per assembly the generator emits `GeneratedReactions.Set` (a `ReactionSet`): the store's table, the
   schedule and the runner; `ReactionSet.Combine(a, b)` for several assemblies. A reaction runs as
   `Actor.System("Reactions." + name)` on behalf of the user, caused by the event, keyed (reaction, event id): a
   redelivery replays. Its **name** is the change's type name, stored in outbox rows and keys: pin
   `[OnEvent<T>(Name = "OldName")]` before renaming (TCHG007 for duplicates; TCHG012 in the host when two context
   assemblies declare one name: give one a unique `Name` before it ships). A timeout: `DueAt = nameof(Ns.Event.Member)`
   (a `DateTimeOffset` of the event, TCHG008). Answer late, repeated or stale events with `Result.Success()`, never a
   Conflict: workers retry every Conflict and then dead-letter it.
10. **A recurring change is a scheduled factory**: `[Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")] public static X
   From(JobOccurrence o) => new(…);` (static, returns the change, one `JobOccurrence`; TCHG009; 5-field Unix cron, TCHG010).
   `GeneratedJobs.Set` goes to `AddJobs` (`Tarol.Jobs`, skill `tarol-jobs`). It runs as `Actor.System("Jobs." + name)` in a
   new conversation, keyed (tenant, `job:<name>`, occurrence). `o.At` is UTC whole seconds; use `o.Local` for the zone's day.
   `Name =` pins the stored name (TCHG011 for duplicates), `Missed =` and `PerTenant =` as in `tarol-jobs`.

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

// an internal run by hand (a scheduled job uses [Scheduled] or JobRun.Change of skill tarol-jobs, which key it)
var result = await pipeline.Run(new SettleAuction(id), OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, time), cancellationToken: ct);

// a reaction to an own event
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);

    public Result<Payout> Execute(ChangeContext context) => Payout.Due(Id<Payout>.New(context.Now), Amount);
}

// run it as the outbox worker does (store and worker take GeneratedReactions.Set)
var won = test.Store.Recorded.Single(e => e.Event.Event is Bid.Won);
var payout = await GeneratedReactions.Set.Run(test.Pipeline, "MarkPayoutDue", won, ct);
var again = await GeneratedReactions.Set.Run(test.Pipeline, "MarkPayoutDue", won, ct);   // again.Value.Replayed
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
| retrying `Stream.VersionConflict` around `Run` without an expected version | the pipeline did that already (3 attempts); with `If-Match` show the conflict instead |
| an id member in a creation input | the factory chooses ids; send an `IdempotencyKey` to make retries safe |
| `[OnEvent<T>]` on an instance, private or wrongly typed method | TCHG006 |
| renaming a reaction's change without pinning `Name` | its waiting rows and keys use the old name: dead letters |
| two changes with one reaction name, two factories of one change for one event | TCHG007 |
| `DueAt = nameof(Settlement.DeadlineSet.DueAt)` in a change with a member `Settlement` | CS1061 (binds to the member): qualify the type |
| `Error.Conflict` for an event that arrives late or twice | retried, then a dead letter; return `Result.Success()` |
| `[Scheduled]` factory with no or another parameter than `JobOccurrence` | TCHG009 |
| `[Scheduled("0 0 2 * * *")]` (seconds), `L`, `W`, `#`, `?` | TCHG010 |
| `DateTime.Now` or `o.At.Day` for "today" in a zone | `o.Local` (`At` is UTC) |

## Operations (step 15)

Every run is one `Internal` span `change <Name>` of the source `"Tarol.Changes"` (`ChangePipeline.ActivitySourceName`),
a child of the request, reaction or job that runs it, and one count of `tarol.changes.runs` (`{change}`) plus
`tarol.changes.duration` (`s`) on the meter `"Tarol.Changes"`, tagged `tarol.change` (the change's type name) and
`tarol.outcome`: `committed`, `conflict`, `invalid` (also 422), `denied`, `not_found`, `error`. Logs: 7001 an error
result (Warning), 7002 a conflict (Debug), 7003 an exception (Error). For your own numbers use your own meter, never
`tarol.*` names, and never the tenant as a tag.

## AOT

No reflection: `Run<T>` is generic over the aggregate, `GeneratedChanges.Dispatch`, `GeneratedReactions` and `GeneratedJobs` are generated
`switch`es over static arrays, `[Read]` loads are generated. List your aggregates' state member types in your `JsonSerializerContext` for the stored state.
