using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs;

/// <summary>
/// The hosted service of jobs (J-8), in the worker process next to <c>ReactionHost</c>: at start it syncs the job rows with the
/// declared jobs (new rows start at their next occurrence, a changed schedule recomputes the cursor, removed jobs are
/// disabled), then runs <see cref="JobHostOptions.Workers"/> claim loops and, with per-tenant jobs, re-syncs the tenants every
/// <see cref="JobHostOptions.TenantSyncEvery"/>. Any number of processes may run it: each occurrence runs once.
/// </summary>
public sealed partial class JobHost : BackgroundService
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> of the job runs: one activity per occurrence, <c>job &lt;name&gt;</c>.</summary>
    public const string ActivitySourceName = "Tarol.Jobs";

    /// <summary>The name of the job host's meter (step 15): <c>tarol.jobs.occurrences</c>, <c>.duration</c>, <c>.lateness</c>.</summary>
    public const string MeterName = "Tarol.Jobs";

    private readonly IJobRows _rows;
    private readonly JobRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly JobHostOptions _options;
    private readonly ITenantDirectory? _tenants;
    private readonly ILogger _log;
    private readonly JobInstruments _instruments;

    internal JobHost(IJobRows rows, JobRegistry registry, IServiceProvider services, TimeProvider time, JobHostOptions options, ITenantDirectory? tenants, ILogger? logger)
    {
        if (registry.PerTenant && tenants is null)
            throw new InvalidOperationException("A job is PerTenant, but no ITenantDirectory is registered: services.AddSingleton<ITenantDirectory, YourTenants>().");
        _rows = rows;
        _registry = registry;
        _services = services;
        _time = time;
        _options = options;
        _tenants = tenants;
        _log = logger ?? NullLogger.Instance;
        _instruments = JobInstruments.For(services.GetService<IMeterFactory>());
    }

    /// <summary>The names of the declared jobs (generated entries, job classes and system jobs), after the configuration override.</summary>
    public IReadOnlyList<string> Jobs => [.. _registry.All.Select(static j => j.Name)];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await Sync(_registry, _rows, _tenants, _time, _log, stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                SyncFailed(_log, e);                                                    // e.g. the database is not up yet
                try
                {
                    await Task.Delay(_options.PollInterval * 5, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        var loops = new List<Task>(_options.Workers + 1);
        for (var i = 0; i < _options.Workers; i++)
            loops.Add(new JobWorker(_rows, _registry, _services, _time, _options, _log, _instruments).Run(stoppingToken));
        if (_registry.PerTenant)
            loops.Add(SyncTenants(stoppingToken));
        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task SyncTenants(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.TenantSyncEvery, _time, stoppingToken).ConfigureAwait(false);
                _ = await Sync(_registry, _rows, _tenants, _time, _log, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                SyncFailed(_log, e);
            }
        }
    }

    /// <summary>Makes the rows match the jobs: one per tenant-less job, one per (tenant, job) of a per-tenant job.</summary>
    internal static async Task<IReadOnlyList<JobSyncChange>> Sync(JobRegistry registry, IJobRows rows, ITenantDirectory? tenants, TimeProvider time, ILogger log,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        IReadOnlyList<Id<ITenant>> all = registry.PerTenant && tenants is not null ? await tenants.Tenants(cancellationToken).ConfigureAwait(false) : [];
        var declared = new List<JobSchedule>();
        foreach (var job in registry.All)
        {
            var first = job.Schedule.After(now, job.Zone);
            if (job.Options.PerTenant)
                declared.AddRange(all.Distinct().Select(t => new JobSchedule(job.Name, t, job.Stored, job.Options.Enabled, first)));
            else
                declared.Add(new JobSchedule(job.Name, Option.None, job.Stored, job.Options.Enabled, first));
        }
        var changes = await rows.Sync(declared, cancellationToken).ConfigureAwait(false);
        var counts = new int[4];
        foreach (var change in changes)
        {
            counts[(int)change.Kind]++;
            if (change.Kind != JobSyncKind.Rescheduled || !registry.TryGet(change.Name, out var job))
                continue;
            var next = job.Schedule.After(now, job.Zone);
            Rescheduled(log, change.Name, job.Stored, next);
        }
        if (changes.Count > 0)
            Synced(log, counts[(int)JobSyncKind.Added], counts[(int)JobSyncKind.Rescheduled], counts[(int)JobSyncKind.Enabled], counts[(int)JobSyncKind.Disabled]);
        return changes;
    }

    [LoggerMessage(EventId = 14004, Level = LogLevel.Information, Message = "Job {Job} has a new schedule '{Schedule}'; its next occurrence is {Next:O} (none of the old schedule runs)")]
    private static partial void Rescheduled(ILogger logger, string job, string schedule, DateTimeOffset next);

    [LoggerMessage(EventId = 14006, Level = LogLevel.Information, Message = "Job rows synced: {Added} added, {Rescheduled} rescheduled, {Enabled} enabled, {Disabled} disabled")]
    private static partial void Synced(ILogger logger, int added, int rescheduled, int enabled, int disabled);

    [LoggerMessage(EventId = 14009, Level = LogLevel.Error, Message = "Syncing the job rows failed; trying again")]
    private static partial void SyncFailed(ILogger logger, Exception exception);
}

/// <summary>Registers the job host on the <see cref="TarolBuilder"/>.</summary>
public static class JobServiceCollectionExtensions
{
    /// <summary>
    /// Runs the declared jobs in this process (the worker): <c>.AddJobs(j => j.Add(GeneratedJobs.Set).Add&lt;Report&gt;("0 6 * * MON"))</c>.
    /// Needs a store with job rows (<c>UseInMemoryStore</c>, <c>UseNpgsqlStore</c>). Validates the jobs and the configuration
    /// override (<c>Tarol:Jobs:&lt;name&gt;:Cron</c>, <c>:TimeZone</c>, <c>:Enabled</c>) when the host is resolved, and adds the
    /// system jobs: <c>tarol.purge-keys</c> when the store purges keys, and every registered <see cref="MaintenanceTask"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="jobs">Declares the jobs.</param>
    /// <param name="configure">Workers, poll interval, lease, tenant sync.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public static TarolBuilder AddJobs(this TarolBuilder builder, Action<JobsBuilder> jobs, Action<JobHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(jobs);
        var services = builder.Services;
        if (services.Any(static d => d.ServiceType == typeof(JobsBuilder)))
            throw new InvalidOperationException("AddJobs was called twice; declare every job in one call (JobSet.Combine for several assemblies).");
        var declared = new JobsBuilder(services);
        jobs(declared);
        var options = new JobHostOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(declared);
        services.AddSingleton(options);
        services.AddSingleton<IMaintenanceHost, Marker>();
        services.AddSingleton(static provider => provider.GetRequiredService<JobsBuilder>().Build(provider));
        services.AddSingleton(static provider => new JobHost(provider.GetRequiredService<IJobRows>(), provider.GetRequiredService<JobRegistry>(), provider,
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<JobHostOptions>(), provider.GetService<ITenantDirectory>(),
            provider.GetService<ILogger<JobHost>>()));
        services.AddHostedService(static provider => provider.GetRequiredService<JobHost>());
        return builder;
    }

    private sealed class Marker : IMaintenanceHost;
}
