using Microsoft.Extensions.DependencyInjection;
using Tarol.Audit;
using Tarol.Audit.Tests;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Security;
using Tarol.StronglyTypedId;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

var tenant = Id<ITenant>.From(Guid.CreateVersion7());
var context = OperationContext.Start(Actor.User("anna"), tenant, TimeProvider.System);
var keys = InMemoryKeyStore.Create();
var services = new ServiceCollection();
services.AddSingleton<IKeyStore>(keys);
services.AddTarol(AuditJson.Default, codec => codec.Add<Child>()).UseInMemoryStore().AddAudit(a => a.Add<Child>()).UseInMemoryAudit();
await using var provider = services.BuildServiceProvider();
var codec = provider.GetRequiredService<EventCodec>();
var events = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions { Codec = codec, Keys = keys });
var host = new AuditHost(provider.GetRequiredService<AuditRegistry>(), new InMemoryAuditStore(), events, codec, keys);
var log = AuditLogs.Over(host, keys);

var child = Child.Register(Id<Child>.New(), "Mia", "Bären");
var allergy = child.Note("Nüsse");
Check("commit", (await events.Append(child, 0, context, Option.None, CancellationToken.None)).IsSuccess);
AggregateChanges.Committed(child);
Check("rename", child.Rename("Mila").IsSuccess);
allergy.Drop();
Check("commit", (await events.Append(child, child.Version - child.UncommittedChanges.Length, context, Option.None, CancellationToken.None)).IsSuccess);
AggregateChanges.Committed(child);
await host.CatchUp(CancellationToken.None);

var rows = (await log.Query(new AuditQuery(), context, CancellationToken.None)).GetValueOrThrow().Items;
Check("rows per field with before and after", rows.Any(r => r is { Kind: AuditKind.Changed, Field: "firstName", Before: "\"Mia\"", After: "\"Mila\"" }));
Check("an unaudited member is no field", rows.All(r => r.Field != "checksum"));
Check("a deleted entity is one removed row", rows.Count(r => r.Target == allergy.Id.Value && r.Kind == AuditKind.Removed) == 1);
var first = (await log.Query(new AuditQuery { Limit = 2 }, context, CancellationToken.None)).GetValueOrThrow();
var second = (await log.Query(new AuditQuery { Limit = 2, After = first.Next }, context, CancellationToken.None)).GetValueOrThrow();
Check("cursor pages", first.Next is not null && second.Items.Count == 2 && second.Items[0].Id != first.Items[1].Id);
await keys.EraseSubject(tenant.Value, child.Id.Value, CancellationToken.None);
var erased = (await log.Query(new AuditQuery { Field = "firstName" }, context, CancellationToken.None)).GetValueOrThrow().Items;
Check("an erased subject's values read as null", erased.Count == 2 && erased.All(r => r.After is null && r.Before is null));

var observer = provider.GetServices<IAccessObserver>().Single();
await observer.Observed(new AccessRecord(Actor.User("ben"), tenant, "Read<Child>", "Child", child.Id.Value, "ChildHealth", AccessDecision.Allowed, null, TimeProvider.System.GetUtcNow()), CancellationToken.None);
var registered = provider.GetRequiredService<IAuditLog>();
Check("a sensitive read is a row", (await registered.Query(new AuditQuery { Kind = AuditKind.Read }, context, CancellationToken.None)).GetValueOrThrow().Items.Count == 1);
Check("the retention task is registered", provider.GetServices<MaintenanceTask>().Any(t => t.Name == AuditTarolBuilderExtensions.PurgeJob));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
