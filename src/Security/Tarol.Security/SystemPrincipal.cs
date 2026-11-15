using System.Security.Claims;
using Tarol.Kernel;

namespace Tarol.Security;

/// <summary>
/// A principal for an internal run that must be authorized after all (R-6: a system action that is a new privilege, e.g. a
/// refund above a limit): pass it as <c>ChangeOptions.Principal</c> and grant its roles in the map.
/// </summary>
public static class SystemPrincipal
{
    /// <summary>The authentication type of system principals.</summary>
    public const string AuthenticationType = "Tarol.System";

    /// <summary>A principal for <paramref name="actor"/> (claims <c>sub</c> = the actor's id, <c>role</c> = <paramref name="roles"/>).</summary>
    /// <param name="actor">The system actor (<c>Actor.System("Jobs.Refunds")</c>).</param>
    /// <param name="roles">The roles to check it with.</param>
    /// <returns>An authenticated principal.</returns>
    public static ClaimsPrincipal For(Actor actor, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(roles);
        var claims = new List<Claim> { new("sub", actor.Id) };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, "sub", "role"));
    }
}
