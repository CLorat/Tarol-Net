using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;

namespace Tarol.Operations;

/// <summary>Operations on the <see cref="TarolBuilder"/>.</summary>
public static class OperationsTarolBuilderExtensions
{
    /// <summary>The name of the dead-letter retention's system job (O-5).</summary>
    public const string PurgeDeadJob = "tarol.purge-dead";

    /// <summary>
    /// The operations of a Tarol process (docs/architecture/operations.md): the <see cref="OperationsHost"/> (snapshot and
    /// gauges), the health checks (<see cref="TarolHealthTags"/>: <c>tarol-live</c>, <c>tarol-ready</c>, and
    /// <c>tarol-reactions</c>, <c>tarol-dead-letters</c>, <c>tarol-jobs</c>, <c>tarol-projections</c> in the <c>tarol</c>
    /// group), the <see cref="IDeadLetterAdmin"/>, and with <see cref="OperationsOptions.DeadRetention"/> the system job
    /// <see cref="PurgeDeadJob"/>. Register it after the store and the other packages; map with <c>MapTarolHealth()</c> and
    /// <c>MapTarolAdmin(prefix, policy)</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Poll interval, thresholds, retention.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException"><c>AddOperations</c> was called before.</exception>
    public static TarolBuilder AddOperations(this TarolBuilder builder, Action<OperationsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (services.Any(static d => d.ServiceType == typeof(OperationsOptions)))
            throw new InvalidOperationException("AddOperations was called twice; configure everything in one call.");
        var options = new OperationsOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(static provider => new OperationsHost(provider.GetServices<OperationsSignal>(), provider,
            provider.GetRequiredService<OperationsOptions>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<OperationsHost>>(), provider.GetService<IMeterFactory>()));
        services.AddSingleton<IHostedService>(static provider => provider.GetRequiredService<OperationsHost>());
        services.AddSingleton<IDeadLetterAdmin>(static provider => new DeadLetterAdmin(provider.GetServices<IDeadLetterSource>(),
            provider.GetService<IReadModelAdmin>(), provider.GetService<ILogger<DeadLetterAdmin>>()));

        var t = options.Thresholds;
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration("tarol-live", static _ => new LiveCheck(), null, [TarolHealthTags.Live]))
            .Add(new HealthCheckRegistration("tarol-ready", static provider => new ReadyCheck(provider.GetRequiredService<OperationsHost>(),
                provider.GetServices<ReadinessCheck>(), provider, provider.GetRequiredService<OperationsOptions>()), null, [TarolHealthTags.Ready]))
            .Add(Backlog("tarol-reactions", BacklogCheck.Age(OperationsSignals.ReactionsOldestDue, t.ReactionsDegraded, t.ReactionsUnhealthy, "oldest due reaction")))
            .Add(Backlog("tarol-dead-letters", BacklogCheck.Dead(t.DeadDegraded)))
            .Add(Backlog("tarol-jobs", BacklogCheck.Age(OperationsSignals.JobsOverdue, t.JobsDegraded, t.JobsUnhealthy, "most overdue job")))
            .Add(Backlog("tarol-projections", BacklogCheck.Age(OperationsSignals.ProjectionsLag, t.LagDegraded, t.LagUnhealthy, "largest projection lag")));

        if (options.DeadRetention is { } retention)
            services.AddSingleton(new MaintenanceTask(PurgeDeadJob, TimeSpan.FromHours(1), async (provider, at, ct) =>
            {
                var total = 0;
                foreach (var source in provider.GetServices<IDeadLetterRetention>())
                    for (int deleted; (deleted = await source.Purge(at - retention, options.PurgeBatch, PurgeDeadJob, ct).ConfigureAwait(false)) > 0;)
                    {
                        total += deleted;
                        if (deleted < options.PurgeBatch)
                            break;
                    }
                return total;
            }));
        return builder;
    }

    private static HealthCheckRegistration Backlog(string name, Func<OperationsSnapshot, HealthCheckResult> judge)
        => new(name, provider => new BacklogCheck(provider.GetRequiredService<OperationsHost>(), judge), null, [TarolHealthTags.Tarol]);
}
