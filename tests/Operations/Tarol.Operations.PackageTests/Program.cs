using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tarol.Operations;
using Tarol.Persistence;

var services = new ServiceCollection();
services.AddLogging();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = OperationsJson.Default };
_ = new TarolBuilder(services, json, new EventCodec(json)).UseInMemoryStore().AddOperations(o => o.Thresholds.ReactionsDegraded = TimeSpan.FromSeconds(10));
await using var provider = services.BuildServiceProvider();

var snapshot = await provider.GetRequiredService<OperationsHost>().PollOnce();
var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
if (snapshot.Failures != 0 || report.Status != HealthStatus.Healthy || report.Entries.Count != 6)
{
    Console.Error.WriteLine($"Tarol.Operations package consumer: unexpected snapshot {snapshot} or report {report.Status} ({report.Entries.Count} checks)");
    return 1;
}
Console.WriteLine($"Tarol.Operations package consumer: {snapshot.Values.Count} signals polled, {report.Entries.Count} checks {report.Status}");
return 0;
