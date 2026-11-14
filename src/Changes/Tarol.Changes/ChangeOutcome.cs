using System.Security.Claims;
using Tarol.Options;
using Tarol.Persistence;

namespace Tarol.Changes;

/// <summary>What a successful change reports.</summary>
/// <param name="StreamId">The aggregate's stream id (for a creation: the id the factory chose).</param>
/// <param name="Version">The stream's version after the commit.</param>
/// <param name="Events">How many events were committed (0: the behaviour raised nothing, nothing was appended).</param>
/// <param name="Replayed">The idempotency key had been seen: this is the first outcome, nothing ran.</param>
public readonly record struct ChangeOutcome(Guid StreamId, long Version, int Events, bool Replayed = false);

/// <summary>What a channel passes along with a change.</summary>
/// <param name="Key">An idempotency key: a redelivery with it returns the first outcome (C-8).</param>
/// <param name="ExpectedVersion">The version the caller saw (HTTP <c>If-Match</c>): another version is <c>Stream.VersionConflict</c>.</param>
/// <param name="Principal">The caller's claims, for the permission check (step 10).</param>
public readonly record struct ChangeOptions(
    Option<IdempotencyKey> Key = default,
    Option<long> ExpectedVersion = default,
    ClaimsPrincipal? Principal = null);
