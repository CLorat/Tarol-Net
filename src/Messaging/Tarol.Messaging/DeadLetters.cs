using System.Globalization;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>A message a consumer gave up (M-9): kept by the consumer, with its headers and bytes, for a human to read and re-drive.</summary>
/// <param name="Id">The dead letter (re-drive it by this id).</param>
/// <param name="Subscription">The consumer's subscription, <c>&lt;application&gt;.&lt;consumer&gt;</c>.</param>
/// <param name="Topic">The topic it arrived on.</param>
/// <param name="MessageId">The message id, when the envelope parsed.</param>
/// <param name="Attempts">The attempt it was given up at.</param>
/// <param name="Reason">Why: the error code, or <c>Messaging.*</c> (Malformed, UnknownVersion, SourceMismatch, UnknownSource, TenantNotAllowed).</param>
/// <param name="Headers">The envelope's headers (empty for a poison message without them).</param>
/// <param name="Data">The body as it arrived.</param>
/// <param name="DeadAt">When it was given up.</param>
public sealed record DeadMessage(long Id, string Subscription, string Topic, Guid? MessageId, int Attempts, string Reason,
    IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Data, DateTimeOffset DeadAt)
{
    /// <summary>The tenant of the envelope (<c>ce-tenant</c>), when it had one.</summary>
    public Option<Id<ITenant>> Tenant
        => Headers.TryGetValue(MessageHeaders.Tenant, out var text) && Guid.TryParse(text, CultureInfo.InvariantCulture, out var tenant) && tenant != Guid.Empty
            ? (Id<ITenant>)tenant
            : Option.None;

    /// <summary>This dead letter as the admin entry lists it (kind <see cref="DeadLetterKinds.Messages"/>).</summary>
    /// <returns>The item; its name is the subscription, its subject <c>&lt;topic&gt;/&lt;message id&gt;</c>.</returns>
    public DeadLetterItem ToItem()
        => new(Id, DeadLetterKinds.Messages, Subscription, Tenant, $"{Topic}/{(MessageId is { } m ? m.ToString() : "-")}", Attempts, Reason, DeadAt);

    /// <summary>Whether it passes the filters of <paramref name="query"/> (the in-memory stores' paging).</summary>
    /// <param name="query">The query.</param>
    /// <returns><see langword="true"/> when it is on the query's pages.</returns>
    internal bool Matches(DeadLetterQuery query)
        => Id > query.After && (query.Name is null || Subscription == query.Name) && (query.Tenant.IsNone || Tenant == query.Tenant);
}

/// <summary>Where a consumer keeps the messages it gave up: in-memory here, <c>messages_dead</c> in <c>Tarol.Messaging.Npgsql</c>.</summary>
public interface IMessageDeadLetters
{
    /// <summary>Keeps one.</summary>
    /// <param name="subscription">The consumer's subscription.</param>
    /// <param name="incoming">The delivery.</param>
    /// <param name="reason">Why it is given up.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the dead letter is durable.</returns>
    Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken);

    /// <summary>The oldest dead letters.</summary>
    /// <param name="max">The most entries.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The dead letters, oldest first.</returns>
    Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken);

    /// <summary>
    /// One page (step 15, O-3): ids after <see cref="DeadLetterQuery.After"/>; <see cref="DeadLetterQuery.Name"/> is the
    /// subscription, <see cref="DeadLetterQuery.Tenant"/> the envelope's <c>ce-tenant</c>.
    /// </summary>
    /// <param name="query">Paging and filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Up to <see cref="DeadLetterQuery.Limit"/> dead letters, by id.</returns>
    Task<IReadOnlyList<DeadMessage>> List(DeadLetterQuery query, CancellationToken cancellationToken);

    /// <summary>Deletes one for good (step 15).</summary>
    /// <param name="id">The dead letter's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is none with that id.</returns>
    Task<bool> Discard(long id, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="Take(long, CancellationToken)"/>, on behalf of <paramref name="actor"/>: a database store writes the audit
    /// row in the same statement (the default calls <see cref="Take(long, CancellationToken)"/>).
    /// </summary>
    /// <param name="id">The dead letter's id.</param>
    /// <param name="actor">Who re-drives it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The dead letter, or <see langword="null"/>.</returns>
    Task<DeadMessage?> Take(long id, string actor, CancellationToken cancellationToken) => Take(id, cancellationToken);

    /// <summary>As <see cref="Discard(long, CancellationToken)"/>, audited for <paramref name="actor"/> by a database store.</summary>
    /// <param name="id">The dead letter's id.</param>
    /// <param name="actor">Who discards it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="false"/> when there is none with that id.</returns>
    Task<bool> Discard(long id, string actor, CancellationToken cancellationToken) => Discard(id, cancellationToken);

    /// <summary>Removes one and returns it (to re-drive it).</summary>
    /// <param name="id">The dead letter's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The dead letter, or <see langword="null"/> when there is none with that id.</returns>
    Task<DeadMessage?> Take(long id, CancellationToken cancellationToken);
}

