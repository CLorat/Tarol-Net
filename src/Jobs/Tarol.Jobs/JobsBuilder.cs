using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.Sagas;

namespace Tarol.Jobs;

/// <summary>
/// Declares the jobs of an application (<c>AddJobs(j => j.Add(GeneratedJobs.Set).Add&lt;SettleEndedAuctions&gt;("15 2 * * *"))</c>):
/// scheduled change entries from generated sets, job classes with a schedule, and settings per name.
/// </summary>
public sealed class JobsBuilder
{
    private readonly List<JobSet> _sets = [];
    private readonly List<Declared> _classes = [];
    private readonly Dictionary<string, Action<JobOptions>> _configure = new(StringComparer.Ordinal);

    private readonly IServiceCollection? _services;

    internal JobsBuilder(IServiceCollection? services) => _services = services;

    /// <summary>Adds the scheduled change entries of an assembly (<c>GeneratedJobs.Set</c>) or a combined set.</summary>
    /// <param name="jobs">The set.</param>
    /// <returns>The builder.</returns>
    public JobsBuilder Add(JobSet jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _sets.Add(jobs);
        return this;
    }

    /// <summary>Adds the scheduled change entries of every module <c>AddTarol(modules)</c> registered (TN-8).</summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">The application was not registered with modules.</exception>
    public JobsBuilder AddModules()
        => Add(Tarol.Persistence.TarolModules.In(_services ?? throw new InvalidOperationException("This jobs builder has no services.")));

