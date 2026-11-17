using System.ComponentModel;
using System.Globalization;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>What a job does with the occurrences that passed while nothing ran (downtime, a long run, a long backoff; J-5).</summary>
public enum Missed
{
    /// <summary>Runs the newest missed occurrence once (the default): a nightly job down for three days runs once.</summary>
    Latest,

    /// <summary>Runs every missed occurrence in order, at most <c>MaxCatchUp</c> (100) per claim; the rest in the next claims.</summary>
    All,

    /// <summary>Runs none of them, unless the newest is within the grace (1 minute): a late process still runs a just-due occurrence.</summary>
    Skip,
}

/// <summary>
/// On a static factory of a change: the change is a <b>scheduled job</b> (docs/architecture/jobs.md §3, J-1). At every
/// occurrence of <see cref="Cron"/> in <see cref="TimeZone"/>, exactly one worker of the cluster calls the factory and runs
/// the change through <see cref="ChangePipeline"/> as <c>Actor.System("Jobs.&lt;name&gt;")</c> with the key
/// (tenant, <c>job:&lt;name&gt;</c>, occurrence), so a crash or a frozen worker never commits it twice.
/// </summary>
/// <example>
/// <code>
/// public sealed partial record CloseDay(DateOnly Day) : ICreation&lt;DayBook&gt;
/// {
///     [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
///     public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));
///     public Result&lt;DayBook&gt; Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
/// }
/// </code>
/// </example>
/// <remarks>
/// The factory is <c>static</c>, returns its change and takes one <see cref="JobOccurrence"/> (TCHG009). It maps and does
/// nothing else; the pipeline validates the result. The schedule is 5-field Unix cron (minute hour day-of-month month
/// day-of-week; names such as <c>MON</c> and <c>JAN</c>; no seconds, no <c>L W # ?</c>); an invalid literal is TCHG010.
/// Configuration overrides it per name (<c>Tarol:Jobs:&lt;name&gt;:Cron</c>, <c>:TimeZone</c>, <c>:Enabled</c>; <c>Tarol.Jobs</c>).
/// </remarks>
/// <param name="cron">The 5-field cron schedule, e.g. <c>"0 2 * * *"</c> (02:00 every day).</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ScheduledAttribute(string cron) : Attribute
{
    /// <summary>The 5-field cron schedule.</summary>
    public string Cron { get; } = cron;

    /// <summary>The IANA time zone the schedule is read in (e.g. <c>"Europe/Vienna"</c>); default UTC. An unknown zone fails at startup.</summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// The job's stable name, stored in its rows and every occurrence's idempotency key; default: the change's type name. Pin it
    /// before renaming the change (a row of a name that is no longer declared is disabled). Unique per assembly (TCHG011) and
    /// across combined sets (<see cref="JobSet.Combine"/>).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>What runs after downtime; default <see cref="Changes.Missed.Latest"/>.</summary>
    public Missed Missed { get; init; }

    /// <summary>One row and one occurrence per tenant of the application's <c>ITenantDirectory</c> (J-6); default: one tenant-less row.</summary>
    public bool PerTenant { get; init; }
}

