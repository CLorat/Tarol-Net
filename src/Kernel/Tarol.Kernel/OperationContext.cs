using System.Text.Json.Serialization;
using Tarol.Kernel.Json;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Kernel;

/// <summary>
/// The origin of an operation, recorded with every event it produces: who acts, for which tenant, in which conversation
/// and because of what. Immutable: build it once at the edge (request, message, job, test) and pass it on.
/// </summary>
/// <param name="Actor">Who acts.</param>
/// <param name="Tenant">The tenant; <see cref="None"/> in a single-tenant application or for a host-level operation.</param>
/// <param name="CorrelationId">The conversation; copied unchanged to every event, message and saga step that follows.</param>
/// <param name="CausationId">The command or message that caused this operation; <see cref="None"/> where a user starts it.</param>
/// <remarks>
/// There is no ambient accessor and no <see cref="AsyncLocal{T}"/>: a context that leaked into another operation would
/// be a security bug (docs/architecture/identity-context.md). Tests construct it directly. JSON: see
/// <see cref="OperationContextJsonConverter"/>; it needs no entry in your <c>JsonSerializerContext</c>.
/// </remarks>
[JsonConverter(typeof(OperationContextJsonConverter))]
public sealed record OperationContext(
    Actor Actor,
    Option<Id<ITenant>> Tenant,
    Id<ICorrelation> CorrelationId,
    Option<Id<ICausation>> CausationId)
{
    /// <summary>The actor; not <see langword="null"/>.</summary>
    public Actor Actor { get; init => field = value ?? throw new ArgumentNullException(nameof(Actor)); } = Actor ?? throw new ArgumentNullException(nameof(Actor));

    /// <summary>
    /// Where and when the operation was captured, when a device recorded it before sending (TN-60, Tatalu FB-F17.3-6): an
    /// offline outbox sends <c>Tarol-Device</c> and <c>Tarol-Captured-At</c>. <see langword="null"/> for a direct request.
    /// </summary>
    public Capture? Capture { get; init; }

    /// <summary>Starts a conversation: a new UUIDv7 correlation id read from <paramref name="clock"/>, no causation.</summary>
    /// <param name="actor">Who acts.</param>
    /// <param name="tenant">The tenant, or <see cref="Option.None"/>.</param>
    /// <param name="clock">The clock for the correlation id's timestamp (a fake one in tests).</param>
    /// <returns>The context.</returns>
    public static OperationContext Start(Actor actor, Option<Id<ITenant>> tenant, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(clock);
        return new(actor, tenant, Id<ICorrelation>.New(clock), Option.None);
    }

    /// <summary>The same conversation, caused by the command or message <paramref name="causation"/> (consumers, sagas, outbox).</summary>
    /// <param name="causation">The id of the command or message being handled.</param>
    /// <returns>A copy with <see cref="CausationId"/> set.</returns>
    public OperationContext CausedBy(Id<ICausation> causation) => this with { CausationId = causation };

    /// <summary>
    /// The system continues the conversation on its own, on behalf of the current user: a <see cref="ActorKind.System"/>
    /// actor whose <see cref="Actor.OnBehalfOf"/> is the user's subject (or the subject the current system actor already
    /// acts for). Tenant, correlation and causation are kept.
    /// </summary>
    /// <param name="component">The acting component, e.g. <c>"Sagas.AuctionTimeout"</c>.</param>
    /// <returns>A copy with the system actor.</returns>
    public OperationContext AsSystem(string component)
    {
        var onBehalfOf = Actor.Kind switch
        {
            ActorKind.User => Actor.Id,
            ActorKind.System => Actor.OnBehalfOf,
            _ => default(Option<string>),
        };
        return this with { Actor = Actor.System(component, onBehalfOf) };
    }
}

/// <summary>The device and the time of an operation captured before it was sent (TN-60): from an offline outbox.</summary>
/// <param name="Device">The app's opaque id of the device (<c>Tarol-Device</c>), or <see langword="null"/>.</param>
/// <param name="At">When the device captured it (<c>Tarol-Captured-At</c>), checked against the server's clock, or <see langword="null"/>.</param>
public sealed record Capture(string? Device, DateTimeOffset? At);
