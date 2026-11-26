using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// The HTTP headers of Tarol's entries that a browser on another origin must be allowed to send and read (BC-1). A CORS policy
/// that does not expose <see cref="Exposed"/> hides <c>ETag</c> and <c>Tarol-Position</c> from the page's script, so a client
/// can neither revalidate nor read its own writes. Use <see cref="WithTarolHeaders"/> on the policy.
/// </summary>
public static class TarolHeaders
{
    /// <summary><c>Idempotent-Replayed: true</c> on an answer replayed for a repeated <c>Idempotency-Key</c>.</summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";

    /// <summary>The id of the request on every answer (BC-11): quote it when reporting an error; problems carry it as <c>traceId</c>.</summary>
    public const string RequestId = "Tarol-Request-Id";

    /// <summary>The request's idempotency key (R-9).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>
    /// <c>Tarol-Confirm: Absence.QuotaExceeded[, …]</c>: the warnings (<c>Error.ConfirmationRequired</c>) the caller confirms
    /// for this change (TN-37); <c>ChangeContext.Confirmed(code)</c> reads them.
    /// </summary>
    public const string Confirm = "Tarol-Confirm";

    /// <summary><c>Tarol-Device: &lt;opaque id&gt;</c>: the device that captured the change (TN-60); <c>OperationContext.Capture.Device</c>,
    /// and the key an <see cref="IDeviceGate"/> refuses ended devices by.</summary>
    public const string Device = "Tarol-Device";

    /// <summary><c>Tarol-Captured-At: 2026-11-26T07:31:00+01:00</c>: when the device captured the change (TN-60, RFC 3339 with an
    /// offset); <c>OperationContext.Capture.At</c>, bounded by <c>ChangeEndpointOptions.CaptureSkew</c> and <c>CaptureMaxAge</c>.</summary>
    public const string CapturedAt = "Tarol-Captured-At";

    /// <summary>The response headers a browser must read: <c>ETag</c>, <c>Location</c>, <c>Tarol-Position</c>,
    /// <c>Tarol-Stale</c>, <c>Idempotent-Replayed</c>, <c>Tarol-Request-Id</c>, <c>Retry-After</c>, <c>WWW-Authenticate</c>.</summary>
    public static IReadOnlyList<string> Exposed { get; } =
        ["ETag", "Location", Positions.PositionHeader, Positions.StaleHeader, IdempotentReplayed, RequestId, "Retry-After", "WWW-Authenticate"];

    /// <summary>The request headers a browser sends: <c>Authorization</c>, <c>Content-Type</c>, <c>Idempotency-Key</c>,
    /// <c>Tarol-Confirm</c>, <c>Tarol-Device</c>, <c>Tarol-Captured-At</c>, <c>If-Match</c>, <c>If-None-Match</c>, <c>Tarol-Read-After</c>, <c>traceparent</c>.</summary>
    public static IReadOnlyList<string> Allowed { get; } =
        ["Authorization", "Content-Type", IdempotencyKey, Confirm, Device, CapturedAt, "If-Match", "If-None-Match", Positions.ReadAfterHeader, "traceparent"];

    /// <summary>Allows <see cref="Allowed"/> as request headers and exposes <see cref="Exposed"/> on the policy. Origins, methods
    /// and credentials stay the application's choice.</summary>
    /// <param name="policy">The CORS policy.</param>
    /// <returns>The policy.</returns>
    public static CorsPolicyBuilder WithTarolHeaders(this CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.WithHeaders([.. Allowed]).WithExposedHeaders([.. Exposed]);
    }
}
