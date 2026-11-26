# Tarol.Audit

The change log of a Tarol application: who changed which field of which object when, from what to what. It is fed from the
event feed, so nothing can be forgotten, and personal values stay sealed, so erasing a subject erases them here too.

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Child>().Add<Guardian>())
    .UseInMemoryStore()
    .AddAudit(a => a.Add<Child>().Add<Guardian>())      // every event of these aggregates
    .UseInMemoryAudit();                                 // or UseNpgsqlAudit() from Tarol.Audit.Npgsql
app.MapAudit("/protokoll", policy: "audit:read");       // GET ?target=&actor=&streamType=&field=&kind=&from=&to=&after=&limit=
```

* **Complete by construction.** One row per event and changed field: every member of every event of the added aggregates is a
  field (its JSON name). `[NotAudited(Reason = …)]` leaves a member or an event out; `[AuditRemoved]` (or an entity's `Deleted`
  event) writes one `Removed` row; `[AuditTime]` names the member with the time on the device (else `Tarol-Captured-At`).
* **Before and after.** The before of a field is its last value; the same value again writes no row. An entity event's target is
  the entity, the stream is its aggregate.
* **Erasure-proof.** A `[PersonalData]` value is stored sealed under its subject's key and reads as `null` once the subject is
  erased; the rows stay. Tenant erasure deletes the tenant's rows.
* **Reads and denials** from `Tarol.Security` (`Sensitive = true` entries and every refusal) are rows of the same log; refusals
  of anonymous callers are only counted (`tarol.audit.anonymous_denials`).
* **Rebuild.** Row ids are derived from the event and the field: raising `a.Version` rebuilds the same rows from the feed.
* **Retention** of its own: `Tarol:Audit:RetentionDays` (or `a.RetentionDays`), run by the maintenance task `tarol.audit.purge`.

`IAuditLog.Query(query, context, ct)` reads a page for code (the tenant is the caller's). Native-AOT ready: no reflection.

Licence: AGPL-3.0-only, or the owner's commercial licence.
