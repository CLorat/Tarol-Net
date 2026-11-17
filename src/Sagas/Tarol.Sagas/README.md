# Tarol.Sagas

Reactions to own events and sagas for [Tarol](https://github.com/CLorat/Tarol-Net): one outbox worker for every reaction
a commit wrote, retries with backoff, dead letters a human can re-drive, a hosted service with key purging, tracing links
and logs, and `ReactionTest` for the whole chain in memory. A saga is an aggregate whose steps are reactions: no saga
runtime, no saga store. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.Sagas          # brings Tarol.Changes (and its generator) and Tarol.Persistence
```

## 30 seconds

```csharp
// a reaction: a change with an [OnEvent<T>] factory (Tarol.Changes)
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);
    public Result<Payout> Execute(ChangeContext context) => Payout.MarkDue(Auction, Amount, context.Now);
}

// the store writes outbox rows for the generated set; the host runs the worker
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Reactions = GeneratedReactions.Set });
builder.Services.AddSingleton<IOutbox>(store.Outbox);
builder.Services.AddSingleton(new ChangePipeline(store, TimeProvider.System));
builder.Services.AddTarolReactions(GeneratedReactions.Set, o => o.PurgeKeys = store.PurgeKeys);
```

Or with the one registration (step 10), which wires the store's outbox, the pipeline and the key purge (with a job host,
`AddJobs` of `Tarol.Jobs`, the purge runs once per cluster as the system job `tarol.purge-keys` instead of in every worker):

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>())
    .WithReactions(GeneratedReactions.Set)
    .UseNpgsqlStore(builder.Configuration.GetConnectionString("tarol")!)
    .AddReactionWorker();
```

## The worker

`ReactionWorker` claims a batch (`IOutbox.Claim`), runs each row's reaction (`ReactionSet.Run`: as
`Actor.System("Reactions.<name>")` on behalf of the user, caused by the event, keyed (reaction, event id)), and then:

| Pipeline answer | Verdict |
|---|---|
| success, or a replayed key | Done: `Complete` |
| any `Error.Conflict`; `NotFound`; 422 (a missing `[Read]`); `Unavailable`, `Timeout`, `TooManyRequests`, `Internal`; an exception | Retry after 1 s, 2 s, 4 s … 5 min (±20 %), the row stays the head of its stream |
| `Invalid`, `Forbidden`, `Unauthorized`, `Gone`; a retry at attempt 10 | Bury: a dead letter; the next row of the stream runs |

`ReactionOptions`: `Workers` (4), `Batch` (32), `Lease` (1 min), `PollInterval` (100 ms), `Policy` (`DeliveryPolicy`:
`MaxAttempts` 10, `FirstDelay` 1 s, `MaxDelay` 5 min), `PurgeKeys`, `KeyRetention` (7 days), `PurgeEvery` (1 h).
`RunOnce`/`Drain` return what happened to each row (`ReactionAttempt`). Dead letters: `IOutbox.DeadLetters`, `Redrive`.

## Sagas

A saga is an aggregate: id = the id of what it is about, started load-or-create (`ILoads<T>`), steps are reactions,
timeouts are scheduled reactions (`[OnEvent<T>(DueAt = nameof(…))]`), compensations are events another reaction turns
into forward changes. Late, repeated or stale messages return `Result.Success()`. See docs/architecture/sagas.md and the
skill `tarol-sagas`.

## Testing

```csharp
var test = ReactionTest.At(start, GeneratedReactions.Set, AppJson.Default.Options).Given(auction);
await test.When(new SettleAuction(auction.Id));
var attempts = await test.Drain();                        // every due row and the rows they cause
await test.Later(TimeSpan.FromDays(3)).Drain();           // timeouts, retries
var dead = await test.DeadLetters();
```

## Operations

* Tracing: `ActivitySource` `"Tarol.Sagas"` (`ReactionWorker.ActivitySourceName`); one Consumer activity per row, linked
  to the request that committed the event; tags `tarol.reaction`, `tarol.stream.type`, `tarol.stream.id`,
  `messaging.message.id`, `tarol.attempt`, `tarol.verdict`.
* Logs (source-generated): 9001 retry, 9002 dead letter, 9003 lease lost, 9004 loop failure, 9010/9011 key purge.
* Hosting: `ReactionHost` is a `BackgroundService`; no dependency on Aspire or ASP.NET Core. The hosting, DI and logging
  abstractions are part of .NET 11's base framework.

Metrics (step 15): meter `"Tarol.Sagas"`: `tarol.reactions.runs` (`{row}`, outcome completed | retried | buried),
`tarol.reactions.duration`, `tarol.reactions.lateness` (start − due) in seconds, from the host's `IMeterFactory`.

## AOT

No reflection: the reaction set is generated, the worker calls the pipeline through it. The smoke app publishes with 0 IL
warnings and runs the hosted worker against PostgreSQL natively.