    /// <summary>Adds the scheduled change entries of the given modules (their <c>GeneratedJobs.Set</c>, TN-8).</summary>
    /// <param name="modules">The modules.</param>
    /// <returns>The builder.</returns>
    public JobsBuilder Add(IEnumerable<Tarol.Persistence.TarolModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules)
            if (module.Part<JobSet>() is { } jobs)
                _sets.Add(jobs);
        return this;
    }

    /// <summary>Adds a job class on a 5-field cron schedule.</summary>
    /// <typeparam name="TJob">The job: resolved from a scope per occurrence (registered scoped here unless it is registered).</typeparam>
    /// <param name="cron">The schedule, e.g. <c>"15 2 * * *"</c>; read in <see cref="JobOptions.TimeZone"/>.</param>
    /// <param name="configure">Name, zone, missed-run policy, per tenant, delivery policy.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="FormatException">The cron expression is invalid.</exception>
    public JobsBuilder Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(string cron, Action<JobOptions>? configure = null)
        where TJob : class, IJob
        => Add<TJob>(Cron.Parse(cron), configure);

    /// <summary>Adds a job class on a schedule (<see cref="Schedule.Every"/> for an interval).</summary>
    /// <typeparam name="TJob">The job: resolved from a scope per occurrence (registered scoped here unless it is registered).</typeparam>
    /// <param name="schedule">The schedule.</param>
    /// <param name="configure">Name, zone, missed-run policy, per tenant, delivery policy.</param>
    /// <returns>The builder.</returns>
    public JobsBuilder Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJob>(Schedule schedule, Action<JobOptions>? configure = null)
        where TJob : class, IJob
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var options = new JobOptions();
        configure?.Invoke(options);
        var name = options.Name ?? typeof(TJob).Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options.Validate(name);
        if (_services is null)
            throw new InvalidOperationException($"JobTest has no container: add an instance with test.Add(new {typeof(TJob).Name}(…), schedule).");
        _services.TryAddScoped<TJob>();
        _classes.Add(new Declared(name, schedule, options, static (services, occurrence, attempt, ct) => RunClass<TJob>(services, occurrence, attempt, ct)));
        return this;
    }

    /// <summary>Adds a job instance (<c>JobTest.Add</c>): it runs without a scope.</summary>
    internal JobsBuilder Add(IJob job, Schedule schedule, Action<JobOptions>? configure, ChangePipeline pipeline, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(schedule);
        var options = new JobOptions();
        configure?.Invoke(options);
        var name = options.Name ?? job.GetType().Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options.Validate(name);
        _classes.Add(new Declared(name, schedule, options, async (_, occurrence, attempt, ct) =>
            new BodyResult((await job.Run(new JobRun(occurrence, attempt, pipeline, clock), ct).ConfigureAwait(false)).TryGetValue(out Violation? violation) ? violation : null, null)));
        return this;
    }

    /// <summary>
    /// Changes the settings of a job by name (a generated entry's delivery policy or catch-up cap, a system job's zone):
    /// applied after the declaration, before the configuration override.
    /// </summary>
    /// <param name="name">The job's name.</param>
    /// <param name="configure">The change.</param>
    /// <returns>The builder.</returns>
    public JobsBuilder Configure(string name, Action<JobOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        _configure[name] = _configure.TryGetValue(name, out var before) ? before + configure : configure;
        return this;
    }

    internal JobRegistry Build(IServiceProvider services)
        => Build(services.GetRequiredService<ChangePipeline>(), services.GetRequiredService<TimeProvider>(), services.GetService<IIdempotencyKeyPurge>(),
            services.GetService<ReactionOptions>(), services.GetServices<MaintenanceTask>(), services.GetService<IConfiguration>());

    internal JobRegistry Build(ChangePipeline pipeline, TimeProvider clock, IIdempotencyKeyPurge? keys, ReactionOptions? reactions,
        IEnumerable<MaintenanceTask> maintenance, IConfiguration? configuration)
    {
        var declared = new List<Declared>();
        foreach (var definition in JobSet.Combine([.. _sets]).Definitions)
        {
            var set = _sets.First(s => s.Contains(definition.Name));
            var options = new JobOptions { TimeZone = definition.TimeZone, Missed = definition.Missed, PerTenant = definition.PerTenant };
            declared.Add(new Declared(definition.Name, Cron.Parse(definition.Cron), options, async (_, occurrence, _, ct) =>
                new BodyResult((await set.Run(pipeline, occurrence, clock, ct).ConfigureAwait(false)).TryGetValue(out Violation? violation) ? violation : null, null)));
        }
        declared.AddRange(_classes);
        if (keys is not null)
        {
            var retention = reactions?.KeyRetention ?? TimeSpan.FromDays(7);
            var batch = reactions?.PurgeBatch ?? 1000;
            declared.Add(System(new MaintenanceTask(JobRegistry.PurgeKeys, TimeSpan.FromHours(1), (_, at, ct) => Purge(keys, at - retention, batch, ct))));
        }
        foreach (var task in maintenance)
            declared.Add(System(task));

        var jobs = new List<JobRegistry.Job>(declared.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in declared)
        {
            if (!names.Add(job.Name))
                throw new InvalidOperationException($"The job '{job.Name}' is declared twice; job names are stored in rows and keys and must be unique.");
            if (_configure.TryGetValue(job.Name, out var change))
                change(job.Options);
            jobs.Add(JobRegistry.Resolve(job.Name, job.Schedule, job.Options, job.Body, configuration));
        }
        foreach (var name in _configure.Keys.Where(n => !names.Contains(n)))
            throw new InvalidOperationException($"Configure(\"{name}\") names no declared job.");
        if (configuration?.GetSection(JobRegistry.Section) is { } section)
            foreach (var child in section.GetChildren().Where(c => !names.Contains(c.Key)))
                throw new InvalidOperationException($"{JobRegistry.Section}:{child.Key} configures no declared job (declared: {string.Join(", ", names.Order(StringComparer.Ordinal))}).");
        return new JobRegistry(jobs);
    }

    private static Declared System(MaintenanceTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new Declared(task.Name, Schedule.Every(task.Every), new JobOptions(), async (services, occurrence, _, ct) =>
        {
            return new BodyResult(null, await task.Run(services, occurrence.At, ct).ConfigureAwait(false));
        });
    }

    private static async Task<int> Purge(IIdempotencyKeyPurge keys, DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
    {
        var total = 0;
        for (int deleted; (deleted = await keys.PurgeKeys(olderThan, batch, cancellationToken).ConfigureAwait(false)) > 0;)
        {
            total += deleted;
            if (deleted < batch)
                break;
        }
        return total;
    }

    private static async ValueTask<BodyResult> RunClass<TJob>(IServiceProvider services, JobOccurrence occurrence, int attempt, CancellationToken cancellationToken)
        where TJob : class, IJob
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var job = scope.ServiceProvider.GetRequiredService<TJob>();
            var run = new JobRun(occurrence, attempt, scope.ServiceProvider.GetRequiredService<ChangePipeline>(), scope.ServiceProvider.GetRequiredService<TimeProvider>());
            return new BodyResult((await job.Run(run, cancellationToken).ConfigureAwait(false)).TryGetValue(out Violation? violation) ? violation : null, null);
        }
    }

    internal sealed record Declared(string Name, Schedule Schedule, JobOptions Options, JobBody Body);
}

