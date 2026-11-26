using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// What a capability link presents (TN-50, owner decision S-1): the entry, the tenant and the token of its route
/// (<c>POST /invitations/{tenant}/{token}/accept</c>). The caller is not signed in; the token is the authorization.
/// </summary>
/// <param name="Entry">The entry's input type name, e.g. <c>AcceptInvitation</c>.</param>
/// <param name="Input">The entry's input type.</param>
/// <param name="Tenant">The tenant of the route.</param>
/// <param name="Token">The token of the route (1 to 512 characters).</param>
public sealed record CapabilityRequest(string Entry, Type Input, Id<ITenant> Tenant, string Token);

/// <summary>
/// Checks the token of a capability link (<see cref="EndpointAttribute.Capability"/>, TN-50, S-1) before its change runs: find
/// what the token opens (by a hash of it, never the token itself), whether it is still open, and who acts. Register it as a
/// singleton; the host does not start with a capability entry and no check. Rate-limit the entries (they are anonymous).
/// </summary>
public interface ICapabilityCheck
{
    /// <summary>Checks a link.</summary>
    /// <param name="request">The entry, tenant and token.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>
    /// The actor the change runs as (e.g. <c>Actor.System("Invitations")</c>), or the refusal to answer (a
    /// <c>Error.NotFound</c> for an unknown or used token, which tells a stranger nothing; <c>Error.Gone</c> for an expired one).
    /// </returns>
    ValueTask<Result<Actor>> Check(CapabilityRequest request, CancellationToken cancellationToken);
}

public sealed partial class ChangeEndpoints
{
    /// <summary>The code of a capability link whose tenant or token is malformed: <c>Capability.Invalid</c> (404, as an unknown token).</summary>
    public const string CapabilityInvalidCode = "Capability.Invalid";

    /// <summary>
    /// The operation context and options of a capability link (TN-50, S-1): the tenant from the route, the actor from
    /// <see cref="ICapabilityCheck"/>, no principal (the token is the authorization; the change is not asked of the role map),
    /// <c>Idempotency-Key</c> scoped to (tenant, <c>capability:&lt;entry&gt;</c>, method and path), <c>If-Match</c>, <c>Tarol-Confirm</c>.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="entry">The entry's input type name.</param>
    /// <param name="input">The entry's input type.</param>
    /// <param name="body">The request body read so far (part of the key's fingerprint).</param>
    /// <returns>The context and options, or the refusal (404 malformed, the check's refusal).</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async ValueTask<(OperationContext Context, ChangeOptions Options, Violation? Refused)> CapabilityEdge(HttpContext http, string entry, Type input,
        ReadOnlyMemory<byte> body = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        ArgumentNullException.ThrowIfNull(input);
        if (http.Request.RouteValues["tenant"] is not string routed || !TenantOf(routed).TryGetValue(out Id<ITenant> tenant)
            || http.Request.RouteValues["token"] is not string { Length: > 0 and <= 512 } token)
            return (null!, default, Error.NotFound(CapabilityInvalidCode));
        var check = http.RequestServices.GetService<ICapabilityCheck>() ?? throw MissingCheck(entry);
        var checkedActor = await check.Check(new CapabilityRequest(entry, input, tenant, token), http.RequestAborted).ConfigureAwait(false);
        if (checkedActor.TryGetValue(out Violation? refused))
            return (null!, default, refused);
        var context = OperationContext.Start(checkedActor.GetValueOrThrow(), Option.Some(tenant), _time);
        return Headers(http, context, $"capability:{entry}:{http.Request.Method} {http.Request.Path}", body.Span, principal: null);
    }

    /// <summary>Fails the start of a host that maps a capability entry without an <see cref="ICapabilityCheck"/> (TN-50).</summary>
    /// <param name="services">The application's services.</param>
    /// <param name="entry">The entry's input type name.</param>
    /// <exception cref="InvalidOperationException">No check is registered.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RequireCapabilityCheck(IServiceProvider services, string entry)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<ICapabilityCheck>() is null)
            throw MissingCheck(entry);
    }

    private static InvalidOperationException MissingCheck(string entry)
        => new($"The capability link {entry} needs an ICapabilityCheck: register one (services.AddSingleton<ICapabilityCheck, …>()).");
}
