namespace Tarol.Persistence;

/// <summary>
/// A store that lists the ids of one stream type (TN-56, TM-6): one indexed query over the streams, never a feed scan. For a
/// <c>[PerTenant]</c> aggregate the ids are the tenants (<c>UseStoreTenants&lt;T&gt;()</c> in <c>Tarol.Jobs</c>). The in-memory and
/// PostgreSQL stores implement it; the PostgreSQL store leaves out streams of a tenant erased by <c>EraseTenant</c>.
/// </summary>
public interface IStreamIds
{
    /// <summary>The ids of every stream of <paramref name="streamType"/> in all tenants, each once, in no particular order.</summary>
    /// <param name="streamType">The aggregate's <c>StreamType</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ids.</returns>
    ValueTask<IReadOnlyList<Guid>> StreamIds(string streamType, CancellationToken cancellationToken);
}
