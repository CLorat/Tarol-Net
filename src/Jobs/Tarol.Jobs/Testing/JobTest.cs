using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs;

/// <summary>
/// Runs jobs on a standing clock (docs/architecture/jobs.md §8): the real runner over an <see cref="InMemoryAggregateStore"/>
/// and its job rows. <see cref="Advance"/> steps the clock through every due time (cursors and retry backoffs) up to the
/// target and runs what is due at each step, so a nightly job advanced by three days runs as the missed-run policy says,
/// and the reactions the jobs cause are drained after every run.
/// </summary>
/// <example>
/// <code>
/// var test = JobTest.At(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), GeneratedJobs.Set);
/// await test.Advance(TimeSpan.FromHours(2));          // 02:00 runs once; the cursor is at tomorrow 02:00
/// Assert.Single(test.Runs);
/// await test.Advance(TimeSpan.FromDays(3));           // Latest: one run for the three missed nights
/// </code>
/// </example>
/// <remarks>
/// Runs are instantaneous on the standing clock, so leases never pass during a run. Job classes are added as instances
/// (<see cref="Add(IJob, string, Action{JobOptions})"/>) with their fakes; there is no container. Per-tenant rows follow
/// <see cref="WithTenants"/>; call <see cref="Sync"/> after changing tenants or configuration, as a restarted host would.
/// </remarks>
public sealed class JobTest
{
    private const int MaxSteps = 1_000_000;
    private readonly TestClock _clock;
    private readonly JobsBuilder _declared = new(null);
    private readonly ReactionWorker? _reactions;
    private readonly List<JobAttempt> _runs = [];
    private readonly List<ReactionAttempt> _reacted = [];
    private readonly TenantList _tenants = new();
    private IConfiguration? _configuration;
    private JobRegistry? _registry;
    private JobWorker? _worker;

    private JobTest(DateTimeOffset now, JobSet jobs, ReactionSet? reactions, JsonSerializerOptions? stateOptions)
    {
        _clock = new TestClock(now);
        Store = new InMemoryAggregateStore(_clock, new InMemoryStoreOptions { StateOptions = stateOptions, Reactions = reactions });
        Pipeline = new ChangePipeline(Store, _clock);
        _declared.Add(jobs);
        if (reactions is not null)
            _reactions = new ReactionWorker(Store.Outbox, reactions, Pipeline, _clock);
    }

    /// <summary>The store: <c>Recorded</c> holds every commit of the jobs (and of their reactions).</summary>
    public InMemoryAggregateStore Store { get; }

    /// <summary>The job rows (cursors, dead jobs).</summary>
    public IJobRows Rows => Store.Jobs;

    /// <summary>The pipeline the jobs run through.</summary>
    public ChangePipeline Pipeline { get; }

    /// <summary>The standing clock.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>The clock's time.</summary>
    public DateTimeOffset Now => _clock.Now;

    /// <summary>Every occurrence's run so far, in order.</summary>
    public IReadOnlyList<JobAttempt> Runs => _runs;

    /// <summary>Every reaction the jobs' commits caused (with a <see cref="ReactionSet"/>).</summary>
    public IReadOnlyList<ReactionAttempt> Reactions => _reacted;

    /// <summary>A test at <paramref name="now"/>: the rows are synced at the first <see cref="Advance"/>, so each job starts at its first occurrence after now.</summary>
    /// <param name="now">The start (whole seconds are not required; occurrences are).</param>
    /// <param name="jobs">The scheduled change entries (<c>GeneratedJobs.Set</c>, or <see cref="JobSet.Empty"/>).</param>
    /// <param name="configure">More declarations: <c>Configure(name, …)</c> (job classes go through <see cref="Add(IJob, string, Action{JobOptions})"/>).</param>
    /// <param name="reactions">The reactions to drain after every run.</param>
    /// <param name="stateOptions">JSON options for the stored state, as the production store has them.</param>
    /// <returns>The test.</returns>
    public static JobTest At(DateTimeOffset now, JobSet jobs, Action<JobsBuilder>? configure = null, ReactionSet? reactions = null,
        JsonSerializerOptions? stateOptions = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var test = new JobTest(now, jobs, reactions, stateOptions);
        configure?.Invoke(test._declared);
        return test;
    }

    /// <summary>Adds a job instance on a cron schedule (before the first <see cref="Advance"/>).</summary>
    /// <param name="job">The job with its fakes.</param>
    /// <param name="cron">The schedule.</param>
    /// <param name="configure">Name (default: the type name), zone, policies.</param>
    /// <returns>The test.</returns>
    public JobTest Add(IJob job, string cron, Action<JobOptions>? configure = null) => Add(job, Jobs.Cron.Parse(cron), configure);

    /// <summary>Adds a job instance on a schedule (before the first <see cref="Advance"/>).</summary>
    /// <param name="job">The job with its fakes.</param>
    /// <param name="schedule">The schedule.</param>
    /// <param name="configure">Name (default: the type name), zone, policies.</param>
    /// <returns>The test.</returns>
    public JobTest Add(IJob job, Schedule schedule, Action<JobOptions>? configure = null)
    {
        ThrowIfStarted();
        _ = _declared.Add(job, schedule, configure, Pipeline, _clock);
        return this;
    }

