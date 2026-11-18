# Tarol.DataProtection

Personal data in an append-only event store stays deletable. Fields marked `[PersonalData]` are sealed under their
subject's key (Tarol.Persistence). Erasing a subject turns that key into a tombstone, and this package makes the rest of
the system forget too. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.DataProtection
```

## 30 seconds

```csharp
builder.Services.AddTarol(AppJson.Default.Options, tarol => tarol
    .Add<Customer>().Add<Order>()
    .UseNpgsqlStore(connectionString).UseNpgsqlKeyStore()      // keys in tenant_keys / subject_keys
    .UseNpgsqlReadModels().AddProjections(p => p.Add<CustomerRow>())
    .AddDataProtection(o => o.Retention["order"] = 3650));       // master key: Tarol:DataProtection:MasterKey

app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");       // GET …/subjects/{id}/export, POST …/subjects/{id}/erase

await subjects.EraseSubject(tenant, customerId, actor: "dpo@example.org", ct);   // IDataSubjects
var export = await subjects.Export(tenant, customerId, actor: "dpo@example.org", ct);
```

## What an erasure does

1. It writes the key's tombstone and a `NOTIFY`: every process evicts the key, and the subject's fields read as `null`.
   New personal data for the subject is refused (`DataProtection.SubjectErased`).
2. It finds the subject's streams through `events.subjects`.
3. It runs every `ErasureParticipant` in order:
   * projections re-fold the affected streams (`ProjectionEraser<TRow>`, registered by `AddProjections`);
   * page caches drop the tenant's pages, in every process (`PageCacheEraser`, registered by `AddCaching`);
   * personal lookups are deleted (`PersonalLookupsEraser`);
   * your own participants, for example for your own SQL tables.
4. It writes an `operations_audit` row (`erase`, `subject:<id>`) and a log line (16001).

A failing participant does not stop the others. The erasure then throws `ErasureIncompleteException`; run it again.

## Export and retention

* `Export` returns one JSON document: every event holding the subject's data, opened with **that subject's key only**,
  plus the state of the subject's own streams. It is audited.
* `[Retention(Days = n)]` on an aggregate, or `Tarol:Retention:<streamType>:Days`, makes the system job `tarol.retention`
  (hourly, with `AddJobs`) delete whole streams n days after their last event, 500 per statement. Streams with pending
  reactions are skipped, and projections drop their rows.

## What it cannot reach

Plaintext written before a member was marked (run `SealHistory<T>` once), backups older than the erasure, your logs,
other services, and contracts you published (contracts carry ids, not personal data). Read
`docs/data-protection/README.md` in the repository.

Meter `Tarol.DataProtection` (`tarol.subjects.erased`, `tarol.subjects.exported`, `tarol.retention.streams`); logs
16001–16008.

## Links

* Skill for coding agents: `skills/tarol-data-protection/SKILL.md` (in the package).
* Concept and decisions: `docs/architecture/data-protection.md`, `docs/migration/data-protection.md` in the repository.

Licence: AGPL-3.0-only, see `LICENSE`; a commercial licence is available from the owner (`NOTICE`).
