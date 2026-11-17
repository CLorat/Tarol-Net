using System.Globalization;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs;

/// <summary>
/// A job class (J-1): DI-resolved, run once per occurrence. It may read projections to <i>find</i> its targets, and runs a
/// change per target with <see cref="JobRun.Change{T}"/>: each change decides on its own aggregate and is keyed
/// (occurrence, target), so a re-run after a crash replays what committed and continues with the rest.
/// </summary>
/// <example>
/// <code>
/// public sealed class SettleEndedAuctions(IReadModels reads) : IJob
/// {
///     public async ValueTask&lt;Result&gt; Run(JobRun run, CancellationToken ct)
///     {
///         foreach (var a in await reads.Store&lt;AuctionRow&gt;().Ended(run.Occurrence.At, ct))
///             if ((await run.Change(new SettleAuction(a.Id), ct)).TryGetValue(out Violation? v))
///                 return v;                                   // retried; what committed replays
///         return Result.Success();                            // nothing to do is a success
///     }
/// }
/// </code>
/// </example>
/// <remarks>
/// The body runs again after a crash or a lost lease, so it sends no mail and calls no HTTP itself: it raises events whose
/// publications or effects do that (step 11). Return <see cref="Result.Success()"/> when there is nothing to do; a
/// <c>Conflict</c> is retried and, after the delivery policy's attempts, a dead job.
/// </remarks>
public interface IJob
{
    /// <summary>Runs one occurrence.</summary>
    /// <param name="run">The occurrence, its context, and the keyed change runner.</param>
    /// <param name="cancellationToken">Cancelled at shutdown or when the worker lost the row's lease.</param>
    /// <returns>Success, or the violation that decides retry or dead job (C-9).</returns>
    ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken);
}

/// <summary>One occurrence handed to an <see cref="IJob"/>: run its changes through <see cref="Change{T}"/> and <see cref="Create{T}"/>.</summary>
public sealed class JobRun
{
    private readonly ChangePipeline _pipeline;

    internal JobRun(JobOccurrence occurrence, int attempt, ChangePipeline pipeline, TimeProvider clock)
    {
        Occurrence = occurrence;
        Attempt = attempt;
        _pipeline = pipeline;
        Context = JobSet.Context(occurrence, clock);
    }

    /// <summary>The occurrence: when it was due (UTC), its zone and its tenant.</summary>
    public JobOccurrence Occurrence { get; }

    /// <summary>This occurrence's attempt (1 on the first run).</summary>
    public int Attempt { get; }

    /// <summary>The run's context: <c>Actor.System("Jobs.&lt;name&gt;")</c>, the row's tenant, a new correlation (one per run).</summary>
    public OperationContext Context { get; }

    /// <summary>Runs <paramref name="change"/> with the key (tenant, <c>job:&lt;name&gt;</c>, <c>occurrence/target</c>).</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="change">The change of one target.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome; a replay after a crash is a success with <see cref="ChangeOutcome.Replayed"/>.</returns>
    public ValueTask<Result<ChangeOutcome>> Change<T>(IChange<T> change, CancellationToken cancellationToken = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(change);
        return _pipeline.Run(change, Context, JobSet.Options(Occurrence, change.Target.Value.ToString("N", CultureInfo.InvariantCulture)), cancellationToken);
    }

    /// <summary>Runs <paramref name="creation"/> with the key (tenant, <c>job:&lt;name&gt;</c>, <c>occurrence/part</c>).</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="creation">The creation.</param>
    /// <param name="part">What makes this creation one of its kind in the occurrence (e.g. the source's id): a creation has no target yet.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome; a replay is a success with <see cref="ChangeOutcome.Replayed"/>.</returns>
    public ValueTask<Result<ChangeOutcome>> Create<T>(ICreation<T> creation, string part, CancellationToken cancellationToken = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        return _pipeline.Run(creation, Context, JobSet.Options(Occurrence, part), cancellationToken);
    }
}

/// <summary>A job's settings (declared with <c>Add&lt;TJob&gt;(cron, o => …)</c> or <c>Configure(name, o => …)</c>).</summary>
public sealed class JobOptions
{
    /// <summary>The stable name (rows, keys, <c>Tarol:Jobs:&lt;name&gt;</c>); default: the job's type name.</summary>
    public string? Name { get; set; }

