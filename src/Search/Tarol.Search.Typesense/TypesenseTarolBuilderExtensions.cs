using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tarol.Persistence;

namespace Tarol.Search.Typesense;

/// <summary>Registers the Typesense search index on the one Tarol builder.</summary>
public static class TypesenseTarolBuilderExtensions
{
    /// <summary>
    /// Keeps the search index in Typesense (GS-3). <paramref name="http"/> gives the client: its <c>BaseAddress</c> the server and a
    /// default header <c>X-TYPESENSE-API-KEY</c> with the key from configuration (<see cref="Client"/> builds one). The checkpoints
    /// come from a registered <see cref="ISearchCheckpoints"/> (<c>UseNpgsqlSearchCheckpoints</c> in Tarol.Search.Npgsql).
    /// </summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <param name="http">Creates the client.</param>
    /// <param name="options">The collection prefix.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseTypesense(this TarolBuilder builder, Func<IServiceProvider, HttpClient> http, TypesenseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(http);
        builder.Services.TryAddSingleton(provider => new TypesenseIndex(http(provider),
            provider.GetService<ISearchCheckpoints>() ?? throw new InvalidOperationException(
                "The Typesense index keeps its checkpoints in an ISearchCheckpoints: call UseNpgsqlSearchCheckpoints() (Tarol.Search.Npgsql)."),
            options));
        builder.Services.TryAddSingleton<ISearchIndex>(static provider => provider.GetRequiredService<TypesenseIndex>());
        builder.Services.AddSingleton(new ReadinessCheck("search-typesense", static provider => provider.GetRequiredService<TypesenseIndex>().Problem));
        return builder;
    }

    /// <summary>A client for a Typesense server with its API key (read the key from configuration or a secret store, never from code).</summary>
    /// <param name="server">The server's address.</param>
    /// <param name="apiKey">The API key.</param>
    /// <returns>The client.</returns>
    public static HttpClient Client(Uri server, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var client = new HttpClient { BaseAddress = server };
        client.DefaultRequestHeaders.Add("X-TYPESENSE-API-KEY", apiKey);
        return client;
    }
}
