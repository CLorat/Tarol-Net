using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas;

/// <summary>
/// Marks a static factory of a change as a reaction to <typeparamref name="TEvent"/> (C-5). The decided shape is
/// <c>[OnEvent&lt;T&gt;]</c> in Tarol.Changes; the probe declares it here.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class OnEventAttribute<TEvent> : Attribute
    where TEvent : DomainEvent
{
    /// <summary>The stable reaction name stored in outbox rows and keys; default: the change's type name.</summary>
    public string? Name { get; init; }

    /// <summary>A <see cref="DateTimeOffset"/> member of the event: the row is due then, not at the commit (saga timeouts).</summary>
    public string? DueAt { get; init; }
}

/// <summary>Runs one reaction of a claimed row: maps the event to the change and runs it through the pipeline.</summary>
public delegate ValueTask<Result<ChangeOutcome>> ReactionRunner(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken);

/// <summary>When a row of <paramref name="reaction"/> for <paramref name="domainEvent"/> is due; <see langword="null"/> = at the commit.</summary>
public delegate DateTimeOffset? ReactionSchedule(DomainEvent domainEvent, string reaction);

/// <summary>
/// What the generator emits per assembly (<c>GeneratedReactions.Set</c>): the reaction names, the store's
/// <see cref="ReactionTable"/>, the schedule and the runner. <see cref="Combine"/> composes assemblies (S-3).
/// </summary>
public sealed class ReactionSet
{
    private readonly Dictionary<string, ReactionRunner> _runners;
    private readonly ReactionTable[] _tables;
    private readonly ReactionSchedule[] _schedules;

    /// <summary>Creates a set (generated code calls this).</summary>
    public ReactionSet(IReadOnlyList<string> names, ReactionTable table, ReactionSchedule schedule, ReactionRunner runner)
    {
        _runners = new(StringComparer.Ordinal);
        foreach (var name in names)
            _runners.Add(name, runner);
        _tables = [table];
        _schedules = [schedule];
        Names = [.. names];
    }

    private ReactionSet(Dictionary<string, ReactionRunner> runners, ReactionTable[] tables, ReactionSchedule[] schedules, string[] names)
        => (_runners, _tables, _schedules, Names) = (runners, tables, schedules, names);

    /// <summary>Every reaction name of the set.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Composes the sets of several assemblies; a name in two sets throws (names are stored in rows and keys).</summary>
    public static ReactionSet Combine(params ReactionSet[] sets)
    {
        var runners = new Dictionary<string, ReactionRunner>(StringComparer.Ordinal);
        foreach (var set in sets)
            foreach (var (name, runner) in set._runners)
                if (!runners.TryAdd(name, runner))
                    throw new InvalidOperationException($"The reaction '{name}' is declared in two assemblies; reaction names must be unique.");
        return new(runners, [.. sets.SelectMany(s => s._tables)], [.. sets.SelectMany(s => s._schedules)], [.. runners.Keys]);
    }

    /// <summary>The store's table: one set returns its span directly; several are concatenated per event.</summary>
    public ReactionTable Table => _tables.Length == 1 ? _tables[0] : Concat;

    private ReadOnlySpan<string> Concat(DomainEvent e)
    {
        List<string>? all = null;
        foreach (var table in _tables)
        {
            var names = table(e);
            if (names.IsEmpty)
                continue;
            all ??= [];
            foreach (var n in names)
                all.Add(n);
        }
        return all is null ? ReadOnlySpan<string>.Empty : all.ToArray();
    }

    /// <summary>When the row is due.</summary>
    public DateTimeOffset? DueAt(DomainEvent e, string reaction)
    {
        foreach (var schedule in _schedules)
            if (schedule(e, reaction) is { } due)
                return due;
        return null;
    }

    /// <summary>Runs <paramref name="reaction"/> for <paramref name="recorded"/>.</summary>
    /// <exception cref="InvalidOperationException">An unknown name: a row of a reaction that was removed or renamed.</exception>
    public ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken ct)
        => _runners.TryGetValue(reaction, out var runner)
            ? runner(pipeline, reaction, recorded, ct)
            : throw new InvalidOperationException($"No reaction named '{reaction}' is registered (removed or renamed? pin it with Name = \"…\").");

    /// <summary>The operation of a reaction: the event's context as the system, caused by the event (changes.md §5).</summary>
    public static OperationContext Context(string reaction, RecordedEvent recorded)
        => recorded.Context.AsSystem("Reactions." + reaction).CausedBy((Id<ICausation>)recorded.EventId);

    /// <summary>The options of a reaction: key (tenant, "reaction:" + name, event id), effect once (C-8).</summary>
    public static ChangeOptions Options(string reaction, RecordedEvent recorded)
        => new(new IdempotencyKey(recorded.Context.Tenant, "reaction:" + reaction, recorded.EventId.ToString("N")));
}
