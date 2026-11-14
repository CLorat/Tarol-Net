using Tarol.Primitives;

namespace Tarol.Persistence;

/// <summary>
/// The global, ordered feed of every committed event, for projections and catch-up readers (docs/architecture/persistence.md §9).
/// A reader stores the <see cref="PositionedEvent.Position"/> of the last event it handled and continues after it; the
/// feed never returns an event behind a position it already returned.
/// <code>
/// await foreach (var e in feed.ReadAll(checkpoint, 500, ct)) { Project(e.Event); checkpoint = e.Position; }
/// </code>
/// </summary>
public interface IEventFeed
{
    /// <summary>Reads up to <paramref name="limit"/> events after <paramref name="after"/>, in commit order, across tenants.</summary>
    /// <param name="after">The last position handled; <see cref="GlobalPosition.Start"/> for the beginning.</param>
    /// <param name="limit">The maximum number of events.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events.</returns>
    IAsyncEnumerable<PositionedEvent> ReadAll(GlobalPosition after, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// A position in the <see cref="IEventFeed"/>, totally ordered by (<see cref="Transaction"/>, <see cref="Position"/>).
/// A plain sequence is not enough: positions are taken before commit, so a later commit can hold an earlier one.
/// </summary>
/// <param name="Transaction">The commit (PostgreSQL: the transaction id).</param>
/// <param name="Position">The event's sequence number.</param>
public readonly record struct GlobalPosition(ulong Transaction, long Position) : IComparable<GlobalPosition>
{
    /// <summary>Before the first event.</summary>
    public static GlobalPosition Start => default;

    /// <inheritdoc />
    public int CompareTo(GlobalPosition other)
    {
        var byTransaction = Transaction.CompareTo(other.Transaction);
        return byTransaction != 0 ? byTransaction : Position.CompareTo(other.Position);
    }

    /// <summary>Whether <paramref name="left"/> comes before <paramref name="right"/>.</summary>
    /// <param name="left">A position.</param>
    /// <param name="right">A position.</param>
    /// <returns>The comparison.</returns>
    public static bool operator <(GlobalPosition left, GlobalPosition right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> comes after <paramref name="right"/>.</summary>
    /// <param name="left">A position.</param>
    /// <param name="right">A position.</param>
    /// <returns>The comparison.</returns>
    public static bool operator >(GlobalPosition left, GlobalPosition right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> is not after <paramref name="right"/>.</summary>
    /// <param name="left">A position.</param>
    /// <param name="right">A position.</param>
    /// <returns>The comparison.</returns>
    public static bool operator <=(GlobalPosition left, GlobalPosition right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> is not before <paramref name="right"/>.</summary>
    /// <param name="left">A position.</param>
    /// <param name="right">A position.</param>
    /// <returns>The comparison.</returns>
    public static bool operator >=(GlobalPosition left, GlobalPosition right) => left.CompareTo(right) >= 0;
}

/// <summary>An event from the <see cref="IEventFeed"/>.</summary>
/// <param name="Position">Its position; store it as the reader's checkpoint.</param>
/// <param name="StreamType">The stream type of its aggregate.</param>
/// <param name="Event">The event with its commit facts.</param>
public sealed record PositionedEvent(GlobalPosition Position, string StreamType, RecordedEvent Event);
