# Audit (Tarol.Audit, Tarol.Audit.Npgsql; Tatalu TN-64)

A new family, not migrated from `CLorat/Tarol`: Tarol had no change log. Design: [audit.md](../architecture/audit.md); owner
decisions AU-1 … AU-5 (2026-11-26, every recommendation taken). Labels as in the plan.

## Decisions as built

| # | Decision | As built |
|---|---|---|
| AU-1 | option B, two packages | `Tarol.Audit` (host, log, endpoint, observer, in-memory store), `Tarol.Audit.Npgsql` (store) |
| AU-2 | every member audited by default, `[NotAudited]` to leave one out | the Primitives generator emits `EventCatalogEntry.Audit` only for events that differ from "every member"; no second generator |
| AU-3 | `RecordedAt` and an optional `[AuditTime]` member | `AuditEntry.At` and `CapturedAt` (`[AuditTime]`, else `Tarol-Captured-At`, TN-60) |
| AU-4 | own retention | `Tarol:Audit:RetentionDays` / `AuditRegistry.RetentionDays`, maintenance task `tarol.audit.purge` |
| AU-5 | authenticated denials only | anonymous refusals counted (`tarol.audit.anonymous_denials`), not written |

Not built from the design: `[Audited("name")]` (mapping several members onto one field: the JSON name already spans events
that name a member alike), `[AuditSubject]` (the row's target is the entity or the stream), `GET {prefix}/objects/{id}` (the
`target` filter answers it), OpenAPI metadata for `MapAudit`. The reaction-side `[OnAnyEvent<T>]` (decision R-3) is not needed by
the audit, which reads the feed (design §5); it is not built.

## Public API

* **Tarol.Primitives:** `NotAuditedAttribute`, `AuditRemovedAttribute`, `AuditTimeAttribute`, `AuditMember`, `EventAudit`,
  `EventCatalogEntry.Audit`. Generator: an entity's `Deleted` event is `Removed` without a mark.
* **Tarol.Persistence:** `EventCodec.TryDescribe(streamType, event, out entry)`, `Encode(streamType, event, payload)`,
  `SubjectsOf(streamType, type, payload, stream, subjects)`, `SealStored(streamType, type, payload, stream, keys, sealed)`.
* **Tarol.Audit:** `AuditKind`, `AuditEntry`, `AuditQuery`, `AuditPage`, `AuditValueKey`, `AuditBatch`, `IAuditStore`,
  `InMemoryAuditStore`, `AuditRegistry`, `AuditHost`, `IAuditLog`, `AuditLogs.Over`, `AuditTarolBuilderExtensions.AddAudit`/
  `UseInMemoryAudit`/`PurgeJob`, `AuditEndpoints.MapAudit`.
* **Tarol.Audit.Npgsql:** `NpgsqlAuditStore` (`Script(version)`), `UseNpgsqlAudit(schema, dataSource)`.

## Behaviour worth knowing

* Values are compared in plain text: a sealed value is sealed with a fresh nonce each time, so the store's seal is opened with
  the subject's key before "same value: no row".
* String values are written without escaping non-ASCII letters (`"Bären"`, not `"Bären"`): a person reads the log.
* Reads and denials carry the aggregate's CLR name in `StreamType` (`Child`), feed rows the stream type (`child`).
* Row ids are UUIDv8 derived from the event id and the field (`Id<T>.Derive`): a replayed batch or a rebuild writes the same rows.

## Tests and verification

* `Tarol.Audit.Tests` (13): `ChangeLogTests` (7: before/after, device time, entities and removal, sealing and erasure, same name
  no row, filters/cursor/tenants, rebuild), `WiringTests` (4), `EndpointTests` (2). `Tarol.Primitives.Tests.AuditCatalogTests` (1).
* `Tarol.Audit.Npgsql.IntegrationTests` (4, Docker): sealed values and erasure, entities/paging/filters/tenants, rebuild and the
  checkpoint guard, reads and denials with retention and tenant erasure.
* `Tarol.Audit.AotSmoke` and `Tarol.Audit.Npgsql.AotSmoke`: 0 IL warnings; the Npgsql app's database checks ran against
  PostgreSQL 17 with `TAROL_PG` **[verified]**.
* `Tarol.Audit.PackageTests`: consumer of the packed packages.
* Suppressions: none.

Full verification 2026-11-26 **[verified]**: `dotnet format --verify-no-changes` clean; `dotnet build Tarol.slnx` 0 warnings;
`dotnet test --solution Tarol.slnx` with Docker 2325 tests: 2 generator snapshots (`Orders`, `Gallery`) differed by the intended
`Audit = new EventAudit(false, true, [])` on entity `Deleted` events and were accepted, then 181 of 181; `dotnet pack -c Release` +
`eng/verify-packages.sh` exit 0 (`skills/tarol-audit`, `skills/tarol-audit-npgsql` in the packages and the consumer's assets);
`eng/aot-smoke.sh` exit 0, 30 apps, 0 IL warnings.

## Skill evaluation checklist

| Task | Expected skill and API | Mistakes to avoid |
|---|---|---|
| a change log of the children's data | `tarol-audit`: `AddAudit(a => a.Add<Child>())`, `UseNpgsqlAudit()`, `MapAudit` | one reaction per event, an audit aggregate |
| a checksum member that is no fact | `[property: NotAudited(Reason = …)]` | filtering rows after the fact |
| the time of an offline capture | `[property: AuditTime] DateTimeOffset At` or `Tarol-Captured-At` | a string member |
| erase a child but keep "who changed what when" | `EraseSubject`; values read `null`, rows stay | deleting rows |
| log sensitive reads | `[Endpoint(…, Sensitive = true)]` with `AddAudit` | a path-matching middleware |
