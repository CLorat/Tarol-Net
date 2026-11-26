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

/// <summary>
/// Where <see cref="DeviceGateExtensions.UseDeviceGate"/> takes the device from (TN-60). A header alone is the client's word: a lost
/// device with a valid token can leave it out. Bind the device to the token (an identity-provider claim) to make the gate a boundary.
/// </summary>
public sealed class DeviceGateOptions
{
    /// <summary>
    /// The token claim that names the device (default <c>device_id</c>). When the token has it, the gate asks for that device, and a
    /// <c>Tarol-Device</c> header naming another one is refused (401 <c>Device.Mismatch</c>).
    /// </summary>
    public string Claim { get; set; } = "device_id";

    /// <summary>
    /// Whether every authenticated request must name a device, by claim or header (default <see langword="false"/>); without one it is
    /// refused (401 <c>Device.Required</c>). Turn it on when every client is a registered device.
    /// </summary>
    public bool RequireDevice { get; set; }
}

/// <summary>The <see cref="IDeviceGate"/> middleware.</summary>
public static class DeviceGateExtensions
{
    /// <summary>The code of the refusal of an ended device.</summary>
    public const string EndedCode = "Device.Ended";

    /// <summary>The code of the refusal of a header that names another device than the token.</summary>
    public const string MismatchCode = "Device.Mismatch";

    /// <summary>The code of the refusal of a request without a device when <see cref="DeviceGateOptions.RequireDevice"/> is set.</summary>
    public const string RequiredCode = "Device.Required";

    /// <summary>
    /// Asks the registered <see cref="IDeviceGate"/> for every authenticated request that names a device, and answers 401 with
    /// <c>WWW-Authenticate: Bearer error="invalid_token", error_description="Device.Ended"</c> for an ended one (the client stops instead
    /// of signing in again). The device is the token's <see cref="DeviceGateOptions.Claim"/>, else the <c>Tarol-Device</c> header; a
    /// header alone can be left out by the client, so bind the device to the token where the gate must hold against a lost device.
    /// Without a registered gate it does nothing. Place it after <c>UseAuthentication</c> and before the endpoints.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="configure">The claim and whether a device is required.</param>
    /// <returns><paramref name="app"/>.</returns>
    public static IApplicationBuilder UseDeviceGate(this IApplicationBuilder app, Action<DeviceGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new DeviceGateOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Claim, nameof(DeviceGateOptions.Claim));
        return app.Use(async (http, next) =>
        {
            if (http.User.Identity?.IsAuthenticated == true && http.RequestServices.GetService<IDeviceGate>() is { } gate)
            {
                var claimed = http.User.FindFirst(options.Claim)?.Value;
                var header = http.Request.Headers[TarolHeaders.Device].ToString();
                var code = (claimed, header) switch
                {
                    ({ Length: > 0 } c, { Length: > 0 } h) when !string.Equals(c, h, StringComparison.Ordinal) => MismatchCode,
                    (null or "", "") when options.RequireDevice => RequiredCode,
                    _ => null,
                };
                var device = string.IsNullOrEmpty(claimed) ? header : claimed;                // the token's word over the client's
                if (code is null && device.Length > 0 && !await gate.IsActive(device, http.User, http.RequestAborted).ConfigureAwait(false))
                    code = EndedCode;
                if (code is not null)
                {
                    // a 401 has no body (ChangeEndpoints.Problem); the reason is in the challenge, which CORS exposes (TarolHeaders.Exposed)
                    http.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{code}\"";
                    await http.RequestServices.GetRequiredService<ChangeEndpoints>()
                        .Problem(http, Error.Unauthorized(code, code == EndedCode ? "this device was ended; sign in on it again" : "the request's device is not the token's")).ConfigureAwait(false);
                    return;
                }
            }
            await next(http).ConfigureAwait(false);
        });
    }
}
