using System.Text.Json.Serialization.Metadata;

namespace Tarol.Persistence;

/// <summary>
/// What one assembly (a bounded context) brings to a Tarol application (TN-8): its JSON context, its aggregates, its reactions
/// and typed parts other packages read (projections, jobs, HTTP entries, OpenAPI, the role map). The generator writes one per
/// assembly with <c>[assembly: GenerateTarolModule]</c> (<c>GeneratedModule.Module</c>); <c>services.AddTarol(moduleA, moduleB)</c>
/// combines them, so an application of several assemblies lists each assembly once. Immutable: <see cref="With{TPart}"/> copies.
/// </summary>
public sealed class TarolModule
{
    private readonly Dictionary<Type, object> _parts;

    /// <summary>Creates a module.</summary>
    /// <param name="name">The module's name (the assembly's by default): unique in the application.</param>
    public TarolModule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _parts = [];
    }

    private TarolModule(TarolModule from, Type type, object part)
    {
        Name = from.Name;
        Json = from.Json;
        Aggregates = from.Aggregates;
        Reactions = from.Reactions;
        _parts = new Dictionary<Type, object>(from._parts) { [type] = part };
    }

    /// <summary>The module's name.</summary>
    public string Name { get; }

    /// <summary>The module's source-generated JSON context(s): events, stored state, inputs, rows; asked with the application's options.</summary>
    public IJsonTypeInfoResolver? Json { get; init; }

    /// <summary>Adds the module's aggregates to the codec: <c>codec =&gt; codec.Add&lt;Membership&gt;()</c>.</summary>
    public Action<EventCodec>? Aggregates { get; init; }

    /// <summary>The module's reactions (<c>GeneratedReactions.Set</c>), if any; combined with the other modules'.</summary>
    public IOutboxReactions? Reactions { get; init; }

    /// <summary>The typed parts (one per type).</summary>
    public IReadOnlyCollection<object> Parts => _parts.Values;

    /// <summary>A copy with <paramref name="part"/> as its <typeparamref name="TPart"/> (replacing one of that type).</summary>
    /// <typeparam name="TPart">The part's type, as readers ask for it (<c>ProjectionModule</c>, <c>EndpointModule</c>, a <c>JobSet</c>, …).</typeparam>
    /// <param name="part">The part.</param>
    /// <returns>The new module.</returns>
    public TarolModule With<TPart>(TPart part)
        where TPart : class
    {
        ArgumentNullException.ThrowIfNull(part);
        return new TarolModule(this, typeof(TPart), part);
    }

    /// <summary>The module's <typeparamref name="TPart"/>, if it has one.</summary>
    /// <typeparam name="TPart">The part's type.</typeparam>
    /// <returns>The part, or <see langword="null"/>.</returns>
    public TPart? Part<TPart>()
        where TPart : class
        => _parts.TryGetValue(typeof(TPart), out var part) ? (TPart)part : null;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>The modules of the application, as <c>AddTarol(modules)</c> registered them (a singleton; <c>MapModules</c> reads it).</summary>
/// <param name="All">The modules, in the order given.</param>
public sealed record TarolModules(IReadOnlyList<TarolModule> All)
{
    /// <summary>The modules registered in <paramref name="services"/> (while building them, before a provider exists).</summary>
    /// <param name="services">The services.</param>
    /// <returns>The modules.</returns>
    /// <exception cref="InvalidOperationException">The application was not registered with <c>AddTarol(modules)</c>.</exception>
    public static IReadOnlyList<TarolModule> In(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var descriptor in services)
            if (descriptor.ServiceType == typeof(TarolModules) && descriptor.ImplementationInstance is TarolModules { All.Count: > 0 } modules)
                return modules.All;
        throw new InvalidOperationException("No modules are registered: call services.AddTarol(modules) first.");
    }
}
