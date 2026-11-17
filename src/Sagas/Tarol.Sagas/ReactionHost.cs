using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Persistence;

namespace Tarol.Sagas;

/// <summary>
/// The hosted service of reactions: <see cref="ReactionOptions.Workers"/> worker loops, and the idempotency key purge
/// every <see cref="ReactionOptions.PurgeEvery"/> (P-11). It depends on no hosting product (D-15): an Aspire AppHost, a
/// container or a console host start it the same way.
/// </summary>
public sealed partial class ReactionHost : BackgroundService
{
    private readonly IOutbox _outbox;
    private readonly IOutboxRowHandler _rows;
    private readonly ChangePipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly ReactionOptions _options;
    private readonly ILogger _log;

    /// <summary>Creates the host.</summary>
    /// <param name="outbox">The store's outbox.</param>
    /// <param name="rows">What runs the rows: the application's reactions, or <see cref="OutboxRows"/> with publications and effects.</param>
    /// <param name="pipeline">The pipeline the reactions run through.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">Workers, policy and key purge.</param>
    /// <param name="logger">The logger of the workers and the purge.</param>
    public ReactionHost(IOutbox outbox, IOutboxRowHandler rows, ChangePipeline pipeline, TimeProvider time, ReactionOptions options, ILogger<ReactionHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _outbox = outbox;
        _rows = rows;
        _pipeline = pipeline;
        _time = time;
        _options = options;
        _log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>(_options.Workers + 1);
        for (var i = 0; i < _options.Workers; i++)
            loops.Add(new ReactionWorker(_outbox, _rows, _pipeline, _time, _options, _log).Run(stoppingToken));
        if (_options.PurgeKeys is not null)
            loops.Add(Purge(_options.PurgeKeys, stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task Purge(Func<DateTimeOffset, int, CancellationToken, Task<int>> purgeKeys, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var olderThan = _time.GetUtcNow() - _options.KeyRetention;
                var total = 0;
                for (int deleted; (deleted = await purgeKeys(olderThan, _options.PurgeBatch, stoppingToken).ConfigureAwait(false)) > 0;)
                {
                    total += deleted;
                    if (deleted < _options.PurgeBatch)
                        break;
                }
                if (total > 0)
                    Purged(_log, total, olderThan);
                await Task.Delay(_options.PurgeEvery, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                PurgeFailed(_log, e);
                try
                {
                    await Task.Delay(_options.PurgeEvery, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(EventId = 9010, Level = LogLevel.Information, Message = "Purged {Count} idempotency keys stored before {OlderThan}")]
    private static partial void Purged(ILogger logger, int count, DateTimeOffset olderThan);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Error, Message = "Purging idempotency keys failed; trying again later")]
    private static partial void PurgeFailed(ILogger logger, Exception exception);
}

/// <summary>Registers the reaction worker.</summary>
public static class ReactionServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="ReactionHost"/> for <paramref name="reactions"/>. It resolves the <see cref="IOutbox"/> (register
    /// the store's: <c>services.AddSingleton&lt;IOutbox&gt;(store.Outbox)</c>), the <see cref="ChangePipeline"/> and the
    /// <see cref="TimeProvider"/> (default <see cref="TimeProvider.System"/>) from the container, and joins every
    /// <see cref="IOutboxRowHandler"/> registered there (publications and effects of <c>Tarol.Messaging</c>) with the reactions.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="reactions">The application's reactions (<c>GeneratedReactions.Set</c>, or <see cref="ReactionSet.Combine"/>).</param>
    /// <param name="configure">Workers, policy, key purge.</param>
    /// <returns>The services.</returns>
    /// <exception cref="InvalidOperationException">Reactions were added already (combine the sets into one call).</exception>
    public static IServiceCollection AddTarolReactions(this IServiceCollection services, ReactionSet reactions, Action<ReactionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(reactions);
        if (services.Any(static d => d.ServiceType == typeof(Marker)))
            throw new InvalidOperationException("AddTarolReactions was called twice; pass one ReactionSet (ReactionSet.Combine for several assemblies).");
        services.AddSingleton<Marker>();
        var options = new ReactionOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(reactions);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService(static provider =>
        {
            var options = provider.GetRequiredService<ReactionOptions>();
            if (options.PurgeKeys is null && provider.GetService<IMaintenanceHost>() is null && provider.GetService<IIdempotencyKeyPurge>() is { } purge)
                options.PurgeKeys = purge.PurgeKeys;                                // the store's, unless a job host runs it once per cluster (J-9)
            // other packages register row handlers (Tarol.Messaging: publications, effects); one worker runs them all
            var reactions = provider.GetRequiredService<ReactionSet>();
            var others = provider.GetServices<IOutboxRowHandler>().ToArray();
            return new ReactionHost(
                provider.GetRequiredService<IOutbox>(),
                others.Length == 0 ? reactions : OutboxRows.Combine([reactions, .. others]),
                provider.GetRequiredService<ChangePipeline>(),
                provider.GetRequiredService<TimeProvider>(),
                options,
                provider.GetService<ILogger<ReactionHost>>());
        });
        return services;
    }

    /// <summary>
    /// Hosts the reaction worker for the builder's reactions (<see cref="TarolBuilder.WithReactions"/>, a
    /// <see cref="ReactionSet"/>): <see cref="AddTarolReactions"/> on the builder's services. The store's key purge is used
    /// unless <paramref name="configure"/> sets <see cref="ReactionOptions.PurgeKeys"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Workers, policy, key purge.</param>
    /// <param name="reactions">The reactions, when <see cref="TarolBuilder.WithReactions"/> was not called (it is then called).</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">The builder's reactions are not a <see cref="ReactionSet"/> (none at all is
    /// <see cref="ReactionSet.Empty"/>: an application that only publishes).</exception>
    public static TarolBuilder AddReactionWorker(this TarolBuilder builder, Action<ReactionOptions>? configure = null, ReactionSet? reactions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (reactions is not null)
            builder.WithReactions(reactions);
        reactions = builder.Reactions switch
        {
            ReactionSet set => set,
            null => ReactionSet.Empty,                                              // publications or effects only
            _ => throw new InvalidOperationException("The worker runs a ReactionSet: call WithReactions(GeneratedReactions.Set) first, or pass it."),
        };
        builder.Services.AddTarolReactions(reactions, configure);
        return builder;
    }

    private sealed class Marker;
}
