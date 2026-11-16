using System.Security.Claims;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Changes;

/// <summary>
/// The resource permission, checked by the pipeline after the load, before the expected version is compared and before
/// <c>Execute</c> (for a creation: on the new aggregate, before the invariants). The pipeline asks only for runs that carry
/// a principal (<see cref="ChangeOptions.Principal"/>); internal runs are not checked. <c>Tarol.Security</c> implements it
/// with ASP.NET Core authorization (docs/architecture/rest.md §3).
/// </summary>
public interface IChangeAuthorization
{
    /// <summary>Whether the caller may apply <paramref name="change"/> to <paramref name="aggregate"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The change (its type is the action).</param>
    /// <param name="aggregate">The resource.</param>
    /// <param name="context">The operation, the time and the change's <c>[Read]</c> aggregates.</param>
    /// <param name="principal">The caller's claims (anonymous when the caller is not authenticated).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Success, or <c>Error.Unauthorized</c> / <c>Error.Forbidden</c>.</returns>
    ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;

    /// <summary>
    /// Whether the caller may list <typeparamref name="T"/> (a list query over a projection of its events, QR-6): the read
    /// intent without a resource, so only grants by role apply; row rules, which judge one loaded aggregate, do not run on
    /// lists. The default refuses (fail closed); <c>Tarol.Security</c> asks its role map for <c>Read&lt;T&gt;</c>.
    /// </summary>
    /// <typeparam name="T">The aggregate whose projection is listed.</typeparam>
    /// <param name="context">The operation and the time.</param>
    /// <param name="principal">The caller's claims.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Success, or <c>Error.Unauthorized</c> / <c>Error.Forbidden</c>.</returns>
    ValueTask<Result> CheckList<T>(ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => ValueTask.FromResult<Result>(principal?.Identity?.IsAuthenticated == true ? Error.Forbidden("Auth.Forbidden") : Error.Unauthorized("Auth.Required"));
}
