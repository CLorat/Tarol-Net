# Strongly-typed ids

Package guides: [Tarol.StronglyTypedId](../../src/StronglyTypedId/Tarol.StronglyTypedId/README.md),
[Tarol.StronglyTypedId.EntityFrameworkCore](../../src/StronglyTypedId/Tarol.StronglyTypedId.EntityFrameworkCore/README.md).
Agent skills: [tarol-strongly-typed-id](../../src/StronglyTypedId/Tarol.StronglyTypedId/skills/tarol-strongly-typed-id/SKILL.md),
[tarol-strongly-typed-id-efcore](../../src/StronglyTypedId/Tarol.StronglyTypedId.EntityFrameworkCore/skills/tarol-strongly-typed-id-efcore/SKILL.md).
Design review and migration notes: [docs/migration/stronglytypedid.md](../migration/stronglytypedid.md).

## Design in five points

1. **Phantom type.** `Id<TEntity>` wraps one `Guid`; `TEntity` exists only for the compiler. Mixing up ids of
   different entities is a compile error, at zero run-time cost.
2. **UUIDv7.** `Id<T>.New()` uses `Guid.CreateVersion7()`: ids are unique without coordination and sort by
   creation time, which keeps B-tree indexes compact.
3. **Explicit at the boundary.** No implicit conversions from or to `Guid`; `From`, `Parse` and casts are the
   only ways in, so a raw `Guid` never becomes an id by accident.
4. **Zero configuration, no reflection.** JSON closes an open generic converter (works with source-generated
   contexts and Native AOT); minimal APIs bind through `ISpanParsable`; `TypeDescriptor` registrations are
   generated per assembly by the bundled source generator.
5. **Infrastructure in satellites.** EF Core support is a separate package so the core stays dependency-free and
   AOT-compatible.
