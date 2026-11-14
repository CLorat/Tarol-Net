using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>
/// Turns an aggregate's events into stored (alias, version, JSON payload) and back, from the generated
/// <c>T.EventTypes</c> catalog and the JSON options of your <c>JsonSerializerContext</c>. No reflection: payloads go
/// through the options' source-generated metadata, so list every event with <c>[JsonSerializable]</c> and put
/// <c>ValueObjectJson.Resolver</c> first (<c>ValueObjectJson.Options(AppJson.Default)</c>).
/// <code>
/// var codec = new EventCodec(ValueObjectJson.Options(AppJson.Default))
///     .Add&lt;Auction&gt;()                      // throws now if an event has no JSON metadata
///     .Upcast&lt;Auction&gt;(new OpenedV1ToV2());
/// </code>
/// </summary>
/// <remarks>
/// Register every aggregate before the codec is used; registration is thread-safe, reads take no lock. A stored alias or
/// version that the aggregate no longer has and that no upcaster moves throws <see cref="InvalidOperationException"/>:
/// events are the truth, a store never skips one.
/// </remarks>
public sealed class EventCodec
{
    private readonly Lock _gate = new();
    private volatile Tables _tables = new([], [], [], []);

    /// <summary>Creates a codec over <paramref name="options"/> (made read-only).</summary>
    /// <param name="options">The JSON options of the payloads, with <c>ValueObjectJson.Resolver</c> first when events hold value objects.</param>
    public EventCodec(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MakeReadOnly(); // throws when the options have no resolver: pass your context's options
        Options = options;
    }

    /// <summary>The payload options (also fit for the stored state of the same aggregates).</summary>
    public JsonSerializerOptions Options { get; }

