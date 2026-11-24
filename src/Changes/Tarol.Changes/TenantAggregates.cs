using Tarol.Kernel;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>The ids of <see cref="PerTenantAttribute"/> aggregates (TN-2): the tenant's id, typed as the aggregate's.</summary>
public static class TenantAggregates
{
    /// <summary>The code of a refusal when a per-tenant aggregate is changed without a tenant (403).</summary>
    public const string TenantRequiredCode = "Tenant.Required";

    /// <summary>The id of the tenant's one <typeparamref name="T"/>: <c>Id&lt;T&gt;.From(tenant.Value)</c>.</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The id.</returns>
    public static Id<T> IdOf<T>(Id<ITenant> tenant)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Id<T>.From(tenant.Value);

    /// <summary>The id of the caller's tenant's <typeparamref name="T"/> (in a creation: <c>IdOf&lt;T&gt;(context.Operation)</c>).</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <returns>The id.</returns>
    /// <exception cref="InvalidOperationException">The operation has no tenant.</exception>
    public static Id<T> IdOf<T>(OperationContext operation)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => TryIdOf<T>(operation, out var id) ? id : throw new InvalidOperationException($"The operation has no tenant; {typeof(T).Name} is one per tenant.");

    /// <summary>The id of the caller's tenant's <typeparamref name="T"/>, if the operation has a tenant.</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="id">The id.</param>
    /// <returns>Whether the operation has a tenant.</returns>
    public static bool TryIdOf<T>(OperationContext operation, out Id<T> id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        id = Id<T>.Empty;
        if (!operation.Tenant.TryGetValue(out Id<ITenant> tenant))
            return false;
        id = IdOf<T>(tenant);
        return true;
    }
}
