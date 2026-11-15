using System.Threading.Channels;

namespace Tarol.Messaging;

/// <summary>What a consumer's handler decided; the transport acknowledges or redelivers.</summary>
public enum Ack
{
    /// <summary>Done, buried by the consumer (DL-1: the dead letter is in the consumer's table), or not for us: remove it.</summary>
    Ack,
    /// <summary>Deliver again after <see cref="Handled.RetryAfter"/>.</summary>
    Retry,
}

/// <summary>A handler's answer.</summary>
public readonly record struct Handled(Ack Ack, TimeSpan RetryAfter = default);

/// <summary>A delivered message: the envelope, the attempt, and the identity the transport authenticated (if it knows one).</summary>
/// <param name="Message">The envelope; <see langword="null"/> for a delivery whose headers did not parse (poison).</param>
/// <param name="Attempt">1 for the first delivery.</param>
/// <param name="AuthenticatedAs">The publisher's identity as the transport saw it (the PostgreSQL role); <see langword="null"/> when the transport stamps none.</param>
/// <param name="Raw">The raw data, for a dead letter of a poison message.</param>
/// <param name="Topic">The topic it arrived on.</param>
public sealed record Incoming(Message? Message, int Attempt, string? AuthenticatedAs, ReadOnlyMemory<byte> Raw, string Topic);

/// <summary>Handles one delivery.</summary>
public delegate ValueTask<Handled> MessageHandler(Incoming incoming, CancellationToken cancellationToken);

/// <summary>A durable subscription: one per consumer (each consumer retries and dead-letters on its own).</summary>
/// <param name="Name">The subscription's durable name, e.g. <c>billing.IssueInvoice</c>.</param>
/// <param name="Topics">The contracts it receives.</param>
public sealed record Subscription(string Name, IReadOnlyList<string> Topics)
{
    /// <summary>Messages handled at once (1 keeps the transport's order).</summary>
    public int Concurrency { get; init; } = 1;
    /// <summary>How long a delivery may take before the transport redelivers it (a lease, NATS AckWait).</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The transport port (candidate T-A): publish durably, consume with ack/retry. Brokers and the in-database bus implement
/// it. The old <c>IMessageBackplane</c> (<c>Publish(topic, bytes)</c>, <c>Start(Func&lt;topic, bytes, Task&gt;)</c>) is this port
/// without an envelope, without a durable subscription and without an answer (candidate T-D).
/// </summary>
public interface IMessageTransport : IAsyncDisposable
{
    /// <summary>A short name for logs and benchmarks.</summary>
    string Name { get; }

    /// <summary>Declares what the transport needs before publishing (streams, exchanges, queues, subscription rows).</summary>
    Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken);

    /// <summary>Publishes; returns when the transport holds the message durably (JetStream ack, publisher confirm, commit).</summary>
    Task Publish(Message message, CancellationToken cancellationToken);

    /// <summary>Consumes until cancelled.</summary>
    Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken);
}

/// <summary>An in-process transport for tests: durable for the process, fan-out per subscription, redelivery with delay.</summary>
public sealed class InMemoryTransport(TimeProvider time) : IMessageTransport
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Channel<(Message Message, int Attempt)>> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _bindings = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Name => "in-memory";

    /// <summary>How many messages were published (duplicates included).</summary>
    public int Published { get; private set; }

    /// <inheritdoc />
    public Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        lock (_gate)
            foreach (var s in subscriptions)
            {
                _queues.TryAdd(s.Name, Channel.CreateUnbounded<(Message, int)>());
                foreach (var topic in s.Topics)
                {
                    if (!_bindings.TryGetValue(topic, out var list))
                        _bindings[topic] = list = [];
                    if (!list.Contains(s.Name))
                        list.Add(s.Name);
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
            Published++;
            if (_bindings.TryGetValue(message.Type, out var subscribers))
                foreach (var s in subscribers)
                    _queues[s].Writer.TryWrite((message, 1));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        Channel<(Message Message, int Attempt)> queue;
        lock (_gate)
            queue = _queues[subscription.Name];
        await foreach (var (message, attempt) in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var handled = await handler(new Incoming(message, attempt, null, message.Data, message.Type), cancellationToken).ConfigureAwait(false);
            if (handled.Ack == Ack.Retry)
                _ = Redeliver(queue, message, attempt + 1, handled.RetryAfter, cancellationToken);
        }
    }

    private async Task Redeliver(Channel<(Message, int)> queue, Message message, int attempt, TimeSpan after, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(after, time, cancellationToken).ConfigureAwait(false);
            queue.Writer.TryWrite((message, attempt));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
