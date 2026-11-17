using System.Globalization;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Probe.Jobs;

/// <summary>
/// A worker process of the chaos runs: <c>worker &lt;A|Anokey|B|D&gt; &lt;schema&gt; &lt;name&gt; &lt;jobs&gt;</c>. Runs until killed.
/// PROBE_CRASH_EVERY=n: <c>Environment.FailFast</c> right after the commit of every n-th run of this process (before the cursor
/// moves or the row completes). PROBE_WORK_MS: the body waits that long before its commit. PROBE_LEASE_MS: the lease.
/// </summary>
internal static class Worker
{
    private static int _runs;
    private static int CrashEvery => int.TryParse(Environment.GetEnvironmentVariable("PROBE_CRASH_EVERY"), out var n) ? n : 0;
    private static int WorkMs => int.TryParse(Environment.GetEnvironmentVariable("PROBE_WORK_MS"), out var n) ? n : 0;
    public static TimeSpan Lease => TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("PROBE_LEASE_MS"), out var n) ? n : 3000);

    /// <summary>Job rows per claim (PROBE_BATCH, default 1): a claim runs its rows one after another, so one job's catch-up delays the others it holds.</summary>
    private static int Batch => int.TryParse(Environment.GetEnvironmentVariable("PROBE_BATCH"), out var n) ? n : 1;

    public static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string JobName(int k) => "job-" + k.ToString(CultureInfo.InvariantCulture);

    /// <summary>The job body of variants A and B: log the attempt, (work), commit the occurrence with its key.</summary>
    public static Func<JobRun, CancellationToken, Task<Result<ChangeOutcome>>> Body(string schema, ChangePipeline pipeline) => async (run, ct) =>
    {
        var stream = Lab.StreamOf(Lab.TenantA, run.Name);
        await Lab.LogRun(schema, run.Occurrence, stream.ToString());
        if (WorkMs > 0) await Task.Delay(WorkMs, ct);
        var r = await pipeline.Run(new RecordOccurrence((Id<Tally>)stream, run.Occurrence, Lab.Me), run.Context, new ChangeOptions(run.Key), ct);
        AfterCommit();
        return r;
    };

    public static void AfterCommit()
    {
        if (CrashEvery > 0 && Interlocked.Increment(ref _runs) % CrashEvery == 0)
            Environment.FailFast("probe: crash after the commit, before the cursor moves");
    }

    public static Dictionary<string, JobSpec> Specs(int jobs, Func<JobRun, CancellationToken, Task<Result<ChangeOutcome>>> body)
        => Enumerable.Range(0, jobs).Select(k => new JobSpec(JobName(k), new IntervalSchedule(TimeSpan.FromSeconds(1), Anchor), TimeZoneInfo.Utc, Missed.All, body)
        { UseKey = true }).ToDictionary(s => s.Name);

    public static async Task<int> Run(string variant, string schema, string name, int jobs)
    {
        Lab.Me = name + ":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var time = TimeProvider.System;
        if (variant == "D")
        {
            var set = GeneratedReactions.Set;
            var store = new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), time, Lab.StoreOptions(schema, set));
            var pipeline = new ChangePipeline(store, time);
            var rows = new LoggingRows(set, schema, WorkMs);
            var options = new ReactionOptions { Lease = Lease, PollInterval = TimeSpan.FromMilliseconds(100), Batch = 16 };
            var loops = Enumerable.Range(0, 2).Select(_ => new ReactionWorker(store.Outbox, rows, pipeline, time, options).Run(stop.Token)).ToArray();
            await Task.WhenAll(loops);
        }
        else
        {
            var store = new NpgsqlAggregateStore(Lab.Db, Lab.Codec(), time, Lab.StoreOptions(schema));
            var pipeline = new ChangePipeline(store, time);
            var specs = Specs(jobs, Body(schema, pipeline));
            if (variant == "Anokey")
                specs = specs.Values.Select(s => s with { UseKey = false }).ToDictionary(s => s.Name);
            var poll = TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("PROBE_POLL_MS"), out var p) ? p : 100);
            if (variant == "B")
                await new Leader(Lab.Db, schema, specs, time).Run(poll, stop.Token);
            else
            {
                var loops = Enumerable.Range(0, 2).Select(_ => new JobRunner(new NpgsqlJobRows(Lab.Db, schema, time), specs, time) { Lease = Lease, Batch = Batch }.Run(poll, stop.Token)).ToArray();
                await Task.WhenAll(loops);
            }
        }
        return 0;
    }
}

/// <summary>Variant D's row handler: the generated reactions, plus the attempt log, the work and the crash of the probe.</summary>
internal sealed class LoggingRows(IOutboxRowHandler inner, string schema, int workMs) : IOutboxRowHandler
{
    public IReadOnlyList<string> Names => inner.Names;
    public string ActivityName(string reaction) => inner.ActivityName(reaction);
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => inner.ReactionsTo(domainEvent);
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => inner.DueAt(domainEvent, reaction);
    public bool IsScheduled(string reaction) => inner.IsScheduled(reaction);

    public async ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken)
    {
        var job = reaction == nameof(RunScheduled) && recorded.Event.Event is Schedule.Due due;
        if (job)
        {
            await Lab.LogRun(schema, ((Schedule.Due)recorded.Event.Event).At, recorded.StreamId.ToString());
            if (workMs > 0) await Task.Delay(workMs, cancellationToken);
        }
        var r = await inner.Run(pipeline, reaction, recorded, cancellationToken);
        if (job) Worker.AfterCommit();
        return r;
    }
}
