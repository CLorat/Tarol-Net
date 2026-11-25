using Microsoft.AspNetCore.Authorization;
using Tarol.Changes;

namespace Tarol.Security;

/// <summary>
/// The requirement of a change: the intent itself (docs/architecture/security.md §2: the action is the change type). The
/// resource is the aggregate the change applies to (the loaded one, or the new one of a creation).
/// </summary>
public sealed class ChangeRequirement : IAuthorizationRequirement
{
    /// <summary>Creates the requirement for <paramref name="change"/>.</summary>
    /// <param name="change">The change input, or <see cref="Read{TAggregate}.Instance"/> for a read.</param>
    /// <param name="context">The run's context: operation, time and the change's <c>[Read]</c> aggregates.</param>
    public ChangeRequirement(object change, ChangeContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        Change = change;
        Context = context;
    }

    /// <summary>The change input (or the <see cref="Read{TAggregate}"/> intent).</summary>
    public object Change { get; }

    /// <summary>
    /// For a read: the view, list or row query it is for (TN-43); <see langword="null"/> for a change or an unscoped read. A
    /// <c>ChangeHandler&lt;Read&lt;T&gt;, T&gt;</c> row rule runs for every view; a <see cref="ViewHandler{TView, TAggregate}"/> for one.
    /// </summary>
    public ReadScope? View { get; init; }

    /// <summary>The run's context: <c>Context.Read&lt;Artwork&gt;()</c> for a <c>[Read]</c> aggregate, <c>Context.Now</c>,
    /// <c>Context.Operation</c> (actor, tenant).</summary>
    public ChangeContext Context { get; }
}

/// <summary>The intent "read this aggregate": checked like a change, granted by the role map or a row rule.</summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
public sealed class Read<TAggregate>
{
    private Read()
    {
    }

    /// <summary>The intent (stateless; one per aggregate type).</summary>
    public static Read<TAggregate> Instance { get; } = new();

    /// <inheritdoc />
    public override string ToString() => $"Read<{typeof(TAggregate).Name}>";
}
