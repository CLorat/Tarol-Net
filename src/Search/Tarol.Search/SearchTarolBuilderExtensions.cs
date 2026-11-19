using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Search;

/// <summary>Registers search on the one Tarol builder (R-14).</summary>
public static class SearchTarolBuilderExtensions
{
    /// <summary>
    /// Registers the searchable aggregates, the <see cref="SearchHost"/> (a hosted service and a <see cref="ProjectionHost"/>),
    /// <see cref="ISearch"/> and the <see cref="SearchEraser"/>. An index comes from <see cref="UseInMemorySearch"/> or an
    /// implementation package (<c>UseNpgsqlSearch</c>, <c>UseOpenSearch</c>, <c>UseTypesense</c>).
    /// </summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <param name="configure">Adds the aggregates (<c>search.Add&lt;Artwork&gt;()</c>), sets the version.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">Search is added twice.</exception>
    public static TarolBuilder AddSearch(this TarolBuilder builder, Action<SearchRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        if (builder.Services.Any(static d => d.ServiceType == typeof(SearchRegistry)))
            throw new InvalidOperationException("AddSearch is called twice; add every aggregate in one call.");
        var registry = new SearchRegistry();
        configure(registry);
        var services = builder.Services;
        services.AddSingleton(registry);
        services.TryAddSingleton(static provider => new SearchTokens(provider.GetService<IKeyStore>()));
        services.AddSingleton(static provider => new SearchHost(
            provider.GetRequiredService<SearchRegistry>(),
            provider.GetService<ISearchIndex>() ?? throw new InvalidOperationException("Search has no index: call UseInMemorySearch(), UseNpgsqlSearch() or an engine's Use…()."),
            provider.GetRequiredService<IEventFeed>(),
            provider.GetRequiredService<IAggregateReader>(),
            provider.GetRequiredService<SearchTokens>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<SearchHost>>(),
            provider.GetService<IFeedSignal>(),
            provider.GetService<IProjectionGate>(),
            provider.GetService<IMeterFactory>()));
        services.AddSingleton<ProjectionHost>(static provider => provider.GetRequiredService<SearchHost>());
        services.AddSingleton<IHostedService>(static provider => provider.GetRequiredService<SearchHost>());
        services.TryAddSingleton<ISearch>(static provider => new SearchService(provider.GetRequiredService<SearchRegistry>(),
            provider.GetRequiredService<SearchHost>().Index, provider.GetRequiredService<SearchTokens>(), provider.GetService<IMeterFactory>()));
        services.AddSingleton<ErasureParticipant>(static provider => new SearchEraser(provider));
        return builder;
    }

    /// <summary>Keeps the index in memory (tests, the contract suite; one process, lost on restart).</summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseInMemorySearch(this TarolBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<InMemorySearchIndex>();
        builder.Services.TryAddSingleton<ISearchIndex>(static provider => provider.GetRequiredService<InMemorySearchIndex>());
        return builder;
    }
}
