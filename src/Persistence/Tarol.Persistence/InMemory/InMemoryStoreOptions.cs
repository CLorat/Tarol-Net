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

    /// <summary>
    /// The reactions (a generated <c>GeneratedReactions.Set</c>): one outbox row per (event, reaction) in the commit, claimed
    /// from <see cref="InMemoryAggregateStore.Outbox"/>; <see langword="null"/> = no rows.
    /// </summary>
    public IOutboxReactions? Reactions { get; init; }

    /// <summary>
    /// The key store (DP-1): with it and a <see cref="Codec"/>, the <c>[PersonalData]</c> fields of events and state are sealed
    /// on append and opened on every read, so a test sees them read as <c>null</c> after an erasure and sees new data for an
    /// erased subject refused (<see cref="DataProtectionErrors.SubjectErased"/>). Aggregates without personal data are unaffected.
    /// </summary>
    public IKeyStore? Keys { get; init; }

    /// <summary>The unique lookups; share one instance with the production store.</summary>
    public StoreLookups Lookups { get; init; } = new();
}
