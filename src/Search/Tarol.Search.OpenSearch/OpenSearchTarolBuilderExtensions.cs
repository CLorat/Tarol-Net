using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tarol.Persistence;

namespace Tarol.Search.OpenSearch;

/// <summary>Registers the OpenSearch search index on the one Tarol builder.</summary>
public static class OpenSearchTarolBuilderExtensions
{
    /// <summary>
    /// Keeps the search index in OpenSearch (GS-3). <paramref name="http"/> gives the client (its <c>BaseAddress</c> the cluster, its
    /// handler the app's authentication); the checkpoints come from a registered <see cref="ISearchCheckpoints"/>
    /// (<c>UseNpgsqlSearchCheckpoints</c> in Tarol.Search.Npgsql).
    /// </summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <param name="http">Creates the client.</param>
    /// <param name="options">Prefix, refresh, shards.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseOpenSearch(this TarolBuilder builder, Func<IServiceProvider, HttpClient> http, OpenSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(http);
        builder.Services.TryAddSingleton(provider => new OpenSearchIndex(http(provider),
            provider.GetService<ISearchCheckpoints>() ?? throw new InvalidOperationException(
                "The OpenSearch index keeps its checkpoints in an ISearchCheckpoints: call UseNpgsqlSearchCheckpoints() (Tarol.Search.Npgsql)."),
            options));
        builder.Services.TryAddSingleton<ISearchIndex>(static provider => provider.GetRequiredService<OpenSearchIndex>());
        builder.Services.AddSingleton(new ReadinessCheck("search-opensearch", static provider => provider.GetRequiredService<OpenSearchIndex>().Problem));
        return builder;
    }
}
