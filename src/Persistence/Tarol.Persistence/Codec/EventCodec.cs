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
    private volatile Tables _tables = new([], [], [], [], [], []);

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
            var personal = new Dictionary<(string, string), ResolvedShape>(t.Personal);
            foreach (var e in catalog.Events)
            {
                current[(catalog.StreamType, Key(e.Type))] = Options.GetTypeInfo(e.ClrType);
                if (e.Personal is { } shape)
                    personal[(catalog.StreamType, Key(e.Type))] = ResolvedShape.From(shape, Options.PropertyNamingPolicy);
            }

            var states = new Dictionary<string, ResolvedShape>(t.States);
            if (catalog.State is { } state)
                states[catalog.StreamType] = ResolvedShape.From(state, naming: null); // state names are fixed by the generator
            _tables = t with
            {
                Streams = new Dictionary<string, Type>(t.Streams) { [catalog.StreamType] = typeof(T) },
                StreamTypes = new Dictionary<Type, string>(t.StreamTypes) { [typeof(T)] = catalog.StreamType },
                Current = current,
                Personal = personal,
                States = states,
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

    /// <summary>Whether a registered aggregate has <c>[PersonalData]</c> in an event or its state (it is sealed and needs keys).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns><see langword="true"/> when anything of it is sealed.</returns>
    public bool HasPersonalData<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => HasPersonalData(StreamTypeOf<T>());

    /// <summary>Whether a registered stream type has <c>[PersonalData]</c> in an event or its state.</summary>
    /// <param name="streamType">The stream type.</param>
    /// <returns><see langword="true"/> when anything of it is sealed.</returns>
    public bool HasPersonalData(string streamType)
    {
        var t = _tables;
        return t.States.ContainsKey(streamType) || t.Personal.Keys.Any(k => k.Item1 == streamType);
    }

    /// <summary>The subjects whose keys sealing a payload needs (DP-2): the stream id or the <c>Subject</c> member of each non-null personal field.</summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <param name="type">The event's stored type (what <see cref="Encode{T}(DomainEvent, IBufferWriter{byte})"/> returned).</param>
    /// <param name="payload">The plain payload.</param>
    /// <param name="stream">The stream id.</param>
    /// <param name="subjects">Receives the subjects (each once).</param>
    public void SubjectsOf<T>(EventType type, ReadOnlySpan<byte> payload, Guid stream, ICollection<Guid> subjects)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (_tables.Personal.TryGetValue((StreamTypeOf<T>(), Key(type)), out var shape))
            PersonalJson.Subjects(shape, payload, stream, subjects);
    }

    /// <summary>The subjects of an event's personal fields (encodes it once; a store prefers the payload overload).</summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <param name="domainEvent">The event.</param>
    /// <param name="stream">The stream id.</param>
    /// <returns>The subjects; empty when the event has no personal data.</returns>
    public IReadOnlyList<Guid> SubjectsOf<T>(DomainEvent domainEvent, Guid stream)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var type = Encode<T>(domainEvent, buffer);
        var subjects = new List<Guid>();
        SubjectsOf<T>(type, buffer.WrittenSpan, stream, subjects);
        return subjects;
    }

    /// <summary>Seals the personal fields of a plain payload (DP-2: after the serializer).</summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <param name="type">The event's stored type.</param>
    /// <param name="payload">The plain payload.</param>
    /// <param name="stream">The stream id.</param>
    /// <param name="keys">The keys of every subject <see cref="SubjectsOf{T}(EventType, ReadOnlySpan{byte}, Guid, ICollection{Guid})"/> returned.</param>
    /// <param name="sealedPayload">Receives the sealed payload; nothing is written when the event has no personal data.</param>
    /// <returns><see langword="false"/> when the event has no personal data: store <paramref name="payload"/> as it is.</returns>
    /// <exception cref="SubjectErasedException">A subject is erased (a store returns <see cref="DataProtectionErrors.SubjectErased"/>).</exception>
    public bool Seal<T>(EventType type, ReadOnlySpan<byte> payload, Guid stream, KeyRing keys, IBufferWriter<byte> sealedPayload)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(sealedPayload);
        if (!_tables.Personal.TryGetValue((StreamTypeOf<T>(), Key(type)), out var shape))
            return false;
        PersonalJson.Seal(shape, payload, stream, keys, sealedPayload);
        return true;
    }

    /// <summary>
    /// Seals the personal fields of a payload stored before they were marked (a history rewrite, e.g.
    /// <c>NpgsqlAggregateStore.SealHistory</c>): like <see cref="Seal{T}"/>, but a field of an erased subject is written as
    /// <c>null</c>, because the erasure could not reach the plaintext.
    /// </summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <param name="type">The event's stored type.</param>
    /// <param name="payload">The stored, plain payload (already sealed fields stay as they are).</param>
    /// <param name="stream">The stream id.</param>
    /// <param name="keys">The keys of its subjects.</param>
    /// <param name="sealedPayload">Receives the sealed payload.</param>
    /// <returns><see langword="false"/> when the event has no personal data.</returns>
    public bool SealStored<T>(EventType type, ReadOnlySpan<byte> payload, Guid stream, KeyRing keys, IBufferWriter<byte> sealedPayload)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(sealedPayload);
        if (!_tables.Personal.TryGetValue((StreamTypeOf<T>(), Key(type)), out var shape))
            return false;
        PersonalJson.Seal(shape, payload, stream, keys, sealedPayload, nullErased: true);
        return true;
    }

    /// <summary>The aliases and versions of <typeparamref name="T"/>'s events that hold personal data.</summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <returns>The stored types.</returns>
    public static IReadOnlyList<EventType> PersonalEventTypes<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => [.. T.EventTypes.Events.Where(static e => e.Personal is not null).Select(static e => e.Type)];

    /// <summary>Writes an event's payload with its personal fields sealed.</summary>
    /// <typeparam name="T">The aggregate the event belongs to (registered).</typeparam>
    /// <param name="domainEvent">The event.</param>
    /// <param name="payload">Where the JSON goes.</param>
    /// <param name="stream">The stream id.</param>
    /// <param name="keys">The keys of the event's subjects.</param>
    /// <returns>The stored alias and version.</returns>
    /// <exception cref="SubjectErasedException">A subject is erased.</exception>
    public EventType Encode<T>(DomainEvent domainEvent, IBufferWriter<byte> payload, Guid stream, KeyRing keys)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(keys);
        var plain = new ArrayBufferWriter<byte>(256);
        var type = Encode<T>(domainEvent, plain);
        if (!Seal<T>(type, plain.WrittenSpan, stream, keys, payload))
        {
            plain.WrittenSpan.CopyTo(payload.GetSpan(plain.WrittenCount));
            payload.Advance(plain.WrittenCount);
        }

        return type;
    }

    /// <summary>The subjects of every sealed field in a stored payload or state (the keys opening it needs).</summary>
    /// <param name="stored">The stored JSON.</param>
    /// <param name="subjects">Receives the subjects (each once).</param>
    public static void SealedSubjects(ReadOnlySpan<byte> stored, ICollection<Guid> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (PersonalJson.HasEnvelope(stored))
            PersonalJson.SealedSubjects(stored, subjects);
    }

    /// <summary>Whether stored JSON holds a sealed field (a cheap byte search, no parse).</summary>
    /// <param name="stored">The stored JSON.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool IsSealed(ReadOnlySpan<byte> stored) => PersonalJson.HasEnvelope(stored);

    /// <summary>Reads a stored event, opening its sealed fields with <paramref name="keys"/> before the upcasters (DP-2).</summary>
    /// <param name="streamType">The stream type it was stored under.</param>
    /// <param name="stored">Its stored alias and version.</param>
    /// <param name="payload">Its stored payload.</param>
    /// <param name="keys">The keys of its <see cref="SealedSubjects"/>; an erased subject's fields read as <c>null</c>.</param>
    /// <returns>The event.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">A sealed field was tampered with or relabelled.</exception>
    public DomainEvent Decode(string streamType, EventType stored, ReadOnlySpan<byte> payload, KeyRing keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return DecodeOpened(streamType, stored, payload, keys);
    }

    /// <summary>The subjects of an aggregate's plain stored state (what <see cref="SealState{T}"/> needs).</summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <param name="state">The plain state (<c>T.WriteState</c>).</param>
    /// <param name="stream">The stream id.</param>
    /// <param name="subjects">Receives the subjects.</param>
    public void StateSubjectsOf<T>(ReadOnlySpan<byte> state, Guid stream, ICollection<Guid> subjects)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (_tables.States.TryGetValue(StreamTypeOf<T>(), out var shape))
            PersonalJson.Subjects(shape, state, stream, subjects);
    }

    /// <summary>Seals the personal members of an aggregate's stored state (and of its entities).</summary>
    /// <typeparam name="T">The aggregate type (registered).</typeparam>
    /// <param name="state">The plain state.</param>
    /// <param name="stream">The stream id.</param>
    /// <param name="keys">The keys of its subjects.</param>
    /// <param name="sealedState">Receives the sealed state; nothing is written when the state has no personal members.</param>
    /// <returns><see langword="false"/> when the state has no personal members: store <paramref name="state"/> as it is.</returns>
    /// <exception cref="SubjectErasedException">A subject is erased.</exception>
    public bool SealState<T>(ReadOnlySpan<byte> state, Guid stream, KeyRing keys, IBufferWriter<byte> sealedState)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(sealedState);
        if (!_tables.States.TryGetValue(StreamTypeOf<T>(), out var shape))
            return false;
        PersonalJson.Seal(shape, state, stream, keys, sealedState);
        return true;
    }

    /// <summary>Opens the sealed fields of stored JSON (a state or a payload); erased subjects' fields read as <c>null</c>.</summary>
    /// <param name="stored">The stored JSON.</param>
    /// <param name="keys">The keys of its <see cref="SealedSubjects"/>; <see langword="null"/> = every sealed field reads as <c>null</c>.</param>
    /// <param name="opened">Receives the opened JSON; nothing is written when nothing is sealed.</param>
    /// <returns><see langword="false"/> when nothing is sealed: read <paramref name="stored"/> as it is.</returns>
    public static bool OpenState(ReadOnlySpan<byte> stored, KeyRing? keys, IBufferWriter<byte> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        if (!PersonalJson.HasEnvelope(stored))
            return false;
        PersonalJson.Open(stored, keys, opened);
        return true;
    }

    /// <summary>Reads a stored event, upcasting an old payload first.</summary>
    /// <param name="streamType">The stream type it was stored under.</param>
    /// <param name="stored">Its stored alias and version.</param>
    /// <param name="payload">Its UTF-8 JSON payload.</param>
    /// <returns>The event.</returns>
    /// <exception cref="InvalidOperationException">The stream type is not registered, or no event and no upcaster match the alias and version.</exception>
    /// <exception cref="JsonException">The payload is malformed.</exception>
    /// <remarks>A sealed field reads as <c>null</c> here; pass a <see cref="KeyRing"/> to open it.</remarks>
    public DomainEvent Decode(string streamType, EventType stored, ReadOnlySpan<byte> payload) => DecodeOpened(streamType, stored, payload, null);

    private DomainEvent DecodeOpened(string streamType, EventType stored, ReadOnlySpan<byte> payload, KeyRing? keys)
    {
        if (!PersonalJson.HasEnvelope(payload))
            return DecodePlain(streamType, stored, payload);
        var opened = new ArrayBufferWriter<byte>(payload.Length);
        PersonalJson.Open(payload, keys, opened);
        return DecodePlain(streamType, stored, opened.WrittenSpan);
    }

    private DomainEvent DecodePlain(string streamType, EventType stored, ReadOnlySpan<byte> payload)
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
        Dictionary<(string, string), IEventUpcaster> Upcasters,
        Dictionary<(string, string), ResolvedShape> Personal,
        Dictionary<string, ResolvedShape> States);
}
