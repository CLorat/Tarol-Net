namespace Tarol.Messaging;

/// <summary>What a consumer's handler tells the transport after a delivery.</summary>
public enum Ack
{
    /// <summary>Remove the delivery: done, replayed, dropped, or buried in the consumer's dead letters (M-9).</summary>
    Ack,

    /// <summary>Deliver it again after <see cref="Handled.RetryAfter"/>.</summary>
    Retry,
}

/// <summary>A handler's answer to one delivery.</summary>
/// <param name="Ack">Remove it or deliver it again.</param>
/// <param name="RetryAfter">The delay before the next delivery (for <see cref="Messaging.Ack.Retry"/>).</param>
public readonly record struct Handled(Ack Ack, TimeSpan RetryAfter = default);

/// <summary>One delivery of a message to a subscription.</summary>
/// <param name="Message">The envelope; <see langword="null"/> when the headers did not parse (a poison message).</param>
/// <param name="Attempt">1 for the first delivery, counted by the transport.</param>
/// <param name="AuthenticatedAs">The publishing system as the transport authenticated it; <see langword="null"/> when the transport stamps none.</param>
/// <param name="Raw">The body as it arrived (kept with a dead letter).</param>
/// <param name="Topic">The topic it arrived on.</param>
public sealed record Incoming(Message? Message, int Attempt, string? AuthenticatedAs, ReadOnlyMemory<byte> Raw, string Topic);

/// <summary>Handles one delivery; must not throw except for cancellation.</summary>
/// <param name="incoming">The delivery.</param>
/// <param name="cancellationToken">Stops the handler.</param>
/// <returns>Ack or retry.</returns>
public delegate ValueTask<Handled> MessageHandler(Incoming incoming, CancellationToken cancellationToken);

/// <summary>A durable subscription: one per consumer, so each consumer retries and dead-letters on its own.</summary>
/// <param name="Name">The durable name, <c>&lt;application&gt;.&lt;consumer&gt;</c>, e.g. <c>billing.IssueInvoice</c>.</param>
/// <param name="Topics">The contracts it receives.</param>
public sealed record Subscription(string Name, IReadOnlyList<string> Topics)
{
    /// <summary>Deliveries handled at once; 1 keeps the transport's order per subscription.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>How long one delivery may take before the transport delivers it again.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The transport port (docs/architecture/messaging.md §6): declare, publish durably, consume with an answer per delivery.
/// Implementations: <see cref="InMemoryTransport"/>, the PostgreSQL bus (<c>Tarol.Messaging.Npgsql</c>), NATS JetStream
/// (<c>Tarol.Messaging.Nats</c>), RabbitMQ (<c>Tarol.Messaging.RabbitMq</c>).
/// </summary>
public interface IMessageTransport
{
    /// <summary>Declares what publishing and consuming need (streams, exchanges, queues, subscription rows); idempotent.</summary>
    /// <param name="topics">The contracts this application publishes.</param>
    /// <param name="subscriptions">The subscriptions of its consumers.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the topology exists.</returns>
    Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken);

    /// <summary>Publishes; completes when the transport holds the message durably.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the message is durable.</returns>
    Task Publish(Message message, CancellationToken cancellationToken);

    /// <summary>Delivers the subscription's messages to <paramref name="handler"/> until cancelled.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="cancellationToken">Stops consuming.</param>
    /// <returns>A task that completes when consuming stopped.</returns>
    Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken);
}
