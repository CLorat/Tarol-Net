namespace Tarol.Primitives;

/// <summary>The stored name of an event type and its schema version, as <see cref="EventTypeAttribute"/> or the default rule gives it.</summary>
/// <param name="Alias">The stable alias, e.g. <c>gallery.artwork.repriced</c>; never the CLR name.</param>
/// <param name="Version">The schema version of the payload, starting at 1.</param>
public readonly record struct EventType(string Alias, int Version);
