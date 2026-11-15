using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Tarol.Kernel;

namespace ArtFlow;

/// <summary>Zitadel → what Tarol reads (app code, R-3): project roles (one JSON-object claim) become "role" claims, the
/// user's organisation (urn:zitadel:iam:user:resourceowner:id, a numeric id) becomes the "tenant" claim (a Guid derived from it),
/// machine users (role "service") are services.</summary>
internal sealed class ZitadelClaims : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(c => c.Type == "tenant"))
            return Task.FromResult(principal);
        foreach (var claim in identity.FindAll(c => c.Type.StartsWith("urn:zitadel:iam:org:project:", StringComparison.Ordinal)
                                                      && c.Type.EndsWith(":roles", StringComparison.Ordinal)).ToArray())
        {
            using var roles = JsonDocument.Parse(claim.Value);
            foreach (var role in roles.RootElement.EnumerateObject())
                identity.AddClaim(new Claim("role", role.Name));
        }
        if (identity.FindFirst("urn:zitadel:iam:user:resourceowner:id")?.Value is { } organisation)
            identity.AddClaim(new Claim("tenant", Tenant(organisation).ToString()));
        return Task.FromResult(principal);
    }

    /// <summary>A stable Guid (version 8) per Zitadel organisation id: Id&lt;ITenant&gt; is a Guid (kernel decision 3).</summary>
    public static Guid Tenant(string organisation)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("zitadel-org:" + organisation));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    public static Actor Actor(ClaimsPrincipal user)
        => user.IsInRole("service") ? Tarol.Kernel.Actor.Service(user.FindFirst("client_id")?.Value ?? "unknown")
         : Tarol.Kernel.Actor.User(user.FindFirst("sub")!.Value);
}
