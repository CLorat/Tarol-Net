using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;

namespace Tarol.Queries;

/// <summary>The projections a process hosts (<see cref="QueriesTarolBuilderExtensions.AddProjections"/>).</summary>
public sealed class ProjectionsBuilder
{
    internal ProjectionsBuilder(IServiceCollection services) => Services = services;

    /// <summary>The application's services.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The options every projection starts from (batch, poll, backoff).</summary>
    public ProjectionOptions Defaults { get; } = new();

    /// <summary>Hosts the row projection <typeparamref name="TRow"/> (a <see cref="ProjectionHost{TRow}"/> hosted service).</summary>
    /// <typeparam name="TRow">The <see cref="ProjectionAttribute"/> row record.</typeparam>
    /// <param name="configure">Options of this projection, after <see cref="Defaults"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The projection is hosted already.</exception>
    public ProjectionsBuilder Add<TRow>(Action<ProjectionOptions>? configure = null)
        where TRow : class, IProjection<TRow>
    {
        Once(TRow.Name, TRow.Version);
        Services.AddSingleton(provider => new ProjectionHost<TRow>(
            provider.GetRequiredService<IEventFeed>(),
            provider.GetRequiredService<IReadModels>().Store<TRow>(),
            Options(configure),
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<ProjectionHost<TRow>>>(),
            provider.GetService<IFeedSignal>(),
            provider.GetService<IProjectionGate>(),
            provider.GetService<IMeterFactory>()));
        Services.AddSingleton<ProjectionHost>(static provider => provider.GetRequiredService<ProjectionHost<TRow>>());
        Services.AddSingleton<IHostedService>(static provider => provider.GetRequiredService<ProjectionHost<TRow>>());
        Services.AddSingleton<ErasureParticipant>(static provider => new ProjectionEraser<TRow>(provider));   // step 16C (DP-5)
        Services.TryAddSingleton(static provider => new ProjectionHosts(provider.GetServices<ProjectionHost>()));
        return this;
    }

    /// <summary>Adds the projections of every module <c>AddTarol(modules)</c> registered (<see cref="ProjectionModule"/>, TN-8).</summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">The application was not registered with modules.</exception>
    public ProjectionsBuilder AddModules() => Add(Tarol.Persistence.TarolModules.In(Services));

    /// <summary>Adds the projections of the given modules (<see cref="ProjectionModule"/>, TN-8).</summary>
    /// <param name="modules">The modules.</param>
    /// <returns>The builder.</returns>
    public ProjectionsBuilder Add(IEnumerable<Tarol.Persistence.TarolModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules)
            module.Part<ProjectionModule>()?.Add(this);
        return this;
    }

    /// <summary>Hosts an <see cref="ISqlProjection"/> (needs a store implementing <see cref="ISqlProjectionStore"/>: <c>UseNpgsqlReadModels</c>).</summary>
    /// <param name="projection">The projection.</param>
    /// <param name="configure">Options of this projection, after <see cref="Defaults"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The projection is hosted already.</exception>
    public ProjectionsBuilder Add(ISqlProjection projection, Action<ProjectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        Once(projection.Name, projection.Version);
        var host = new HostSlot();
        Services.AddSingleton<ProjectionHost>(provider => host.Get(provider, projection, Options(configure)));
        Services.AddSingleton<IHostedService>(provider => host.Get(provider, projection, Options(configure)));
        Services.TryAddSingleton(static provider => new ProjectionHosts(provider.GetServices<ProjectionHost>()));
        return this;
    }

    /// <summary>
    /// Makes the process ready (the <c>ready</c> health group of <c>AddOperations</c>) only when every projection it hosts is
    /// current (TN-26, Tatalu FB-INT2-3). Opt-in: a worker that rebuilds a new projection version is not ready meanwhile.
    /// </summary>
    /// <returns>This builder.</returns>
    public ProjectionsBuilder ReadyWhenCurrent()
    {
        Services.TryAddSingleton(static provider => new ProjectionHosts(provider.GetServices<ProjectionHost>()));
        Services.AddSingleton(new ReadinessCheck("projections", static provider =>
            Readiness(provider.GetRequiredService<ProjectionHosts>())));
        return this;
    }

    private static string? Readiness(ProjectionHosts hosts)
    {
        var building = hosts.Building;
        if (building.Count == 0)
            return null;
        var failing = hosts.Failing;
        return "building: " + string.Join(", ", building) + (failing.Count > 0 ? "; failing: " + string.Join("; ", failing) : "");
    }

    private readonly HashSet<(string, int)> _added = [];

    private void Once(string name, int version)
    {
        if (!_added.Add((name, version)))
            throw new InvalidOperationException($"The projection '{name}' v{version} is added twice.");
    }

    private ProjectionOptions Options(Action<ProjectionOptions>? configure)
    {
        var options = Defaults.Copy();
        configure?.Invoke(options);
        options.Validate();
        return options;
    }

    private sealed class HostSlot
    {
        private readonly Lock _gate = new();
        private SqlProjectionHost? _host;

        public SqlProjectionHost Get(IServiceProvider provider, ISqlProjection projection, ProjectionOptions options)
        {
            lock (_gate)
                return _host ??= new SqlProjectionHost(projection,
                    provider.GetRequiredService<IEventFeed>(),
                    provider.GetService<ISqlProjectionStore>()
                        ?? throw new InvalidOperationException($"The SQL projection '{projection.Name}' needs a store that runs SQL: UseNpgsqlReadModels()."),
                    options,
                    provider.GetService<TimeProvider>(),
                    provider.GetService<ILogger<SqlProjectionHost>>(),
                    provider.GetService<IFeedSignal>(),
                    provider.GetService<IProjectionGate>(),
                    provider.GetService<IMeterFactory>());
        }
    }
}

/// <summary>Registers read stores and projection hosts on the one Tarol builder (R-14).</summary>
public static class QueriesTarolBuilderExtensions
{
    /// <summary>
    /// The in-memory read stores (<see cref="InMemoryReadModels"/>, tests and prototypes) as <see cref="IReadModels"/>, and
    /// <see cref="IProjectionPositions"/>. Register them in every process that reads or hosts projections.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseInMemoryReadModels(this TarolBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<InMemoryReadModels>();
        builder.Services.TryAddSingleton<IReadModels>(static provider => provider.GetRequiredService<InMemoryReadModels>());
        return builder.AddProjectionPositions();
    }

    /// <summary>
    /// Registers <see cref="IProjectionPositions"/> over the registered <see cref="IReadModels"/> and this process's hosts
    /// (read stores call it; call it yourself only with a read store of your own).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder AddProjectionPositions(this TarolBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IProjectionPositions>(static provider => new ProjectionPositions(
            provider.GetRequiredService<IReadModels>(), provider.GetServices<ProjectionHost>(), provider.GetService<TimeProvider>()));
        return builder;
    }

    /// <summary>
    /// Hosts projections in this process (the worker): one <see cref="ProjectionHost"/> per projection, reading the store's
    /// <see cref="IEventFeed"/> and writing to the registered <see cref="IReadModels"/>.
    /// </summary>
    /// <example><code>builder.UseNpgsqlStore(cs).UseNpgsqlReadModels().AddProjections(p => p.Add&lt;AuctionRow&gt;());</code></example>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Adds the projections.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder AddProjections(this TarolBuilder builder, Action<ProjectionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(new ProjectionsBuilder(builder.Services));
        return builder;
    }
}
