using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;

namespace Tarol.Changes.AspNetCore;

/// <summary>The HTTP entries of a <see cref="TarolModule"/> (TN-8): its generated <c>GeneratedEndpoints.MapChanges</c>.</summary>
/// <param name="Map">Maps the entries on a route builder with a prefix for the endpoint names.</param>
public sealed record EndpointModule(Func<IEndpointRouteBuilder, string?, IEndpointRouteBuilder> Map);

/// <summary>
/// The OpenAPI transformers of a <see cref="TarolModule"/> (TN-8): its generated <c>GeneratedOpenApi.AddTarolEndpoints</c>. The
/// options are an <c>OpenApiOptions</c>, typed <see cref="object"/> because this package does not reference the OpenAPI package.
/// </summary>
/// <param name="Configure">Adds the transformers to the options.</param>
public sealed record OpenApiModule(Action<object> Configure);

/// <summary>The application side of modules over HTTP (TN-8).</summary>
public static class ModuleEndpointExtensions
{
    /// <summary>Maps the HTTP entries of every module <c>AddTarol(modules)</c> registered, in order.</summary>
    /// <param name="app">The application or a route group.</param>
    /// <param name="names">The endpoint name prefix per module (endpoint names are the OpenAPI operation ids); absent: none.</param>
    /// <returns><paramref name="app"/>.</returns>
    /// <exception cref="InvalidOperationException">The application was not registered with <c>AddTarol(modules)</c>.</exception>
    public static IEndpointRouteBuilder MapModules(this IEndpointRouteBuilder app, Func<TarolModule, string?>? names = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var modules = app.ServiceProvider.GetService<TarolModules>();
        if (modules is null || modules.All.Count == 0)
            throw new InvalidOperationException("MapModules needs an application registered with services.AddTarol(modules).");
        foreach (var module in modules.All)
            module.Part<EndpointModule>()?.Map(app, names?.Invoke(module));
        return app;
    }

    /// <summary>
    /// Adds the OpenAPI transformers of every module: <c>services.AddOpenApi(o =&gt; tarol.Modules.ConfigureOpenApi(o))</c>. Each
    /// module's transformer completes its own operations only (TN-9), so no parameter appears twice.
    /// </summary>
    /// <param name="modules">The modules (<see cref="TarolBuilder.Modules"/>).</param>
    /// <param name="openApiOptions">The document's <c>OpenApiOptions</c>.</param>
    public static void ConfigureOpenApi(this IEnumerable<TarolModule> modules, object openApiOptions)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(openApiOptions);
        foreach (var module in modules)
            module.Part<OpenApiModule>()?.Configure(openApiOptions);
    }
}
