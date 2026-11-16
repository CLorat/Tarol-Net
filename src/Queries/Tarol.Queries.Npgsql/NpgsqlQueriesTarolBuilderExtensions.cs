using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
}

/// <summary>The PostgreSQL read stores on the <see cref="TarolBuilder"/>.</summary>
public static class NpgsqlQueriesTarolBuilderExtensions
{
    /// <summary>
    /// The read tables on PostgreSQL: <see cref="NpgsqlReadModels"/> as <see cref="IReadModels"/> and
    /// <see cref="ISqlProjectionStore"/>, <see cref="IProjectionPositions"/>, and the feed wake-up (<see cref="NpgsqlFeedSignal"/>)
    /// when the store notifies. The data source is the store's (<c>UseNpgsqlStore(connectionString)</c> registers it) unless
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
        builder.Services.TryAddSingleton(provider => new NpgsqlReadModels(dataSource(provider), json,
            options.Schema ?? provider.GetService<NpgsqlAggregateStore>()?.Schema ?? NpgsqlReadModelSchema.DefaultSchema));
        builder.Services.TryAddSingleton<IReadModels>(static provider => provider.GetRequiredService<NpgsqlReadModels>());
        builder.Services.TryAddSingleton<ISqlProjectionStore>(static provider => provider.GetRequiredService<NpgsqlReadModels>());
        builder.Services.TryAddSingleton(provider =>
        {
            var store = provider.GetService<NpgsqlAggregateStore>();
            var listen = options.Listen ?? store?.NotifiesFeed ?? false;
            return new FeedSignalSlot(listen
                ? new NpgsqlFeedSignal(dataSource(provider), store?.FeedChannel ?? NpgsqlReadModelSchema.DefaultSchema + "_feed", provider.GetService<TimeProvider>())
                : null);
        });
        builder.Services.TryAddSingleton<IFeedSignal>(static provider => (IFeedSignal?)provider.GetRequiredService<FeedSignalSlot>().Signal ?? NoSignal.Instance);
        return builder.AddProjectionPositions();
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
