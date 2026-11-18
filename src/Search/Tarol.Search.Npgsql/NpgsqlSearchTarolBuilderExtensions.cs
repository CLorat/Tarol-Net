using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries.Npgsql;

namespace Tarol.Search.Npgsql;

/// <summary>Registers the PostgreSQL search index on the one Tarol builder.</summary>
public static class NpgsqlSearchTarolBuilderExtensions
{
    /// <summary>
    /// Keeps the search index in the store's database (GS-2), in the store's schema unless <see cref="NpgsqlSearchOptions.Schema"/>
    /// says otherwise. Call after <c>AddSearch</c>; <c>UseNpgsqlReadModels</c> brings the gate and the feed signal the host uses.
    /// </summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <param name="options">Schema, rank bound, typo threshold.</param>
    /// <param name="dataSource">The data source; <see langword="null"/> = the registered <see cref="NpgsqlDataSource"/>.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlSearch(this TarolBuilder builder, NpgsqlSearchOptions? options = null, Func<IServiceProvider, NpgsqlDataSource>? dataSource = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new NpgsqlSearchOptions();
        if (options.Schema is { } schema)
            NpgsqlSearchSchema.Validate(schema);
        dataSource ??= static provider => provider.GetRequiredService<NpgsqlDataSource>();
        builder.Services.TryAddSingleton(provider => new NpgsqlSearchIndex(dataSource(provider),
            options.Schema ?? provider.GetService<NpgsqlAggregateStore>()?.Schema ?? NpgsqlReadModelSchema.DefaultSchema, options));
        builder.Services.TryAddSingleton<ISearchIndex>(static provider => provider.GetRequiredService<NpgsqlSearchIndex>());
        return builder;
    }
}
