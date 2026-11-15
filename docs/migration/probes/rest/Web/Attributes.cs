using Tarol.Primitives;

namespace Tarol.Changes.AspNetCore;

/// <summary>An HTTP entry: on a change record (POST/PUT/PATCH/DELETE), on an aggregate or on a view (GET).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EndpointAttribute(string route) : Attribute
{
    /// <summary>"POST /auctions/{auction}/bids": the verb, a space, the route pattern.</summary>
    public string Route { get; } = route;

    /// <summary>The coarse endpoint policy (RequireAuthorization). Absent: the default policy (an authenticated caller).</summary>
    public string? Policy { get; init; }

    /// <summary>Opt out of authentication at the endpoint; the resource check still runs on the anonymous principal.</summary>
    public bool Anonymous { get; init; }
}

/// <summary>A webhook entry on a static From factory: the sender's event type (and contract version) mapped to the change.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class WebhookAttribute(string sender, string type) : Attribute
{
    public string Sender { get; } = sender;
    public string Type { get; } = type;
    public string? Version { get; init; }
}

/// <summary>A read model of one aggregate, built from the loaded aggregate (candidate G2).</summary>
public interface IView<TAggregate, TSelf>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
    where TSelf : IView<TAggregate, TSelf>
{
    static abstract TSelf From(TAggregate aggregate);
}

/// <summary>The intent "read this aggregate", authorized like a change (resource handlers on the aggregate).</summary>
public sealed class Read<TAggregate>
{
    public static Read<TAggregate> Instance { get; } = new();
    private Read() { }
}
