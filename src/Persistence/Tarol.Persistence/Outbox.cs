using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// What a store needs to know about the reactions to write outbox rows in a commit (docs/architecture/sagas.md §4):
/// which reactions an event has, when a scheduled one is due, and which reactions are scheduled (ordered by due time
/// only, never blocking each other). <c>Tarol.Changes</c>' generated <c>GeneratedReactions.Set</c> implements it.
/// </summary>
public interface IOutboxReactions
{
    /// <summary>The names of the reactions to <paramref name="domainEvent"/>; empty for none. Return spans over static arrays.</summary>
    /// <param name="domainEvent">A committed event.</param>
    /// <returns>The names.</returns>
    ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent);

    /// <summary>When the row of a scheduled reaction is due; <see langword="null"/> = at the commit.</summary>
    /// <param name="domainEvent">A committed event.</param>
    /// <param name="reaction">One of its reactions.</param>
    /// <returns>The due time (a time before the commit means the commit).</returns>
    DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction);

    /// <summary>Whether <paramref name="reaction"/> is scheduled: its rows are ordered by due time only (S-9).</summary>
    /// <param name="reaction">A reaction name.</param>
    /// <returns><see langword="true"/> for a scheduled reaction.</returns>
    bool IsScheduled(string reaction);
}

/// <summary>Builds <see cref="IOutboxReactions"/> without a generator (tests, hand-written tables).</summary>
public static class OutboxReactions
{
    /// <summary>Unscheduled reactions from a <see cref="ReactionTable"/>: every row is due at its commit.</summary>
    /// <param name="table">The reactions per event (spans over static arrays).</param>
    /// <returns>The reactions.</returns>
    public static IOutboxReactions From(ReactionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new Table(table);
    }

    /// <summary>
    /// Joins the outbox rows of several parts into the one table of a store (reactions, publications, effects): the rows
    /// of an event are the parts' rows in order; a row's due time and schedule are its part's. A single part is returned as is.
    /// </summary>
    /// <param name="parts">The parts; <see langword="null"/> entries are skipped.</param>
    /// <returns>The combined table, or <see langword="null"/> when no part is given.</returns>
    public static IOutboxReactions? Combine(params IOutboxReactions?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var given = parts.OfType<IOutboxReactions>().ToArray();
        return given.Length switch
        {
            0 => null,
            1 => given[0],
            _ => new Combined(given),
        };
    }

    private sealed class Combined(IOutboxReactions[] parts) : IOutboxReactions
    {
        public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent)
        {
            List<string>? all = null;
            ReadOnlySpan<string> only = default;
            foreach (var part in parts)
            {
                var names = part.ReactionsTo(domainEvent);
                if (names.IsEmpty)
                    continue;
                if (all is null && only.IsEmpty)
                {
                    only = names;                                       // one part with rows: its static array, no copy
                    continue;
                }
                all ??= [.. only];
                foreach (var name in names)
                    all.Add(name);
            }
            return all is null ? only : all.ToArray();
        }

        public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction)
        {
            foreach (var part in parts)
                if (part.DueAt(domainEvent, reaction) is { } due)
                    return due;
            return null;
        }

        public bool IsScheduled(string reaction) => parts.Any(p => p.IsScheduled(reaction));
    }

    private sealed class Table(ReactionTable table) : IOutboxReactions
    {
        public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => table(domainEvent);
        public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => null;
        public bool IsScheduled(string reaction) => false;
    }
}

/// <summary>
/// A claimed outbox row: run <see cref="Reaction"/> for <see cref="Event"/>, then <see cref="IOutbox.Complete"/> it, or
/// <see cref="IOutbox.Retry"/> or <see cref="IOutbox.Bury"/> it.
/// </summary>
/// <param name="Id">The row.</param>
/// <param name="ClaimId">This claim; complete, retry and bury must present it.</param>
/// <param name="Reaction">The reaction's name.</param>
/// <param name="Tenant">The tenant of the event.</param>
/// <param name="Attempts">How many times the row was claimed, this claim included.</param>
/// <param name="LastError">The error of the previous attempt.</param>
/// <param name="Event">The event with its commit facts.</param>
/// <param name="Traceparent">The W3C trace context of the activity that committed the event, for an <c>ActivityLink</c>.</param>
public sealed record OutboxRow(long Id, Guid ClaimId, string Reaction, Option<Id<ITenant>> Tenant, int Attempts, Option<string> LastError,
    PositionedEvent Event, Option<string> Traceparent);

