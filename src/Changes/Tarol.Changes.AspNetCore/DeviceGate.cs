using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Results;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// Refuses requests of an ended device (TN-60, Tatalu FB-F13.2-4): a lost tablet's token stays valid until it expires, but its
/// <c>Tarol-Device</c> is ended in the app's device registry. Register it as a singleton and add <see cref="DeviceGateExtensions.UseDeviceGate"/>
/// after <c>UseAuthentication</c>.
/// </summary>
public interface IDeviceGate
{
    /// <summary>Whether <paramref name="user"/> may still use <paramref name="device"/>; asked for authenticated requests with a <c>Tarol-Device</c>.</summary>
    /// <param name="device">The <c>Tarol-Device</c> value.</param>
    /// <param name="user">The caller.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns><see langword="false"/>: the request is refused with 401 <c>Device.Ended</c>.</returns>
    ValueTask<bool> IsActive(string device, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>The <see cref="IDeviceGate"/> middleware.</summary>
public static class DeviceGateExtensions
{
    /// <summary>The code of the refusal of an ended device.</summary>
    public const string EndedCode = "Device.Ended";

    /// <summary>
    /// Asks the registered <see cref="IDeviceGate"/> for every authenticated request that names a device (<c>Tarol-Device</c>), and
    /// answers 401 with <c>WWW-Authenticate: Bearer error="invalid_token", error_description="Device.Ended"</c> for an ended one
    /// (the client stops instead of signing in again). Without a registered gate it does nothing. Place it after
    /// <c>UseAuthentication</c> and before the endpoints.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns><paramref name="app"/>.</returns>
    public static IApplicationBuilder UseDeviceGate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (http, next) =>
        {
            if (http.User.Identity?.IsAuthenticated == true
                && http.Request.Headers[TarolHeaders.Device].ToString() is { Length: > 0 } device
                && http.RequestServices.GetService<IDeviceGate>() is { } gate
                && !await gate.IsActive(device, http.User, http.RequestAborted).ConfigureAwait(false))
            {
                // a 401 has no body (ChangeEndpoints.Problem); the reason is in the challenge, which CORS exposes (TarolHeaders.Exposed)
                http.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{EndedCode}\"";
                await http.RequestServices.GetRequiredService<ChangeEndpoints>()
                    .Problem(http, Error.Unauthorized(EndedCode, "this device was ended; sign in on it again")).ConfigureAwait(false);
                return;
            }
            await next(http).ConfigureAwait(false);
        });
    }
}
