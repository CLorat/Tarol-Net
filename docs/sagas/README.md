# Sagas and reactions

User guide for `Tarol.Sagas` (with the reaction parts of `Tarol.Changes` and the outbox of `Tarol.Persistence`). Concept:
[architecture/sagas.md](../architecture/sagas.md); decisions: [migration/sagas.md](../migration/sagas.md) §11.

## Design in six points

1. **A reaction is a change with a different context.** An `[OnEvent<T>]` factory on a change maps a committed event to
   the change's input; the change runs through the same pipeline as the system on behalf of the user who caused the
   event, caused by the event, keyed by (reaction, event id), so a redelivery commits once.
2. **The outbox is part of the commit.** A commit writes one row per (event, reaction) in its own transaction; a reaction
   runs after its event is durable, at least once, effect once.
3. **One worker for every reaction.** It claims the heads of the outbox (one row per stream and reaction at a time, in
   version order), runs them, and completes, retries (any Conflict, a NotFound, an outage: 1 s … 5 min) or buries them
   after 10 attempts. Dead letters wait for a human; they never block their stream.
4. **A saga is an aggregate.** Its state, concurrency, replay and lifecycle are the aggregate's; its steps are reactions;
   its timeouts are scheduled reactions (`DueAt`); its compensations are forward changes of other aggregates.
5. **Late, repeated or stale messages are successes.** A saga that already ended, a payment that arrives twice, a
   deadline that moved: the behaviour returns `Result.Success()` and changes nothing. A Conflict would be retried and
   end as a dead letter.
6. **Tests run the real chain.** `ReactionTest` drains every due row and the rows they cause on the in-memory store,
   with a clock that stands still until the test moves it.

## Packages

| Package | Step 9 parts |
|---|---|
| `Tarol.Changes` | `[OnEvent<T>]`, `ReactionSet`, generated `GeneratedReactions.Set` (TCHG006–TCHG008) |
| `Tarol.Persistence` | `IOutbox`, `IOutboxReactions`, `OutboxRow`, `DeadLetter`; the in-memory outbox |
| `Tarol.Persistence.Npgsql` | head-flag claims, scheduled rows, `outbox_dead`, `traceparent`, `Notify` |
| `Tarol.Sagas` | `ReactionWorker`, `DeliveryPolicy`, `ReactionOptions`, `ReactionHost`, `AddTarolReactions`, `ReactionTest` |

## Running it

```csharp
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Reactions = GeneratedReactions.Set });
builder.Services.AddSingleton<IOutbox>(store.Outbox);
builder.Services.AddSingleton(new ChangePipeline(store, TimeProvider.System));
builder.Services.AddTarolReactions(GeneratedReactions.Set, o => o.PurgeKeys = store.PurgeKeys);
```

Add the `ActivitySource` `"Tarol.Sagas"` to your tracing. Leave Npgsql's own source out of the default (or sample it):
every idle claim would be a span. A runnable prototype with an Aspire AppHost is in
[`samples/Sagas`](../../samples/Sagas/README.md).

## Skills

`tarol-sagas` (worker, sagas, `ReactionTest`) and `tarol-reactions` (in `Tarol.Changes`: `[OnEvent<T>]`, `[Scheduled]`) ship in the packages.
