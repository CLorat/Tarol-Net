namespace Tarol.Changes;

// Entry declarations (data only, no ASP.NET Core dependency). The core generator reads them; channel packages
// (Tarol.Changes.AspNetCore in step 10, messaging in step 11) emit the entry code.

/// <summary>An HTTP entry: <c>"POST /auctions/{auction}/close"</c>. Route values bind to members of the same name.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EndpointAttribute(string route) : Attribute
{
    public string Route { get; } = route;
    /// <summary>The ASP.NET Core authorization policy at the endpoint (coarse check, 401/403).</summary>
    public string? Policy { get; init; }
}

/// <summary>On a static factory <c>static TChange From(TEvent e, RecordedEvent recorded)</c>: a reaction to an own event, after commit.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OnEventAttribute<TEvent> : Attribute;

/// <summary>On a static factory <c>static TChange From(TPayload payload)</c>: a webhook of the named sender.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class WebhookAttribute(string sender, string type) : Attribute
{
    public string Sender { get; } = sender;
    public string Type { get; } = type;
}

/// <summary>On a static factory <c>static TChange From(TMessage message)</c>: a broker message of a contract version.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ConsumesAttribute(string contract, int version) : Attribute
{
    public string Contract { get; } = contract;
    public int Version { get; } = version;
}
