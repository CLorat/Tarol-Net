using System.Security.Claims;
using Tarol.Options;
using Tarol.Persistence;

namespace Tarol.Changes;

/// <summary>What a successful change reports.</summary>
/// <param name="StreamId">The aggregate's stream id (for a creation: the id the factory chose).</param>
/// <param name="Version">The stream's version after the commit.</param>
/// <param name="Events">How many events were committed (0: the behaviour raised nothing, nothing was appended).</param>
/// <param name="Replayed">The idempotency key had been seen: this is the first outcome, nothing ran.</param>
/// <param name="Position">
/// The commit's position in the event feed (QR-5): wait for a projection to pass it to read your own write
/// (<c>Tarol-Position</c> on HTTP). <see cref="GlobalPosition.Start"/> when nothing was committed or the outcome is replayed
/// (the idempotency key stores no position).
/// </param>
public readonly record struct ChangeOutcome(Guid StreamId, long Version, int Events, bool Replayed = false, GlobalPosition Position = default)
{
    /// <summary>
    /// Whether this commit started the stream (a creation, a load-or-create, an <see cref="IOrStart{T}"/> change; TN-42): HTTP
    /// answers 201 with <c>{id, version}</c> for every such change (owner decision CR-4a), not only for creations.
    /// </summary>
    public bool Created { get; init; }
}

/// <summary>What a channel passes along with a change.</summary>
/// <param name="Key">An idempotency key: a redelivery with it returns the first outcome (C-8).</param>
/// <param name="ExpectedVersion">The version the caller saw (HTTP <c>If-Match</c>): another version is <c>Stream.VersionConflict</c>.</param>
/// <param name="Principal">The caller's claims: when set, the pipeline asks its <see cref="IChangeAuthorization"/>
/// (HTTP entries always set it); when <see langword="null"/>, the run is internal and not checked.</param>
public readonly record struct ChangeOptions(
    Option<IdempotencyKey> Key = default,
    Option<long> ExpectedVersion = default,
    ClaimsPrincipal? Principal = null)
{
    /// <summary>The warnings the caller confirmed (TN-37: <see cref="ChangeContext.Confirmed"/>); HTTP: the <c>Tarol-Confirm</c> header.</summary>
    public IReadOnlyCollection<string>? Confirmed { get; init; }
}
