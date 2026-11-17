using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tarol.Persistence.Npgsql;

/// <summary>The PostgreSQL store on the <see cref="TarolBuilder"/>.</summary>
public static class NpgsqlTarolBuilderExtensions
{
    /// <summary>
    /// The PostgreSQL store on a data source built from <paramref name="connectionString"/> (slim, arrays enabled, Npgsql spans
    /// only inside an activity unless <see cref="NpgsqlStoreOptions.TraceRootCommands"/>; registered as the
    /// <see cref="NpgsqlDataSource"/>, disposed with the container). The builder's reactions and JSON options are used unless
    /// <paramref name="options"/> sets them.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="options">Schema, lookups, notify, …</param>
    /// <param name="ensureSchema">Run <see cref="NpgsqlAggregateStore.EnsureSchema(CancellationToken)"/> when the host starts (upgrade, or verify with <see cref="NpgsqlStoreOptions.SchemaMode"/>).</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlStore(this TarolBuilder builder, string connectionString, NpgsqlStoreOptions? options = null, bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var traceRoots = options?.TraceRootCommands ?? false;
        builder.Services.AddSingleton(_ =>
        {
            var source = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays();
            if (!traceRoots)
                source.TraceOnlyInActivities();                                                     // O-6: no root spans from idle polls
            return source.Build();
        });
        return builder.UseNpgsqlStore(static provider => provider.GetRequiredService<NpgsqlDataSource>(), options, ensureSchema);
    }

    /// <summary>
    /// The PostgreSQL store on a data source the application provides (e.g. from Aspire's integration). Registers it as the
    /// <see cref="NpgsqlDataSource"/> unless one is registered already (step 15), so the read models and the dead letters find it.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="dataSource">Resolves the data source; it needs arrays enabled.</param>
    /// <param name="options">Schema, lookups, notify, …</param>
    /// <param name="ensureSchema">Run <see cref="NpgsqlAggregateStore.EnsureSchema(CancellationToken)"/> when the host starts (upgrade, or verify with <see cref="NpgsqlStoreOptions.SchemaMode"/>).</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlStore(this TarolBuilder builder, Func<IServiceProvider, NpgsqlDataSource> dataSource, NpgsqlStoreOptions? options = null,
        bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        // the read models (UseNpgsqlReadModels) and the dead letters (UseNpgsqlDeadLetters) resolve NpgsqlDataSource: register
        // the store's unless the application (or Aspire's integration) did (operations.md §5 item 13)
        builder.Services.TryAddSingleton(dataSource);
        builder.UseStore(provider =>
        {
            var configured = options ?? new NpgsqlStoreOptions();
            return new NpgsqlAggregateStore(dataSource(provider), builder.Codec, provider.GetRequiredService<TimeProvider>(), configured with
            {
                Reactions = configured.Reactions ?? builder.OutboxTable,
                StateOptions = configured.StateOptions ?? builder.Json,
                Keys = configured.Keys ?? provider.GetService<IKeyStore>(),   // step 16: UseNpgsqlKeyStore or AddDataProtection
            }, provider.GetService<ILoggerFactory>()?.CreateLogger<NpgsqlAggregateStore>());
        }, static store => store.Outbox, static store => store.Jobs);
        foreach (var signal in NpgsqlStoreSignals.All)
            builder.Services.AddSingleton(signal);
        builder.Services.AddSingleton<IDeadLetterRetention>(static provider => provider.GetRequiredService<NpgsqlAggregateStore>().Outbox);
        builder.Services.AddSingleton<IDeadLetterRetention>(static provider => provider.GetRequiredService<NpgsqlAggregateStore>().Jobs);
        if (ensureSchema)
            builder.Services.AddSingleton(new ReadinessCheck("schema", static provider => provider.GetRequiredService<NpgsqlAggregateStore>() is { Ensured: null } store
                ? $"the schema '{store.Schema}' is not ensured yet"
                : null));
        if (ensureSchema)
            builder.Services.AddHostedService(static provider => new EnsureSchemaService(provider.GetRequiredService<NpgsqlAggregateStore>()));
        return builder;
    }

    /// <summary>
    /// Registers an <see cref="NpgsqlKeyStore"/> on the store's data source as the <see cref="IKeyStore"/> (step 16, DP-1): the
    /// store then seals <c>[PersonalData]</c>. It needs an <see cref="IKeyEncryptionKey"/> in the container (the master key;
    /// <c>Tarol.DataProtection</c>'s <c>AddDataProtection</c> registers one from configuration).
    /// </summary>
    /// <param name="builder">The Tarol builder (after <c>UseNpgsqlStore</c>).</param>
    /// <param name="options">Schema (the store's) and cache bounds; <see langword="null"/> = defaults.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlKeyStore(this TarolBuilder builder, NpgsqlKeyStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(provider => new NpgsqlKeyStore(provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<IKeyEncryptionKey>(),
            provider.GetRequiredService<TimeProvider>(), options, provider.GetService<ILoggerFactory>()?.CreateLogger<NpgsqlKeyStore>()));
        builder.Services.TryAddSingleton<IKeyStore>(static provider => provider.GetRequiredService<NpgsqlKeyStore>());
        return builder;
    }

    private sealed class EnsureSchemaService(NpgsqlAggregateStore store) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchema(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
