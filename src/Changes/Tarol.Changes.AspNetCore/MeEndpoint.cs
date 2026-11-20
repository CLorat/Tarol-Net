using System.Buffers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tarol.Kernel;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary><c>GET /me</c> (BC-13, opt-in): who the caller is to Tarol, so a UI shows role-dependent actions without decoding the
/// identity provider's token itself.</summary>
public static class MeEndpoint
{
    /// <summary>
    /// Maps <c>GET {pattern}</c> for authenticated callers: <c>{ "subject", "actor": { "kind", "id" }, "tenant", "roles" }</c>,
    /// read by the same rules as the change entries (the actor rule, the tenant claim and its mapping, the identity's role
    /// claims). <c>tenant</c> is <see langword="null"/> without one; 401 anonymously. The roles are what the principal carries
    /// (an identity provider's claims transformation included), not the role map's grants.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route (default <c>/me</c>).</param>
    /// <returns>The endpoint's builder.</returns>
    public static IEndpointConventionBuilder MapTarolMe(this IEndpointRouteBuilder endpoints, string pattern = "/me")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var builder = endpoints.MapGet(pattern, Me).WithName("TarolMe").WithMetadata(
            ((RequestDelegate)Me).Method,                                       // OpenAPI describes endpoints that carry a MethodInfo
            new ProducesResponseTypeMetadata(StatusCodes.Status200OK),
            new TarolEndpointMetadata("me", typeof(MeEndpoint), [], [], null) { Errors = ["Auth.Tenant", "Auth.Required"] });
        builder.RequireAuthorization();
        return builder;
    }

    private static async Task Me(HttpContext http)
    {
        var entries = ChangeEndpoints.Of(http);
        var (context, _, refused) = entries.Edge(http);
        if (refused is not null)
        {
            await entries.Problem(http, refused).ConfigureAwait(false);
            return;
        }
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await entries.Problem(http, Error.Unauthorized("Auth.Required")).ConfigureAwait(false);
            return;
        }
        CacheHeaders.Apply(http);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (user.FindFirst(entries.Options.SubjectClaim)?.Value is { } subject)
                writer.WriteString("subject", subject);
            else
                writer.WriteNull("subject");
            writer.WriteStartObject("actor");
            writer.WriteString("kind", context.Actor.Kind.ToString());
            writer.WriteString("id", context.Actor.Id);
            writer.WriteEndObject();
            if (context.Tenant.TryGetValue(out Id<ITenant> tenant))
                writer.WriteString("tenant", tenant.Value);
            else
                writer.WriteNull("tenant");
            writer.WriteStartArray("roles");
            foreach (var role in Roles(user))
                writer.WriteStringValue(role);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted).ConfigureAwait(false);
    }

    private static SortedSet<string> Roles(ClaimsPrincipal user)
    {
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var identity in user.Identities)
            foreach (var role in identity.FindAll(identity.RoleClaimType))
                roles.Add(role.Value);
        return roles;
    }
}
