using System.Text.Json;

namespace Tarol.Persistence;

/// <summary>How an <see cref="InMemoryAggregateStore"/> behaves; the defaults keep events as objects and load by replay.</summary>
public sealed record InMemoryStoreOptions
{
    /// <summary>
    /// JSON options for the stored state (with <c>ValueObjectJson.Resolver</c> first when the state holds value objects);
    /// <see langword="null"/> = load by replay. Defaults to <see cref="EventCodec.Options"/> when a <see cref="Codec"/> is set.
    /// </summary>
    public JsonSerializerOptions? StateOptions { get; init; }

    /// <summary>
    /// When set, every appended event is written and read back through the codec, as the database store does, so a test
    /// fails on an event without JSON metadata or with a payload that does not round-trip.
    /// </summary>
    public EventCodec? Codec { get; init; }

    /// <summary>The unique lookups; share one instance with the production store.</summary>
    public StoreLookups Lookups { get; init; } = new();
}
