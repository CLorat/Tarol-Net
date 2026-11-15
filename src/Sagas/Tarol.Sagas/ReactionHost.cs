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
    private readonly ReactionSet _reactions;
    private readonly ChangePipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly ReactionOptions _options;
    private readonly ILogger _log;

    /// <summary>Creates the host.</summary>
    /// <param name="outbox">The store's outbox.</param>
    /// <param name="reactions">The application's reactions.</param>
    /// <param name="pipeline">The pipeline the reactions run through.</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">Workers, policy and key purge.</param>
    /// <param name="logger">The logger of the workers and the purge.</param>
    public ReactionHost(IOutbox outbox, ReactionSet reactions, ChangePipeline pipeline, TimeProvider time, ReactionOptions options, ILogger<ReactionHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(reactions);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _outbox = outbox;
        _reactions = reactions;
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
            loops.Add(new ReactionWorker(_outbox, _reactions, _pipeline, _time, _options, _log).Run(stoppingToken));
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
    /// <see cref="TimeProvider"/> (default <see cref="TimeProvider.System"/>) from the container.
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
        if (services.Any(static d => d.ServiceType == typeof(ReactionSet)))
            throw new InvalidOperationException("AddTarolReactions was called twice; pass one ReactionSet (ReactionSet.Combine for several assemblies).");
        var options = new ReactionOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(reactions);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService(static provider => new ReactionHost(
            provider.GetRequiredService<IOutbox>(),
            provider.GetRequiredService<ReactionSet>(),
            provider.GetRequiredService<ChangePipeline>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ReactionOptions>(),
            provider.GetService<ILogger<ReactionHost>>()));
        return services;
    }
}
