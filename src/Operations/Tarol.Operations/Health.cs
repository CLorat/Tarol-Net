using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tarol.Persistence;

namespace Tarol.Operations;

/// <summary>The tags of Tarol's health checks (O-2).</summary>
public static class TarolHealthTags
{
    /// <summary>The process answers: liveness (never the database: a restart does not fix PostgreSQL).</summary>
    public const string Live = "live";

    /// <summary>The process can serve: the snapshot is fresh, the schema ensured, the checkpoint <c>LISTEN</c> up: readiness.</summary>
    public const string Ready = "ready";

    /// <summary>Tarol's backlogs: for dashboards and alerts, <b>never</b> readiness (a backlog must not take an API pod out).</summary>
    public const string Tarol = "tarol";
}

internal sealed class LiveCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy("the process answers"));
}

internal sealed class ReadyCheck(OperationsHost host, IEnumerable<ReadinessCheck> readiness, IServiceProvider services, OperationsOptions options) : IHealthCheck
{
    private readonly ReadinessCheck[] _readiness = [.. readiness];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = host.Snapshot;
        if (snapshot.LastPoll is null)
            return Task.FromResult(HealthCheckResult.Unhealthy("no operations snapshot yet"));
        if (snapshot.Failures >= options.StaleAfter)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                string.Create(CultureInfo.InvariantCulture, $"{snapshot.Failures} polls in a row failed (the database is unreachable?): {snapshot.LastError}")));
        foreach (var check in _readiness)
            if (check.Problem(services) is { } problem)
                return Task.FromResult(HealthCheckResult.Unhealthy($"{check.Name}: {problem}"));
        return Task.FromResult(HealthCheckResult.Healthy());
    }
}

/// <summary>A backlog check of the <c>tarol</c> group over the snapshot (no round trip).</summary>
internal sealed class BacklogCheck(OperationsHost host, Func<OperationsSnapshot, HealthCheckResult> judge) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(judge(host.Snapshot));

    internal static Func<OperationsSnapshot, HealthCheckResult> Age(string signal, TimeSpan degraded, TimeSpan unhealthy, string what) => snapshot =>
    {
        if (!snapshot.Has(signal))
            return HealthCheckResult.Healthy("not measured");
        var age = TimeSpan.FromSeconds(snapshot.Max(signal));
        var text = string.Create(CultureInfo.InvariantCulture, $"{what} {age.TotalSeconds:0} s");
        return age > unhealthy ? HealthCheckResult.Unhealthy(text) : age > degraded ? HealthCheckResult.Degraded(text) : HealthCheckResult.Healthy(text);
    };

    internal static Func<OperationsSnapshot, HealthCheckResult> Dead(long degraded) => snapshot =>
    {
        var reactions = snapshot.Sum(OperationsSignals.ReactionsDead);
        var jobs = snapshot.Sum(OperationsSignals.JobsDead);
        var messages = snapshot.Sum(OperationsSignals.MessagesDead);
        var text = string.Create(CultureInfo.InvariantCulture, $"{reactions:0} dead reactions, {jobs:0} dead jobs, {messages:0} dead messages");
        return reactions + jobs + messages > degraded ? HealthCheckResult.Degraded(text) : HealthCheckResult.Healthy(text);
    };
}

/// <summary>Maps the three health groups.</summary>
public static class TarolHealthEndpoints
{
    /// <summary>
    /// Maps <c>{prefix}/live</c> (liveness), <c>{prefix}/ready</c> (readiness) and <c>{prefix}/tarol</c> (the backlogs, a JSON
    /// report: 200 while Healthy or Degraded, 503 when Unhealthy). Point Kubernetes' liveness probe at <c>/live</c> and its
    /// readiness probe at <c>/ready</c>, never at <c>/tarol</c>.
    /// </summary>
    /// <param name="endpoints">The routes.</param>
    /// <param name="prefix">The path prefix (default <c>/health</c>).</param>
    /// <returns>The routes.</returns>
    public static IEndpointRouteBuilder MapTarolHealth(this IEndpointRouteBuilder endpoints, string prefix = "/health")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        prefix = prefix.TrimEnd('/');
        endpoints.MapHealthChecks(prefix + "/live", new HealthCheckOptions { Predicate = static r => r.Tags.Contains(TarolHealthTags.Live) });
        endpoints.MapHealthChecks(prefix + "/ready", new HealthCheckOptions { Predicate = static r => r.Tags.Contains(TarolHealthTags.Ready) });
        endpoints.MapHealthChecks(prefix + "/tarol", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains(TarolHealthTags.Tarol),
            ResponseWriter = WriteReport,
        });
        return endpoints;
    }

    /// <summary>The report as JSON, written with <see cref="Utf8JsonWriter"/> (no reflection).</summary>
    internal static async Task WriteReport(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await using var json = new Utf8JsonWriter(context.Response.Body);
        json.WriteStartObject();
        json.WriteString("status", report.Status.ToString());
        json.WriteNumber("durationMs", report.TotalDuration.TotalMilliseconds);
        json.WriteStartObject("checks");
        foreach (var (name, entry) in report.Entries)
        {
            json.WriteStartObject(name);
            json.WriteString("status", entry.Status.ToString());
            if (entry.Description is { } description)
                json.WriteString("description", description);
            json.WriteEndObject();
        }
        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }
}
