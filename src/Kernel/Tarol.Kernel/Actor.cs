using System.Text.Json.Serialization;
using Tarol.Kernel.Json;
using Tarol.Options;

namespace Tarol.Kernel;

/// <summary>
/// Who performs an operation: a kind and an id. Every event records it, so it holds no personal data (no name, no
/// e-mail, no roles): events are immutable, and a subject id can be resolved or forgotten outside the event store.
/// </summary>
/// <remarks>
/// The id of a <see cref="ActorKind.User"/> is the identity provider's subject (<c>sub</c>), a string: providers issue
/// subjects that are not GUIDs, and the application's own <c>User</c> aggregate keeps its <c>Id&lt;User&gt;</c>.
/// JSON: <c>{"kind":"System","id":"Sagas.AuctionTimeout","onBehalfOf":"alice"}</c> (<see cref="ActorJsonConverter"/>).
/// </remarks>
[JsonConverter(typeof(ActorJsonConverter))]
public sealed record Actor
{
    /// <summary>Creates an actor; prefer the factories <see cref="User"/>, <see cref="Service"/>, <see cref="System"/> and <see cref="Anonymous"/>.</summary>
    /// <param name="kind">The kind of caller.</param>
    /// <param name="id">The subject, client id or component name; not empty.</param>
    /// <param name="onBehalfOf">The user subject a <see cref="ActorKind.System"/> actor acts for; only allowed for that kind.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty, or <paramref name="onBehalfOf"/> is given for a kind other than <see cref="ActorKind.System"/>.</exception>
    public Actor(ActorKind kind, string id, Option<string> onBehalfOf = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown actor kind.");
        }

        if (onBehalfOf.TryGetValue(out string? subject))
        {
            if (kind != ActorKind.System)
            {
                throw new ArgumentException("Only a System actor acts on behalf of someone.", nameof(onBehalfOf));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(subject, nameof(onBehalfOf));
        }

        Kind = kind;
        Id = id;
        OnBehalfOf = onBehalfOf;
    }

    /// <summary>The unauthenticated caller (id <c>"anonymous"</c>).</summary>
    public static Actor Anonymous { get; } = new(ActorKind.Anonymous, "anonymous");

    /// <summary>The kind of caller.</summary>
    public ActorKind Kind { get; }

    /// <summary>The subject (user), client id (service) or component name (system).</summary>
    public string Id { get; }

    /// <summary>For a <see cref="ActorKind.System"/> actor: the user subject it acts for; otherwise <see cref="None"/>.</summary>
    public Option<string> OnBehalfOf { get; }

    /// <summary>A person, by the identity provider's subject.</summary>
    /// <param name="subject">The <c>sub</c> claim.</param>
    /// <returns>The actor.</returns>
    public static Actor User(string subject) => new(ActorKind.User, subject);

    /// <summary>Another process, by its client id.</summary>
    /// <param name="clientId">The client id (e.g. the <c>azp</c> or <c>client_id</c> claim).</param>
    /// <returns>The actor.</returns>
    public static Actor Service(string clientId) => new(ActorKind.Service, clientId);

    /// <summary>Tarol or the application acting on its own, optionally on behalf of a user.</summary>
    /// <param name="component">The component, e.g. <c>"Sagas.AuctionTimeout"</c>.</param>
    /// <param name="onBehalfOf">The user subject it acts for.</param>
    /// <returns>The actor.</returns>
    public static Actor System(string component, Option<string> onBehalfOf = default) => new(ActorKind.System, component, onBehalfOf);

    /// <summary>Returns <c>User:alice</c>, <c>System:Sagas.X (for alice)</c>, …</summary>
    /// <returns>The text.</returns>
    public override string ToString()
        => OnBehalfOf.TryGetValue(out string? subject) ? $"{Kind}:{Id} (for {subject})" : $"{Kind}:{Id}";
}
