using System.Security.Claims;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;

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

    /// <summary>
    /// Nothing was to be done (R-2, TN-47): a reaction factory returned <see langword="null"/>, or <c>Load</c> answered <see cref="Skip"/>.
    /// Nothing was appended; a reaction's row is done (not retried), HTTP answers 204.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>The code of <see cref="Skip"/>: <c>Change.Skipped</c>.</summary>
    public const string SkippedCode = "Change.Skipped";

    /// <summary>The outcome of a change that had nothing to do (<see cref="Skipped"/>).</summary>
    public static ChangeOutcome Nothing { get; } = new(Guid.Empty, 0, 0) { Skipped = true };

    /// <summary>
    /// What <c>Load</c> answers when there is nothing to do (R-2, TN-47): the target is gone or the event no longer applies. The
    /// pipeline answers <see cref="Nothing"/> instead of a failure, so a reaction's row is done instead of retried as a <c>NotFound</c>.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    /// <returns>The violation <c>Load</c> returns.</returns>
    public static Error Skip(string? reason = null) => Error.NotFound(SkippedCode, reason);
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
