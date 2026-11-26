using System.ComponentModel;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes;

/// <summary>
/// On a static factory of a change: the change is a <b>reaction</b> to <typeparamref name="TEvent"/>. When an event of that
/// type commits, the store writes an outbox row in the same transaction; a worker later calls the factory and runs the
/// change through <see cref="ChangePipeline"/> as the system on behalf of the original user, caused by the event, with the
/// key (reaction, event id), so a redelivery commits once (docs/architecture/sagas.md §2).
/// </summary>
/// <typeparam name="TEvent">The committed event that triggers the reaction.</typeparam>
/// <example>
/// <code>
/// public sealed partial record MarkPayoutDue(Guid Auction, decimal Amount) : ICreation&lt;Payout&gt;
/// {
///     [OnEvent&lt;Bid.Won&gt;]
///     public static MarkPayoutDue From(Bid.Won e, RecordedEvent recorded) => new(recorded.StreamId, e.Amount);
///     public Result&lt;Payout&gt; Execute(ChangeContext context) => Payout.MarkDue(Auction, Amount, context.Now);
/// }
/// </code>
/// </example>
/// <remarks>
/// The factory is <c>static</c>, returns its change, and takes <c>(TEvent)</c> or <c>(TEvent, RecordedEvent)</c> (TCHG006).
/// It maps and does nothing else; the pipeline validates the result.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OnEventAttribute<TEvent> : Attribute
    where TEvent : DomainEvent
{
    /// <summary>
    /// The reaction's stable name, stored in every outbox row and idempotency key; default: the change's type name. Pin it
    /// before renaming the change, as with an event alias (rows of a name that no longer exists are dead-lettered).
    /// Unique per assembly (TCHG007) and across combined sets (<see cref="ReactionSet.Combine"/>).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The name of a <see cref="DateTimeOffset"/> property of <typeparamref name="TEvent"/>: the row is due then, not at the
    /// commit (a saga timeout; TCHG008). Such a <i>scheduled</i> reaction is ordered by due time only. Qualify the type when
    /// a member of the change has its name: <c>nameof(MyApp.Settlement.DeadlineSet.DueAt)</c>.
    /// </summary>
    public string? DueAt { get; init; }
}

/// <summary>Runs one reaction of a claimed outbox row: the factory, then <see cref="ChangePipeline"/>.</summary>
/// <param name="pipeline">The pipeline.</param>
/// <param name="reaction">The reaction's name.</param>
/// <param name="recorded">The committed event.</param>
/// <param name="cancellationToken">Cancels the run.</param>
/// <returns>The pipeline's outcome.</returns>
public delegate ValueTask<Result<ChangeOutcome>> ReactionRunner(ChangePipeline pipeline, string reaction, RecordedEvent recorded,
    CancellationToken cancellationToken);

/// <summary>When the row of <paramref name="reaction"/> for <paramref name="domainEvent"/> is due; <see langword="null"/> = at the commit.</summary>
/// <param name="domainEvent">The committed event.</param>
/// <param name="reaction">The reaction's name.</param>
/// <returns>The due time, or <see langword="null"/>.</returns>
public delegate DateTimeOffset? ReactionSchedule(DomainEvent domainEvent, string reaction);

/// <summary>
/// The reactions of an application: their names, the store's <see cref="ReactionTable"/>, the schedule of timed reactions
/// and the runner. Each assembly with <see cref="OnEventAttribute{TEvent}"/> factories has a generated
/// <c>GeneratedReactions.Set</c>; <see cref="Combine"/> joins several. Pass it to the store
/// (<c>NpgsqlStoreOptions.Reactions</c>, <see cref="InMemoryStoreOptions.Reactions"/>) and to the worker.
/// </summary>
public sealed class ReactionSet : IOutboxRowHandler
{
    private readonly Dictionary<string, ReactionRunner> _runners;
    private readonly ReactionTable[] _tables;
    private readonly ReactionSchedule[] _schedules;
    private readonly HashSet<string> _scheduled;

