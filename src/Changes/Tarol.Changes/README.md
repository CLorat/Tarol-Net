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
| idempotency key seen? (`ChangeOptions.Key`) | the first outcome, `Replayed = true`; nothing runs; another fingerprint → `Idempotency.KeyReused` (422) |
| `[Read]` aggregates | `Error.UnprocessableEntity("X.NotFound")` (422) |
| load the target: by `Target`, or the change's own `ILoads<T>.Load` | `Error.NotFound("T.NotFound")` (404, also for another tenant) |
| `IChangeAuthorization`, **only when `ChangeOptions.Principal` is set** (HTTP); jobs and reactions pass none | `Error.Forbidden` / `Unauthorized`; a principal without an authorization → `Auth.NotConfigured` (403) |
| expected version from the caller (`ChangeOptions.ExpectedVersion`, HTTP `If-Match`), after the permission | `Stream.VersionConflict` (409) |
| `Execute` with `context.Now` read once from the `TimeProvider` | the guard's error (409, or 400 for an input field) |
| `AggregateChanges.Prepare`: invariants, lifecycle steps | the rule's error |
| append with the expected version and the key, then `Committed` | `Stream.VersionConflict`, **returned, never retried**: the caller decides |

Register everything at once with `services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())` and a store
(`.UseInMemoryStore()`, `.UseNpgsqlStore(…)`): the codec, `TimeProvider.System` (unless the app registered one) and the
pipeline over the store and the `IChangeAuthorization` (`Tarol.Security`). `Delivery.Classify` is the table every
at-least-once entry shares (`Verdict.Done` / `Retry` / `Bury`).

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

## Reactions to own events

```csharp
public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation<Payout>
{
    [OnEvent<Bid.Won>]                                   // Name = "…" pins the stored name; DueAt = nameof(…) schedules it
    public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);
    public Result<Payout> Execute(ChangeContext context) => Payout.Due(Id<Payout>.New(context.Now), Amount);
}
```

The store writes one outbox row per (event, reaction) in the event's commit; the worker (`Tarol.Sagas`) runs
`GeneratedReactions.Set.Run(pipeline, row.Reaction, row.Event)`. A reaction is effect-once by its key; answer late or
repeated events with `Result.Success()` (a Conflict is retried, then dead-lettered). See docs/architecture/sagas.md.

## Scheduled changes

```csharp
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]   // Name =, Missed = Missed.All, PerTenant = true
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}
```

The job host (`Tarol.Jobs`, `AddJobs(j => j.Add(GeneratedJobs.Set))`) runs every occurrence once across the cluster as
`Actor.System("Jobs.<name>")`, keyed (tenant, `job:<name>`, occurrence UTC). The schedule is 5-field Unix cron; an invalid
literal is TCHG010. See docs/architecture/jobs.md.

## Generated

* `IChange<T>.Target` from the record's only `Id<T>` member (TCHG001 when there is none or several; implement it yourself
  then, or make the change an `ILoads<T>`).
* `IHasReads.LoadReads` for `[Read]` members.
* Per assembly: `internal static class GeneratedChanges` (namespace = assembly name) with
  `pipeline.Dispatch(object change, context, …)`, a typed switch for outbox rows and tests, without reflection.
* Per assembly with `[OnEvent<T>]` factories: `public static class GeneratedReactions` with `Set` (a `ReactionSet`): the
  store's reaction table (static arrays per event type), the schedule of `DueAt` reactions, and the runner
  (name, event) → factory → `pipeline.Run` as `Actor.System("Reactions.<name>")` on behalf of the user, caused by the
  event, with the key (reaction, event id). `ReactionSet.Combine(a, b)` joins assemblies.
* Per assembly with `[Scheduled]` factories: `public static class GeneratedJobs` with `Set` (a `JobSet`): the definitions
  (name, cron, zone, missed-run policy, per tenant) and the runner (job name) → factory → `pipeline.Run` as
  `Actor.System("Jobs.<name>")` with the occurrence key. `JobSet.Combine(a, b)` joins assemblies.

| Diagnostic | When |
|---|---|
| TCHG001 | no single `Id<T>` target member |
| TCHG002 | the change or a containing type is not `partial` |
| TCHG003 | generic, file-local, or both `IChange<T>` and `ICreation<T>` |
| TCHG004 | `[Read]` on something that is not an `Id<X>` of an aggregate, or on the own target |
| TCHG005 (warning) | members with validation rules, but the change is not `IValidatable` |
| TCHG006 | an `[OnEvent<T>]` factory that is not static, private, generic, not on a change, not returning it, or not `(T)` / `(T, RecordedEvent)` |
| TCHG007 | a reaction name used twice, or one change with two factories for one event |
| TCHG008 | `DueAt` names no `DateTimeOffset` property of the event |
| TCHG009 | a `[Scheduled]` factory that is not static, private, generic, not on a change, not returning it, or not `(JobOccurrence)`; an empty `Name` or `TimeZone` |
| TCHG010 | an invalid cron literal (5 fields; no seconds, `L W # ?`) |
| TCHG011 | a job name used by two changes |

## Testing

```csharp
var test = ChangeTest.At(start, MyJson.Default.Options).Given(auction);      // real pipeline, in-memory store, fixed clock
var then = await test.Later(TimeSpan.FromDays(8)).When(new SettleAuction(auction.Id), Actor.System("Jobs.AuctionSettlement"));
// then.Result, then.Events (what this change committed), then.Recorded (with the operation context), test.Load(id)
```

## AOT

No reflection: the pipeline is generic over the aggregate (static abstracts), the dispatcher is a generated `switch`, and
reads are generated loads. The smoke app publishes with 0 IL warnings.
