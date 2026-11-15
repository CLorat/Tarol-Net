using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Smoke;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Npgsql;
using Tarol.Messaging.Npgsql.AotSmoke;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

Check("bus script", NpgsqlBusSchema.Script("smoke_bus").Contains("CREATE TABLE IF NOT EXISTS smoke_bus.deliveries", StringComparison.Ordinal));
Check("row-level security script", NpgsqlBusSchema.RowLevelSecurityScript("smoke_bus").Contains("publish_own_prefix", StringComparison.Ordinal));

// The database checks need a PostgreSQL: TAROL_PG=<connection string> (the CI aot-smoke job provides one).
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    var storeSchema = $"aot_msg_{Environment.ProcessId}";
    var busSchema = $"aot_bus_{Environment.ProcessId}";
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.AddTarol(SmokeJson.Default, codec => codec.Add<Account>().Add<Audit>())
        .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { Schema = storeSchema })
        .AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20))
        .AddMessaging("smoke", m => m
            .Consume(GeneratedConsumers.Set)
            .Publish(GeneratedPublications.Set)
            .Trust("billing")
            .Trust("smoke")
            .UseNpgsqlBus(connectionString, new NpgsqlBusOptions { Schema = busSchema, Notify = true })
            .UseNpgsqlDeadLetters(storeSchema));
    using var host = builder.Build();
    await host.StartAsync();
    await Task.Delay(300);                                                              // the host declares the subscriptions
    var bus = (NpgsqlBus)host.Services.GetRequiredService<IMessageTransport>();
    var account = Guid.CreateVersion7();
    Message Paid(decimal amount) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = "billing",
        Type = "billing.paid",
        Version = 1,
        Time = TimeProvider.System.GetUtcNow(),
        Correlation = Id<ICorrelation>.New(),
        Actor = "User:alice",
        Data = JsonSerializer.SerializeToUtf8Bytes(new PaidV1(account, amount), SmokeJson.Default.PaidV1),
    };
    await bus.Publish(Paid(5m), default);
    await bus.Publish(Paid(5m) with { Data = "not json"u8.ToArray() }, default);
    var dead = host.Services.GetRequiredService<MessageConsumers>().DeadLetters;
    await using var db = NpgsqlDataSource.Create(connectionString);
    async Task<long> Count(string sql)
    {
        await using var command = db.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
    var waited = Stopwatch.StartNew();
    while ((await Count($"SELECT count(*) FROM {storeSchema}.events") < 2 || (await dead.List(10, default)).Count == 0) && waited.Elapsed < TimeSpan.FromSeconds(20))
        await Task.Delay(50);
    await host.StopAsync();
    Check("consumed from the bus", await Count($"SELECT count(*) FROM {storeSchema}.events WHERE actor_id = 'Consumers.CreditAccount' AND on_behalf_of = 'alice'") == 1);
    Check("published through the outbox and consumed back", await Count($"SELECT count(*) FROM {storeSchema}.events WHERE actor_id = 'Consumers.AuditCredit'") == 1);
    Check("dead letter in messages_dead", (await dead.List(10, default)) is [{ Reason: var reason }] && reason.StartsWith("Messaging.Malformed", StringComparison.Ordinal));
    Check("bus drained", await Count($"SELECT count(*) FROM {busSchema}.deliveries") == 0);
    await using (var drop = db.CreateCommand($"DROP SCHEMA {storeSchema} CASCADE; DROP SCHEMA {busSchema} CASCADE"))
        await drop.ExecuteNonQueryAsync();
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
