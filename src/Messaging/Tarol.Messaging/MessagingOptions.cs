using Tarol.Kernel;
using Tarol.Options;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>
/// What a consumer believes of one source system's envelopes (M-13). The source itself is the contract's prefix, enforced by
/// the transport's permissions; the tenant an envelope names is accepted only if it is allowed here (default: none).
/// </summary>
public sealed class SourceTrust
{
    private readonly HashSet<Guid> _tenants = [];

    /// <summary>Whether the source may name any tenant (a system serving every tenant, e.g. one company's own billing).</summary>
    public bool AllowsAnyTenant { get; private set; }

    /// <summary>The tenants the source may name.</summary>
    public IReadOnlyCollection<Guid> AllowedTenants => _tenants;

    /// <summary>Allows every tenant.</summary>
    /// <returns>This.</returns>
    public SourceTrust AnyTenant()
    {
        AllowsAnyTenant = true;
        return this;
    }

    /// <summary>Allows these tenants.</summary>
    /// <param name="tenants">The tenants.</param>
    /// <returns>This.</returns>
    public SourceTrust Tenants(params Id<ITenant>[] tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        foreach (var tenant in tenants)
            _tenants.Add(tenant.Value);
        return this;
    }

    /// <summary>Whether <paramref name="tenant"/> is allowed; a message without a tenant always is.</summary>
    /// <param name="tenant">The envelope's tenant.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public bool Allows(Option<Id<ITenant>> tenant)
        => !tenant.TryGetValue(out Id<ITenant> named) || AllowsAnyTenant || _tenants.Contains(named.Value);
}

/// <summary>The consumers' configuration.</summary>
public sealed class MessagingOptions
{
    private readonly Dictionary<string, SourceTrust> _sources = new(StringComparer.Ordinal);

    /// <summary>This application's name, the prefix of its subscriptions (<c>billing</c> → <c>billing.IssueInvoice</c>); required.</summary>
    public string Application { get; set; } = "";

    /// <summary>Attempts and backoff (the reaction worker's policy, S-13).</summary>
    public DeliveryPolicy Policy { get; set; } = new();

    /// <summary>Deliveries handled at once per subscription (default 1: the transport's order per subscription holds).</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>How long one delivery may take before the transport delivers it again (default 1 minute).</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The source systems this application accepts messages from.</summary>
    public IReadOnlyDictionary<string, SourceTrust> Sources => _sources;

    /// <summary>Accepts messages of <paramref name="source"/>'s contracts (<c>&lt;source&gt;.*</c>).</summary>
    /// <param name="source">The source system, e.g. <c>billing</c>.</param>
    /// <param name="configure">Which tenants it may name (default: none, so only messages without a tenant).</param>
    /// <returns>This.</returns>
    public MessagingOptions Trust(string source, Action<SourceTrust>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!_sources.TryGetValue(source, out var trust))
            _sources[source] = trust = new SourceTrust();
        configure?.Invoke(trust);
        return this;
    }

    /// <summary>Checks the options.</summary>
    /// <exception cref="InvalidOperationException">No application name, or a concurrency below 1, or a lease that is not positive.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Application) || Application.Contains('.', StringComparison.Ordinal))
            throw new InvalidOperationException("MessagingOptions.Application is required: this application's name without dots, e.g. \"billing\".");
        if (Concurrency < 1)
            throw new InvalidOperationException("MessagingOptions.Concurrency must be 1 or more.");
        if (Lease <= TimeSpan.Zero)
            throw new InvalidOperationException("MessagingOptions.Lease must be positive.");
        ArgumentNullException.ThrowIfNull(Policy);
    }
}
