using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
        ArgumentNullException.ThrowIfNull(json);
        return Register(services, ValueObjectJson.Options(json), aggregates, []);
    }

    /// <summary>
    /// <see cref="AddTarol(IServiceCollection, JsonSerializerContext, Action{EventCodec})"/> over several JSON contexts (TN-10: one
    /// per assembly). Each context is asked with the application's options, so the copies other packages make of them
    /// (<see cref="ValueObjectJson.Options(IReadOnlyList{IJsonTypeInfoResolver})"/>) read every context; the first context's
    /// settings apply to all.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="json">The contexts, e.g. <c>[DatenschutzJson.Default, EinrichtungJson.Default]</c>.</param>
    /// <param name="aggregates">Adds the aggregates to the codec.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException"><c>AddTarol</c> was called before.</exception>
    public static TarolBuilder AddTarol(this IServiceCollection services, IReadOnlyList<IJsonTypeInfoResolver> json, Action<EventCodec> aggregates)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Register(services, ValueObjectJson.Options(json), aggregates, []);
    }

    /// <summary>
    /// Registers an application made of modules (TN-8): one <see cref="TarolModule"/> per assembly, generated with
    /// <c>[assembly: GenerateTarolModule]</c> (<c>GeneratedModule.Module</c>). Their JSON contexts are combined (in order), their
    /// aggregates added to one codec, their reactions combined (<see cref="ReactionSet.Combine"/>: a reaction name twice is an
    /// error) and registered with <see cref="TarolBuilder.WithReactions"/>. <see cref="TarolBuilder.Modules"/> and the
    /// <see cref="TarolModules"/> singleton list them for the other packages: <c>AddProjections(p =&gt; p.Add(tarol.Modules))</c>,
    /// <c>AddJobs(j =&gt; j.Add(tarol.Modules))</c>, <c>AddSecurity</c>, <c>app.MapModules()</c>, <c>tarol.Modules.ConfigureOpenApi(o)</c>.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="modules">The modules; names are unique.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">No module, two modules of one name, or no module with a JSON context.</exception>
    /// <exception cref="InvalidOperationException"><c>AddTarol</c> was called before.</exception>
    public static TarolBuilder AddTarol(this IServiceCollection services, params TarolModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Length == 0)
            throw new ArgumentException("An application needs at least one module.", nameof(modules));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
            if (!names.Add((module ?? throw new ArgumentException("A module is null.", nameof(modules))).Name))
                throw new ArgumentException($"Two modules are named '{module.Name}'; module names are unique.", nameof(modules));
        var json = modules.Select(static m => m.Json).OfType<IJsonTypeInfoResolver>().ToArray();
        if (json.Length == 0)
            throw new ArgumentException("No module has a JSON context; declare a JsonSerializerContext in one of them.", nameof(modules));
        var builder = Register(services, ValueObjectJson.Options(json), codec =>
        {
            foreach (var module in modules)
                module.Aggregates?.Invoke(codec);
        }, modules);
        var reactions = modules.Select(static m => m.Reactions).OfType<IOutboxReactions>().ToArray();
        if (reactions.Length > 0)
            builder.WithReactions(reactions.All(static r => r is ReactionSet)
                ? ReactionSet.Combine([.. reactions.Cast<ReactionSet>()])
                : OutboxReactions.Combine(reactions)!);
        return builder;
    }

    private static TarolBuilder Register(IServiceCollection services, System.Text.Json.JsonSerializerOptions options, Action<EventCodec> aggregates,
        IReadOnlyList<TarolModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(aggregates);
        if (services.Any(static d => d.ServiceType == typeof(Marker)))
            throw new InvalidOperationException("AddTarol was called twice; one call registers the application.");
        services.AddSingleton<Marker>();
        var codec = new EventCodec(options);
        aggregates(codec);
        var builder = new TarolBuilder(services, options, codec) { Modules = modules };
        services.AddSingleton(new TarolModules(modules));
        services.AddSingleton(static provider => new ChangePipeline(provider.GetRequiredService<IAggregateStore>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<IChangeAuthorization>(),
            provider.GetService<ILogger<ChangePipeline>>(), provider.GetService<IMeterFactory>()));
        return builder;
    }

    private sealed class Marker;
}
