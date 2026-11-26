---
name: tarol-reactions
description: >-
  Use when a Tarol change should run because an event committed (a reaction, a saga step, a timeout) or on a schedule:
  [OnEvent<T>] factories (Name, DueAt), the generated GeneratedReactions.Set, ReactionSet (Combine, Run), [Scheduled] factories
  (JobOccurrence, GeneratedJobs.Set, JobSet), and fixing TCHG006-TCHG012. Do not write an event handler class, a
  BackgroundService or a polling loop, and do not answer a late or repeated event with a Conflict.
---
# Reactions and scheduled changes (Tarol.Changes)

A reaction or a scheduled run is an ordinary change (skill `tarol-changes`) with a static factory; the store writes an outbox row
in the commit's transaction, a worker (`Tarol.Sagas`, skill `tarol-sagas`; jobs: `Tarol.Jobs`, skill `tarol-jobs`) runs it later.

## Rules

1. **A reaction to an own event is a change with a factory**: `[OnEvent<Bid.Won>] public static X From(Bid.Won e,
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
2. **Nothing to do, or several changes**: the factory may return `X?`; `null` runs nothing and the row is done
   (`ChangeOutcome.Skipped`). In an `ILoads<T>`, `Load` answers `ChangeOutcome.Skip("why")` when the target is gone: done, not
   retried like a `NotFound`. The factory may return `IEnumerable<X>` (a list, an array): one change per element, in order, each
   keyed (reaction, event id, element target; a creation by position): a retried row replays the elements that committed. Two
   elements with one target share a key: the second replays.
3. **A recurring change is a scheduled factory**: `[Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")] public static X
   From(JobOccurrence o) => new(…);` (static, returns the change, one `JobOccurrence`; TCHG009; 5-field Unix cron, TCHG010).
   `GeneratedJobs.Set` goes to `AddJobs` (`Tarol.Jobs`, skill `tarol-jobs`). It runs as `Actor.System("Jobs." + name)` in a
   new conversation, keyed (tenant, `job:<name>`, occurrence). `o.At` is UTC whole seconds; use `o.Local` for the zone's day.
   `Name =` pins the stored name (TCHG011 for duplicates), `Missed =` and `PerTenant =` as in `tarol-jobs`.
4. **Effects are not reactions**: a call to the outside (mail, push, HTTP) is an `IEffect<TEvent>` of `Tarol.Messaging` (skill
   `tarol-messaging`), also with a due time; a reaction only changes an aggregate.
5. **Tests**: `ChangeTest.At(now, new ChangeTestOptions { Reactions = GeneratedReactions.Set, RunReactions = true })` runs the
   reactions a change causes (`outcome.Reactions`); `GeneratedReactions.Set.Run(pipeline, name, recorded, ct)` runs one by hand.

## Example

```csharp
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
| `[OnEvent<T>]` on an instance, private or wrongly typed method | TCHG006 |
| `Load` answering `Error.NotFound` for a target that left | retried, then a dead letter; answer `ChangeOutcome.Skip(…)` |
| renaming a reaction's change without pinning `Name` | its waiting rows and keys use the old name: dead letters |
| two changes with one reaction name, two factories of one change for one event | TCHG007 |
| `DueAt = nameof(Settlement.DeadlineSet.DueAt)` in a change with a member `Settlement` | CS1061 (binds to the member): qualify the type |
| `Error.Conflict` for an event that arrives late or twice | retried, then a dead letter; return `Result.Success()` |
| `[Scheduled]` factory with no or another parameter than `JobOccurrence` | TCHG009 |
| `[Scheduled("0 0 2 * * *")]` (seconds), `L`, `W`, `#`, `?` | TCHG010 |
| `DateTime.Now` or `o.At.Day` for "today" in a zone | `o.Local` (`At` is UTC) |

## AOT

`GeneratedReactions` and `GeneratedJobs` are generated `switch`es over static arrays; no reflection.
