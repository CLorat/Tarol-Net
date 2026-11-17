using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;

namespace Tarol.Queries.Npgsql;

/// <summary>How <see cref="NpgsqlQueriesTarolBuilderExtensions.UseNpgsqlReadModels"/> registers the read stores.</summary>
public sealed record NpgsqlReadModelOptions
{
    /// <summary>The schema of the read tables; <see langword="null"/> (default): the registered store's schema, else <c>tarol</c>.</summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Whether the projection hosts <c>LISTEN</c> on the store's feed channel; <see langword="null"/> (default): when the
    /// registered store sends the feed <c>NOTIFY</c> (<see cref="NpgsqlStoreOptions.NotifyFeed"/>).
    /// </summary>
    public bool? Listen { get; init; }

    /// <summary>
    /// Whether checkpoints are pushed (CA-2; default <see langword="false"/>): every projection batch sends
    /// <c>NOTIFY &lt;schema&gt;_checkpoint</c> in its transaction (delivered after the commit), and
    /// <see cref="IProjectionPositions"/> is <see cref="NpgsqlProjectionPositions"/>, which listens and answers
    /// <c>Checkpoint</c> from memory. Set it in the worker (it sends) and in the API process (it listens).
    /// </summary>
    public bool PushCheckpoints { get; init; }

    /// <summary>
    /// One runner per projection version (step 15, O-8; default <see langword="true"/>): the hosts of every process take a
    /// session advisory lock (<see cref="NpgsqlProjectionGate"/>); the others wait and take over when the runner stops. Off:
    /// every host runs and the checkpoint guard refuses all but one batch (4 runners wasted 75 % of their batches).
    /// </summary>
    public bool SingleRunner { get; init; } = true;
}

