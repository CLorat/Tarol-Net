using Tarol.Primitives;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// An HTTP entry (docs/architecture/rest.md §2): on a change record (<c>"POST /auctions/{auction}/bids"</c>; POST, PUT,
/// PATCH or DELETE) or on a view record (<c>"GET /auctions/{auction}"</c>, an <see cref="IView{TAggregate, TSelf}"/>).
/// The generator writes one <c>RequestDelegate</c> per entry and <c>MapChanges()</c>.
/// </summary>
/// <param name="route">The verb, a space and the route pattern; route values bind the input members of the same name.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EndpointAttribute(string route) : Attribute
{
    /// <summary>The verb and pattern.</summary>
    public string Route { get; } = route;

    /// <summary>The endpoint policy (<c>RequireAuthorization(Policy)</c>), e.g. a scope; absent: the default policy (an
    /// authenticated caller). The change is still authorized on its aggregate (Tarol.Security).</summary>
    public string? Policy { get; init; }

    /// <summary>Lets unauthenticated callers reach the entry; the change is still authorized for the anonymous principal.</summary>
    public bool Anonymous { get; init; }

    /// <summary>
    /// For a list query: an <see cref="IListScope{TQuery}"/> must be registered (startup fails without one), so a list whose rows
    /// depend on the caller can never answer unfiltered (TN-6). Without it, a registered scope still applies.
    /// </summary>
    public bool RequiresScope { get; init; }
}

/// <summary>
/// Binds a member of a list query record (<c>IQuery&lt;TRow&gt;</c>) to a claim of the caller, never to the request (QR-6):
/// <c>[FromClaim("sub")] string Seller</c> lists the caller's own rows. A request without the claim is refused (403).
/// </summary>
/// <param name="type">The claim type, e.g. <c>"sub"</c>.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class FromClaimAttribute(string type) : Attribute
{
    /// <summary>The claim type.</summary>
    public string Type { get; } = type;
}

/// <summary>
/// A webhook entry on a static factory <c>From(TPayload)</c> of a change: the sender's event type (and contract version)
/// mapped to the change (anti-corruption layer). Served at <c>POST /webhooks/{sender}</c> and
/// <c>POST /webhooks/{sender}/{tenant}</c>; configured in <see cref="WebhookOptions"/>.
/// </summary>
/// <param name="sender">The sender's name, e.g. <c>"payments"</c>.</param>
/// <param name="type">The envelope's <c>type</c>, e.g. <c>"payment.succeeded"</c>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class WebhookAttribute(string sender, string type) : Attribute
{
    /// <summary>The sender.</summary>
    public string Sender { get; } = sender;

    /// <summary>The event type.</summary>
    public string Type { get; } = type;

    /// <summary>The envelope's contract <c>version</c> this factory reads; absent: deliveries without a version.</summary>
    public string? Version { get; init; }
}

/// <summary>A read model of one aggregate, returned by <c>GET</c> by id (R-8): the API's contract, not the stored state.</summary>
/// <typeparam name="TAggregate">The aggregate.</typeparam>
/// <typeparam name="TSelf">The view.</typeparam>
public interface IView<TAggregate, TSelf>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
    where TSelf : IView<TAggregate, TSelf>
{
    /// <summary>Builds the view from the loaded (and authorized) aggregate. Implement this or the overload with a <see cref="ViewContext"/>.</summary>
    /// <param name="aggregate">The aggregate.</param>
    /// <returns>The view.</returns>
    static virtual TSelf From(TAggregate aggregate)
        => throw new InvalidOperationException($"{typeof(TSelf).Name} implements From(aggregate, ViewContext); the endpoint calls that one.");

    /// <summary>
    /// Builds the view for the reader (TN-43, owner decision VA-1: the actor and the time, not the roles; Tatalu FB-E07-3 "the
    /// substitute sees the last 14 days", FB-F01.1-6). Its ETag is <c>"&lt;version&gt;-&lt;hash&gt;"</c> (VA-1b): the view can
    /// change without a new version (another day, another reader).
    /// </summary>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="context">Who reads, and when.</param>
    /// <returns>The view.</returns>
    static virtual TSelf From(TAggregate aggregate, ViewContext context) => TSelf.From(aggregate);
}

