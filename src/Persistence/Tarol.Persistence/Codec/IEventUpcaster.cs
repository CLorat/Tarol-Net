using System.Text.Json.Nodes;
using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>
/// Moves a stored event payload one step forward: from <see cref="From"/> to <see cref="Into"/> (a newer version of the
/// same alias, or a renamed alias). The <see cref="EventCodec"/> chains upcasters until it reaches a type the aggregate
/// still has. Register one per old (alias, version) with <see cref="EventCodec.Upcast{T}"/>.
/// <code>
/// public sealed class OpenedV1ToV2 : IEventUpcaster
/// {
///     public EventType From => new("auction.opened", 1);
///     public EventType Into => new("auction.opened", 2);
///     public void Upcast(JsonObject payload) => payload["currency"] = "EUR";
/// }
/// </code>
/// </summary>
public interface IEventUpcaster
{
    /// <summary>The stored alias and version this upcaster reads.</summary>
    EventType From { get; }

    /// <summary>The alias and version the payload has afterwards.</summary>
    EventType Into { get; }

    /// <summary>Rewrites the payload in place (property names as the event's JSON options write them).</summary>
    /// <param name="payload">The stored payload.</param>
    void Upcast(JsonObject payload);
}