/// <summary>
/// One occurrence of a job: when it was due (whole seconds, UTC), its zone and its tenant. A factory reads
/// <see cref="Local"/> for the calendar day of the zone.
/// </summary>
public sealed record JobOccurrence
{
    /// <summary>Creates an occurrence.</summary>
    /// <param name="job">The job's name.</param>
    /// <param name="at">When it was due: a whole second (an occurrence must round-trip through the store unchanged, jobs.md §5 item 1).</param>
    /// <param name="zone">The zone the schedule is read in.</param>
    /// <param name="tenant">The row's tenant, or <see cref="Option.None"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="job"/> is empty, or <paramref name="at"/> is not a whole second.</exception>
    public JobOccurrence(string job, DateTimeOffset at, TimeZoneInfo zone, Option<Id<ITenant>> tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(job);
        ArgumentNullException.ThrowIfNull(zone);
        if (at.UtcTicks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException($"An occurrence is a whole second; {at:O} is not.", nameof(at));
        Job = job;
        At = at.ToUniversalTime();
        Zone = zone;
        Tenant = tenant;
    }

    /// <summary>The job's name.</summary>
    public string Job { get; }

    /// <summary>When the occurrence was due, in UTC.</summary>
    public DateTimeOffset At { get; }

    /// <summary>The zone the schedule is read in.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>The row's tenant (a tenant-less job has none).</summary>
    public Option<Id<ITenant>> Tenant { get; }

    /// <summary><see cref="At"/> in <see cref="Zone"/>, with the zone's offset at that instant.</summary>
    public DateTimeOffset Local => TimeZoneInfo.ConvertTime(At, Zone);

    /// <summary>The key text of the occurrence: <see cref="At"/> in UTC, round-trip format (<c>2026-11-16T02:00:00.0000000Z</c>).</summary>
    public string KeyText => At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>A declared job: what the job host makes rows from (generated for <see cref="ScheduledAttribute"/> factories).</summary>
/// <param name="Name">The stable name.</param>
/// <param name="Cron">The 5-field cron schedule as declared (configuration may override it).</param>
/// <param name="TimeZone">The IANA zone id as declared; <c>"UTC"</c> by default.</param>
/// <param name="Missed">The missed-run policy.</param>
/// <param name="PerTenant">One row per tenant of the directory.</param>
public sealed record JobDefinition(string Name, string Cron, string TimeZone, Missed Missed, bool PerTenant);

/// <summary>Runs one occurrence of a scheduled change entry: the factory, then <see cref="ChangePipeline"/> with the occurrence key.</summary>
/// <param name="pipeline">The pipeline.</param>
/// <param name="occurrence">The occurrence (its <see cref="JobOccurrence.Job"/> selects the factory).</param>
/// <param name="clock">The clock of the context's correlation id.</param>
/// <param name="cancellationToken">Cancels the run.</param>
/// <returns>The pipeline's outcome.</returns>
public delegate ValueTask<Result<ChangeOutcome>> JobRunner(ChangePipeline pipeline, JobOccurrence occurrence, TimeProvider clock,
    CancellationToken cancellationToken);

/// <summary>
/// The scheduled change entries of an application: their definitions and the runner. Each assembly with
/// <see cref="ScheduledAttribute"/> factories has a generated <c>GeneratedJobs.Set</c>; <see cref="Combine"/> joins several.
/// Pass it to <c>AddJobs</c> (<c>Tarol.Jobs</c>).
/// </summary>
public sealed class JobSet
{
    private readonly Dictionary<string, (JobDefinition Definition, JobRunner Runner)> _jobs;

    /// <summary>Creates the set of one assembly. Called by generated code.</summary>
    /// <param name="definitions">Every job of the assembly.</param>
    /// <param name="runner">The switch from the occurrence's job name to the factory and the pipeline.</param>
    /// <exception cref="ArgumentException">A name or a schedule is empty, or a name is listed twice.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JobSet(IReadOnlyList<JobDefinition> definitions, JobRunner runner)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(runner);
        _jobs = new(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(definitions));
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name, nameof(definitions));
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Cron, nameof(definitions));
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.TimeZone, nameof(definitions));
            if (!_jobs.TryAdd(definition.Name, (definition, runner)))
                throw new ArgumentException($"The job '{definition.Name}' is listed twice.", nameof(definitions));
        }
        Definitions = [.. definitions];
    }

    private JobSet(Dictionary<string, (JobDefinition, JobRunner)> jobs)
    {
        _jobs = jobs;
        Definitions = [.. jobs.Values.Select(static j => j.Item1)];
    }

    /// <summary>A set without jobs.</summary>
    public static JobSet Empty { get; } = new([], static (_, occurrence, _, _) =>
        throw new InvalidOperationException($"No scheduled change named '{occurrence.Job}' is registered."));

    /// <summary>Every job of the set.</summary>
    public IReadOnlyList<JobDefinition> Definitions { get; }

    /// <summary>Whether the set declares <paramref name="name"/>.</summary>
    /// <param name="name">A job name.</param>
    /// <returns><see langword="true"/> when declared.</returns>
    public bool Contains(string name) => _jobs.ContainsKey(name);

    /// <summary>Joins the sets of several assemblies into the one set of an application.</summary>
    /// <param name="sets">The sets.</param>
    /// <returns>The combined set.</returns>
    /// <exception cref="InvalidOperationException">A name is declared in two sets: names are stored in rows and keys, so they must be unique.</exception>
    public static JobSet Combine(params JobSet[] sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var jobs = new Dictionary<string, (JobDefinition, JobRunner)>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set, nameof(sets));
            foreach (var (name, job) in set._jobs)
                if (!jobs.TryAdd(name, job))
                    throw new InvalidOperationException($"The job '{name}' is declared in two assemblies; job names must be unique.");
        }
        return new(jobs);
    }

    /// <summary>Runs the occurrence's job through <paramref name="pipeline"/>.</summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="occurrence">The occurrence.</param>
    /// <param name="clock">The clock of the context's correlation id.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome; a replayed key is a success with <see cref="ChangeOutcome.Replayed"/>.</returns>
    /// <exception cref="InvalidOperationException">No job has that name.</exception>
    public ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, JobOccurrence occurrence, TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(clock);
        return _jobs.TryGetValue(occurrence.Job, out var job)
            ? job.Runner(pipeline, occurrence, clock, cancellationToken)
            : throw new InvalidOperationException($"No scheduled change named '{occurrence.Job}' is registered.");
    }

    /// <summary>
    /// The operation an occurrence runs in (docs/architecture/changes.md §5): <c>Actor.System("Jobs.&lt;name&gt;")</c>, the
    /// row's tenant, a new correlation, no causation. An internal entry: no principal, no permission check (R-6).
    /// </summary>
    /// <param name="occurrence">The occurrence.</param>
    /// <param name="clock">The clock of the correlation id.</param>
    /// <returns>The context.</returns>
    public static OperationContext Context(JobOccurrence occurrence, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(clock);
        return OperationContext.Start(Actor.System("Jobs." + occurrence.Job), occurrence.Tenant, clock);
    }

    /// <summary>
    /// The key of an occurrence's change: (tenant, <c>job:&lt;name&gt;</c>, occurrence UTC), and with <paramref name="part"/>
    /// (the target of one change of a fan-out) <c>occurrence/part</c>. Stored in the change's commit (C-8), so a re-run commits once.
    /// </summary>
    /// <param name="occurrence">The occurrence.</param>
    /// <param name="part">The part of a job that runs several changes per occurrence; <see langword="null"/> for the one change.</param>
    /// <returns>The key.</returns>
    public static IdempotencyKey Key(JobOccurrence occurrence, string? part = null)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (part is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(part);
        return new(occurrence.Tenant, "job:" + occurrence.Job, part is null ? occurrence.KeyText : occurrence.KeyText + "/" + part);
    }

    /// <summary>The options an occurrence's change runs with: its <see cref="Key"/>.</summary>
    /// <param name="occurrence">The occurrence.</param>
    /// <param name="part">The part, as for <see cref="Key"/>.</param>
    /// <returns>The options.</returns>
    public static ChangeOptions Options(JobOccurrence occurrence, string? part = null) => new(Key(occurrence, part));
}
