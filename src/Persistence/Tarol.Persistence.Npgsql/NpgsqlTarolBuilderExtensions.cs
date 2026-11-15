using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Tarol.Persistence.Npgsql;

/// <summary>The PostgreSQL store on the <see cref="TarolBuilder"/>.</summary>
public static class NpgsqlTarolBuilderExtensions
{
    /// <summary>
    /// The PostgreSQL store on a data source built from <paramref name="connectionString"/> (slim, arrays enabled; disposed
    /// with the container). The builder's reactions and JSON options are used unless <paramref name="options"/> sets them.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="options">Schema, lookups, notify, …</param>
    /// <param name="ensureSchema">Run <see cref="NpgsqlAggregateStore.EnsureSchema"/> when the host starts.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlStore(this TarolBuilder builder, string connectionString, NpgsqlStoreOptions? options = null, bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.Services.AddSingleton(_ => new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build());
        return builder.UseNpgsqlStore(static provider => provider.GetRequiredService<NpgsqlDataSource>(), options, ensureSchema);
    }

    /// <summary>The PostgreSQL store on a data source the application provides (e.g. from Aspire's integration).</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="dataSource">Resolves the data source; it needs arrays enabled.</param>
    /// <param name="options">Schema, lookups, notify, …</param>
    /// <param name="ensureSchema">Run <see cref="NpgsqlAggregateStore.EnsureSchema"/> when the host starts.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlStore(this TarolBuilder builder, Func<IServiceProvider, NpgsqlDataSource> dataSource, NpgsqlStoreOptions? options = null,
        bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.UseStore(provider =>
        {
            var configured = options ?? new NpgsqlStoreOptions();
            return new NpgsqlAggregateStore(dataSource(provider), builder.Codec, provider.GetRequiredService<TimeProvider>(), configured with
            {
                Reactions = configured.Reactions ?? builder.Reactions,
                StateOptions = configured.StateOptions ?? builder.Json,
            });
        }, static store => store.Outbox);
        if (ensureSchema)
            builder.Services.AddHostedService(static provider => new EnsureSchemaService(provider.GetRequiredService<NpgsqlAggregateStore>()));
        return builder;
    }

    private sealed class EnsureSchemaService(NpgsqlAggregateStore store) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchema(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
