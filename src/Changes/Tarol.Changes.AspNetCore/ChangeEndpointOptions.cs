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

    /// <summary>Write problem details through the application's <c>IProblemDetailsService</c> (<c>AddProblemDetails</c>)
    /// when one is registered; else (or when <see langword="false"/>) they are written directly.</summary>
    public bool UseProblemDetailsService { get; set; } = true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Json);
        ArgumentException.ThrowIfNullOrWhiteSpace(SubjectClaim);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientClaim);
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantClaim);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBody, 1);
    }
}