/// <summary>A row that kept failing and left the order of its stream: for a human to read and re-drive.</summary>
/// <param name="Id">The dead letter (re-drive it by this id).</param>
/// <param name="Reaction">The reaction's name.</param>
/// <param name="Tenant">The tenant of the event.</param>
/// <param name="StreamType">The stream type of the event's aggregate.</param>
/// <param name="StreamId">The event's stream.</param>
/// <param name="EventId">The event.</param>
/// <param name="Attempts">How many times it was claimed.</param>
/// <param name="LastError">Why the last attempt failed.</param>
/// <param name="DeadAt">When it was buried.</param>
public sealed record DeadLetter(long Id, string Reaction, Option<Id<ITenant>> Tenant, string StreamType, Guid StreamId, Guid EventId, int Attempts,
    Option<string> LastError, DateTimeOffset DeadAt);

/// <summary>
/// The outbox of a store (P-1, P-10, S-5, S-8, S-9): rows written in the commit of their event, claimed by workers with a
/// lease. Only the <b>head</b> of a (tenant, stream type, stream, reaction) is claimable, so one stream's events reach a
/// reaction one at a time in version order; rows of scheduled reactions are all heads and run by due time.
/// </summary>
/// <remarks>
/// At least once: a row whose lease expired is claimable again, and a worker that lost its lease gets
/// <see langword="false"/> from <see cref="Complete"/>, <see cref="Retry"/> and <see cref="Bury"/>.
/// </remarks>
public interface IOutbox
{
    /// <summary>Claims up to <paramref name="max"/> due heads for <paramref name="lease"/>.</summary>
    /// <param name="max">The most rows.</param>
    /// <param name="lease">How long the rows stay with this worker.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The claimed rows.</returns>
    Task<IReadOnlyList<OutboxRow>> Claim(int max, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Removes a row whose reaction completed; the next row of its stream and reaction becomes the head.</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost.</returns>
    Task<bool> Complete(OutboxRow row, CancellationToken cancellationToken);

    /// <summary>Releases a row to be claimed again at <paramref name="retryAt"/>; it stays the head (later rows wait).</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="retryAt">When it is due again.</param>
    /// <param name="reason">What went wrong (the next claim's <see cref="OutboxRow.LastError"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost.</returns>
    Task<bool> Retry(OutboxRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken);

    /// <summary>Moves a row to the dead letters; the next row of its stream and reaction becomes the head.</summary>
    /// <param name="row">The claimed row.</param>
    /// <param name="reason">Why it is given up.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when the lease was lost.</returns>
    Task<bool> Bury(OutboxRow row, string reason, CancellationToken cancellationToken);

    /// <summary>The oldest dead letters.</summary>
    /// <param name="max">The most entries.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The dead letters, oldest first.</returns>
    Task<IReadOnlyList<DeadLetter>> DeadLetters(int max, CancellationToken cancellationToken);

    /// <summary>
    /// Re-drives a dead letter: a new row, due now, behind the rows of its stream and reaction that waited meanwhile.
    /// The reaction runs again with its key, so an effect that did commit is not repeated.
    /// </summary>
    /// <param name="id">The dead letter's <see cref="DeadLetter.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is no such dead letter.</returns>
    Task<bool> Redrive(long id, CancellationToken cancellationToken);

    /// <summary>How many rows wait (claimed or not, dead letters excluded); for health checks.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count.</returns>
    Task<long> Pending(CancellationToken cancellationToken);

    /// <summary>
    /// Waits until a commit may have written rows, or <paramref name="timeout"/> passed (the poll interval). A store
    /// without notifications just waits the timeout; a worker claims again either way.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes at the next notification or after the timeout.</returns>
    Task WaitForRows(TimeSpan timeout, CancellationToken cancellationToken);
}