/// <summary>The PostgreSQL read stores on the <see cref="TarolBuilder"/>.</summary>
public static class NpgsqlQueriesTarolBuilderExtensions
{
    /// <summary>
    /// The read tables on PostgreSQL: <see cref="NpgsqlReadModels"/> as <see cref="IReadModels"/> and
    /// <see cref="ISqlProjectionStore"/>, <see cref="IProjectionPositions"/> (<see cref="NpgsqlProjectionPositions"/> with
    /// <see cref="NpgsqlReadModelOptions.PushCheckpoints"/>), and the feed wake-up (<see cref="NpgsqlFeedSignal"/>) when the
    /// store notifies. The data source is the store's (<c>UseNpgsqlStore(connectionString)</c> registers it) unless
    /// <paramref name="dataSource"/> resolves another. Register it in every process that reads or hosts projections.
    /// </summary>
    /// <param name="builder">The builder (after <c>UseNpgsqlStore</c>).</param>
    /// <param name="options">Schema and wake-up.</param>
    /// <param name="dataSource">Resolves the data source; <see langword="null"/> = the registered <see cref="NpgsqlDataSource"/>.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlReadModels(this TarolBuilder builder, NpgsqlReadModelOptions? options = null,
        Func<IServiceProvider, NpgsqlDataSource>? dataSource = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new NpgsqlReadModelOptions();
        if (options.Schema is { } schema)
            NpgsqlReadModelSchema.Validate(schema);
        var json = builder.Json;
        dataSource ??= static provider => provider.GetRequiredService<NpgsqlDataSource>();
        var push = options.PushCheckpoints;
        builder.Services.TryAddSingleton(provider => new NpgsqlReadModels(dataSource(provider), json,
            options.Schema ?? provider.GetService<NpgsqlAggregateStore>()?.Schema ?? NpgsqlReadModelSchema.DefaultSchema, push, provider.GetService<TimeProvider>()));
        builder.Services.TryAddSingleton<IReadModels>(static provider => provider.GetRequiredService<NpgsqlReadModels>());
        builder.Services.TryAddSingleton<ISqlProjectionStore>(static provider => provider.GetRequiredService<NpgsqlReadModels>());
        builder.Services.TryAddSingleton<IReadModelAdmin>(static provider => provider.GetRequiredService<NpgsqlReadModels>());
        builder.Services.TryAddSingleton(provider =>
        {
            var store = provider.GetService<NpgsqlAggregateStore>();
            var listen = options.Listen ?? store?.NotifiesFeed ?? false;
            return new FeedSignalSlot(listen
                ? new NpgsqlFeedSignal(dataSource(provider), store?.FeedChannel ?? NpgsqlReadModelSchema.DefaultSchema + "_feed", provider.GetService<TimeProvider>())
                : null);
        });
        builder.Services.TryAddSingleton<IFeedSignal>(static provider => (IFeedSignal?)provider.GetRequiredService<FeedSignalSlot>().Signal ?? NoSignal.Instance);
        if (options.SingleRunner)
            builder.Services.TryAddSingleton<IProjectionGate>(provider => new NpgsqlProjectionGate(dataSource(provider),
                provider.GetRequiredService<NpgsqlReadModels>().Schema, provider.GetService<TimeProvider>()));
        builder.Services.AddSingleton(new OperationsSignal(OperationsSignals.Meter, OperationsSignals.ProjectionsLag, "s",
            "Feed head time minus the time of each projection's checkpoint (its newest version).", static (provider, ct) => ProjectionLag(provider, ct)));
        if (push)
        {
            builder.Services.TryAddSingleton(provider => new NpgsqlProjectionPositions(dataSource(provider),
                provider.GetRequiredService<NpgsqlReadModels>().Schema, provider.GetServices<ProjectionHost>(),
                provider.GetService<TimeProvider>(), provider.GetService<ILogger<NpgsqlProjectionPositions>>()));
            builder.Services.TryAddSingleton<IProjectionPositions>(static provider => provider.GetRequiredService<NpgsqlProjectionPositions>());
            builder.Services.AddSingleton(new ReadinessCheck("checkpoint-listen", static provider =>
                provider.GetRequiredService<NpgsqlProjectionPositions>().IsListening ? null : "the checkpoint LISTEN connection is down"));
        }
        return builder.AddProjectionPositions();
    }

    /// <summary>
    /// The lag of each projection's newest version in seconds (step 15): recorded time of the feed head minus that of the
    /// checkpoint's event (of the first event while nothing was handled); index lookups on <c>events_feed</c> and the event's key.
    /// Needs the PostgreSQL store in the same database; without one there are no values.
    /// </summary>
    private static async Task<IReadOnlyList<SignalValue>> ProjectionLag(IServiceProvider provider, CancellationToken cancellationToken)
    {
        if (provider.GetService<NpgsqlAggregateStore>() is not { } store)
            return [];
        var models = provider.GetRequiredService<NpgsqlReadModels>();
        var (s, rm) = (store.Schema, models.Schema);
        await using var command = models.DataSource.CreateCommand($"""
            WITH head AS (SELECT recorded_at FROM {s}.events ORDER BY tx_id DESC, position DESC LIMIT 1),
                 first AS (SELECT recorded_at FROM {s}.events ORDER BY tx_id, position LIMIT 1),
                 p AS (SELECT DISTINCT ON (name) name, tx, pos FROM {rm}.projections ORDER BY name, version DESC)
            SELECT p.name, coalesce(extract(epoch FROM (SELECT recorded_at FROM head)
                     - coalesce((SELECT e.recorded_at FROM {s}.events e WHERE e.tx_id = p.tx AND e.position = p.pos), (SELECT recorded_at FROM first))), 0)::float8
            FROM p ORDER BY p.name
            """);
        var values = new List<SignalValue>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                values.Add(new SignalValue(Math.Max(0, reader.GetDouble(1)), "tarol.projection", reader.GetString(0)));
        return values;
    }

    // DI cannot register "no service" from a factory; the slot holds the signal or null, NoSignal polls.
    private sealed class FeedSignalSlot(NpgsqlFeedSignal? signal) : IAsyncDisposable
    {
        public NpgsqlFeedSignal? Signal { get; } = signal;

        public ValueTask DisposeAsync() => Signal?.DisposeAsync() ?? ValueTask.CompletedTask;   // idempotent: DI disposes it as IFeedSignal too
    }

    private sealed class NoSignal : IFeedSignal
    {
        public static readonly NoSignal Instance = new();

        public ValueTask Wait(TimeSpan max, CancellationToken cancellationToken) => new(Task.Delay(TimeSpan.FromTicks(Math.Min(max.Ticks, TimeSpan.FromMilliseconds(100).Ticks)), cancellationToken));
    }
}
