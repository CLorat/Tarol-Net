---
name: tarol-audit
description: >-
  Use when a Tarol application needs a change log ("who changed what when, from what to what"), an access log of sensitive
  reads and denials, or erasure-proof audit rows. Covers AddAudit, AuditRegistry, [NotAudited], [AuditRemoved], [AuditTime],
  IAuditLog, AuditQuery, AuditEntry, MapAudit, UseInMemoryAudit, retention and rebuilds. Do not write one reaction per event,
  an audit aggregate, or a path-matching access-log middleware.
---
# Tarol change log (Tarol.Audit)

## Rules
1. `AddAudit(a => a.Add<Child>())` audits **every** event of each added aggregate and **every** member of it: nothing is
   forgotten when an event is added. Pick a store: `UseInMemoryAudit()` or `UseNpgsqlAudit()` (Tarol.Audit.Npgsql).
2. Leave a member or a whole event out with `[property: NotAudited(Reason = "…")]` / `[NotAudited(Reason = "…")]` on the event.
3. `[AuditRemoved]` on an event (or an entity's `Deleted` event) writes one `Removed` row and forgets the target's values.
4. `[property: AuditTime] DateTimeOffset At` is the time on the device (`AuditEntry.CapturedAt`); without it the operation's
   `Tarol-Captured-At` is used. The member is not a field.
5. A field is the member's JSON name; `Before`/`After` are JSON values (`"\"Mia\""`, `3`, `null`). The same value again writes no row.
   An entity event's `Target` is the entity, `Stream` its aggregate.
6. `[PersonalData]` values are stored sealed; after `EraseSubject` they read as `null` and the row stays. Tenant erasure deletes rows.
7. Sensitive reads (`[Endpoint(…, Sensitive = true)]`) and every denial of a signed-in caller become `Read`/`Denied` rows; nothing
   to register (AddAudit adds the `IAccessObserver`).
8. Read with `IAuditLog.Query(query, context, ct)` (tenant = the caller's) or `app.MapAudit("/protokoll", policy)`. Name a policy:
   the log holds personal data.
9. Raise `a.Version` after changing marks to rebuild from the feed; the rows keep their ids. Retention: `Tarol:Audit:RetentionDays`.

## Example
```csharp
public sealed record Registered([property: PersonalData] string? FirstName, string Group,
    [property: NotAudited(Reason = "a checksum, no fact")] string Checksum) : Event;
public sealed record Moved(string Group, [property: AuditTime] DateTimeOffset At) : Event;
[AuditRemoved] public sealed record Left : Event;

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Child>()).UseInMemoryStore()
    .AddAudit(a => a.Add<Child>()).UseInMemoryAudit();
app.MapAudit("/protokoll", "audit:read");

var page = (await log.Query(new AuditQuery { Target = childId, Limit = 50 }, context, ct)).GetValueOrThrow();
foreach (var row in page.Items)        // row.Kind, row.Field, row.Before, row.After, row.Actor, row.At, row.CapturedAt
    Console.WriteLine($"{row.At:u} {row.Actor.Id} {row.Field}: {row.Before} -> {row.After}");
// next page: query with { After = page.Next }
```

## Common mistakes
| Mistake | What happens |
|---|---|
| `AddAudit(_ => { })` or `AddAudit` twice | `InvalidOperationException` at registration |
| `AddAudit` without `UseInMemoryAudit`/`UseNpgsqlAudit` | `InvalidOperationException` "The audit has no store" when the host is resolved |
| an aggregate added to the audit but not to the codec | its events are skipped (the codec cannot describe them) |
| `Limit = 500` or a hand-made `After` | 400: `limit` `Range`, `after` `Query.Cursor` |
| expecting plain names in the database | personal values are sealed (`tdp1.…`); read them through `IAuditLog` |
| filtering reads by `StreamType = "child"` | reads and denials carry the aggregate's CLR name (`Child`) |

## AOT
No reflection: events are written through the app's source-generated JSON context; values are compared and opened with the
codec. `Tarol.Audit.AotSmoke` publishes with 0 IL warnings.
