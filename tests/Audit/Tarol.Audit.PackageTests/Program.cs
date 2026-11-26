using Tarol.Audit;
using Tarol.Audit.Npgsql;
using Tarol.Audit.PackageTests;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

var codec = new EventCodec(ConsumerJson.Default.Options).Add<Note>();
var events = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions { Codec = codec });
var host = new AuditHost(new AuditRegistry().Add<Note>(), new InMemoryAuditStore(), events, codec);
var context = OperationContext.Start(Actor.User("anna"), Id<ITenant>.From(Guid.CreateVersion7()), TimeProvider.System);
var note = Note.Write("hello", "sum");
if (!(await events.Append(note, 0, context, Option.None, CancellationToken.None)).IsSuccess)
    return 1;
await host.CatchUp(CancellationToken.None);
var rows = (await AuditLogs.Over(host, null).Query(new AuditQuery(), context, CancellationToken.None)).GetValueOrThrow().Items;
var ddl = typeof(NpgsqlAuditStore).Name;
if (rows is not [{ Field: "text", After: "\"hello\"" }] || ddl.Length == 0)
{
    Console.Error.WriteLine($"Tarol.Audit package consumer: unexpected rows {string.Join(", ", rows.Select(r => r.Field))}");
    return 1;
}
Console.WriteLine("Tarol.Audit package consumer: OK");
return 0;