    /// <summary>Registers <typeparamref name="T"/>'s events.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns>This codec.</returns>
    /// <exception cref="InvalidOperationException">An event has no JSON metadata in <see cref="Options"/>, or another aggregate has the same stream type.</exception>
    public EventCodec Add<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var catalog = T.EventTypes;
        var missing = catalog.Events.Where(e => !Options.TryGetTypeInfo(e.ClrType, out _)).Select(static e => e.ClrType.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"The JSON options of the event codec have no metadata for {string.Join(", ", missing)} ({typeof(T).Name}): add [JsonSerializable(typeof(…))] for each to your JsonSerializerContext.");
        lock (_gate)
        {
            var t = _tables;
            if (t.StreamTypes.TryGetValue(typeof(T), out _))
                return this;
            if (t.Streams.ContainsKey(catalog.StreamType))
                throw new InvalidOperationException($"Stream type '{catalog.StreamType}' of {typeof(T).Name} is registered for another aggregate; pin a unique one with [StreamType].");
            var current = new Dictionary<(string, string), JsonTypeInfo>(t.Current);
            foreach (var e in catalog.Events)
                current[(catalog.StreamType, Key(e.Type))] = Options.GetTypeInfo(e.ClrType);
            _tables = t with
            {
                Streams = new Dictionary<string, Type>(t.Streams) { [catalog.StreamType] = typeof(T) },
                StreamTypes = new Dictionary<Type, string>(t.StreamTypes) { [typeof(T)] = catalog.StreamType },
                Current = current,
            };
        }
        return this;
    }

    /// <summary>Registers an upcaster for a stored (alias, version) of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The aggregate type (registered with <see cref="Add{T}"/>).</typeparam>
    /// <param name="upcaster">The upcaster.</param>
    /// <returns>This codec.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not registered, or an upcaster for the same <see cref="IEventUpcaster.From"/> exists.</exception>
    public EventCodec Upcast<T>(IEventUpcaster upcaster)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(upcaster);
        if (upcaster.From == upcaster.Into)
            throw new ArgumentException($"The upcaster {upcaster.GetType().Name} does not move '{upcaster.From.Alias}' v{upcaster.From.Version}.", nameof(upcaster));
        var streamType = StreamTypeOf<T>();
        lock (_gate)
        {
            var t = _tables;
            var key = (streamType, Key(upcaster.From));
            if (t.Upcasters.ContainsKey(key))
                throw new InvalidOperationException($"An upcaster for '{upcaster.From.Alias}' v{upcaster.From.Version} of {typeof(T).Name} is already registered.");
            _tables = t with { Upcasters = new Dictionary<(string, string), IEventUpcaster>(t.Upcasters) { [key] = upcaster } };
        }
        return this;
    }

    /// <summary>Whether <typeparamref name="T"/> is registered.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns><see langword="true"/> after <see cref="Add{T}"/>.</returns>
    public bool IsRegistered<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => _tables.StreamTypes.ContainsKey(typeof(T));

    /// <summary>The stream type of a registered aggregate.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns>Its stream type.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not registered.</exception>
    public string StreamTypeOf<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => _tables.StreamTypes.TryGetValue(typeof(T), out var streamType) ? streamType
            : throw new InvalidOperationException($"{typeof(T).Name} is not registered with the event codec: call Add<{typeof(T).Name}>().");

    /// <summary>Writes <paramref name="domainEvent"/>'s payload as UTF-8 JSON.</summary>
    /// <typeparam name="T">The aggregate the event belongs to (registered).</typeparam>
    /// <param name="domainEvent">The event.</param>
    /// <param name="payload">Where the JSON goes.</param>
    /// <returns>The stored alias and version.</returns>
    public EventType Encode<T>(DomainEvent domainEvent, IBufferWriter<byte> payload)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(payload);
        var type = T.EventTypeOf(domainEvent);
        if (!_tables.Current.TryGetValue((StreamTypeOf<T>(), Key(type)), out var info))
            throw new InvalidOperationException($"{domainEvent.GetType().Name} is not an event of {typeof(T).Name}.");
        using var writer = new Utf8JsonWriter(payload);
        JsonSerializer.Serialize(writer, domainEvent, info);
        return type;
    }

    /// <summary>Reads a stored event, upcasting an old payload first.</summary>
    /// <param name="streamType">The stream type it was stored under.</param>
    /// <param name="stored">Its stored alias and version.</param>
    /// <param name="payload">Its UTF-8 JSON payload.</param>
    /// <returns>The event.</returns>
    /// <exception cref="InvalidOperationException">The stream type is not registered, or no event and no upcaster match the alias and version.</exception>
    /// <exception cref="JsonException">The payload is malformed.</exception>
    public DomainEvent Decode(string streamType, EventType stored, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(streamType);
        var t = _tables;
        if (!t.Streams.ContainsKey(streamType))
            throw new InvalidOperationException($"Stream type '{streamType}' is not registered with the event codec.");
        if (t.Current.TryGetValue((streamType, Key(stored)), out var info))
            return (DomainEvent)JsonSerializer.Deserialize(payload, info)!;

        var node = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException($"The payload of '{stored.Alias}' v{stored.Version} is not a JSON object.");
        var type = stored;
        for (var steps = 0; steps <= t.Upcasters.Count; steps++)
        {
            if (!t.Upcasters.TryGetValue((streamType, Key(type)), out var upcaster))
                break;
            upcaster.Upcast(node);
            type = upcaster.Into;
            if (t.Current.TryGetValue((streamType, Key(type)), out info))
                return (DomainEvent)JsonSerializer.Deserialize(node, info)!;
        }
        throw new InvalidOperationException(
            $"No event of '{streamType}' is stored as '{stored.Alias}' v{stored.Version} and no upcaster moves it: pin the old alias with [EventType] or register an IEventUpcaster.");
    }

    private static string Key(EventType type) => string.Concat(type.Alias, "@", type.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private sealed record Tables(
        Dictionary<string, Type> Streams,
        Dictionary<Type, string> StreamTypes,
        Dictionary<(string, string), JsonTypeInfo> Current,
        Dictionary<(string, string), IEventUpcaster> Upcasters);
}
