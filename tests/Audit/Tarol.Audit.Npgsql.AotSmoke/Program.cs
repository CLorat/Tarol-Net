using Npgsql;
using Tarol.Audit;
using Tarol.Audit.Npgsql;
using Tarol.Audit.Tests;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

await using (var none = new NpgsqlSlimDataSourceBuilder("Host=localhost").Build())
{
    var script = new NpgsqlAuditStore(none, "smoke").Script(3);
    Check("the DDL: rows and values per version, the access table, the list index", script.Contains("smoke.audit_rows_v3", StringComparison.Ordinal)
        && script.Contains("smoke.audit_values_v3", StringComparison.Ordinal) && script.Contains("smoke.audit_access", StringComparison.Ordinal)
        && script.Contains("(tenant, at DESC, id DESC)", StringComparison.Ordinal));
}

var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"audit_smoke_{Environment.ProcessId}";
    var keys = InMemoryKeyStore.Create();
    var codec = new EventCodec(AuditJson.Default.Options).Add<Child>();
    var events = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
    await events.EnsureSchema(CancellationToken.None);
    var host = new AuditHost(new AuditRegistry().Add<Child>(), new NpgsqlAuditStore(db, schema), events, codec, keys);
    var log = AuditLogs.Over(host, keys);
    var tenant = Id<ITenant>.From(Guid.CreateVersion7());
    var context = OperationContext.Start(Actor.User("anna"), tenant, TimeProvider.System);
    var child = Child.Register(Id<Child>.New(), "Mia", "Bären");
    Check("commit", (await events.Append(child, 0, context, Option.None, CancellationToken.None)).IsSuccess);
    AggregateChanges.Committed(child);
    Check("rename", child.Rename("Mila").IsSuccess);
    Check("commit", (await events.Append(child, 1, context, Option.None, CancellationToken.None)).IsSuccess);
    AggregateChanges.Committed(child);
    await host.CatchUp(CancellationToken.None);
    var rows = (await log.Query(new AuditQuery(), context, CancellationToken.None)).GetValueOrThrow().Items;
    Check("rows from PostgreSQL with before and after", rows.Any(r => r is { Field: "firstName", Before: "\"Mia\"", After: "\"Mila\"" }));
    await keys.EraseSubject(tenant.Value, child.Id.Value, CancellationToken.None);
    Check("erased values read as null", (await log.Query(new AuditQuery { Field = "firstName" }, context, CancellationToken.None)).GetValueOrThrow().Items.All(r => r.After is null));
    await using var drop = db.CreateCommand($"DROP SCHEMA {schema} CASCADE");
    await drop.ExecuteNonQueryAsync();
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
