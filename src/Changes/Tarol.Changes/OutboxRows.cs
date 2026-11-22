using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Changes;

/// <summary>
/// Runs one kind of outbox row (docs/architecture/messaging.md §4, M-10): the store writes the rows of its table in the
/// commit, and the reaction worker claims them and calls <see cref="RunPositioned"/>. <see cref="ReactionSet"/> runs reactions;
/// <c>Tarol.Messaging</c> adds publications (<c>publish:&lt;contract&gt;@&lt;version&gt;</c>) and effects (<c>effect:&lt;name&gt;</c>).
/// </summary>
/// <remarks>
/// A handler's rows get the worker's claim, head per (stream, row name), backoff, lease, dead letters and trace links. A
/// row's result is classified like a change's (<see cref="Delivery.Classify"/>); an exception is a retry.
/// </remarks>
public interface IOutboxRowHandler : IOutboxReactions
{
    /// <summary>Every row name this handler runs.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The name of the worker's activity for a row, e.g. <c>reaction RemindBuyer</c> or <c>publish artflow.auction-settled</c>.</summary>
    /// <param name="reaction">The row's name (<see cref="OutboxRow.Reaction"/>).</param>
    /// <returns>The activity name.</returns>
    string ActivityName(string reaction);

    /// <summary>Runs one row.</summary>
    /// <param name="pipeline">The pipeline (reactions run their change through it; publications and effects ignore it).</param>
    /// <param name="reaction">The row's name (<see cref="OutboxRow.Reaction"/>).</param>
    /// <param name="recorded">The row's event.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A success (for a publication or an effect: the event's stream and version, no events), or the violation to classify.</returns>
    ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one row with the event's position in the feed, as the reaction worker claims it (BC-23): publications put the
    /// position into the message (<c>ce-position</c>). The default runs <see cref="Run(ChangePipeline, string, RecordedEvent, CancellationToken)"/>
    /// with <see cref="PositionedEvent.Event"/>; a handler that needs the position overrides it, and a handler that wraps
    /// another forwards it.
    /// </summary>
    /// <param name="pipeline">The pipeline (reactions run their change through it; publications and effects ignore it).</param>
    /// <param name="reaction">The row's name (<see cref="OutboxRow.Reaction"/>).</param>
    /// <param name="positioned">The row's event (<see cref="OutboxRow.Event"/>).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A success (for a publication or an effect: the event's stream and version, no events), or the violation to classify.</returns>
    ValueTask<Result<ChangeOutcome>> RunPositioned(ChangePipeline pipeline, string reaction, PositionedEvent positioned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(positioned);
        return Run(pipeline, reaction, positioned.Event, cancellationToken);
    }
}

/// <summary>
/// The row handlers of an application joined into one: the store's table (<see cref="IOutboxReactions"/>) and the
/// worker's dispatch by row name. Names are stored in rows and keys, so they must be unique across the parts.
/// </summary>
public sealed class OutboxRows : IOutboxRowHandler
{
    private readonly IOutboxRowHandler[] _parts;
    private readonly Dictionary<string, IOutboxRowHandler> _byName;
    private readonly IOutboxReactions _table;

    private OutboxRows(IOutboxRowHandler[] parts, Dictionary<string, IOutboxRowHandler> byName)
    {
        _parts = parts;
        _byName = byName;
        _table = OutboxReactions.Combine(parts) ?? OutboxReactions.From(static _ => ReadOnlySpan<string>.Empty);
        Names = [.. byName.Keys];
    }

    /// <summary>The joined handlers, in order.</summary>
    public IReadOnlyList<IOutboxRowHandler> Parts => _parts;

    /// <inheritdoc />
    public IReadOnlyList<string> Names { get; }

    /// <summary>Joins row handlers.</summary>
    /// <param name="handlers">The handlers (a <see cref="ReactionSet"/>, publications, effects).</param>
    /// <returns>The joined handler.</returns>
    /// <exception cref="InvalidOperationException">A row name is in two handlers.</exception>
    public static OutboxRows Combine(params IOutboxRowHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var byName = new Dictionary<string, IOutboxRowHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler, nameof(handlers));
            foreach (var name in handler.Names)
                if (!byName.TryAdd(name, handler))
                    throw new InvalidOperationException($"The outbox row '{name}' is declared twice; row names must be unique.");
        }
        return new([.. handlers], byName);
    }

    /// <inheritdoc />
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => _table.ReactionsTo(domainEvent);

    /// <inheritdoc />
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => _table.DueAt(domainEvent, reaction);

    /// <inheritdoc />
    public bool IsScheduled(string reaction) => _byName.TryGetValue(reaction, out var handler) && handler.IsScheduled(reaction);

    /// <inheritdoc />
    public string ActivityName(string reaction) => _byName.TryGetValue(reaction, out var handler) ? handler.ActivityName(reaction) : "outbox " + reaction;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No handler runs a row of that name (removed, or renamed without pinning its name).</exception>
    public ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        return _byName.TryGetValue(reaction, out var handler)
            ? handler.Run(pipeline, reaction, recorded, cancellationToken)
            : throw new InvalidOperationException($"No outbox row handler runs '{reaction}' (removed, or renamed without pinning its name).");
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No handler runs a row of that name (removed, or renamed without pinning its name).</exception>
    public ValueTask<Result<ChangeOutcome>> RunPositioned(ChangePipeline pipeline, string reaction, PositionedEvent positioned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        return _byName.TryGetValue(reaction, out var handler)
            ? handler.RunPositioned(pipeline, reaction, positioned, cancellationToken)
            : throw new InvalidOperationException($"No outbox row handler runs '{reaction}' (removed, or renamed without pinning its name).");
    }
}
