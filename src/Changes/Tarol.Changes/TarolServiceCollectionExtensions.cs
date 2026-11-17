using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;
using Tarol.Validations;

namespace Tarol.Changes;

/// <summary>The entry of the one registration (docs/architecture/rest.md §10, R-14).</summary>
public static class TarolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the codec (the application's aggregates over its JSON context, <see cref="ValueObjectJson.Resolver"/>
    /// first), <see cref="TimeProvider.System"/> unless one is registered, and the <see cref="ChangePipeline"/> over the store
    /// and the <see cref="IChangeAuthorization"/> (if any). Add a store to the returned builder.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="json">The application's source-generated JSON context (events, stored state, inputs).</param>
    /// <param name="aggregates">Adds the aggregates to the codec: <c>codec =&gt; codec.Add&lt;Auction&gt;().Add&lt;Payout&gt;()</c>.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException"><c>AddTarol</c> was called before.</exception>
    public static TarolBuilder AddTarol(this IServiceCollection services, JsonSerializerContext json, Action<EventCodec> aggregates)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(aggregates);
        if (services.Any(static d => d.ServiceType == typeof(Marker)))
            throw new InvalidOperationException("AddTarol was called twice; one call registers the application.");
        services.AddSingleton<Marker>();
        var options = ValueObjectJson.Options(json);
        var codec = new EventCodec(options);
        aggregates(codec);
        var builder = new TarolBuilder(services, options, codec);
        services.AddSingleton(static provider => new ChangePipeline(provider.GetRequiredService<IAggregateStore>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<IChangeAuthorization>(),
            provider.GetService<ILogger<ChangePipeline>>(), provider.GetService<IMeterFactory>()));
        return builder;
    }

    private sealed class Marker;
}
