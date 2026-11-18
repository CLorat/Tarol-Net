using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tarol.Operations;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = OperationsJson.Default };

// --- in memory, over Kestrel: the snapshot, the three groups, the admin entry behind a policy
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    _ = new TarolBuilder(builder.Services, json, new EventCodec(json)).UseInMemoryStore().AddOperations();
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
    builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));
    await using var app = builder.Build();
    app.UseAuthorization();
    app.MapTarolHealth();
    app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");
    await app.StartAsync();
    var address = app.Urls.First();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true }) { BaseAddress = new Uri(address) };

    var host = app.Services.GetRequiredService<OperationsHost>();
    var snapshot = await host.PollOnce();
    Check("in-memory snapshot", snapshot.Failures == 0 && snapshot.Has(OperationsSignals.ReactionsOldestDue) && snapshot.Has(OperationsSignals.JobsDead));
    Check("GET /health/live 200", (await http.GetAsync("/health/live")).StatusCode == HttpStatusCode.OK);
    Check("GET /health/ready 200", (await http.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK);
    var tarol = await http.GetStringAsync("/health/tarol");
    using (var report = JsonDocument.Parse(tarol))
        Check("GET /health/tarol: JSON with the four backlog checks", report.RootElement.GetProperty("checks").EnumerateObject().Count() == 4);
    Check("the admin entry needs the policy", (await http.GetAsync("/tarol/admin/dead/reactions")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
    Check("the admin kinds", app.Services.GetRequiredService<IDeadLetterAdmin>().Kinds.SequenceEqual([DeadLetterKinds.Jobs, DeadLetterKinds.Reactions]));
    var all = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
    Check("six checks registered", all.Entries.Count == 6);
    await app.StopAsync();
}

// --- the database checks need a PostgreSQL: TAROL_PG=<connection string> (the CI aot-smoke job provides one)
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    var services = new ServiceCollection();
    services.AddLogging();
    var schema = $"aot_ops_{Environment.ProcessId}";
    _ = new TarolBuilder(services, json, new EventCodec(json)).UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { Schema = schema }).AddOperations();
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredService<NpgsqlAggregateStore>();
    var db = provider.GetRequiredService<Npgsql.NpgsqlDataSource>();
    try
    {
        await store.EnsureSchema(default);
        var start = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        _ = await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "0 2 * * * UTC", true, start)], default);
        var row = (await store.Jobs.Claim(TimeSpan.FromMinutes(1), default)).GetValueOrThrow();
        Check("a dead job", await store.Jobs.Bury(row, start.AddDays(1), "gave up", default));

        var snapshot = await provider.GetRequiredService<OperationsHost>().PollOnce();
        Check("database snapshot", snapshot.Failures == 0 && snapshot.Max(OperationsSignals.JobsDead) == 1);
        var admin = provider.GetRequiredService<IDeadLetterAdmin>();
        var page = await admin.List(DeadLetterKinds.Jobs, new DeadLetterQuery(), default);
        Check("a page of dead jobs", page.Items.Count == 1 && page.Next is null);
        Check("an audited re-drive", await admin.Redrive(DeadLetterKinds.Jobs, page.Items[0].Id, "aot-smoke", default));
        await using var audit = db.CreateCommand($"SELECT count(*) FROM {schema}.operations_audit WHERE actor = 'aot-smoke'");
        Check("the audit row", await audit.ExecuteScalarAsync() is 1L);
        var ready = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains(TarolHealthTags.Ready));
        Check("ready over the database", ready.Status == HealthStatus.Healthy);
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
