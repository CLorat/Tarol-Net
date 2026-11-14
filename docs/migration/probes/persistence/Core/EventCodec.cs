using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tarol.Primitives;

namespace Tarol.Persistence;

// ---------------------------------------------------------------------------------------------------------------
// Candidate K-A: a library codec over (1) the alias table the Primitives generator would emit per aggregate
// (`T.Events`, written by hand in the probe as an EventCatalog) and (2) the user's JsonSerializerContext, with
// ValueObjectJson.Resolver first. Upcasters rewrite an old payload (JsonObject) one version at a time.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>One event of an aggregate: its stored name and its CLR type (what the generator would emit).</summary>
public sealed record EventCatalogEntry(EventType Type, Type ClrType);

/// <summary>The stored stream type of an aggregate and its events (generated as <c>T.Events</c> in the real design).</summary>
public sealed record EventCatalog(string StreamType, IReadOnlyList<EventCatalogEntry> Events);

/// <summary>Moves a stored payload from <see cref="From"/> to the next version of the same (or a renamed) alias.</summary>
public interface IEventUpcaster
{
    EventType From { get; }
    EventType To { get; }
    void Upcast(JsonObject payload);
}

/// <summary>An event as the codec writes it: the columns beside the payload.</summary>
public readonly record struct EncodedEvent(EventType Type, ReadOnlyMemory<byte> Payload);

public sealed class EventCodec
{
    private readonly JsonSerializerOptions _options;
    private readonly Dictionary<string, EventCatalog> _byStreamType = [];
    private readonly Dictionary<Type, string> _streamTypeOf = [];
    private readonly Dictionary<(string Stream, string Alias), (int Version, Type ClrType)> _current = [];
    private readonly Dictionary<(string Stream, string Alias, int Version), IEventUpcaster> _upcasters = [];
    private readonly Lock _gate = new();

    public EventCodec(JsonSerializerOptions options)
    {
        _options = options;
        _options.MakeReadOnly();
    }

    public JsonSerializerOptions Options => _options;

    /// <summary>Registers an aggregate; throws when an event has no JSON metadata in the options (fail at startup, not at the first write).</summary>
    public EventCodec Add<T>(EventCatalog catalog) where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var missing = catalog.Events.Where(e => !_options.TryGetTypeInfo(e.ClrType, out _)).Select(e => e.ClrType.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"No JSON metadata for {string.Join(", ", missing)}: add [JsonSerializable(typeof(…))] to your context.");
        lock (_gate)
        {
            _byStreamType[catalog.StreamType] = catalog;
            _streamTypeOf[typeof(T)] = catalog.StreamType;
            foreach (var e in catalog.Events)
                _current[(catalog.StreamType, e.Type.Alias)] = (e.Type.Version, e.ClrType);
        }
        return this;
    }

    public EventCodec Upcast<T>(IEventUpcaster upcaster) where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate)
            _upcasters[(StreamTypeOf<T>(), upcaster.From.Alias, upcaster.From.Version)] = upcaster;
        return this;
    }

    public string StreamTypeOf<T>() where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        lock (_gate)
            return _streamTypeOf.TryGetValue(typeof(T), out var s) ? s
                : throw new InvalidOperationException($"{typeof(T).Name} is not registered with the event codec.");
    }

    public bool IsRegistered(string streamType)
    {
        lock (_gate)
            return _byStreamType.ContainsKey(streamType);
    }

    public EncodedEvent Encode<T>(DomainEvent domainEvent, ArrayBufferWriter<byte> buffer) where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var type = T.EventTypeOf(domainEvent);
        lock (_gate)
            if (!_current.TryGetValue((_streamTypeOf[typeof(T)], type.Alias), out var known) || known.Version != type.Version || known.ClrType != domainEvent.GetType())
                throw new InvalidOperationException($"{domainEvent.GetType().Name} ({type.Alias} v{type.Version}) is not in the catalog of {typeof(T).Name}.");
        var start = buffer.WrittenCount;
        using (var writer = new Utf8JsonWriter(buffer))
            JsonSerializer.Serialize(writer, domainEvent, _options.GetTypeInfo(domainEvent.GetType()));
        return new EncodedEvent(type, buffer.WrittenMemory[start..]);
    }

    public DomainEvent Decode(string streamType, string alias, int version, ReadOnlySpan<byte> payload)
    {
        (int Version, Type ClrType) current;
        lock (_gate)
        {
            if (!_byStreamType.ContainsKey(streamType))
                throw new InvalidOperationException($"Stream type '{streamType}' is not registered with the event codec.");
            if (!_current.TryGetValue((streamType, alias), out current) || current.Version != version)
                return DecodeUpcast(streamType, alias, version, payload);
        }
        return (DomainEvent)JsonSerializer.Deserialize(payload, _options.GetTypeInfo(current.ClrType))!;
    }

    private DomainEvent DecodeUpcast(string streamType, string alias, int version, ReadOnlySpan<byte> payload)
    {
        var node = JsonNode.Parse(payload)!.AsObject();
        while (true)
        {
            if (_current.TryGetValue((streamType, alias), out var current) && current.Version == version)
                return (DomainEvent)JsonSerializer.Deserialize(node, _options.GetTypeInfo(current.ClrType))!;
            if (!_upcasters.TryGetValue((streamType, alias, version), out var upcaster))
                throw new InvalidOperationException(
                    $"No event of '{streamType}' is stored as '{alias}' v{version} and no upcaster moves it: pin the old alias with [EventType] or register an IEventUpcaster.");
            upcaster.Upcast(node);
            (alias, version) = (upcaster.To.Alias, upcaster.To.Version);
        }
    }
}
