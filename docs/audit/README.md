# The change log (Tarol.Audit)

Who changed which field of which object when, from what to what; who read a sensitive view; who was refused. One list, per
tenant, newest first.

## Set up

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Child>().Add<Guardian>())
    .UseNpgsqlStore(dataSource).UseNpgsqlReadModels()
    .AddSecurity(roles => roles.Role("dpo").May<Read<Child>>())
    .AddEndpoints()
    .AddAudit(a => a.Add<Child>().Add<Guardian>())       // every event of these aggregates
    .UseNpgsqlAudit();                                    // UseInMemoryAudit() in tests
builder.Services.AddAuthorizationBuilder().AddPolicy("audit:read", p => p.RequireRole("dpo"));

app.MapAudit("/protokoll", "audit:read");
```

## What becomes a row

| Event | Row |
|---|---|
| the first event of a stream or an entity | `Created`, one per member with a value |
| a later event | `Changed`, one per member whose value differs from the field's last value (`Before` → `After`) |
| `[AuditRemoved]` or an entity's `Deleted` | `Removed`, one row; the target's last values are forgotten |
| `[NotAudited]` on the event | none |
| a granted read of an `[Endpoint(…, Sensitive = true)]` (a 304 too) | `Read` |
| a refusal of a signed-in caller | `Denied` with the code; anonymous refusals are only counted |

A field is the member's JSON name; `Before`/`After` are JSON values. `[property: NotAudited(Reason = "…")]` leaves a member out.
`[property: AuditTime]` names the member with the time on the device; the row's `CapturedAt` takes it, else the operation's
`Tarol-Captured-At`, and `Device` the `Tarol-Device`.

## Personal data

A `[PersonalData]` value is stored as the event store stores it: sealed under its subject's key. The log opens it for the reader;
after `EraseSubject` it reads as `null`, the row (who, when, which field) stays. `EraseTenant` deletes the tenant's rows.

## Reading

`GET /protokoll?target=&actor=&streamType=&field=&kind=&from=&to=&limit=&after=` answers
`{ "items": [{ id, at, kind, actor, streamType, target, stream, event, field, before, after, capturedAt, device, view, code }], "next" }`.
In code: `IAuditLog.Query(new AuditQuery { Target = id }, context, ct)`. The tenant is always the caller's.

## Operating it

* **Rebuild:** after changing marks, raise `a.Version`; the new version is built from the feed with the same row ids.
* **Retention:** `Tarol:Audit:RetentionDays` (the log usually outlives the data); the maintenance task `tarol.audit.purge` runs hourly
  with the job host.
* **Tables (PostgreSQL):** `audit_rows_v<n>`, `audit_values_v<n>`, `audit_access`, the checkpoint row `tarol-audit` in `projections`;
  `NpgsqlAuditStore.Script(version)` for a migration tool.