/// <summary>Dead letters in memory (tests, single-process apps without a store).</summary>
public sealed class InMemoryMessageDeadLetters : IMessageDeadLetters
{
    private readonly TimeProvider _time;
    private readonly List<DeadMessage> _dead = [];
    private long _next;

    /// <summary>Creates the store.</summary>
    /// <param name="time">The clock of <see cref="DeadMessage.DeadAt"/>.</param>
    public InMemoryMessageDeadLetters(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <inheritdoc />
    public Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(reason);
        var headers = incoming.Message is { } message
            ? message.Headers().ToDictionary(static h => h.Key, static h => h.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_dead)
            _dead.Add(new DeadMessage(++_next, subscription, incoming.Topic, incoming.Message?.Id, incoming.Attempt, reason, headers,
                incoming.Raw.ToArray(), _time.GetUtcNow()));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken)
    {
        lock (_dead)
            return Task.FromResult<IReadOnlyList<DeadMessage>>([.. _dead.Take(max)]);
    }

    /// <summary>How many dead letters it holds.</summary>
    public int Count
    {
        get
        {
            lock (_dead)
                return _dead.Count;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadMessage>> List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_dead)
            return Task.FromResult<IReadOnlyList<DeadMessage>>([.. _dead.Where(d => d.Matches(query)).OrderBy(d => d.Id).Take(query.Limit)]);
    }

    /// <inheritdoc />
    public Task<bool> Discard(long id, CancellationToken cancellationToken)
    {
        lock (_dead)
            return Task.FromResult(_dead.RemoveAll(d => d.Id == id) == 1);
    }

    /// <inheritdoc />
    public Task<DeadMessage?> Take(long id, CancellationToken cancellationToken)
    {
        lock (_dead)
        {
            var found = _dead.Find(d => d.Id == id);
            if (found is not null)
                _dead.Remove(found);
            return Task.FromResult(found);
        }
    }
}

/// <summary>
/// The consumers' dead letters as an <see cref="IDeadLetterSource"/> of kind <see cref="DeadLetterKinds.Messages"/> (step 15):
/// a re-drive takes the message out (audited by a database store) and runs its consumer again.
/// </summary>
internal sealed class MessageDeadLetterSource(IMessageDeadLetters dead, MessageConsumers consumers) : IDeadLetterSource
{
    public string Kind => DeadLetterKinds.Messages;

    public async Task<DeadLetterPage> List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = await dead.List(query, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage([.. rows.Select(static d => d.ToItem())], rows.Count == query.Limit ? rows[^1].Id : null);
    }

    public async Task<bool> Redrive(long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return await consumers.Redrive(id, actor, cancellationToken).ConfigureAwait(false) is not null;
    }

    public Task<bool> Discard(long id, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return dead.Discard(id, actor, cancellationToken);
    }
}
