using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs;

/// <summary>
/// One claim loop of the job host (jobs.md §2): claim <b>one</b> due row, pick its occurrences by the job's missed-run
/// policy, run each with the occurrence key, move the cursor after each, beat the lease every lease/3, and advance (release)
/// at the end. A failure retries the same occurrence with the delivery policy's backoff; after its attempts it is a dead job
/// and the cursor moves on.
/// </summary>
internal sealed partial class JobWorker(IJobRows rows, JobRegistry registry, IServiceProvider services, TimeProvider time, JobHostOptions options, ILogger log,
    JobInstruments? instruments = null)
{
    internal static readonly ActivitySource Source = new(JobHost.ActivitySourceName);
    private readonly JobInstruments _instruments = instruments ?? JobInstruments.Shared;

    /// <summary>Claims and runs one row; <see cref="Option.None"/> when nothing was due.</summary>
    public async Task<Option<IReadOnlyList<JobAttempt>>> RunOnce(CancellationToken cancellationToken)
    {
        if (!(await rows.Claim(options.Lease, cancellationToken).ConfigureAwait(false)).TryGetValue(out JobRow? row))
            return Option.None;
        return Option.Some(await Handle(row, cancellationToken).ConfigureAwait(false));
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!(await RunOnce(cancellationToken).ConfigureAwait(false)).TryGetValue(out IReadOnlyList<JobAttempt>? _))
                    await Task.Delay(options.PollInterval, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                LoopFailed(log, e);                                                     // the store is unreachable: the rows stay
                try
                {
                    await Task.Delay(options.PollInterval * 5, time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The occurrences a claim runs (J-5) and the cursor after them, from the cursor (due) and now.</summary>
    internal static (List<DateTimeOffset> Run, DateTimeOffset Cursor, bool Skipped) Pick(JobRegistry.Job job, DateTimeOffset cursor, DateTimeOffset now)
    {
        var (schedule, zone) = (job.Schedule, job.Zone);
        switch (job.Options.Missed)
        {
            case Missed.All:
                var run = new List<DateTimeOffset> { cursor };
                var next = schedule.After(cursor, zone);
                while (next <= now && run.Count < job.Options.MaxCatchUp)
                {
                    run.Add(next);
                    next = schedule.After(next, zone);
                }
                return (run, next, false);                                              // past the cap: the next claim goes on
            case Missed.Skip:
                var newest = schedule.Latest(cursor, now, zone);
                return now - newest <= job.Options.Grace ? ([newest], schedule.After(newest, zone), newest != cursor) : ([], schedule.After(newest, zone), true);
            default:
                var latest = schedule.Latest(cursor, now, zone);
                return ([latest], schedule.After(latest, zone), latest != cursor);
        }
    }

    internal async Task<IReadOnlyList<JobAttempt>> Handle(JobRow row, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(row.Name, out var job))
        {
            Undeclared(log, row.Name);                                                  // another version's job (a rolling deploy)
            _ = await rows.Retry(row, time.GetUtcNow() + options.Lease, "not declared in this process", CancellationToken.None).ConfigureAwait(false);
            return [];
        }
        var redrive = row.Redrive.TryGetValue(out long _);
        List<DateTimeOffset> run;
        DateTimeOffset cursor;
        if (redrive)
            (run, cursor) = ([row.Occurrence], row.NextAt);
        else
        {
            (run, cursor, var skipped) = Pick(job, row.NextAt, time.GetUtcNow());
            if (skipped)
            {
                MissedSkipped(log, job.Name, row.NextAt, run.Count == 0 ? cursor : run[0], job.Options.Missed);
                _instruments.Occurrences.Add(1, new TagList { { "tarol.job", job.Name }, { "tarol.outcome", "skipped" } });
            }
        }

        using var lease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = Beat(row, lease);
        var attempts = new List<JobAttempt>(run.Count);
        try
        {
            if (run.Count == 0)
            {
                if (!await rows.Advance(row, cursor, CancellationToken.None).ConfigureAwait(false))
                    LeaseLost(log, job.Name, row.Occurrence);
                return attempts;
            }
            for (var i = 0; i < run.Count; i++)
            {
                var occurrence = run[i];
                var attempt = i == 0 ? row.Attempts : 1;                                // Move started the next occurrence's attempts
                var following = redrive ? row.NextAt : i + 1 < run.Count ? run[i + 1] : cursor;
                _instruments.Lateness.Record(Math.Max(0, (time.GetUtcNow() - occurrence).TotalSeconds), new KeyValuePair<string, object?>("tarol.job", job.Name));
                var started = Stopwatch.GetTimestamp();
                var (verdict, reason) = await Execute(job, row, occurrence, attempt, lease.Token).ConfigureAwait(false);
                if (verdict is null)
                {
                    LeaseLost(log, job.Name, occurrence);                               // the heartbeat lost the claim: write nothing
                    return attempts;
                }
                if (verdict == Verdict.Retry && attempt >= job.Options.Policy.MaxAttempts)
                    verdict = Verdict.Bury;
                var tags = new TagList
                {
                    { "tarol.job", job.Name },
                    { "tarol.outcome", verdict switch { Verdict.Done => "ran", Verdict.Retry => "retried", _ => "dead" } },
                };
                _instruments.Occurrences.Add(1, tags);
                _instruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
                attempts.Add(new JobAttempt(job.Name, row.Tenant, occurrence, attempt, verdict.Value, reason is null ? Option.None : reason, redrive));

                // the occurrence has run: record its verdict even while shutting down, so it is not run again
                bool kept;
                if (verdict == Verdict.Done)
                    kept = i + 1 < run.Count
                        ? await rows.Move(row, following, CancellationToken.None).ConfigureAwait(false)
                        : await rows.Advance(row, following, CancellationToken.None).ConfigureAwait(false);
                else if (verdict == Verdict.Retry)
                {
                    var at = time.GetUtcNow() + job.Options.Policy.Backoff(HashCode.Combine(job.Name, row.Tenant), attempt);
                    kept = await rows.Retry(row, at, reason ?? "", CancellationToken.None).ConfigureAwait(false);
                    if (kept)
                        Retrying(log, job.Name, occurrence, attempt, at, reason);
                }
                else
                {
                    kept = await rows.Bury(row with { Occurrence = occurrence }, following, reason ?? "", CancellationToken.None).ConfigureAwait(false);
                    if (kept)
                        Buried(log, job.Name, occurrence, attempt, reason);
                }
                if (!kept)
                {
                    LeaseLost(log, job.Name, occurrence);
                    return attempts;
                }
                if (verdict != Verdict.Done || redrive)
                    return attempts;
            }
            return attempts;
        }
        finally
        {
            await lease.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    /// <summary>Runs one occurrence in its activity; a <see langword="null"/> verdict = the lease was lost while it ran.</summary>
    private async Task<(Verdict? Verdict, string? Reason)> Execute(JobRegistry.Job job, JobRow row, DateTimeOffset at, int attempt, CancellationToken lease)
    {
        using var activity = Source.StartActivity("job " + job.Name, ActivityKind.Internal);
        activity?.SetTag("tarol.job", job.Name);
        activity?.SetTag("tarol.occurrence", at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (row.Tenant.TryGetValue(out Id<ITenant> tenant))
            activity?.SetTag("tarol.tenant", tenant.ToString());
        activity?.SetTag("tarol.attempt", attempt);
        Verdict verdict;
        string? reason = null;
        try
        {
            var result = await job.Body(services, new JobOccurrence(job.Name, at, job.Zone, row.Tenant), attempt, lease).ConfigureAwait(false);
            if (result.Violation is { } violation)
            {
                verdict = Delivery.Classify(violation);
                reason = Describe(violation);
            }
            else
            {
                verdict = Verdict.Done;
                if (result.Count is { } count)
                    Maintained(log, job.Name, count);
            }
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            return (null, null);                                                        // shutdown or a lost lease: the row comes back
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            verdict = Verdict.Retry;
            reason = e.GetType().Name + ": " + e.Message;
        }
        activity?.SetTag("tarol.verdict", verdict.ToString());
        if (verdict != Verdict.Done)
            activity?.SetStatus(ActivityStatusCode.Error, reason);
        return (verdict, reason);
    }

    /// <summary>Extends the lease every lease/3 while the row runs; on a lost claim it cancels the run.</summary>
    private async Task Beat(JobRow row, CancellationTokenSource lease)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.Lease / 3, time, lease.Token).ConfigureAwait(false);
                if (!await rows.Extend(row, options.Lease, lease.Token).ConfigureAwait(false))
                {
                    await lease.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LoopFailed(log, e);                                                         // the lease may still hold; the claim decides
        }
    }

    private static string Describe(Violation violation) => violation switch
    {
        Error e => $"{e.Type} {e.Code}" + (e.Detail is null ? "" : ": " + e.Detail),
        Invalid i => "Invalid " + string.Join(", ", i.Errors.Select(static f => f.Field + " " + f.Code)),
        _ => violation.ToString(),
    };

    [LoggerMessage(EventId = 14001, Level = LogLevel.Warning, Message = "Job {Job} occurrence {Occurrence:O} failed at attempt {Attempt} and runs again at {RetryAt:O}: {Reason}")]
    private static partial void Retrying(ILogger logger, string job, DateTimeOffset occurrence, int attempt, DateTimeOffset retryAt, string? reason);

    [LoggerMessage(EventId = 14002, Level = LogLevel.Error, Message = "Job {Job} occurrence {Occurrence:O} was given up after attempt {Attempt} and is a dead job: {Reason}")]
    private static partial void Buried(ILogger logger, string job, DateTimeOffset occurrence, int attempt, string? reason);

    [LoggerMessage(EventId = 14003, Level = LogLevel.Warning, Message = "Job {Job} occurrence {Occurrence:O} outlived its lease; another worker owns the row")]
    private static partial void LeaseLost(ILogger logger, string job, DateTimeOffset occurrence);

    [LoggerMessage(EventId = 14005, Level = LogLevel.Information, Message = "Job {Job} skipped missed occurrences from {From:O}; it runs {Next:O} ({Policy})")]
    private static partial void MissedSkipped(ILogger logger, string job, DateTimeOffset from, DateTimeOffset next, Missed policy);

    [LoggerMessage(EventId = 14007, Level = LogLevel.Error, Message = "The job loop failed; it waits and claims again")]
    private static partial void LoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 14008, Level = LogLevel.Warning, Message = "Job row {Job} is not declared in this process; it is left to a process that declares it")]
    private static partial void Undeclared(ILogger logger, string job);

    [LoggerMessage(EventId = 14010, Level = LogLevel.Information, Message = "System job {Job} handled {Count} items")]
    private static partial void Maintained(ILogger logger, string job, int count);
}

/// <summary>The job host's instruments on the meter <see cref="JobHost.MeterName"/> (step 15, O-1).</summary>
internal sealed class JobInstruments
{
    private static readonly Meter SharedMeter = new(JobHost.MeterName);

    public static JobInstruments Shared { get; } = new(SharedMeter);

    private JobInstruments(Meter meter)
    {
        Occurrences = meter.CreateCounter<long>("tarol.jobs.occurrences", "{occurrence}", "Job occurrences by job and outcome (ran, retried, dead, skipped).");
        Duration = meter.CreateHistogram<double>("tarol.jobs.duration", "s", "Duration of a job occurrence.");
        Lateness = meter.CreateHistogram<double>("tarol.jobs.lateness", "s", "Start of an occurrence minus its scheduled time.");
    }

    public Counter<long> Occurrences { get; }

    public Histogram<double> Duration { get; }

    public Histogram<double> Lateness { get; }

    public static JobInstruments For(IMeterFactory? meters) => meters is null ? Shared : new(meters.Create(JobHost.MeterName));
}
