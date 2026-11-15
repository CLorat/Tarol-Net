using System.Diagnostics;
using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Primitives;

namespace Tarol.Messaging;

/// <summary>
/// On a static mapping method (candidate PB-A): the outward anti-corruption layer. When <typeparamref name="TEvent"/>
/// commits, the store writes one outbox row per publication in the same transaction; the worker maps the event to the
/// contract record and publishes it, at least once, with the event id as the message id.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PublishesAttribute<TEvent>(string contract, int version) : Attribute
    where TEvent : DomainEvent
{
    /// <summary>The contract, prefixed by this system.</summary>
    public string Contract { get; } = contract;
    /// <summary>Its version.</summary>
    public int Version { get; } = version;
}

/// <summary>One publication: its outbox name (<c>publish:&lt;contract&gt;@&lt;version&gt;</c>), contract and version.</summary>
public sealed record PublicationInfo(string Name, string Contract, int Version);

/// <summary>Maps and serializes one event for a publication; <see langword="null"/> = not published (a filter).</summary>
public delegate byte[]? PublicationMapper(string publication, RecordedEvent recorded);

/// <summary>The publications of one assembly (generated <c>GeneratedPublications.Set</c>).</summary>
public sealed class PublicationSet(string source, IReadOnlyList<PublicationInfo> publications, ReactionTable table, PublicationMapper mapper)
{
    private readonly Dictionary<string, PublicationInfo> _byName = publications.ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>The publishing system (the prefix of its contracts).</summary>
    public string Source { get; } = source;
    /// <summary>The publications.</summary>
    public IReadOnlyList<PublicationInfo> Publications { get; } = publications;
    /// <summary>The contracts it publishes.</summary>
    public IReadOnlyList<string> Topics => [.. Publications.Select(p => p.Contract).Distinct(StringComparer.Ordinal)];
    /// <summary>The outbox rows per event.</summary>
    public ReactionTable Table { get; } = table;

    /// <summary>Observes every publish (probe).</summary>
    public Action<Message>? Published { get; set; }
    /// <summary>Probe: fail after publishing (a crash between publish and complete).</summary>
    public Func<Message, bool>? FailAfterPublish { get; set; }

    /// <summary>
    /// The publications as rows of the reaction worker (candidate O-A): same outbox table, the name tells them apart, the
    /// worker's claim, head order, backoff, dead letters, lease and tracing apply unchanged. The runner ignores the pipeline.
    /// </summary>
    public ReactionSet AsReactions(IMessageTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return new ReactionSet([.. Publications.Select(p => p.Name)], Table, static (_, _) => null, [],
            async (pipeline, name, recorded, cancellationToken) =>
            {
                var info = _byName[name];
                if (mapper(name, recorded) is not { } data)
                    return new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0);
                var message = Message.Of(recorded, Source, info.Contract, info.Version, data,
                    Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null);
                await transport.Publish(message, cancellationToken).ConfigureAwait(false);
                Published?.Invoke(message);
                if (FailAfterPublish?.Invoke(message) == true)
                    throw new InvalidOperationException("probe: crashed after publish, before complete");
                return new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0);
            });
    }
}

/// <summary>Joins several <see cref="IOutboxReactions"/> for the store (reactions + publications + effects).</summary>
public sealed class OutboxRows(params ReactionSet[] sets) : IOutboxReactions
{
    private readonly ReactionSet _all = ReactionSet.Combine(sets);

    /// <summary>The combined set (the worker's).</summary>
    public ReactionSet Set => _all;

    /// <inheritdoc />
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => _all.ReactionsTo(domainEvent);
    /// <inheritdoc />
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => _all.DueAt(domainEvent, reaction);
    /// <inheritdoc />
    public bool IsScheduled(string reaction) => _all.IsScheduled(reaction);
}

/// <summary>
/// An effect (S-2): a handler of an outgoing message that calls something outside (e-mail, an HTTP API) at least once,
/// with the event id as the idempotency key the receiver deduplicates by. The same outbox rows as a publication.
/// </summary>
public static class Effects
{
    /// <summary>One effect as a reaction set: <paramref name="name"/> runs <paramref name="effect"/> for every event the table names.</summary>
    public static ReactionSet Of(string name, ReactionTable table, Func<RecordedEvent, string, CancellationToken, Task> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new ReactionSet([name], table, static (_, _) => null, [], async (pipeline, n, recorded, cancellationToken) =>
        {
            await effect(recorded, recorded.EventId.ToString("N", System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0);
        });
    }
}
