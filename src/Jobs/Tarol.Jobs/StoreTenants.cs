using Microsoft.Extensions.DependencyInjection;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs;

/// <summary>The <see cref="ITenantDirectory"/> from the store (TN-56, owner decision TM-6): the stream ids of a <c>[PerTenant]</c> aggregate.</summary>
public static class StoreTenantsTarolBuilderExtensions
{
    /// <summary>
    /// The tenants are the ids of the streams of <typeparamref name="T"/>, a <c>[PerTenant]</c> aggregate (its id is the tenant's):
    /// a tenant exists once its <typeparamref name="T"/> was created. One indexed query per sync (<see cref="JobHostOptions.TenantSyncEvery"/>),
    /// no feed scan; the PostgreSQL store leaves out a tenant erased by <c>EraseTenant</c>. Replaces a hand-written directory over a projection.
    /// </summary>
    /// <typeparam name="T">The <c>[PerTenant]</c> aggregate whose streams are the tenants (e.g. the tenant's settings).</typeparam>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not <c>[PerTenant]</c>; or, when resolved, the store cannot list stream ids.</exception>
    public static TarolBuilder UseStoreTenants<T>(this TarolBuilder builder)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!T.PerTenant)
            throw new InvalidOperationException($"UseStoreTenants<{typeof(T).Name}>: {typeof(T).Name} is not [PerTenant]; its stream ids are not tenants.");
        builder.Services.AddSingleton<ITenantDirectory>(static provider => new StoreTenants(
            provider.GetRequiredService<IAggregateStore>() as IStreamIds
            ?? throw new InvalidOperationException($"UseStoreTenants<{typeof(T).Name}>: the registered store cannot list stream ids (IStreamIds); use UseInMemoryStore or UseNpgsqlStore."),
            T.StreamType));
        return builder;
    }

    private sealed class StoreTenants(IStreamIds store, string streamType) : ITenantDirectory
    {
        public async ValueTask<IReadOnlyList<Id<ITenant>>> Tenants(CancellationToken cancellationToken)
            => [.. (await store.StreamIds(streamType, cancellationToken).ConfigureAwait(false)).Select(static id => Id<ITenant>.From(id))];
    }
}
