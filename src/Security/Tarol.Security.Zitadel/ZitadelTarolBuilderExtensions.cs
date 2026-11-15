using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Tarol.Changes.AspNetCore;
using Tarol.Persistence;

namespace Tarol.Security.Zitadel;

/// <summary>Zitadel on the <see cref="TarolBuilder"/>.</summary>
public static class ZitadelTarolBuilderExtensions
{
    /// <summary>
    /// JwtBearer as the default scheme against <see cref="ZitadelOptions.Authority"/> (audience = the project, claims as
    /// issued, <c>sub</c> as the name, <c>role</c> as the role claim), the <see cref="ZitadelClaimsTransformation"/>, and
    /// the Zitadel actor rule on the HTTP entries. Call it after <c>AddEndpoints</c>. Clients request the scopes
    /// <c>openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner
    /// urn:zitadel:iam:org:project:id:&lt;project&gt;:aud</c>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">The instance and project.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException"><c>AddEndpoints</c> was not called first, or the options are incomplete.</exception>
    public static TarolBuilder AddZitadel(this TarolBuilder builder, Action<ZitadelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ZitadelOptions();
        configure(options);
        options.Validate();
        var endpoints = builder.Services.FirstOrDefault(static d => d.ServiceType == typeof(ChangeEndpointOptions))?.ImplementationInstance as ChangeEndpointOptions
            ?? throw new InvalidOperationException("Call AddEndpoints() before AddZitadel(…): it sets the HTTP entries' actor rule.");
        endpoints.Actor ??= ZitadelTenants.Actors(options.ServiceRole);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IClaimsTransformation>(new ZitadelClaimsTransformation(options));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.Authority = options.Authority;
            jwt.Audience = options.ProjectId;
            jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters { NameClaimType = "sub", RoleClaimType = "role" };
        });
        return builder;
    }
}