    /// <summary>The configuration override (<c>Tarol:Jobs:&lt;name&gt;:Cron</c>, <c>:TimeZone</c>, <c>:Enabled</c>); takes effect at the next <see cref="Sync"/>.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The test.</returns>
    public JobTest WithConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _registry = null;
        return this;
    }

    /// <summary>The tenants of per-tenant jobs; takes effect at the next <see cref="Sync"/>.</summary>
    /// <param name="tenants">The tenants.</param>
    /// <returns>The test.</returns>
    public JobTest WithTenants(params Id<ITenant>[] tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        _tenants.All = [.. tenants];
        return this;
    }

    /// <summary>Syncs the rows as a starting (or restarting) host does: new rows, changed schedules, disabled jobs, tenants.</summary>
    /// <returns>What changed.</returns>
    /// <exception cref="InvalidOperationException">A job, a cron override or a zone is invalid (as the host fails at startup).</exception>
    public Task<IReadOnlyList<JobSyncChange>> Sync()
    {
        _registry = _declared.Build(Pipeline, _clock, null, null, [], _configuration);
        _worker = new JobWorker(Store.Jobs, _registry, EmptyServices.Instance, _clock, new JobHostOptions(), NullLogger.Instance);
        return JobHost.Sync(_registry, Store.Jobs, _tenants, _clock, NullLogger.Instance, CancellationToken.None);
    }

    /// <summary>Moves the clock by <paramref name="delta"/>, stopping at every due time on the way and running what is due.</summary>
    /// <param name="delta">How far.</param>
    /// <returns>The runs of this advance.</returns>
    public Task<IReadOnlyList<JobAttempt>> Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        return AdvanceTo(_clock.Now + delta);
    }

    /// <summary>Moves the clock to <paramref name="target"/>, stopping at every due time on the way and running what is due.</summary>
    /// <param name="target">Where to; not before <see cref="Now"/>.</param>
    /// <returns>The runs of this advance.</returns>
    public async Task<IReadOnlyList<JobAttempt>> AdvanceTo(DateTimeOffset target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(target, _clock.Now);
        if (_registry is null)
            _ = await Sync().ConfigureAwait(false);
        var before = _runs.Count;
        for (var step = 0; ; step++)
        {
            if (step > MaxSteps)
                throw new InvalidOperationException($"More than {MaxSteps:N0} steps up to {target:O}: a schedule that fires every second over a long advance?");
            await RunDue().ConfigureAwait(false);
            if (await NextDue().ConfigureAwait(false) is not { } next || next > target)
                break;
            _clock.Now = next;
        }
        _clock.Now = target;
        await RunDue().ConfigureAwait(false);
        return _runs[before..];
    }

    /// <summary>The dead jobs.</summary>
    /// <returns>Them, oldest first.</returns>
    public Task<IReadOnlyList<DeadJob>> DeadJobs() => Store.Jobs.DeadJobs(1000, CancellationToken.None);

    /// <summary>Re-drives a dead job: it runs at the next <see cref="Advance"/> (<c>Advance(TimeSpan.Zero)</c> runs it now).</summary>
    /// <param name="id">The dead job.</param>
    /// <returns><see langword="false"/> when there is none.</returns>
    public Task<bool> Redrive(long id) => Store.Jobs.Redrive(id, CancellationToken.None);

    /// <summary>The rows with their cursors.</summary>
    /// <returns>The rows.</returns>
    public Task<IReadOnlyList<JobState>> JobRows() => Store.Jobs.Rows(CancellationToken.None);

    private async Task RunDue()
    {
        for (var claims = 0; ; claims++)
        {
            if (claims > MaxSteps)
                throw new InvalidOperationException("A job row is claimable again at once forever: a job that never leaves its occurrence?");
            if (!(await _worker!.RunOnce(CancellationToken.None).ConfigureAwait(false)).TryGetValue(out IReadOnlyList<JobAttempt>? attempts))
                return;
            _runs.AddRange(attempts);
            if (_reactions is not null)
                _reacted.AddRange(await _reactions.Drain(CancellationToken.None).ConfigureAwait(false));
        }
    }

    /// <summary>The earliest time a row is claimable after now: its cursor, or its retry backoff when later.</summary>
    private async Task<DateTimeOffset?> NextDue()
    {
        DateTimeOffset? next = null;
        foreach (var row in await Store.Jobs.Rows(CancellationToken.None).ConfigureAwait(false))
        {
            if (!row.Enabled)
                continue;
            var at = row.LeaseUntil.TryGetValue(out DateTimeOffset lease) && lease > row.NextAt ? lease : row.NextAt;
            if (at > _clock.Now && (next is null || at < next))
                next = at;
        }
        return next;
    }

    private void ThrowIfStarted()
    {
        if (_registry is not null)
            throw new InvalidOperationException("Add jobs before the first Advance or Sync.");
    }

    private sealed class TenantList : ITenantDirectory
    {
        public IReadOnlyList<Id<ITenant>> All { get; set; } = [];

        public ValueTask<IReadOnlyList<Id<ITenant>>> Tenants(CancellationToken cancellationToken) => ValueTask.FromResult(All);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
