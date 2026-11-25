using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Tarol.Changes;

namespace Tarol.Security;

/// <summary>
/// The application's roles mapped onto the changes and reads each may run (docs/architecture/rest.md §3, R-5). The map is
/// one grant among the row rules: a change is allowed when the map or a rule grants it and no rule denies it; nothing
/// granted is denied. Roles are read with <see cref="ClaimsPrincipal.IsInRole"/> (the identity's role claim type).
/// </summary>
public sealed class ChangeRoles
{
    private readonly Dictionary<string, HashSet<Type>> _grants = new(StringComparer.Ordinal);
    private readonly HashSet<Type> _authenticated = [];
    private readonly Dictionary<string, HashSet<(Type Read, Type View)>> _views = new(StringComparer.Ordinal);
    private readonly HashSet<(Type Read, Type View)> _authenticatedViews = [];

    /// <summary>The changes and reads <paramref name="role"/> may run.</summary>
    /// <param name="role">The application's role name.</param>
    /// <returns>The role's grants, to add to.</returns>
    public RoleGrants Role(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (!_grants.TryGetValue(role, out var set))
            _grants[role] = set = [];
        if (!_views.TryGetValue(role, out var views))
            _views[role] = views = [];
        return new RoleGrants(set, views);
    }

    /// <summary>The changes and reads any authenticated caller may run (row rules still apply).</summary>
    /// <returns>The grants, to add to.</returns>
    public RoleGrants Authenticated() => new(_authenticated, _authenticatedViews);

    /// <summary>
    /// Whether the role map grants the read <paramref name="read"/> (a <c>Read&lt;T&gt;</c>) of <paramref name="scope"/>'s view (TN-43,
    /// VA-2): its own grant (<c>MayRead&lt;T, TView&gt;</c>), or <c>Read&lt;T&gt;</c> when the view is not restricted.
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="read">The read intent.</param>
    /// <param name="scope">The view.</param>
    /// <returns>Whether a role grants it.</returns>
    public bool Grants(ClaimsPrincipal user, object read, ReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(read);
        if (user.Identity?.IsAuthenticated != true)
            return false;
        var key = (read.GetType(), scope.View);
        if (_authenticatedViews.Contains(key))
            return true;
        foreach (var (role, views) in _views)
            if (views.Contains(key) && user.IsInRole(role))
                return true;
        return !scope.Restricted && Grants(user, read);
    }

    /// <summary>Whether the map grants <paramref name="change"/> to <paramref name="user"/>.</summary>
    /// <param name="user">The caller.</param>
    /// <param name="change">The change input (or a <see cref="Read{TAggregate}"/> intent).</param>
    /// <returns>Whether a role of the caller (or "authenticated") grants it.</returns>
    public bool Grants(ClaimsPrincipal user, object change)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(change);
        if (user.Identity?.IsAuthenticated != true)
            return false;
        var type = change.GetType();
        if (_authenticated.Contains(type))
            return true;
        foreach (var (role, set) in _grants)
            if (set.Contains(type) && user.IsInRole(role))
                return true;
        return false;
    }
}

/// <summary>The grants of one role.</summary>
public readonly struct RoleGrants : IEquatable<RoleGrants>
{
    private readonly HashSet<Type> _set;

    private readonly HashSet<(Type Read, Type View)> _views;

    internal RoleGrants(HashSet<Type> set, HashSet<(Type Read, Type View)> views)
    {
        _set = set;
        _views = views;
    }

    /// <summary>
    /// Grants one view, list or row query of <typeparamref name="TAggregate"/> (TN-43, owner decision VA-2b): needed for a
    /// <c>Restricted</c> one, and enough without <c>Read&lt;TAggregate&gt;</c> for an unrestricted one. Row rules still run.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate.</typeparam>
    /// <typeparam name="TView">The view or query record.</typeparam>
    /// <returns>These grants.</returns>
    public RoleGrants MayRead<TAggregate, TView>()
    {
        _views.Add((typeof(Read<TAggregate>), typeof(TView)));
        return this;
    }

    /// <summary>The role may run <typeparamref name="TChange"/> (a change record, or <c>Read&lt;TAggregate&gt;</c>).</summary>
    /// <typeparam name="TChange">The change or read intent.</typeparam>
    /// <returns>The same grants, to chain.</returns>
    public RoleGrants May<TChange>()
    {
        _set.Add(typeof(TChange));
        return this;
    }

    /// <inheritdoc />
    public bool Equals(RoleGrants other) => ReferenceEquals(_set, other._set);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RoleGrants other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _set?.GetHashCode() ?? 0;

    /// <summary>Equality.</summary>
    /// <param name="left">A grant set.</param>
    /// <param name="right">Another.</param>
    /// <returns>Whether both are the same role's grants.</returns>
    public static bool operator ==(RoleGrants left, RoleGrants right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">A grant set.</param>
    /// <param name="right">Another.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(RoleGrants left, RoleGrants right) => !left.Equals(right);
}

/// <summary>The role map as a handler: it grants, it never denies.</summary>
internal sealed class RoleGrantHandler(ChangeRoles roles) : AuthorizationHandler<ChangeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ChangeRequirement requirement)
    {
        if (requirement.View is { } scope ? roles.Grants(context.User, requirement.Change, scope) : roles.Grants(context.User, requirement.Change))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
