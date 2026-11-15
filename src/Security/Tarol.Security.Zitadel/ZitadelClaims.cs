using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Tarol.Kernel;
using Tarol.StronglyTypedId;

namespace Tarol.Security.Zitadel;

/// <summary>
/// Zitadel's token shape → what Tarol reads (docs/architecture/rest.md §11): the roles of the project (one JSON-object
/// claim <c>urn:zitadel:iam:org:project:&lt;id&gt;:roles</c>) become <c>role</c> claims; the user's organisation
/// (<c>urn:zitadel:iam:user:resourceowner:id</c>, requested with the scope <c>urn:zitadel:iam:user:resourceowner</c>)
/// becomes the <c>tenant</c> claim through <see cref="ZitadelTenants.Of"/>.
/// </summary>
public sealed class ZitadelClaimsTransformation : IClaimsTransformation
{
    /// <summary>Added once per identity, so a second call (ASP.NET Core may call it per authentication) changes nothing.</summary>
    public const string MarkerClaim = "tarol:zitadel";

    private readonly string _projectRoles;

    /// <summary>Creates the transformation for a project.</summary>
    /// <param name="options">The project.</param>
    public ZitadelClaimsTransformation(ZitadelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _projectRoles = $"urn:zitadel:iam:org:project:{options.ProjectId}:roles";
    }

    /// <inheritdoc />
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(c => c.Type == MarkerClaim))
            return Task.FromResult(principal);
        identity.AddClaim(new Claim(MarkerClaim, "1"));
        foreach (var claim in identity.FindAll(c => c.Type == _projectRoles || c.Type == "urn:zitadel:iam:org:project:roles").ToArray())
        {
            try
            {
                using var roles = JsonDocument.Parse(claim.Value);
                if (roles.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var role in roles.RootElement.EnumerateObject())
                    if (!identity.HasClaim(identity.RoleClaimType, role.Name))
                        identity.AddClaim(new Claim(identity.RoleClaimType, role.Name));
            }
            catch (JsonException)
            {
                // not Zitadel's shape: no roles from it
            }
        }
        if (!identity.HasClaim(c => c.Type == "tenant") && identity.FindFirst("urn:zitadel:iam:user:resourceowner:id")?.Value is { Length: > 0 } organisation)
            identity.AddClaim(new Claim("tenant", ZitadelTenants.Of(organisation).ToString()));
        return Task.FromResult(principal);
    }
}

/// <summary>Zitadel organisations as Tarol tenants: an organisation id is numeric, a tenant an <c>Id&lt;ITenant&gt;</c>.</summary>
public static class ZitadelTenants
{
    /// <summary>The tenant of an organisation: a stable (name-based, version 8) Guid, the same in every process and release.
    /// Use it wherever an organisation id must be a tenant (webhook sender entries, seed data).</summary>
    /// <param name="organisation">The organisation id, e.g. <c>"399724476671000579"</c>.</param>
    /// <returns>The tenant.</returns>
    public static Id<ITenant> Of(string organisation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organisation);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("zitadel-organisation:" + organisation));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return (Id<ITenant>)new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>The actor rule for Zitadel: a principal with the service role is <c>Actor.Service(client_id)</c>, any other
    /// <c>Actor.User(sub)</c>.</summary>
    /// <param name="serviceRole">The service role, or <see langword="null"/>.</param>
    /// <returns>The rule for <c>ChangeEndpointOptions.Actor</c>.</returns>
    public static Func<ClaimsPrincipal, Actor> Actors(string? serviceRole) => user =>
    {
        var subject = user.FindFirst("sub")?.Value ?? "unknown";
        return serviceRole is not null && user.IsInRole(serviceRole)
            ? Actor.Service(user.FindFirst("client_id")?.Value ?? subject)
            : Actor.User(subject);
    };
}
