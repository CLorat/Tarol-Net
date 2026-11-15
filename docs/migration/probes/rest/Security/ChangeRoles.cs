using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Tarol.Security;

/// <summary>Candidate AZ-D: the app defines its roles; Tarol maps each role to the changes and reads it may run.
/// A grant is one handler among the others (ASP.NET Core semantics: allowed = something succeeded and nothing failed),
/// so row rules on the loaded aggregate (ChangeHandler: Deny/Succeed) combine with it without a new evaluation model.</summary>
public sealed class ChangeRoles
{
    private readonly Dictionary<string, HashSet<Type>> _grants = new(StringComparer.Ordinal);
    private readonly HashSet<Type> _authenticated = [];

    /// <summary>The changes (and Read&lt;T&gt; intents) a role may run.</summary>
    public RoleGrants Role(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (!_grants.TryGetValue(role, out var set))
            _grants[role] = set = [];
        return new RoleGrants(set);
    }

    /// <summary>Changes any authenticated caller may run (row rules still apply).</summary>
    public RoleGrants Authenticated() => new(_authenticated);

    internal bool Grants(ClaimsPrincipal user, Type change)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;
        if (_authenticated.Contains(change))
            return true;
        foreach (var (role, set) in _grants)
            if (set.Contains(change) && user.IsInRole(role))
                return true;
        return false;
    }
}

public readonly struct RoleGrants
{
    private readonly HashSet<Type> _set;
    internal RoleGrants(HashSet<Type> set) => _set = set;

    public RoleGrants May<TChange>()
    {
        _set.Add(typeof(TChange));
        return this;
    }
}

internal sealed class RoleGrantHandler(ChangeRoles roles) : AuthorizationHandler<ChangeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ChangeRequirement requirement)
    {
        if (roles.Grants(context.User, requirement.Change.GetType()))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class ChangeRolesServiceCollectionExtensions
{
    public static IServiceCollection AddTarolSecurity(this IServiceCollection services, Action<ChangeRoles> roles)
    {
        var map = new ChangeRoles();
        roles(map);
        services.AddTarolSecurity();
        services.AddSingleton(map);
        services.AddSingleton<IAuthorizationHandler, RoleGrantHandler>();
        return services;
    }
}
