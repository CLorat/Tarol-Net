# Persistence

Package guides: [Tarol.Persistence](../../src/Persistence/Tarol.Persistence/README.md),
[Tarol.Persistence.Npgsql](../../src/Persistence/Tarol.Persistence.Npgsql/README.md).
Agent skills: [tarol-persistence](../../src/Persistence/Tarol.Persistence/skills/tarol-persistence/SKILL.md),
[tarol-persistence-npgsql](../../src/Persistence/Tarol.Persistence.Npgsql/skills/tarol-persistence-npgsql/SKILL.md).
Concept: [docs/architecture/changes.md](../architecture/changes.md) §2, §7; the PostgreSQL store (step 8):
[docs/architecture/persistence.md](../architecture/persistence.md). Design decisions and migration notes:
[docs/migration/changes.md](../migration/changes.md) (C-6, C-8, C-16) and [docs/migration/persistence.md](../migration/persistence.md).

## Design in five points

1. **One port, two implementations.** `IAggregateStore` is what the change pipeline (and your own code) loads and
   commits through. `InMemoryAggregateStore` ships for tests and samples; `Tarol.Persistence.Npgsql`
   implements the same contract against PostgreSQL, and one contract test suite runs against both.
2. **The tenant comes from the operation.** Every call takes the `OperationContext`; a stream is (tenant, type, id), so
   another tenant's aggregate simply does not exist: a 404, never a 403.
3. **Conflicts are results.** An append checks the expected version and returns `Stream.VersionConflict` instead of
   throwing; the caller decides whether to retry (the change pipeline never does, C-7).
4. **Idempotency in the same commit.** A key passed to `Append` is stored atomically with the events, and only for a
   success; `Seen(key)` returns the first outcome so a redelivered webhook, reaction or request commits once (C-8).
5. **The test store behaves like production.** It records every event with its context and commit time, writes and
   reads the stored state, keeps unique lookups and serializes concurrent appends, so a test catches what the database
   would refuse.
