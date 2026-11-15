using Microsoft.AspNetCore.Authorization;
using Tarol.Primitives;

namespace Tarol.Security;

/// <summary>
/// A row rule for one change on its aggregate: it sees the input and the loaded (or new) aggregate. <see cref="Deny"/>
/// refuses (it wins over every grant, 403 with its code); <c>context.Succeed(requirement)</c> grants (an ownership grant,
/// e.g. "the seller may read their own auction without the reader role"); doing nothing leaves the decision to the others.
/// Register it as a singleton <see cref="IAuthorizationHandler"/>.
/// </summary>
/// <typeparam name="TChange">The change (or <see cref="Read{TAggregate}"/>).</typeparam>
/// <typeparam name="TAggregate">The aggregate.</typeparam>
public abstract class ChangeHandler<TChange, TAggregate> : AuthorizationHandler<ChangeRequirement, TAggregate>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
{
    /// <inheritdoc />
    protected sealed override Task HandleRequirementAsync(AuthorizationHandlerContext context, ChangeRequirement requirement, TAggregate resource)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return requirement.Change is TChange change ? Handle(context, requirement, change, resource) : Task.CompletedTask;
    }

    /// <summary>Decides for one change on one aggregate.</summary>
    /// <param name="context">The authorization context (<c>context.User</c> is the caller).</param>
    /// <param name="requirement">The requirement to succeed.</param>
    /// <param name="change">The change input.</param>
    /// <param name="aggregate">The aggregate.</param>
    /// <returns>A completed task, or the asynchronous decision.</returns>
    protected abstract Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, TChange change, TAggregate aggregate);

    /// <summary>Refuses with <paramref name="code"/>: the problem details' <c>code</c> and the denial's log reason.</summary>
    /// <param name="context">The authorization context.</param>
    /// <param name="code">A stable code, e.g. <c>"Auction.NotSeller"</c>.</param>
    protected void Deny(AuthorizationHandlerContext context, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        context.Fail(new AuthorizationFailureReason(this, code));
    }
}