/// <summary>What a view built for its reader gets (TN-43, VA-1): the operation (actor, tenant) and the request's time.</summary>
/// <param name="Operation">Who reads, for which tenant.</param>
/// <param name="Now">The time of the request, read once from the application's <see cref="TimeProvider"/>.</param>
public readonly record struct ViewContext(Tarol.Kernel.OperationContext Operation, DateTimeOffset Now);

/// <summary>What the generator knows about an entry, as endpoint metadata (read by the generated OpenAPI transformers).</summary>
/// <param name="Kind">"change", "creation", "view", "list", "search" or "webhook" (the OpenAPI document's <c>x-tarol-kind</c>).</param>
/// <param name="Input">The input (change or view) type.</param>
/// <param name="RouteValues">The route values the input binds.</param>
/// <param name="BodyMembers">The JSON names of the members read from the body (empty: no body).</param>
/// <param name="Policy">The endpoint policy, if any.</param>
/// <param name="Query">The query-string parameters of a list (filters, <c>sort</c>, <c>after</c>, <c>limit</c>, <c>offset</c>, <c>count</c>).</param>
public sealed record TarolEndpointMetadata(string Kind, Type Input, IReadOnlyList<RouteValue> RouteValues, IReadOnlyList<string> BodyMembers, string? Policy,
    IReadOnlyList<QueryParameter>? Query = null)
{
    /// <summary>The problem codes the entry can answer that are known at compile time (the OpenAPI document's
    /// <c>x-tarol-errors</c>): Tarol's own for the kind, and the literal codes of <c>Error.*("…")</c> calls in the input and the
    /// aggregate methods it calls. Codes built at run time are not listed.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>A change that may start its stream (load-or-create, <c>IOrStart&lt;T&gt;</c>): 201 with <c>{id, version}</c> then (TN-42).</summary>
    public bool MayCreate { get; init; }

    /// <summary>
    /// The assembly whose generated <c>MapChanges</c> mapped the entry; its generated OpenAPI transformer completes the operation
    /// (TN-9). <see langword="null"/> for Tarol's own entries (<c>GET /me</c>, search): the first transformer completes them.
    /// </summary>
    public System.Reflection.Assembly? Owner { get; init; }

    /// <summary>A list whose rows an <see cref="IListScope{TQuery}"/> restricts to the caller's (the OpenAPI document's <c>x-tarol-scoped</c>).</summary>
    public bool Scoped { get; init; }
}

/// <summary>A query-string parameter of a list entry.</summary>
/// <param name="Name">Its key (<c>status</c>, <c>sort</c>).</param>
/// <param name="Kind"><c>"string"</c>, <c>"uuid"</c>, <c>"boolean"</c>, <c>"int32"</c>, <c>"int64"</c>, <c>"number"</c>, <c>"date-time"</c>, <c>"date"</c>, <c>"time"</c> or <c>"enum"</c>.</param>
/// <param name="Values">The allowed values of an enum (its member names).</param>
/// <param name="Description">What it does.</param>
public sealed record QueryParameter(string Name, string Kind, IReadOnlyList<string> Values, string Description);

/// <summary>A route value of an entry.</summary>
/// <param name="Name">Its name in the pattern (<c>auction</c>).</param>
/// <param name="Kind"><c>"id"</c> (an <c>Id&lt;T&gt;</c>, a UUID), <c>"guid"</c>, <c>"string"</c>, <c>"int"</c> or <c>"long"</c>.</param>
/// <param name="Property">The JSON name of the input member it binds (not read from the body).</param>
public sealed record RouteValue(string Name, string Kind, string Property)
{
    /// <summary>The aggregate of an <c>"id"</c> route value (<c>Id&lt;Auction&gt;</c>: <c>Auction</c>; the OpenAPI document's
    /// <c>x-tarol-id</c>), else <see langword="null"/>.</summary>
    public string? Aggregate { get; init; }

    /// <summary>The names of an <c>"enum"</c> route value (TN-34; matched case-insensitively), else <see langword="null"/>.</summary>
    public IReadOnlyList<string>? Values { get; init; }
}
