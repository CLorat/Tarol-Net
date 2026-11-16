using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Caching;

/// <summary>List page caching on the one Tarol builder (R-14).</summary>
public static class CachingTarolBuilderExtensions
{
    /// <summary>The service key of the read stores that <see cref="CachedReadModels"/> decorates.</summary>
    public const string InnerReadModelsKey = "Tarol.Caching.Inner";

    /// <summary>
    /// Caches list pages in this process (the API): decorates the registered <see cref="IReadModels"/> with
    /// <see cref="CachedReadModels"/> over an <see cref="IPageCache"/> (<see cref="MemoryPageCache"/> unless one is
    /// registered). Call it after <c>UseNpgsqlReadModels</c> (with <c>PushCheckpoints = true</c>, so a hit costs no round
    /// trip) or <c>UseInMemoryReadModels</c>.
    /// </summary>
    /// <example><code>builder.UseNpgsqlStore(cs).UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true }).AddCaching();</code></example>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">The bounds (entries, bytes, idle expiry).</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="IReadModels"/> is registered yet, or caching is added twice.</exception>
    public static TarolBuilder AddCaching(this TarolBuilder builder, Action<CacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new CacheOptions();
        configure?.Invoke(options);
        options.Validate();
        var services = builder.Services;
        if (services.Any(static d => d.ServiceType == typeof(CachingMarker)))
            throw new InvalidOperationException("AddCaching() is called twice.");
        var inner = services.LastOrDefault(static d => d.ServiceType == typeof(IReadModels) && !d.IsKeyedService)
            ?? throw new InvalidOperationException("AddCaching() decorates the read stores: call UseNpgsqlReadModels() or UseInMemoryReadModels() before it.");
        services.Remove(inner);
        services.Add(Keyed(inner));
        services.AddSingleton(new CachingMarker());
        services.TryAddSingleton(options);
        services.TryAddSingleton<IPageCache>(static provider => new MemoryPageCache(provider.GetRequiredService<CacheOptions>(),
            provider.GetService<TimeProvider>(), provider.GetService<IMeterFactory>()));
        var json = builder.Json;
        services.AddSingleton<IReadModels>(provider => new CachedReadModels(provider.GetRequiredKeyedService<IReadModels>(InnerReadModelsKey),
            provider.GetRequiredService<IProjectionPositions>, provider.GetRequiredService<IPageCache>(), json));
        return builder;
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor inner)
    {
        if (inner.ImplementationInstance is { } instance)
            return new ServiceDescriptor(typeof(IReadModels), InnerReadModelsKey, instance);
        if (inner.ImplementationFactory is { } factory)
            return new ServiceDescriptor(typeof(IReadModels), InnerReadModelsKey, (provider, _) => factory(provider), inner.Lifetime);
        return new ServiceDescriptor(typeof(IReadModels), InnerReadModelsKey, inner.ImplementationType!, inner.Lifetime);
    }

    private sealed class CachingMarker;
}
