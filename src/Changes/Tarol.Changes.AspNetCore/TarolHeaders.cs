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

    /// <summary>The request's idempotency key (R-9).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The response headers a browser must read: <c>ETag</c>, <c>Location</c>, <c>Tarol-Position</c>,
    /// <c>Tarol-Stale</c>, <c>Idempotent-Replayed</c>, <c>Retry-After</c>, <c>WWW-Authenticate</c>.</summary>
    public static IReadOnlyList<string> Exposed { get; } =
        ["ETag", "Location", Positions.PositionHeader, Positions.StaleHeader, IdempotentReplayed, "Retry-After", "WWW-Authenticate"];

    /// <summary>The request headers a browser sends: <c>Authorization</c>, <c>Content-Type</c>, <c>Idempotency-Key</c>,
    /// <c>If-Match</c>, <c>If-None-Match</c>, <c>Tarol-Read-After</c>, <c>traceparent</c>.</summary>
    public static IReadOnlyList<string> Allowed { get; } =
        ["Authorization", "Content-Type", IdempotencyKey, "If-Match", "If-None-Match", Positions.ReadAfterHeader, "traceparent"];

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