    /// <summary>Creates the set of one assembly. Called by generated code.</summary>
    /// <param name="names">Every reaction name of the assembly.</param>
    /// <param name="table">The reactions per event (static arrays).</param>
    /// <param name="schedule">The due times of scheduled reactions.</param>
    /// <param name="scheduled">The names of the scheduled reactions.</param>
    /// <param name="runner">The switch from (name, event) to the factory and the pipeline.</param>
    /// <exception cref="ArgumentException">A name is empty or listed twice.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReactionSet(IReadOnlyList<string> names, ReactionTable table, ReactionSchedule schedule, IReadOnlyCollection<string> scheduled,
        ReactionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(scheduled);
        ArgumentNullException.ThrowIfNull(runner);
        _runners = new(StringComparer.Ordinal);
        foreach (var name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(names));
            if (!_runners.TryAdd(name, runner))
                throw new ArgumentException($"The reaction '{name}' is listed twice.", nameof(names));
        }
        _tables = [table];
        _schedules = [schedule];
        _scheduled = new(scheduled, StringComparer.Ordinal);
        Names = [.. names];
    }

    private ReactionSet(Dictionary<string, ReactionRunner> runners, ReactionTable[] tables, ReactionSchedule[] schedules, HashSet<string> scheduled)
    {
        _runners = runners;
        _tables = tables;
        _schedules = schedules;
        _scheduled = scheduled;
        Names = [.. runners.Keys];
    }

    /// <summary>A set without reactions (an application that only publishes).</summary>
    public static ReactionSet Empty { get; } = new([], static _ => ReadOnlySpan<string>.Empty, static (_, _) => null, [],
        static (_, reaction, _, _) => throw new InvalidOperationException($"No reaction named '{reaction}' is registered."));

    /// <summary>Every reaction name.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <inheritdoc />
    public string ActivityName(string reaction) => "reaction " + reaction;

    /// <summary>
    /// The table the store writes outbox rows from: one set returns its static arrays directly; a combined set concatenates
    /// the arrays of its parts per event.
    /// </summary>
    public ReactionTable Table => _tables.Length == 1 ? _tables[0] : Concatenated;

    /// <summary>Joins the sets of several assemblies into the one set of an application.</summary>
    /// <param name="sets">The sets.</param>
    /// <returns>The combined set.</returns>
    /// <exception cref="InvalidOperationException">A name is declared in two sets: names are stored in rows and keys, so they must be unique.</exception>
    public static ReactionSet Combine(params ReactionSet[] sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var runners = new Dictionary<string, ReactionRunner>(StringComparer.Ordinal);
        var scheduled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set, nameof(sets));
            foreach (var (name, runner) in set._runners)
                if (!runners.TryAdd(name, runner))
                    throw new InvalidOperationException($"The reaction '{name}' is declared in two assemblies; reaction names must be unique.");
            scheduled.UnionWith(set._scheduled);
        }
        return new(runners, [.. sets.SelectMany(s => s._tables)], [.. sets.SelectMany(s => s._schedules)], scheduled);
    }

    /// <inheritdoc />
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => Table(domainEvent);

    /// <summary>When the row of <paramref name="reaction"/> for <paramref name="domainEvent"/> is due.</summary>
    /// <param name="domainEvent">The committed event.</param>
    /// <param name="reaction">The reaction's name.</param>
    /// <returns>The due time of a scheduled reaction; <see langword="null"/> = at the commit.</returns>
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction)
    {
        foreach (var schedule in _schedules)
            if (schedule(domainEvent, reaction) is { } due)
                return due;
        return null;
    }

    /// <summary>Whether <paramref name="reaction"/> is scheduled (<see cref="OnEventAttribute{TEvent}.DueAt"/>): ordered by due time only.</summary>
    /// <param name="reaction">The reaction's name.</param>
    /// <returns><see langword="true"/> for a scheduled reaction.</returns>
    public bool IsScheduled(string reaction) => _scheduled.Contains(reaction);

    /// <summary>Runs <paramref name="reaction"/> for <paramref name="recorded"/> through <paramref name="pipeline"/>.</summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="reaction">The row's reaction name.</param>
    /// <param name="recorded">The row's event.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome; a replayed key is a success with <see cref="ChangeOutcome.Replayed"/>.</returns>
    /// <exception cref="InvalidOperationException">No reaction has that name (removed, or renamed without pinning its name).</exception>
    public ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(reaction);
        ArgumentNullException.ThrowIfNull(recorded);
        return _runners.TryGetValue(reaction, out var runner)
            ? runner(pipeline, reaction, recorded, cancellationToken)
            : throw new InvalidOperationException($"No reaction named '{reaction}' is registered (removed, or renamed without pinning its Name).");
    }

    /// <summary>
    /// The operation a reaction runs in: the event's context as the system (<c>Reactions.&lt;name&gt;</c>) on behalf of the
    /// original user, same tenant and correlation, caused by the event (docs/architecture/changes.md §5).
    /// </summary>
    /// <param name="reaction">The reaction's name.</param>
    /// <param name="recorded">The committed event.</param>
    /// <returns>The context.</returns>
    public static OperationContext Context(string reaction, RecordedEvent recorded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
        ArgumentNullException.ThrowIfNull(recorded);
        return recorded.Context.AsSystem("Reactions." + reaction).CausedBy((Id<ICausation>)recorded.EventId);
    }

    /// <summary>The options a reaction runs with: the key (tenant, <c>reaction:&lt;name&gt;</c>, event id), so it commits once (C-8).</summary>
    /// <param name="reaction">The reaction's name.</param>
    /// <param name="recorded">The committed event.</param>
    /// <returns>The options.</returns>
    public static ChangeOptions Options(string reaction, RecordedEvent recorded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
        ArgumentNullException.ThrowIfNull(recorded);
        return new(new IdempotencyKey(recorded.Context.Tenant, "reaction:" + reaction,
            recorded.EventId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The options of one element of a fan-out (R-1, TN-47): the key (tenant, <c>reaction:&lt;name&gt;</c>, event id and element), so a
    /// retried row commits each element once and runs only the ones that did not commit.
    /// </summary>
    /// <param name="reaction">The reaction's name.</param>
    /// <param name="recorded">The committed event.</param>
    /// <param name="element">The element: its target (<see cref="Element"/>).</param>
    /// <returns>The options.</returns>
    public static ChangeOptions Options(string reaction, RecordedEvent recorded, string element)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);
        return new(new IdempotencyKey(recorded.Context.Tenant, "reaction:" + reaction,
            recorded.EventId.ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ":" + element));
    }

    /// <summary>The element part of a fan-out key: the change's target, or its position for a change without one (a creation).</summary>
    /// <param name="target">The change's target; <see cref="Guid.Empty"/> for none.</param>
    /// <param name="index">The element's position in what the factory returned.</param>
    /// <returns>The element.</returns>
    public static string Element(Guid target, int index)
        => target == Guid.Empty ? "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : target.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A reaction that has nothing to do (R-2, TN-47): its factory returned <see langword="null"/>; the row is done.</summary>
    /// <returns>A success with <see cref="ChangeOutcome.Skipped"/>.</returns>
    public static ValueTask<Result<ChangeOutcome>> Skip() => ValueTask.FromResult<Result<ChangeOutcome>>(ChangeOutcome.Nothing);

    /// <summary>
    /// Runs every change a fan-out factory returned (R-1, TN-47), in order, each with its own key (<see cref="Options(string, RecordedEvent, string)"/>).
    /// The first failure stops and fails the row; its retry replays the elements that committed. <see langword="null"/> elements are
    /// skipped; none at all is <see cref="ChangeOutcome.Skipped"/>.
    /// </summary>
    /// <typeparam name="TChange">The change.</typeparam>
    /// <param name="changes">What the factory returned.</param>
    /// <param name="run">Runs one element (the element and its position).</param>
    /// <returns>The first failure, or a success counting the events of all elements (replayed when every element was).</returns>
    public static async ValueTask<Result<ChangeOutcome>> Each<TChange>(IEnumerable<TChange?>? changes, Func<TChange, int, ValueTask<Result<ChangeOutcome>>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (changes is null)
            return ChangeOutcome.Nothing;
        var (ran, events, replayed, position, index) = (0, 0, true, GlobalPosition.Start, -1);
        foreach (var change in changes)
        {
            index++;
            if (change is null)
                continue;
            var result = await run(change, index).ConfigureAwait(false);
            if (result.TryGetValue(out Violation? failed))
                return failed;
            var outcome = result.GetValueOrThrow();
            if (outcome.Skipped)
                continue;
            ran++;
            events += outcome.Events;
            replayed &= outcome.Replayed;
            if (outcome.Position.CompareTo(position) > 0)
                position = outcome.Position;
        }
        return ran == 0 ? ChangeOutcome.Nothing : new ChangeOutcome(Guid.Empty, 0, events, replayed, position);
    }

    private ReadOnlySpan<string> Concatenated(DomainEvent domainEvent)
    {
        List<string>? all = null;
        foreach (var table in _tables)
        {
            var names = table(domainEvent);
            if (names.IsEmpty)
                continue;
            all ??= [];
            foreach (var name in names)
                all.Add(name);
        }
        return all is null ? ReadOnlySpan<string>.Empty : all.ToArray();
    }
}
