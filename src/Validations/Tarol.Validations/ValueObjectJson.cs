using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tarol.Validations;

/// <summary>
///     The JSON converters of every generated value object, as a resolver for <see cref="JsonSerializerOptions" />.
/// </summary>
/// <remarks>
///     <para>
///         A value object with one field serializes as its bare value (<c>"total":40</c>) through a generated
///         <c>ValueJsonConverter</c> that reads <b>without validation</b>, so old events always load. The type carries a
///         <c>[JsonConverter]</c> attribute, which reflection and source-generated contexts in <b>other</b> assemblies see.
///         A source-generated <see cref="JsonSerializerContext" /> in the <b>same</b> assembly does not (source generators
///         do not see each other's output) and would write <c>{"value":40}</c>; put <see cref="Resolver" /> first in its
///         options with <see cref="Options(JsonSerializerContext)" /> (TVAL009 points at the direct use).
///     </para>
///     <para>Generated code registers each converter from a module initializer; registration is AOT-safe.</para>
/// </remarks>
public static class ValueObjectJson
{
    private static readonly ConcurrentDictionary<Type, Func<JsonSerializerOptions, JsonTypeInfo>> Factories = new();

    /// <summary>The resolver that returns metadata for every registered value object and <see langword="null" /> for other types.</summary>
    public static IJsonTypeInfoResolver Resolver { get; } = new ValueObjectResolver();

    /// <summary>A copy of <paramref name="context" />'s options with <see cref="Resolver" /> first in the resolver chain.</summary>
    /// <param name="context">A source-generated context.</param>
    /// <returns>New options; cache them, as with any <see cref="JsonSerializerOptions" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context" /> is <see langword="null" />.</exception>
    public static JsonSerializerOptions Options(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new JsonSerializerOptions(context.Options);
        options.TypeInfoResolverChain.Insert(0, Resolver);
        return options;
    }

    /// <summary>Puts <see cref="Resolver" /> first in the resolver chain of <paramref name="options" /> (before they are used).</summary>
    /// <param name="options">Mutable options, e.g. the event store's or ASP.NET Core's.</param>
    /// <returns><paramref name="options" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The options are read-only.</exception>
    public static JsonSerializerOptions AddValueObjects(this JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TypeInfoResolverChain.Contains(Resolver))
            options.TypeInfoResolverChain.Insert(0, Resolver);
        return options;
    }

    /// <summary>Registers the converter of a generated value object. Called by generated code.</summary>
    /// <typeparam name="T">The value object.</typeparam>
    /// <param name="converter">Its converter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="converter" /> is <see langword="null" />.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Register<T>(JsonConverter<T> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        Factories[typeof(T)] = options => JsonMetadataServices.CreateValueInfo<T>(options, converter);
    }

    /// <summary>Whether a converter for <paramref name="type" /> is registered.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true" /> for a registered value object.</returns>
    public static bool IsRegistered(Type type) => Factories.ContainsKey(type);

    private sealed class ValueObjectResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            => Factories.TryGetValue(type, out var factory) ? factory(options) : null;
    }
}
