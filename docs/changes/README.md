# Changes

Package guide: [Tarol.Changes](../../src/Changes/Tarol.Changes/README.md).
Agent skill: [tarol-changes](../../src/Changes/Tarol.Changes/skills/tarol-changes/SKILL.md).
Concept: [docs/architecture/changes.md](../architecture/changes.md). Design, measurements, decisions C-1 … C-16 and the
implementation notes: [docs/migration/changes.md](../migration/changes.md). The store it runs on:
[docs/persistence](../persistence/README.md).

## Design in six points

1. **A change is the entry of an actor into the domain.** You write the input once, as a record that names its target
   and calls one behaviour method. Who calls it (a job, a saga, a test; later REST, reactions, webhooks, consumers) only
   changes how the input and the context are built and how the outcome is reported.
2. **One pipeline, in library code.** Validate before any read, load, check the permission, decide with the time read
   once, run the invariants, append with the expected version and the idempotency key. It is generic over the aggregate,
   needs no reflection and costs about 145 ns and 56 B over hand-written code.
3. **The pipeline loads; special changes say how.** The default loads the target named by the record's `Id<T>`.
   `ILoads<T>` covers another key or load-or-create, and `[Read]` brings other aggregates read-only. A change that must
   write two aggregates is a saga.
4. **Failure discards, conflicts are answers.** A failed guard, invariant or lifecycle step, or an exception, drops the
   loaded instance. A version conflict comes back as 409; the pipeline never retries, the caller decides.
5. **Generated where a list of your types is needed:** the target, the read-only loads and a typed `Dispatch(object)`.
   Never the pipeline itself, never input records (other generators could not see them).
6. **Tests run the real thing.** `ChangeTest` runs your change through the pipeline on the in-memory store, with a
   clock that stands still.
7. **A reaction to an own event is a change with a factory.** `[OnEvent<Bid.Won>] public static MarkPayoutDue From(Bid.Won e,
   RecordedEvent recorded)` maps the committed event to the input; the generated `GeneratedReactions.Set` gives the store
   its outbox table and the worker its runner, which runs the change as the system on behalf of the user, caused by the
   event, keyed (reaction, event id). Sagas are aggregates whose steps are such reactions
   ([sagas.md](../architecture/sagas.md)).
