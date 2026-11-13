using System.Text.Json.Serialization;

namespace Tarol.Kernel;

/// <summary>What kind of caller an <see cref="Actor"/> is. JSON: the name (<c>"User"</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActorKind>))]
public enum ActorKind
{
    /// <summary>An unauthenticated caller.</summary>
    Anonymous,

    /// <summary>A person, identified by the identity provider's subject (<c>sub</c>).</summary>
    User,

    /// <summary>Another process that authenticated itself (client credentials), identified by its client id.</summary>
    Service,

    /// <summary>Tarol or the application acting on its own later (saga, timeout, outbox, job, migration), identified by a component name.</summary>
    System,
}
