using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Persistence;

namespace Tarol.Security;

/// <summary>Registers the change authorization.</summary>
public static class SecurityTarolBuilderExtensions
{
    /// <summary>
    /// ASP.NET Core authorization (core), the role map and <see cref="ChangeAuthorization"/> as the pipeline's
    /// <see cref="IChangeAuthorization"/>. Row rules (<see cref="ChangeHandler{TChange, TAggregate}"/>) are registered by the
    /// app as singleton <see cref="IAuthorizationHandler"/>s.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="roles">The app's roles onto changes and reads: <c>roles =&gt; roles.Role("seller").May&lt;CloseAuction&gt;()</c>.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder AddSecurity(this TarolBuilder builder, Action<ChangeRoles> roles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddTarolSecurity(roles);
        return builder;
    }

    /// <summary>The same as <see cref="AddSecurity"/> for an app wired without the builder.</summary>
    /// <param name="services">The services.</param>
    /// <param name="roles">The app's roles onto changes and reads.</param>
    /// <returns>The services.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public static IServiceCollection AddTarolSecurity(this IServiceCollection services, Action<ChangeRoles> roles)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(roles);
        if (services.Any(static d => d.ServiceType == typeof(ChangeRoles)))
            throw new InvalidOperationException("AddSecurity was called twice; one role map per application.");
        var map = new ChangeRoles();
        roles(map);
        services.AddLogging();                                  // AddAuthorizationCore needs ILogger<T>; TryAdd, keeps the host's
        services.AddAuthorizationCore();
        services.AddSingleton(map);
        services.AddSingleton<IAuthorizationHandler, RoleGrantHandler>();
        services.AddSingleton<IChangeAuthorization, ChangeAuthorization>();
        return services;
    }
}
