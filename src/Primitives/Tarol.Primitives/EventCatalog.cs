namespace Tarol.Primitives;

/// <summary>One event type of an aggregate as it is stored: its alias and schema version, and its CLR type.</summary>
/// <param name="Type">The stored alias and version (<see cref="EventTypeAttribute"/> or the default rule).</param>
/// <param name="ClrType">The event record.</param>
public readonly record struct EventCatalogEntry(EventType Type, Type ClrType)
{
    /// <summary>The event's <see cref="PersonalDataAttribute">[PersonalData]</see> members; <see langword="null"/> = none (stored as plain JSON).</summary>
    public PersonalShape? Personal { get; init; }

    /// <summary>How the event is audited (TN-64); <see langword="null"/> = every member, as a change.</summary>
    public EventAudit? Audit { get; init; }
}

/// <summary>
/// The stored names of an aggregate: its stream type and every concrete event it raises. The generator emits one per
/// aggregate as <c>T.EventTypes</c>; an event codec reads it to map a stored alias back to a type, without reflection.
/// </summary>
public sealed class EventCatalog
{
    /// <summary>Creates a catalog.</summary>
    /// <param name="streamType">The aggregate's stream type.</param>
    /// <param name="events">Its events.</param>
    /// <exception cref="ArgumentException"><paramref name="streamType"/> is empty.</exception>
    public EventCatalog(string streamType, params EventCatalogEntry[] events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamType);
        ArgumentNullException.ThrowIfNull(events);
        StreamType = streamType;
        Events = events;
    }

    /// <summary>The stream type, e.g. <c>auction</c>.</summary>
    public string StreamType { get; }

    /// <summary>The concrete events, sorted by CLR name.</summary>
    public IReadOnlyList<EventCatalogEntry> Events { get; }

    /// <summary>The <see cref="PersonalDataAttribute">[PersonalData]</see> members of the aggregate's stored state; <see langword="null"/> = none.</summary>
    public PersonalShape? State { get; init; }

    /// <summary>The aggregate's <see cref="RetentionAttribute.Days"/>; <see langword="null"/> = kept forever (unless configured).</summary>
    public int? RetentionDays { get; init; }

    /// <summary>Whether any event or the state of the aggregate holds personal data.</summary>
    public bool HasPersonalData => State is not null || Events.Any(static e => e.Personal is not null);
}
