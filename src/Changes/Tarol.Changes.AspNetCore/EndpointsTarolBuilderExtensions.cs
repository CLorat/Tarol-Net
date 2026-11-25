using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore;

/// <summary>Registers the HTTP entries on the <see cref="TarolBuilder"/>.</summary>
public static class EndpointsTarolBuilderExtensions
{
    /// <summary>
    /// The runtime of the generated entries (<see cref="ChangeEndpoints"/>), the webhook edge (<see cref="Webhooks"/>,
    /// configured with <c>services.Configure&lt;WebhookOptions&gt;(…)</c>), and the application's JSON context in ASP.NET
    /// Core's own options (<c>ValueObjectJson.Resolver</c> first). Map the entries with the generated <c>app.MapChanges()</c>.
    /// Needs <c>AddSecurity</c> (the entries fail closed without an authorization).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Claim names, actor and tenant hooks, body limit, problem details.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public static TarolBuilder AddEndpoints(this TarolBuilder builder, Action<ChangeEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (services.Any(static d => d.ServiceType == typeof(ChangeEndpointOptions)))
            throw new InvalidOperationException("AddEndpoints was called twice.");
        var options = new ChangeEndpointOptions { Json = builder.Json };
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(static provider => new ChangeEndpoints(provider.GetRequiredService<ChangeEndpointOptions>(),
            provider.GetRequiredService<ChangePipeline>(), provider.GetRequiredService<IAggregateReader>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<IChangeAuthorization>()));
        services.AddOptions<WebhookOptions>();
        services.AddSingleton<Webhooks>();
        services.Configure<JsonOptions>(o =>
        {
            // TN-33 (Tatalu FB-4): the application's contexts come right after the value objects, before ASP.NET's own
            // resolvers (the reflection-based default when reflection is on). Appended, the default answered first: it writes
            // enums as numbers although a context with UseStringEnumConverter writes names, so the OpenAPI document said
            // integer while the entries wrote names.
            o.SerializerOptions.AddValueObjects();
            var at = o.SerializerOptions.TypeInfoResolverChain.IndexOf(ValueObjectJson.Resolver) + 1;
            foreach (var resolver in options.Json.TypeInfoResolverChain)
            {
                if (resolver == ValueObjectJson.Resolver || o.SerializerOptions.TypeInfoResolverChain.Contains(resolver))
                    continue;
                o.SerializerOptions.TypeInfoResolverChain.Insert(at++, resolver);
            }
        });
        return builder;
    }
}
