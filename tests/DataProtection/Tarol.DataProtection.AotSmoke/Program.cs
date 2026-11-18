using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tarol.DataProtection;
using Tarol.DataProtection.AotSmoke;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

Check("running as native code", !RuntimeFeature.IsDynamicCodeSupported);
var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
var tenant = context.Tenant.GetValueOrThrow().Value;

async Task Run(string name, Func<TarolBuilder, TarolBuilder> store)
{
    var services = new ServiceCollection();
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Tarol:DataProtection:MasterKey"] = key, ["Tarol:Retention:patient:Days"] = "1" }).Build());
    var json = new JsonSerializerOptions(SmokeJson.Default.Options);
    _ = store(new TarolBuilder(services, json, new EventCodec(json).Add<Patient>()))
        .UseInMemoryReadModels()
        .AddProjections(p => p.Add<PatientRow>())
        .AddDataProtection();
    await using var provider = services.BuildServiceProvider();
    if (provider.GetService<NpgsqlAggregateStore>() is { } npgsql)
        await npgsql.EnsureSchema(default);
    var aggregates = provider.GetRequiredService<IAggregateStore>();
    var patient = Patient.Admit("Ada", "B2");
    Check($"{name}: append sealed", (await aggregates.Append(patient, 0, context, Option.None, default)).IsSuccess);
    var host = provider.GetServices<ProjectionHost>().Single();
    await host.CatchUp();
    var rows = provider.GetRequiredService<IReadModels>().Store<PatientRow>();
    Check($"{name}: projected", (await rows.Find(new RowKey(tenant, patient.Id.Value), default))?.Row.Name == "Ada");
    var subjects = provider.GetRequiredService<IDataSubjects>();
    var export = await subjects.Export(tenant, patient.Id.Value, "dpo", default);
    Check($"{name}: export opens the subject's data", JsonDocument.Parse(export.Utf8Json).RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("name").GetString() == "Ada");
    var erasure = await subjects.EraseSubject(tenant, patient.Id.Value, "dpo", default);
    Check($"{name}: erasure", erasure.Streams.Count == 1 && !erasure.AlreadyErased);
    Check($"{name}: the row re-folded", (await rows.Find(new RowKey(tenant, patient.Id.Value), default))?.Row is { Name: null, Ward: "B2" });
    Check($"{name}: the aggregate loads with the field null", (await aggregates.Load(patient.Id, context, default)).GetValueOrThrow() is { Name: null, Ward: "B2" });
    var retention = provider.GetServices<MaintenanceTask>().Single(static t => t.Name == "tarol.retention");
    Check($"{name}: retention", await retention.Run(provider, DateTimeOffset.UtcNow.AddDays(2), default) == 1
                                && (await aggregates.Load(patient.Id, context, default)).IsNone);
}

await Run("in memory", static b => b.UseInMemoryStore().UseInMemoryKeyStore());

// The database checks need a PostgreSQL: TAROL_PG=<connection string> (the CI aot-smoke job provides one).
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    var schema = $"dp_smoke_{Environment.ProcessId}";
    try
    {
        await Run("postgres", b => b.UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { Schema = schema }, ensureSchema: false)
            .UseNpgsqlKeyStore(new NpgsqlKeyStoreOptions { Schema = schema }));
    }
    finally
    {
        await using var db = Npgsql.NpgsqlDataSource.Create(connectionString);
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.DataProtection.AotSmoke
{
    public partial class Patient : AggregateRoot<Patient>
    {
        [PersonalData] public string? Name { get; private set; }
        public string Ward { get; private set; } = "";

        public static Patient Admit(string name, string ward)
        {
            var patient = new Patient(Id<Patient>.New());
            patient.Raise(new Admitted(name, ward));
            return patient;
        }

        public sealed record Admitted([property: PersonalData] string? Name, string Ward) : Event;

        private void On(Admitted e) { Name = e.Name; Ward = e.Ward; }
    }

    [Projection("patients", Version = 1)]
    public sealed partial record PatientRow(Guid Id, string? Name, string Ward)
    {
        public static PatientRow When(Patient.Admitted e, RecordedEvent r) => new(r.StreamId, e.Name, e.Ward);
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Patient.Admitted))]
    [JsonSerializable(typeof(PatientRow))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
