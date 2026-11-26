# Tarol.Audit.Npgsql

The PostgreSQL store of [Tarol.Audit](../Tarol.Audit/README.md), in the event store's own database.

```csharp
builder.UseNpgsqlStore(dataSource)
       .UseNpgsqlReadModels()                          // the gate (one runner) and the feed signal
       .AddAudit(a => a.Add<Child>())
       .UseNpgsqlAudit();                              // the event store's schema by default
```

* Per projection version `<schema>.audit_rows_v<n>` (one row per event and field) and `audit_values_v<n>` (the last value per
  tenant, target and field), written with the checkpoint (row `tarol-audit` of `projections`) in one transaction.
* Reads and denials in `<schema>.audit_access`, kept across versions.
* Indexes for the list newest first by tenant, by target and by actor; keyset paging (`at`, `id`).
* `NpgsqlAuditStore.Script(version)` is the DDL for a migration tool; the host creates the tables at start otherwise.

Licence: AGPL-3.0-only, or the owner's commercial licence.
