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

/// <summary>
/// A row rule for one view, list or row query of its aggregate (TN-43, VA-2): it runs for reads of <typeparamref name="TView"/>
/// only, after the role map. <c>ChangeHandler&lt;Read&lt;TAggregate&gt;, TAggregate&gt;</c> rules still run for every view.
/// </summary>
/// <typeparam name="TView">The view or query record.</typeparam>
/// <typeparam name="TAggregate">The aggregate.</typeparam>
public abstract class ViewHandler<TView, TAggregate> : ChangeHandler<Read<TAggregate>, TAggregate>
    where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
{
    /// <inheritdoc />
    protected sealed override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, Read<TAggregate> change, TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return requirement.View?.View == typeof(TView) ? HandleView(context, requirement, aggregate) : Task.CompletedTask;
    }

    /// <summary>Decides for a read of <typeparamref name="TView"/>: <c>Deny</c>, <c>context.Succeed(requirement)</c>, or nothing.</summary>
    /// <param name="context">The authorization context.</param>
    /// <param name="requirement">The read requirement (its <see cref="ChangeRequirement.Context"/> has the time and the operation).</param>
    /// <param name="aggregate">The loaded aggregate.</param>
    /// <returns>A task.</returns>
    protected abstract Task HandleView(AuthorizationHandlerContext context, ChangeRequirement requirement, TAggregate aggregate);
}
