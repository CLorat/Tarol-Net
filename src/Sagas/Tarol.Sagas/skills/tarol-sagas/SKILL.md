---
name: tarol-sagas
description: >-
  Use when a Tarol app must react to its own committed events or run a long process across aggregates (a saga, a
  process manager, a timeout, a compensation): the outbox worker (ReactionWorker, ReactionHost, AddTarolReactions,
  ReactionOptions, DeliveryPolicy, Verdict), dead letters (IOutbox.DeadLetters/Redrive), sagas as aggregates with
  [OnEvent<T>] reactions, scheduled reactions (DueAt), and ReactionTest. Do not write a Saga base class, a message
  handler class, a timer or a polling loop of your own; do not decompile or web-search.
---
# Tarol.Sagas

## Rules

1. **A reaction is a change with an `[OnEvent<T>]` factory** (skill `tarol-changes`). The store writes one outbox row
   per (event, reaction) in the event's commit; the worker runs it through the pipeline as the system on behalf of the
   user, caused by the event, keyed (reaction, event id): a redelivery commits nothing twice.
2. **Run the worker**: register the store's outbox and the pipeline, then `services.AddTarolReactions(GeneratedReactions.Set)`
   (several assemblies: `ReactionSet.Combine(a.Set, b.Set)`, one call). Pass the same set to the store:
   `new NpgsqlStoreOptions { Reactions = GeneratedReactions.Set }`. Purge keys: `o.PurgeKeys = store.PurgeKeys;`.
   With the one registration: `services.AddTarol(…).WithReactions(GeneratedReactions.Set).UseNpgsqlStore(cs).AddReactionWorker()`
   (the store's key purge is used; with `AddJobs` it is the system job `tarol.purge-keys`, once per cluster). `Verdict` and `Delivery.Classify` live in `Tarol.Changes`.
3. **Failures** (C-9): any `Error.Conflict`, a `NotFound`, a missing `[Read]` (422), an unavailable dependency or an
   exception → retry after 1 s, 2 s, 4 s … 5 min; after 10 attempts → dead letter. `Invalid`, `Forbidden`,
   `Unauthorized`, `Gone` → dead letter at once. A retried row holds back the later rows of its stream for that
   reaction; a dead letter does not. A human reads `outbox.DeadLetters(max, ct)` and calls `outbox.Redrive(id, ct)`.
4. **A saga is an aggregate.** Its id is the id of what it is about (`(Id<Settlement>)auctionId`), its steps are
   changes with `[OnEvent<T>]` factories on events of any aggregate, what it asks of others is an event that another
   reaction turns into a change, its timeout is a scheduled reaction, and a compensation is a forward change.
5. **Start load-or-create** (`ILoads<T>`: load by the correlation id, else a new instance) with one behaviour that
   starts, restarts an ended saga in place, or returns `Result.Success()` for a duplicate start.
6. **Late, repeated or stale messages return `Result.Success()`**, never a Conflict (it would retry, then dead-letter).
   A timeout checks its own deadline: `now < Deadline` → Success (the deadline moved; the new row fires later).
7. **Timeouts**: raise an event with the due time, react with `[OnEvent<T>(DueAt = nameof(Ns.T.Member))]`. Scheduled
   reactions run by due time only and never block other rows. Time comes from `context.Now`, never `DateTimeOffset.UtcNow`.
8. **Test with `ReactionTest`**: `Given` (history, no reactions), `When`, `Drain()` (runs due rows and what they cause),
   `Later(span)` (timeouts and retries become due), `DeadLetters()`.
9. **The entity of an event**: a factory `From(TEvent e, RecordedEvent r)` reads `r.Event.EntityId` (and `ParentId`) for an
   entity's event; `r.StreamId` is the root's id. A reaction runs shortly after the commit: a change whose precondition
   is a reaction's result can still see the old state for a moment; send it after the reaction (tests: `Drain()`).
10. **A limit over all aggregates of a type** (places, capacity) is a **reservation**: one counter aggregate per scope
   (`Occupancy`, id = the tenant's) whose `Reserve` refuses when full; the change that needs a place reserves first (a
   reaction or the saga's first step), then confirms or releases. Two concurrent reservations race on the counter's
   version (the pipeline loads and decides again, TN-45), so the limit holds; a projection read in a decision does not.
11. **Rules with versioned parameters** (legal ages, deadlines): keep them in an aggregate (`RuleProfile`, one per tenant or
   global), read it with `[Read]`, and store the parameter value used **on the event** (`DueOn`), so a later profile does
   not change an old decision.

## Example

```csharp
public sealed partial record StartSettlement(Guid Auction, string Buyer, decimal Amount) : ILoads<Settlement>
{
    [OnEvent<Bid.Won>]
    public static StartSettlement From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Bidder, e.Amount);

    public async ValueTask<Result<Settlement>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Settlement>)Auction, context.Operation, cancellationToken)).TryGetValue(out Settlement? existing)
            ? existing : Tests.Settlement.For(Auction);

    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Begin(Buyer, Amount, context.Now);
}

public sealed partial record ExpireSettlement(Id<Settlement> Settlement) : IChange<Settlement>
{
    [OnEvent<Settlement.DeadlineSet>(DueAt = nameof(Tests.Settlement.DeadlineSet.DueAt))]
    public static ExpireSettlement From(Settlement.DeadlineSet e, RecordedEvent recorded) => new((Id<Settlement>)recorded.StreamId);

    public Result Execute(Settlement settlement, ChangeContext context) => settlement.Expire(context.Now);
}

public sealed partial record ReopenAuction(Id<Auction> Auction) : IChange<Auction>
{
    [OnEvent<Settlement.Expired>]
    public static ReopenAuction From(Settlement.Expired e, RecordedEvent recorded) => new((Id<Auction>)recorded.StreamId);

    public Result Execute(Auction auction, ChangeContext context) => auction.Reopen();
}

// in Settlement : AggregateRoot<Settlement>
public Result Begin(string buyer, decimal amount, DateTimeOffset now)
{
    if (Version > 0 && Status == SettlementStatus.AwaitingPayment)
        return Result.Success();
    Raise(new Started(buyer, amount));
    return Raise(new DeadlineSet(now.AddDays(3)));
}

// test
var test = ReactionTest.At(start, GeneratedReactions.Set, AppJson.Default.Options).Given(auction);
await test.When(new SettleAuction(auction.Id));
await test.Drain();                                           // MarkPayoutDue, StartSettlement
var expired = await test.Later(TimeSpan.FromDays(3)).Drain(); // ExpireSettlement, ReopenAuction

// host
builder.Services.AddSingleton<IOutbox>(store.Outbox);
builder.Services.AddSingleton(new ChangePipeline(store, TimeProvider.System));
builder.Services.AddTarolReactions(GeneratedReactions.Set, o => o.PurgeKeys = store.PurgeKeys);
```

## Common mistakes

| Mistake | Result |
|---|---|
| a `Saga<T>` base class, a handler class with injected services | not Tarol: a saga is an aggregate, a reaction a change |
| `ICreation<T>` to start a saga that can start again | a second start is `Settlement.AlreadyExists`: retried, then a dead letter |
| `Error.Conflict("Settlement.Completed")` for a late payment | retried for 12 minutes, then a dead letter |
| a timer or `Task.Delay` for a timeout | lost on restart; use a scheduled reaction (`DueAt`) |
| `DueAt = nameof(Settlement.DeadlineSet.DueAt)` in a change with a member `Settlement` | CS1061; qualify the type |
| renaming a reaction's change without pinning `Name` | its waiting rows dead-letter with "No reaction named …" |
| `AddTarolReactions` twice | `InvalidOperationException`: combine the sets |
| a different set for the store and the worker | rows of reactions the worker does not know: dead letters |
| `DELETE FROM outbox` to unblock a stream | lost effects; let it dead-letter, then `Redrive` |

## AOT and operations

No reflection: the set is generated, the worker calls the pipeline through the generated switch. Hosting, DI and
logging come from .NET 11's base framework (no package reference). Traces: add the `ActivitySource` `"Tarol.Sagas"`;
each reaction is a Consumer activity linked to the request that committed its event, and the change it runs is its
child (`change <Name>`, source `"Tarol.Changes"`). Metrics (step 15): meter `"Tarol.Sagas"` (`ReactionWorker.MeterName`):
`tarol.reactions.runs` (`{row}`, tags `tarol.reaction`, `tarol.outcome` = completed | retried | buried),
`tarol.reactions.duration` and `tarol.reactions.lateness` (start − due) in seconds; never the tenant. Export everything
with `AddMeter("Tarol.*")` / `AddSource("Tarol.*")`. Alert on the gauge `tarol.reactions.oldest_due` (`Tarol.Operations`),
not on `IOutbox.Pending` (a full count). Dead letters: `IOutbox.DeadLetters(new DeadLetterQuery { After, Limit, Tenant,
Name })` pages by id; `Redrive(id)`, `Discard(id)`. Logs: event ids 9001 (retry), 9002 (dead letter), 9003 (lease
lost), 9004 (loop failure), 9010/9011 (key purge).
