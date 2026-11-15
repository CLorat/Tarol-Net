using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// A key under which the store remembers a successful commit, so a redelivery returns the first outcome instead of
/// committing again (docs/architecture/changes.md §7, C-8).
/// </summary>
/// <remarks>
/// Scopes per channel: REST <c>(tenant, "caller:route", header)</c>, webhook <c>(tenant, sender, delivery id)</c>,
/// reaction <c>(tenant, reaction, event id)</c>, consumer <c>(tenant, contract, message id)</c>.
/// <para>
/// A store identifies a key by <see cref="Tenant"/>, <see cref="Scope"/> and <see cref="Key"/>; the <see cref="Fingerprint"/>
/// is stored with the outcome. When both the stored and the presented key carry one and they differ, the pipeline refuses
/// the request with <c>Error.UnprocessableEntity("Idempotency.KeyReused")</c> instead of replaying another request's
/// outcome (docs/architecture/rest.md §6, R-9).
/// </para>
/// </remarks>
public readonly record struct IdempotencyKey
{
    /// <summary>Creates a key.</summary>
    /// <param name="tenant">The tenant the key belongs to (<c>None</c> for a single-tenant app).</param>
    /// <param name="scope">What the key is unique within, e.g. the webhook sender.</param>
    /// <param name="key">The key the channel supplied.</param>
    /// <exception cref="ArgumentException"><paramref name="scope"/> or <paramref name="key"/> is empty.</exception>
    public IdempotencyKey(Option<Id<ITenant>> tenant, string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Tenant = tenant;
        Scope = scope;
        Key = key;
    }

    /// <summary>The tenant.</summary>
    public Option<Id<ITenant>> Tenant { get; }

    /// <summary>The scope.</summary>
    public string Scope { get; }

    /// <summary>The key.</summary>
    public string Key { get; }

    /// <summary>What the key was used for, e.g. a hash of an HTTP request's method, path and body; <c>None</c> when the
    /// channel has nothing to compare (reactions, webhooks).</summary>
    public Option<string> Fingerprint { get; init; }
}

/// <summary>What the store remembers under an <see cref="IdempotencyKey"/>.</summary>
/// <param name="StreamId">The stream the commit went to.</param>
/// <param name="Version">The stream's version after the commit.</param>
/// <param name="Events">How many events the commit appended.</param>
/// <param name="Fingerprint">The fingerprint of the key that stored it, if it had one.</param>
public readonly record struct StoredOutcome(Guid StreamId, long Version, int Events, Option<string> Fingerprint = default);
