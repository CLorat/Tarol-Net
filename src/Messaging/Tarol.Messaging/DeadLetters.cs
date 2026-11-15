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
    IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Data, DateTimeOffset DeadAt);

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
