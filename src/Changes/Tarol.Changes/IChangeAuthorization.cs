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

    /// <summary>
    /// <see cref="Check{T}(object, T, ChangeContext, ClaimsPrincipal, CancellationToken)"/> for one view (TN-43, owner decision
    /// VA-2): a <see cref="ReadScope.Restricted"/> view needs its own grant (<c>MayRead&lt;T, TView&gt;</c>); the row rules of
    /// the read still run. The default ignores the scope.
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="change">The read intent.</param>
    /// <param name="aggregate">The resource.</param>
    /// <param name="context">The operation and the time.</param>
    /// <param name="principal">The caller's claims.</param>
    /// <param name="scope">The view and whether it is restricted.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Success, or <c>Error.Unauthorized</c> / <c>Error.Forbidden</c>.</returns>
    ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, ReadScope scope, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Check(change, aggregate, context, principal, cancellationToken);

    /// <summary><see cref="CheckList{T}(ChangeContext, ClaimsPrincipal, CancellationToken)"/> for one list or row query (TN-43, VA-2).</summary>
    /// <typeparam name="T">The aggregate whose projection is read.</typeparam>
    /// <param name="context">The operation and the time.</param>
    /// <param name="principal">The caller's claims.</param>
    /// <param name="scope">The query record and whether it is restricted.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Success, or <c>Error.Unauthorized</c> / <c>Error.Forbidden</c>.</returns>
    ValueTask<Result> CheckList<T>(ChangeContext context, ClaimsPrincipal principal, ReadScope scope, CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => CheckList<T>(context, principal, cancellationToken);
}

/// <summary>
/// Which view, list or row query a read is for (TN-43, owner decision VA-2a): an unrestricted one is granted by <c>Read&lt;T&gt;</c>
/// or by its own <c>MayRead&lt;T, TView&gt;</c>; a <see cref="Restricted"/> one only by <c>MayRead&lt;T, TView&gt;</c>.
/// </summary>
/// <param name="View">The view or query record type.</param>
/// <param name="Restricted">Whether it needs its own grant (<c>[Endpoint(…, Restricted = true)]</c>).</param>
public readonly record struct ReadScope(Type View, bool Restricted);
