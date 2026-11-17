using System.Diagnostics.Metrics;
using Npgsql;

namespace Probe.Operations;

/// <summary>The numbers the gauges and health checks of step 15 read: one round of queries.</summary>
internal sealed record Snapshot(
    DateTimeOffset At,
    long OutboxRows,                 // reltuples estimate (count(*) is a full scan)
    double OldestDueSeconds,         // now − min(due_at) of due heads; 0 when none
    long OutboxDead,
    long JobsDead,
    double JobsOverdueSeconds,       // now − min(next_at) of enabled due rows; 0 when none
    long FeedHead,                   // the position of the newest event
    double ProjectionLagSeconds,     // recorded_at of the newest event − recorded_at of the checkpoint's event
    bool DatabaseUp);

/// <summary>
/// One query round per call (the would-be operations poll): cheap index scans only. The meter's observable gauges read the
/// last snapshot, never the database, so a scrape costs nanoseconds and never blocks on PostgreSQL.
/// </summary>
internal sealed class Signals : IDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly string _schema;
    private readonly string _rm;
    private readonly Meter _meter;
    private volatile Snapshot? _last;

    public Signals(NpgsqlDataSource db, string schema, string readModels, string meterName = "Tarol.Operations")
    {
        _db = db;
        _schema = schema;
        _rm = readModels;
        _meter = new Meter(meterName);
        _meter.CreateObservableGauge("tarol.reactions.oldest_due", () => _last?.OldestDueSeconds ?? 0, "s", "Age of the oldest due reaction row.");
        _meter.CreateObservableGauge("tarol.reactions.pending", () => _last?.OutboxRows ?? 0, "{row}", "Outbox rows (estimate).");
        _meter.CreateObservableGauge("tarol.reactions.dead", () => _last?.OutboxDead ?? 0, "{row}", "Dead reaction rows.");
        _meter.CreateObservableGauge("tarol.jobs.overdue", () => _last?.JobsOverdueSeconds ?? 0, "s", "How late the most overdue job cursor is.");
        _meter.CreateObservableGauge("tarol.jobs.dead", () => _last?.JobsDead ?? 0, "{occurrence}", "Dead job occurrences.");
        _meter.CreateObservableGauge("tarol.projections.lag", () => _last?.ProjectionLagSeconds ?? 0, "s", "Feed head time − checkpoint time.");
    }

    public Snapshot? Last => _last;

    public string Sql => $"""
        SELECT
          (SELECT greatest(reltuples, 0)::bigint FROM pg_class WHERE oid = '{_schema}.outbox'::regclass),
          coalesce(extract(epoch FROM clock_timestamp() - (SELECT min(due_at) FROM {_schema}.outbox WHERE head AND due_at <= clock_timestamp())), 0)::float8,
          (SELECT count(*) FROM {_schema}.outbox_dead),
          (SELECT count(*) FROM {_schema}.jobs_dead),
          coalesce(extract(epoch FROM clock_timestamp() - (SELECT min(next_at) FROM {_schema}.jobs WHERE enabled AND next_at <= clock_timestamp())), 0)::float8,
          coalesce((SELECT position FROM {_schema}.events ORDER BY tx_id DESC, position DESC LIMIT 1), 0),
          coalesce(extract(epoch FROM
            (SELECT recorded_at FROM {_schema}.events ORDER BY tx_id DESC, position DESC LIMIT 1) -
            coalesce((SELECT e.recorded_at FROM {_rm}.projections p JOIN {_schema}.events e ON e.tx_id = p.tx AND e.position = p.pos
                      WHERE p.name = 'item-list' ORDER BY p.version DESC LIMIT 1),
                     (SELECT recorded_at FROM {_schema}.events ORDER BY tx_id, position LIMIT 1))), 0)::float8
        """;

    public async Task<Snapshot> Poll(CancellationToken ct = default)
    {
        try
        {
            await using var c = _db.CreateCommand(Sql);
            c.CommandTimeout = 5;
            await using var r = await c.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var s = new Snapshot(Lab.Time.GetUtcNow(), r.GetInt64(0), r.GetDouble(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4), r.GetInt64(5), r.GetDouble(6), true);
            _last = s;
            return s;
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            var s = (_last ?? new Snapshot(Lab.Time.GetUtcNow(), 0, 0, 0, 0, 0, 0, 0, false)) with { At = Lab.Time.GetUtcNow(), DatabaseUp = false };
            _last = s;
            return s;
        }
    }

    public void Dispose() => _meter.Dispose();
}

#if HEALTH
internal static class HealthChecksProbe
{
    /// <summary>Thresholds of the would-be readiness checks (the evaluation's §2.2 table).</summary>
    public static Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult Judge(string check, Snapshot? s)
    {
        if (s is null) return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded("no snapshot yet");
        if (!s.DatabaseUp) return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("database unreachable");
        return check switch
        {
            "reactions" => s.OldestDueSeconds > 300 ? Unhealthy($"oldest due reaction {s.OldestDueSeconds:0} s") : s.OldestDueSeconds > 30 ? Degraded($"oldest due reaction {s.OldestDueSeconds:0} s") : Ok(),
            "dead-letters" => s.OutboxDead + s.JobsDead > 0 ? Degraded($"{s.OutboxDead} dead reactions, {s.JobsDead} dead jobs") : Ok(),
            "jobs" => s.JobsOverdueSeconds > 600 ? Unhealthy($"a job is {s.JobsOverdueSeconds:0} s overdue") : s.JobsOverdueSeconds > 60 ? Degraded($"a job is {s.JobsOverdueSeconds:0} s overdue") : Ok(),
            "projections" => s.ProjectionLagSeconds > 300 ? Unhealthy($"projection lag {s.ProjectionLagSeconds:0} s") : s.ProjectionLagSeconds > 30 ? Degraded($"projection lag {s.ProjectionLagSeconds:0} s") : Ok(),
            _ => Ok(),
        };

        static Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult Ok() => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        static Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult Degraded(string m) => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(m);
        static Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult Unhealthy(string m) => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(m);
    }

    internal sealed class SnapshotCheck(Signals signals, string check) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
    {
        public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(Judge(check, signals.Last));
    }
}
#endif
