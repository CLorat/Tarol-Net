using System.Security.Claims;
using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Changes;

/// <summary>
/// The resource permission, checked by the pipeline after the load and before <c>Execute</c> (for a creation: on the new
/// aggregate, before the invariants). <c>Tarol.Security</c> (step 10) implements it with ASP.NET Core authorization; until
/// then a pipeline without one checks nothing (internal callers only, C-11).
/// </summary>
public interface IChangeAuthorization
{
    /// <summary>Whether the caller may apply <paramref name="change"/> to <paramref name="aggregate"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The change (its type is the action).</param>
    /// <param name="aggregate">The resource.</param>
    /// <param name="context">The operation.</param>
    /// <param name="principal">The caller's claims, if the channel has them.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Success, or <c>Error.Unauthorized</c> / <c>Error.Forbidden</c>.</returns>
    ValueTask<Result> Check<T>(object change, T aggregate, OperationContext context, ClaimsPrincipal? principal, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;
}
