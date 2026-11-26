using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Security;

namespace Tarol.Audit;

/// <summary>Registers the change log (TN-64).</summary>
public static class AuditTarolBuilderExtensions
{
    /// <summary>The maintenance task that deletes rows past <see cref="AuditRegistry.RetentionDays"/>: <c>tarol.audit.purge</c>.</summary>
    public const string PurgeJob = "tarol.audit.purge";

    /// <summary>
    /// Audits every event of the given aggregates (AU-2): <c>.AddAudit(a => a.Add&lt;Child&gt;().Add&lt;Guardian&gt;())</c>, then a store
    /// (<see cref="UseInMemoryAudit"/>, or <c>UseNpgsqlAudit()</c> from <c>Tarol.Audit.Npgsql</c>). Registers the feeding host, <see cref="IAuditLog"/>,
    /// an <see cref="IAccessObserver"/> for sensitive reads and denials (AU-5), the tenant erasure, and, with a retention (configuration
    /// <c>Tarol:Audit:RetentionDays</c> or <see cref="AuditRegistry.RetentionDays"/>), the maintenance task <see cref="PurgeJob"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Adds the aggregates.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">Called twice, or no aggregate is added.</exception>
    public static TarolBuilder AddAudit(this TarolBuilder builder, Action<AuditRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var services = builder.Services;
        if (services.Any(static d => d.ServiceType == typeof(AuditRegistry)))
            throw new InvalidOperationException("AddAudit is called twice; add every aggregate in one call.");
        var registry = new AuditRegistry();
        configure(registry);
        if (registry.StreamTypes.Count == 0)
            throw new InvalidOperationException("AddAudit needs at least one aggregate: a => a.Add<Child>().");
        services.AddSingleton(registry);
        services.AddSingleton(static provider => new AuditHost(
            Configured(provider),
            provider.GetService<IAuditStore>() ?? throw new InvalidOperationException("The audit has no store: call UseInMemoryAudit() or UseNpgsqlAudit()."),
            provider.GetRequiredService<IEventFeed>(),
            provider.GetRequiredService<EventCodec>(),
            provider.GetService<IKeyStore>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<AuditHost>>(),
            provider.GetService<IFeedSignal>(),
            provider.GetService<IProjectionGate>(),
            provider.GetService<IMeterFactory>()));
        services.AddSingleton<ProjectionHost>(static provider => provider.GetRequiredService<AuditHost>());
        services.AddSingleton<IHostedService>(static provider => provider.GetRequiredService<AuditHost>());
        services.TryAddSingleton<IAuditLog>(static provider => new AuditLog(provider.GetRequiredService<AuditHost>(), provider.GetService<IKeyStore>()));
        services.AddSingleton<IAccessObserver>(static provider => new AuditAccessObserver(provider.GetRequiredService<AuditHost>().Store, provider.GetService<IMeterFactory>()));
        services.AddSingleton<ErasureParticipant>(static provider => new AuditEraser(provider));
        services.AddSingleton(new MaintenanceTask(PurgeJob, TimeSpan.FromHours(1), static async (provider, at, ct) =>
        {
            if (Configured(provider).RetentionDays is not { } days)
                return 0;
            var store = provider.GetRequiredService<AuditHost>().Store;
            var total = 0;
            int gone;
            do
            {
                gone = await store.Purge(at.AddDays(-days), 1000, ct).ConfigureAwait(false);
                total += gone;
            }
            while (gone == 1000);
            return total;
        }));
        return builder;
    }

    /// <summary>Keeps the change log in memory (<see cref="InMemoryAuditStore"/>).</summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseInMemoryAudit(this TarolBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<InMemoryAuditStore>();
        builder.Services.TryAddSingleton<IAuditStore>(static provider => provider.GetRequiredService<InMemoryAuditStore>());
        return builder;
    }

    // the registry with Tarol:Audit:RetentionDays from the configuration, when the code set none
    private static AuditRegistry Configured(IServiceProvider provider)
    {
        var registry = provider.GetRequiredService<AuditRegistry>();
        if (registry.RetentionDays is null && provider.GetService<IConfiguration>()?["Tarol:Audit:RetentionDays"] is { Length: > 0 } text
            && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) && days > 0)
            registry.RetentionDays = days;
        return registry;
    }

    private sealed class AuditEraser(IServiceProvider services) : ErasureParticipant
    {
        public override string Name => "audit";

        // a subject's values are sealed under its key: erasing the key is the erasure (crypto-shredding), nothing to delete here
        public override ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public override async ValueTask ForgetTenant(Guid tenant, CancellationToken cancellationToken)
            => await services.GetRequiredService<AuditHost>().Store.ForgetTenant(tenant, cancellationToken).ConfigureAwait(false);
    }
}
