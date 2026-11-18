using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Tarol.Persistence;
using Tarol.Testing.Metrics;

namespace Tarol.Operations.Tests;

/// <summary>The snapshot, the gauges over it and the three health groups (O-1, O-2; operations.md §11, 15C).</summary>
public sealed class SnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Dictionary<string, (HealthStatus Status, string? Description)>> Report(ServiceProvider provider, string tag)
    {
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains(tag), Ct);
        return report.Entries.ToDictionary(e => e.Key, e => (e.Value.Status, e.Value.Description));
    }

    [Fact]
    public async Task The_backlog_checks_judge_the_snapshot_by_the_thresholds_and_never_touch_live_or_ready()
    {
        var knobs = new Knobs();
        await using var provider = Services.With(knobs, new FakeTimeProvider(Services.Start)).BuildServiceProvider();
        var host = provider.GetRequiredService<OperationsHost>();

        knobs.Values[OperationsSignals.ReactionsOldestDue] = 10;
        knobs.Values[OperationsSignals.JobsOverdue] = 61;
        knobs.Values[OperationsSignals.ProjectionsLag] = 301;
        knobs.Values[OperationsSignals.ReactionsDead] = 0;
        _ = await host.PollOnce(Ct);
        var tarol = await Report(provider, TarolHealthTags.Tarol);
        tarol["tarol-reactions"].Status.ShouldBe(HealthStatus.Healthy);
        tarol["tarol-jobs"].ShouldBe((HealthStatus.Degraded, "most overdue job 61 s"));
        tarol["tarol-projections"].Status.ShouldBe(HealthStatus.Unhealthy);
        tarol["tarol-dead-letters"].Status.ShouldBe(HealthStatus.Healthy);

        knobs.Values[OperationsSignals.ReactionsOldestDue] = 31;
        knobs.Values[OperationsSignals.MessagesDead] = 2;
        _ = await host.PollOnce(Ct);
        tarol = await Report(provider, TarolHealthTags.Tarol);
        tarol["tarol-reactions"].Status.ShouldBe(HealthStatus.Degraded);
        tarol["tarol-dead-letters"].ShouldBe((HealthStatus.Degraded, "0 dead reactions, 0 dead jobs, 2 dead messages"));
        knobs.Values[OperationsSignals.ReactionsOldestDue] = 301;
        _ = await host.PollOnce(Ct);
        (await Report(provider, TarolHealthTags.Tarol))["tarol-reactions"].Status.ShouldBe(HealthStatus.Unhealthy);

        (await Report(provider, TarolHealthTags.Live)).ShouldHaveSingleItem().Value.Status.ShouldBe(HealthStatus.Healthy);
        (await Report(provider, TarolHealthTags.Ready)).ShouldHaveSingleItem().Value.Status.ShouldBe(HealthStatus.Healthy);   // backlogs are not readiness
    }

    [Fact]
    public async Task Thresholds_are_configurable_and_a_signal_nobody_registered_is_healthy()
    {
        var knobs = new Knobs();
        await using var provider = Services.With(knobs, new FakeTimeProvider(Services.Start), o =>
        {
            o.Thresholds.ReactionsDegraded = TimeSpan.FromSeconds(5);
            o.Thresholds.DeadDegraded = 10;
        }).BuildServiceProvider();
        knobs.Values[OperationsSignals.ReactionsOldestDue] = 6;
        knobs.Values[OperationsSignals.ReactionsDead] = 10;
        _ = await provider.GetRequiredService<OperationsHost>().PollOnce(Ct);

        var tarol = await Report(provider, TarolHealthTags.Tarol);
        tarol["tarol-reactions"].Status.ShouldBe(HealthStatus.Degraded);
        tarol["tarol-dead-letters"].Status.ShouldBe(HealthStatus.Healthy);                        // 10 is not more than 10
        tarol["tarol-jobs"].ShouldBe((HealthStatus.Healthy, "not measured"));
        Should.Throw<ArgumentOutOfRangeException>(() => Services.With(knobs, new FakeTimeProvider(), o => o.Thresholds.LagUnhealthy = TimeSpan.FromSeconds(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => Services.With(knobs, new FakeTimeProvider(), o => o.PollEvery = TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => Services.With(knobs, new FakeTimeProvider(), o => o.DeadRetention = TimeSpan.FromMinutes(1)));
        Should.Throw<ArgumentOutOfRangeException>(() => Services.With(knobs, new FakeTimeProvider(), o => o.AuditRetention = TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task An_audit_retention_registers_tarol_purge_audit_over_the_store()
    {
        // step 16C (DP-9): operations_audit rows are kept for ever unless AuditRetention is set
        var time = new FakeTimeProvider(Services.Start);
        var store = new InMemoryAggregateStore(time);
        await store.Audit("ops", "redrive", "reactions:1", null, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromDays(40));
        await store.Audit("ops", "redrive", "reactions:2", null, TestContext.Current.CancellationToken);
        var services = Services.With(new Knobs(), time, o => o.AuditRetention = TimeSpan.FromDays(30));
        services.AddSingleton<IDataProtectionStore>(store);
        await using var provider = services.BuildServiceProvider();
        Services.With(new Knobs(), time).Any(static s => s.ImplementationInstance is MaintenanceTask { Name: "tarol.purge-audit" }).ShouldBeFalse();

        var purge = provider.GetServices<MaintenanceTask>().Single(static t => t.Name == OperationsTarolBuilderExtensions.PurgeAuditJob);
        (await purge.Run(provider, time.GetUtcNow(), TestContext.Current.CancellationToken)).ShouldBe(1);

        store.AuditRows.ShouldHaveSingleItem().Target.ShouldBe("reactions:2");
    }

    [Fact]
    public async Task A_failed_poll_keeps_the_last_values_and_ready_fails_after_three_in_a_row()
    {
        var knobs = new Knobs();
        var time = new FakeTimeProvider(Services.Start);
        await using var provider = Services.With(knobs, time).BuildServiceProvider();
        var host = provider.GetRequiredService<OperationsHost>();
        (await Report(provider, TarolHealthTags.Ready)).Single().Value.ShouldBe((HealthStatus.Unhealthy, "no operations snapshot yet"));
        knobs.Values[OperationsSignals.ReactionsOldestDue] = 12;
        (await host.PollOnce(Ct)).LastSuccess.ShouldBe(Services.Start);

        knobs.Fail = true;
        for (var i = 1; i <= 3; i++)
        {
            (await Report(provider, TarolHealthTags.Ready)).Single().Value.Status.ShouldBe(HealthStatus.Healthy, $"after {i - 1} failed polls");
            time.Advance(TimeSpan.FromSeconds(15));
            var snapshot = await host.PollOnce(Ct);
            (snapshot.Failures, snapshot.LastSuccess, snapshot.Max(OperationsSignals.ReactionsOldestDue)).ShouldBe((i, Services.Start, 12));
        }
        var ready = (await Report(provider, TarolHealthTags.Ready)).Single().Value;
        ready.Status.ShouldBe(HealthStatus.Unhealthy);
        ready.Description!.ShouldContain("3 polls in a row failed");
        (await Report(provider, TarolHealthTags.Live)).Single().Value.Status.ShouldBe(HealthStatus.Healthy);

        knobs.Fail = false;
        (await host.PollOnce(Ct)).Failures.ShouldBe(0);
        (await Report(provider, TarolHealthTags.Ready)).Single().Value.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_readiness_problem_a_package_reports_fails_ready()
    {
        var knobs = new Knobs();
        var services = Services.With(knobs, new FakeTimeProvider(Services.Start));
        var listening = false;
        services.AddSingleton(new ReadinessCheck("checkpoint-listen", _ => listening ? null : "the LISTEN connection is down"));
        await using var provider = services.BuildServiceProvider();
        _ = await provider.GetRequiredService<OperationsHost>().PollOnce(Ct);

        (await Report(provider, TarolHealthTags.Ready)).Single().Value.ShouldBe((HealthStatus.Unhealthy, "checkpoint-listen: the LISTEN connection is down"));
        listening = true;
        (await Report(provider, TarolHealthTags.Ready)).Single().Value.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task The_gauges_publish_the_snapshot_with_its_tags_and_a_scrape_reads_no_signal()
    {
        using var meters = new Measurements();
        var knobs = new Knobs();
        var services = Services.With(knobs, new FakeTimeProvider(Services.Start));
        services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory>(meters);
        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<OperationsHost>();
        knobs.Values[OperationsSignals.ReactionsOldestDue] = 42;
        knobs.Values[OperationsSignals.ProjectionsLag] = 7;
        _ = await host.PollOnce(Ct);
        var reads = knobs.Reads;

        meters.Collect();
        meters.Collect();

        knobs.Reads.ShouldBe(reads);                                                              // the scrape reads the snapshot
        var oldest = meters.Of(OperationsSignals.ReactionsOldestDue);
        oldest.Select(m => m.Value).ShouldBe([42, 42]);
        oldest.ShouldAllBe(m => m.Unit == "s" && m.Meter == OperationsSignals.Meter && m.Tags.Count == 0);
        var lag = meters.Of(OperationsSignals.ProjectionsLag)[0];
        (lag.Value, lag.Tags["tarol.projection"]).ShouldBe((7, (object?)"auction-list"));
    }

    [Fact]
    public async Task The_host_polls_on_its_clock()
    {
        var knobs = new Knobs();
        var time = new FakeTimeProvider(Services.Start);
        await using var provider = Services.With(knobs, time, o => o.PollEvery = TimeSpan.FromSeconds(15)).BuildServiceProvider();
        var host = provider.GetRequiredService<OperationsHost>();
        await host.StartAsync(Ct);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (host.Snapshot.LastPoll is null && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, Ct);
        host.Snapshot.LastPoll.ShouldBe(Services.Start);

        while (host.Snapshot.LastPoll == Services.Start && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            time.Advance(TimeSpan.FromSeconds(5));                                                // smaller steps than the interval, and yield
            await Task.Delay(20, Ct);
        }
        await host.StopAsync(Ct);

        host.Snapshot.LastPoll!.Value.ShouldBeInRange(Services.Start.AddSeconds(15), Services.Start.AddSeconds(20));
    }

    [Fact]
    public async Task The_tarol_report_is_json_with_every_check()
    {
        var knobs = new Knobs();
        await using var provider = Services.With(knobs, new FakeTimeProvider(Services.Start)).BuildServiceProvider();
        knobs.Values[OperationsSignals.JobsOverdue] = 700;
        _ = await provider.GetRequiredService<OperationsHost>().PollOnce(Ct);
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains(TarolHealthTags.Tarol), Ct);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await TarolHealthEndpoints.WriteReport(context, report);

        using var json = JsonDocument.Parse(body.ToArray());
        json.RootElement.GetProperty("status").GetString().ShouldBe("Unhealthy");
        json.RootElement.GetProperty("checks").GetProperty("tarol-jobs").GetProperty("description").GetString().ShouldBe("most overdue job 700 s");
        json.RootElement.GetProperty("checks").EnumerateObject().Select(p => p.Name).Order()
            .ShouldBe(["tarol-dead-letters", "tarol-jobs", "tarol-projections", "tarol-reactions"]);
    }
}
