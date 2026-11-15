using System.Globalization;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Tarol.Messaging.RabbitMq;

/// <summary>
/// The transport port over RabbitMQ (candidate T-3): one durable topic exchange per source system, routing key = contract;
/// one quorum queue per subscription; publisher confirms (publish returns after the broker's confirm); manual ack; a retry
/// = ack + a copy into <c>&lt;queue&gt;.retry</c> with a per-message TTL that dead-letters back to the queue (RabbitMQ has no
/// native delayed nack). The broker validates <c>user_id</c> against the connection's user when it is set.
/// </summary>
public sealed class RabbitTransport(string uri) : IMessageTransport
{
    private IConnection? _connection;
    private IChannel? _publish;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Probe: set <c>user_id</c> on published messages to this value (a forged one is refused by the broker).</summary>
    public string? UserId { get; init; }

    /// <inheritdoc />
    public string Name => "rabbitmq";

    private static string ExchangeOf(string topic) => topic[..topic.IndexOf('.', StringComparison.Ordinal)];

    private async Task<IConnection> Connection(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
            return open;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } again)
                return again;
            _connection = await new ConnectionFactory { Uri = new Uri(uri), ClientProvidedName = "tarol-probe" }.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _publish = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(subscriptions);
        var connection = await Connection(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var exchange in topics.Concat(subscriptions.SelectMany(s => s.Topics)).Select(ExchangeOf).Distinct(StringComparer.Ordinal))
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var s in subscriptions)
        {
            await channel.QueueDeclareAsync(s.Name, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueDeclareAsync(s.Name + ".retry", durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = s.Name,
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var topic in s.Topics)
                await channel.QueueBindAsync(s.Name, ExchangeOf(topic), topic, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Connection(cancellationToken).ConfigureAwait(false);
        var properties = new BasicProperties
        {
            MessageId = message.DedupId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            UserId = UserId,
            Headers = message.Headers().ToDictionary(h => h.Key, h => (object?)h.Value),
        };
        await _publish!.BasicPublishAsync(ExchangeOf(message.Type), message.Type, mandatory: true, properties, message.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        var connection = await Connection(cancellationToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: (ushort)subscription.Concurrency),
            cancellationToken).ConfigureAwait(false);
        await using var retry = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        await channel.BasicQosAsync(0, (ushort)Math.Max(1, subscription.Concurrency), false, cancellationToken).ConfigureAwait(false);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var data = delivery.Body.ToArray();                                           // the body is only valid in the handler
            var headers = delivery.BasicProperties.Headers;
            string? Header(string name) => headers is not null && headers.TryGetValue(name, out var v) ? v switch
            {
                byte[] b => Encoding.UTF8.GetString(b),
                string s => s,
                null => null,
                _ => Convert.ToString(v, CultureInfo.InvariantCulture),
            } : null;
            var message = Message.From(Header, data);
            var attempt = int.TryParse(Header("x-tarol-attempt"), NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 1;
            if (delivery.Redelivered)
                attempt++;                                                                // a lease ran out or a connection broke
            var handled = await handler(new Incoming(message, attempt, delivery.BasicProperties.UserId, data, delivery.RoutingKey), cancellationToken).ConfigureAwait(false);
            if (handled.Ack == Ack.Retry)
            {
                var copy = new BasicProperties(delivery.BasicProperties)
                {
                    Expiration = ((long)handled.RetryAfter.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                    Headers = new Dictionary<string, object?>(headers ?? new Dictionary<string, object?>()) { ["x-tarol-attempt"] = (attempt + 1).ToString(CultureInfo.InvariantCulture) },
                };
                await retry.BasicPublishAsync("", subscription.Name + ".retry", mandatory: true, copy, data, CancellationToken.None).ConfigureAwait(false);
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
        };
        await channel.BasicConsumeAsync(subscription.Name, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Raw publish with a routing key, for the permission probe.</summary>
    public async Task<string> TryPublishRaw(string exchange, string routingKey, string? userId, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), cancellationToken).ConfigureAwait(false);
                await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, new BasicProperties { UserId = userId }, "forged"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                return "accepted";
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "refused: " + e.GetType().Name + ": " + e.Message.Split('\n')[0];
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_publish is not null)
            await _publish.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
