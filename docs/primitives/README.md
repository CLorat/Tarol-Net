# Primitives

Package guide: [Tarol.Primitives](../../src/Primitives/Tarol.Primitives/README.md).
Agent skill: [tarol-primitives](../../src/Primitives/Tarol.Primitives/skills/tarol-primitives/SKILL.md).
Concept: [docs/architecture/domain-core.md](../architecture/domain-core.md). Review, measurements and migration notes:
[docs/migration/primitives.md](../migration/primitives.md).

## Design in five points

1. **Decide, then apply.** A behaviour method checks its guards against the state and the `now` it is given, returns a
   `Result`, and raises events. An `On` handler applies one event and does nothing else: no rule, no throw, no clock, no
   raise. The same handlers run live and on replay, so history always loads. Analyzers enforce both halves (TPRM013,
   TPRM014).
2. **Events are facts, nothing else.** A `sealed record` nested in the type it targets (`: Event`, `: Created`,
   `: Deleted`) with value equality and no envelope. Version and routing travel beside it in a `StreamEvent`; who, when
   and for which tenant belong to the commit (`RecordedEvent`, filled by the store).
3. **Child entities read like plain objects, the library does the rest.** `EntitySet<T>` / `EntityRef<T>` on the owner,
   `Raise(new Line.Added(…))` returns the child, an entity deletes itself and takes its children with it; the generated
   dispatch routes at any depth in O(1), runs handlers target first and the root last, and replays strictly. Only the
   owner can raise a child's creation (a compile error otherwise).
4. **Invariants at the hand-off, failure means discard.** `[Invariant]` rules (returning `Result`) run when the events are
   handed to the store, on the root and every entity, never on replay. A failed behaviour, a broken invariant or an
   exception discards the instance; after a throwing `On` handler it refuses further work.
5. **Generated, AOT-ready, testable without a host.** No reflection; private constructors and container mutators are
   reached through `[UnsafeAccessor]`; `AggregateTest.Given(…).When(…)` gives behaviour tests in a few lines. Stored
   state (the current state written with every commit, queryable) follows in part B.
