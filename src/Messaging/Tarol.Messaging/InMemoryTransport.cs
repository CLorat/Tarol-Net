namespace Tarol.Messaging;

/// <summary>
/// An in-process transport for tests and single-process apps: one queue per subscription (a fan-out per topic), redelivery
/// at a due time on the injected <see cref="TimeProvider"/>. Durable for the life of the process only.
/// </summary>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Delivery>> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _bindings = new(StringComparer.Ordinal);
    private readonly List<Message> _published = [];
    private TaskCompletionSource _signal = NewSignal();
    private long _sequence;

    /// <summary>Creates the transport.</summary>
    /// <param name="time">The clock of due times.</param>
    public InMemoryTransport(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>How long <see cref="Consume"/> waits when nothing is due and nothing was published (it wakes up at once on a publish).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Every message published, duplicates included, in order.</summary>
    public IReadOnlyList<Message> Published
    {
        get
        {
            lock (_gate)
                return [.. _published];
        }
    }

    /// <summary>How many deliveries wait (due or not) across all subscriptions.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
                return _queues.Values.Sum(static q => q.Count);
        }
    }

    /// <inheritdoc />
    public Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        lock (_gate)
            foreach (var subscription in subscriptions)
            {
                _queues.TryAdd(subscription.Name, []);
                foreach (var topic in subscription.Topics)
                {
                    if (!_bindings.TryGetValue(topic, out var names))
                        _bindings[topic] = names = [];
                    if (!names.Contains(subscription.Name, StringComparer.Ordinal))
                        names.Add(subscription.Name);
                }
            }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            _published.Add(message);
            if (_bindings.TryGetValue(message.Type, out var names))
                foreach (var name in names)
                    _queues[name].Add(new Delivery(++_sequence, message, 1, _time.GetUtcNow()));
        }
        Wake();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        while (!cancellationToken.IsCancellationRequested)
        {
            Task signal;
            lock (_gate)
                signal = _signal.Task;
            if (await RunDue(subscription.Name, handler, cancellationToken).ConfigureAwait(false) > 0)
                continue;
            try
            {
                await Task.WhenAny(signal, Task.Delay(PollInterval, _time, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Handles every delivery of <paramref name="subscription"/> that is due now, in publish order.</summary>
    /// <param name="subscription">The subscription's name.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>How many deliveries were handled.</returns>
    public async Task<int> RunDue(string subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        List<Delivery> due;
        lock (_gate)
        {
            if (!_queues.TryGetValue(subscription, out var queue))
                return 0;
            var now = _time.GetUtcNow();
            due = [.. queue.Where(d => d.DueAt <= now).OrderBy(static d => d.DueAt).ThenBy(static d => d.Sequence)];
            foreach (var delivery in due)
                queue.Remove(delivery);
        }
        foreach (var delivery in due)
        {
            Handled handled;
            try
            {
                handled = await handler(new Incoming(delivery.Message, delivery.Attempt, null, delivery.Message.Data, delivery.Message.Type), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                Requeue(subscription, delivery with { DueAt = _time.GetUtcNow() });  // nothing is lost when a handler breaks its contract
                throw;
            }
            if (handled.Ack == Ack.Retry)
                Requeue(subscription, delivery with { Attempt = delivery.Attempt + 1, DueAt = _time.GetUtcNow() + handled.RetryAfter });
        }
        return due.Count;
    }

    private void Requeue(string subscription, Delivery delivery)
    {
        lock (_gate)
            _queues[subscription].Add(delivery);
        Wake();
    }

    private void Wake()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _signal;
            _signal = NewSignal();
        }
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Delivery(long Sequence, Message Message, int Attempt, DateTimeOffset DueAt);
}