/// <summary>Runs one occurrence.</summary>
internal delegate ValueTask<BodyResult> JobBody(IServiceProvider services, JobOccurrence occurrence, int attempt, CancellationToken cancellationToken);

/// <summary>What a body returned: <see cref="Violation"/> <see langword="null"/> = success; <see cref="Count"/> of a maintenance job (logged).</summary>
internal readonly record struct BodyResult(Violation? Violation, int? Count);

/// <summary>The resolved jobs of a host: declarations with the configuration override applied and validated (J-2).</summary>
internal sealed class JobRegistry
{
    public const string Section = "Tarol:Jobs";
    public const string PurgeKeys = "tarol.purge-keys";

    private readonly Dictionary<string, Job> _jobs;

    public JobRegistry(IReadOnlyList<Job> jobs)
    {
        _jobs = jobs.ToDictionary(static j => j.Name, StringComparer.Ordinal);
        All = jobs;
    }

    public IReadOnlyList<Job> All { get; }

    public bool PerTenant => All.Any(static j => j.Options.PerTenant);

    public bool TryGet(string name, [NotNullWhen(true)] out Job? job) => _jobs.TryGetValue(name, out job);

    /// <summary>One job after the override <c>Tarol:Jobs:&lt;name&gt;:Cron</c>, <c>:TimeZone</c>, <c>:Enabled</c>.</summary>
    public static Job Resolve(string name, Schedule schedule, JobOptions options, JobBody body, IConfiguration? configuration)
    {
        var key = Section + ":" + name;
        var section = configuration?.GetSection(key);
        if (section?["Cron"] is { } cron)
            schedule = Cron.TryParse(cron, out var parsed, out var error)
                ? parsed
                : throw new InvalidOperationException($"{key}:Cron '{cron}' is invalid: {error}.");
        if (section?["TimeZone"] is { } zoneId)
            options.TimeZone = zoneId;
        if (section?["Enabled"] is { } enabled)
            options.Enabled = bool.TryParse(enabled, out var on) ? on : throw new InvalidOperationException($"{key}:Enabled '{enabled}' is not true or false.");
        options.Validate(name);
        TimeZoneInfo zone;
        try
        {
            zone = options.TimeZone is "UTC" or "Etc/UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        }
        catch (TimeZoneNotFoundException e)
        {
            throw new InvalidOperationException($"The time zone '{options.TimeZone}' of the job '{name}' is unknown (an IANA id such as Europe/Vienna; the image needs tzdata).", e);
        }
        return new Job(name, schedule, zone, options, body);
    }

    internal sealed record Job(string Name, Schedule Schedule, TimeZoneInfo Zone, JobOptions Options, JobBody Body)
    {
        public string Stored => Schedule.Stored(Zone);
    }
}