    /// <summary>The IANA zone the cron schedule is read in; default <c>"UTC"</c>.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>What runs after downtime (J-5); default <see cref="Changes.Missed.Latest"/>.</summary>
    public Missed Missed { get; set; } = Missed.Latest;

    /// <summary>With <see cref="Changes.Missed.All"/>: the most occurrences one claim runs; the rest follow in the next claims (default 100).</summary>
    public int MaxCatchUp { get; set; } = 100;

    /// <summary>With <see cref="Changes.Missed.Skip"/>: how late the newest occurrence may still run (default 1 minute).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>One row and one occurrence per tenant of the application's <see cref="ITenantDirectory"/> (J-6).</summary>
    public bool PerTenant { get; set; }

    /// <summary>Whether the job runs (configuration: <c>Tarol:Jobs:&lt;name&gt;:Enabled</c>); a disabled row keeps its place.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Attempts and backoff of a failing occurrence (C-9, S-13): 10 attempts, 1 s … 5 min; then a dead job.</summary>
    public DeliveryPolicy Policy { get; set; } = new();

    internal void Validate(string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCatchUp, 1, name + "." + nameof(MaxCatchUp));
        ArgumentOutOfRangeException.ThrowIfLessThan(Grace, TimeSpan.Zero, name + "." + nameof(Grace));
        ArgumentNullException.ThrowIfNull(Policy, name + "." + nameof(Policy));
        ArgumentException.ThrowIfNullOrWhiteSpace(TimeZone, name + "." + nameof(TimeZone));
        if (!Enum.IsDefined(Missed))
            throw new ArgumentOutOfRangeException(name + "." + nameof(Missed), Missed, "Not a Missed value.");
    }
}

/// <summary>The job host's settings (<c>AddJobs(jobs, o => …)</c>).</summary>
public sealed class JobHostOptions
{
    /// <summary>Claim loops per process; each holds one row at a time (default 2).</summary>
    public int Workers { get; set; } = 2;

    /// <summary>How long a loop waits when nothing is due (default 1 s: fire latency p50 0.5 s, p99 1 s, jobs.md §7).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a claim holds a row without a heartbeat; the heartbeat extends it every lease/3 (default 1 minute).</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the rows of per-tenant jobs are synced with the <see cref="ITenantDirectory"/> (default 1 hour).</summary>
    public TimeSpan TenantSyncEvery { get; set; } = TimeSpan.FromHours(1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Workers, 1, nameof(Workers));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(Lease, TimeSpan.FromSeconds(3), nameof(Lease));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TenantSyncEvery, TimeSpan.Zero, nameof(TenantSyncEvery));
    }
}

/// <summary>
/// The application's tenants, for jobs with <see cref="JobOptions.PerTenant"/> (J-6). Tarol has no tenant registry; register
/// yours (<c>services.AddSingleton&lt;ITenantDirectory, MyTenants&gt;()</c>). The host syncs the rows at startup and every
/// <see cref="JobHostOptions.TenantSyncEvery"/>: a new tenant starts at its next occurrence, a removed one's row is disabled.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>Every tenant that has the per-tenant jobs.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tenants.</returns>
    ValueTask<IReadOnlyList<Id<ITenant>>> Tenants(CancellationToken cancellationToken);
}

/// <summary>What one occurrence's run did (<c>JobTest.Runs</c>).</summary>
/// <param name="Job">The job.</param>
/// <param name="Tenant">The row's tenant.</param>
/// <param name="Occurrence">The occurrence (UTC).</param>
/// <param name="Attempt">Its attempt.</param>
/// <param name="Verdict">Done, Retry or Bury (after the policy's attempts a Retry becomes Bury).</param>
/// <param name="Reason">Why it failed.</param>
/// <param name="Redrive">Whether it was a re-drive of a dead job.</param>
public sealed record JobAttempt(string Job, Option<Id<ITenant>> Tenant, DateTimeOffset Occurrence, int Attempt, Verdict Verdict, Option<string> Reason,
    bool Redrive);
