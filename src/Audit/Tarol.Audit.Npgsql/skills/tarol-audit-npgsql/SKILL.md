---
name: tarol-audit-npgsql
description: >-
  Use when the Tarol change log (Tarol.Audit) runs on PostgreSQL. Covers UseNpgsqlAudit, NpgsqlAuditStore, its tables, Script
  for migration tools, versions and the one-runner gate. Do not create the audit tables by hand or query them directly.
---
# Tarol change log on PostgreSQL

## Rules
* Register after `AddAudit`: `builder.UseNpgsqlStore(dataSource).UseNpgsqlReadModels().AddAudit(a => a.Add<Child>()).UseNpgsqlAudit();`
  `UseNpgsqlReadModels` brings the one-runner gate and the feed signal; the log uses the store's schema by default.
* Tables: `audit_rows_v<n>`, `audit_values_v<n>` per version, `audit_access` for reads and denials, checkpoint row `tarol-audit`
  in `projections`. Rows, values and checkpoint commit together.
* A migration tool runs `new NpgsqlAuditStore(dataSource, schema).Script(version)`; otherwise the host creates them at start.
* Values are text holding JSON; personal ones sealed (`tdp1.…`). Read through `IAuditLog`, never with SQL: SQL sees seals.
* Raising `a.Version` builds `audit_rows_v<n+1>` from the feed while the old version stays; drop it when done.

## Example
```csharp
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Child>())
    .UseNpgsqlStore(dataSource).UseNpgsqlReadModels()
    .AddAudit(a => a.Add<Child>()).UseNpgsqlAudit();
```

## Common mistakes
| Mistake | What happens |
|---|---|
| a data source without `EnableArrays()` | the batch write fails (the rows go in as arrays) |
| a schema with capitals or quotes | `ArgumentException`: a lowercase identifier |
