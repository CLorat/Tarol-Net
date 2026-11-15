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
}
