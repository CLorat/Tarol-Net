using System.Security.Claims;
using System.Text.Json;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.AspNetCore;

/// <summary>How the HTTP edge builds the operation context and reads bodies (docs/architecture/rest.md §5).</summary>
public sealed class ChangeEndpointOptions
{
    /// <summary>The JSON options inputs and views are read and written with: the application's context with
    /// <c>ValueObjectJson.Resolver</c> first (set by <c>AddEndpoints</c> from <c>AddTarol</c>).</summary>
    public JsonSerializerOptions Json { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>The claim of a user's subject (default <c>sub</c>).</summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>The claim of a service's client id (default <c>client_id</c>).</summary>
    public string ClientClaim { get; set; } = "client_id";

    /// <summary>The claim of the tenant (default <c>tenant</c>); its value must be an <c>Id&lt;ITenant&gt;</c> unless
    /// <see cref="Tenant"/> maps it.</summary>
    public string TenantClaim { get; set; } = "tenant";

    /// <summary>The application's actor rule, replacing the default (<c>sub</c> → <c>Actor.User</c>; <c>client_id</c> alone or
    /// equal to <c>sub</c> → <c>Actor.Service</c>). An identity provider whose machine users have their own <c>sub</c> needs
    /// one (R-16).</summary>
    public Func<ClaimsPrincipal, Actor>? Actor { get; set; }

    /// <summary>Maps the tenant claim's value to the tenant (R-16), e.g. an identity provider's numeric organisation id;
    /// <c>None</c> refuses the request (401 <c>Auth.Tenant</c>). Absent: the value must parse as an <c>Id&lt;ITenant&gt;</c>.</summary>
    public Func<string, Option<Id<ITenant>>>? Tenant { get; set; }

    /// <summary>The largest body read (bytes); larger requests get 413. Default 64 KiB.</summary>
    public int MaxBody { get; set; } = 64 * 1024;

    /// <summary>How far <c>Tarol-Captured-At</c> may lie ahead of the server's clock (default 5 minutes; a device's clock drifts; TN-60).</summary>
    public TimeSpan CaptureSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How old <c>Tarol-Captured-At</c> may be (default 30 days: an outbox that waited longer is refused with 400 <c>TooOld</c>).</summary>
    public TimeSpan CaptureMaxAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Write problem details through the application's <c>IProblemDetailsService</c> (<c>AddProblemDetails</c>)
    /// when one is registered; else (or when <see langword="false"/>) they are written directly.</summary>
    public bool UseProblemDetailsService { get; set; } = true;

    /// <summary>Limits and waits of list entries (<c>[Endpoint("GET …")]</c> on an <c>IQuery&lt;TRow&gt;</c> record).</summary>
    public QueryEndpointOptions Queries { get; } = new();

    internal void Validate()
    {
        Queries.Validate();
        ArgumentNullException.ThrowIfNull(Json);
        ArgumentException.ThrowIfNullOrWhiteSpace(SubjectClaim);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientClaim);
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantClaim);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBody, 1);
    }
}

/// <summary>Limits and waits of list entries (QR-4, QR-5).</summary>
public sealed class QueryEndpointOptions
{
    /// <summary>The largest <c>limit</c> (default and at most <see cref="Tarol.Queries.ListQuery.MaxLimit"/>, 200); larger is 400.</summary>
    public int MaxLimit { get; set; } = Tarol.Queries.ListQuery.MaxLimit;

    /// <summary>The page size without <c>limit</c> (default 50).</summary>
    public int DefaultLimit { get; set; } = Tarol.Queries.ListQuery.DefaultLimit;

    /// <summary>The deepest <c>offset</c> (default and at most <see cref="Tarol.Queries.ListQuery.MaxOffset"/>, 1,000); deeper is 400.</summary>
    public int MaxOffset { get; set; } = Tarol.Queries.ListQuery.MaxOffset;

    /// <summary>How long a <c>Tarol-Read-After</c> request waits for the projection (default 2 s); then 200 with <c>Tarol-Stale: true</c>.</summary>
    public TimeSpan ReadAfterTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The <c>Retry-After</c> of a 503 while the projection builds (default 5 s).</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLimit, 1, nameof(MaxLimit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxLimit, Tarol.Queries.ListQuery.MaxLimit, nameof(MaxLimit));
        ArgumentOutOfRangeException.ThrowIfLessThan(DefaultLimit, 1, nameof(DefaultLimit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DefaultLimit, MaxLimit, nameof(DefaultLimit));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxOffset, nameof(MaxOffset));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxOffset, Tarol.Queries.ListQuery.MaxOffset, nameof(MaxOffset));
        ArgumentOutOfRangeException.ThrowIfLessThan(ReadAfterTimeout, TimeSpan.Zero, nameof(ReadAfterTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetryAfter, TimeSpan.Zero, nameof(RetryAfter));
    }
}
