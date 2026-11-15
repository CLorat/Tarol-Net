using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Tarol.Messaging.RabbitMq;

/// <summary>How the RabbitMQ transport connects and names its exchanges and queues.</summary>
public sealed record RabbitMqTransportOptions
{
    /// <summary>The broker, e.g. <c>amqp://billing:secret@localhost:5672/</c> (the user is this system's); required.</summary>
    public required Uri Uri { get; init; }

    /// <summary>
    /// A namespace in front of every exchange and queue (e.g. <c>staging</c> → exchange <c>staging.artflow</c>), for
    /// environments that share a virtual host; <see langword="null"/> (default) = none. Permissions then name the prefixed names.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// Stamp <c>user_id</c> = the connection's user on every publish (default <see langword="true"/>): the broker refuses a
    /// different one, so a consumer sees the publishing user as <see cref="Incoming.AuthenticatedAs"/>. Name each system's
    /// user like its contract prefix.
    /// </summary>
    public bool StampUserId { get; init; } = true;

    /// <summary>The connection's name shown by the broker (default <c>tarol</c>).</summary>
    public string ClientName { get; init; } = "tarol";

    internal string User => Uri.UserInfo.Split(':', 2)[0] is { Length: > 0 } user ? Uri.UnescapeDataString(user) : "guest";

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Uri);
        if (Prefix is { } prefix && (prefix.Length == 0 || prefix.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            throw new ArgumentException($"The prefix '{prefix}' is letters, digits, '-' and '_'.", nameof(Prefix));
    }
}

/// <summary>
/// The transport port over RabbitMQ (docs/architecture/messaging.md §6, T-3): one durable topic exchange per source system
/// (routing key = contract), one quorum queue per subscription, publisher confirms (a publish returns after the broker's
/// confirm), manual acknowledgement. A retry is an acknowledgement plus a copy into <c>&lt;queue&gt;.retry</c> whose per-message
/// TTL dead-letters it back to the queue; the attempt travels in <c>x-tarol-attempt</c>.
/// </summary>
/// <remarks>
/// A publish is <c>mandatory</c>: a contract nobody subscribes is returned by the broker; it is logged and counts as
/// delivered (a publisher does not know its consumers). The broker validates <c>user_id</c>, so it is the authenticated
/// publisher. A duplicate is delivered again and replayed by the consumer's key.
/// </remarks>
public sealed partial class RabbitMqTransport : IMessageTransport, IAsyncDisposable
{
    private const string AttemptHeader = "x-tarol-attempt";

    private readonly RabbitMqTransportOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _prefix;
    private IConnection? _connection;
    private IChannel? _publish;
    private long _unroutable;

    /// <summary>Creates the transport; it connects on first use.</summary>
    /// <param name="options">Broker, prefix, user id.</param>
    /// <param name="logger">Logs unroutable messages and broken channels; <see langword="null"/> = none.</param>
    public RabbitMqTransport(RabbitMqTransportOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _log = logger ?? NullLogger.Instance;
        _prefix = options.Prefix is { } p ? p + "." : "";
    }

    /// <summary>How many publishes the broker returned as unroutable (no queue bound; counted as delivered).</summary>
    public long Unroutable => Interlocked.Read(ref _unroutable);

    /// <summary>The exchange of a contract's source: <c>[prefix.]source</c>.</summary>
    /// <param name="contract">A contract such as <c>artflow.auction-settled</c>.</param>
    /// <returns>The exchange.</returns>
    public string ExchangeOf(string contract)
    {
        var source = Message.SystemOf(contract);
        return source.Length > 0 ? _prefix + source : throw new ArgumentException($"The contract '{contract}' has no system prefix.", nameof(contract));
    }

    /// <summary>The queue of a subscription: <c>[prefix.]&lt;subscription&gt;</c>.</summary>
    /// <param name="subscription">The subscription's name.</param>
    /// <returns>The queue.</returns>
    public string QueueOf(string subscription) => _prefix + subscription;

    /// <inheritdoc />
    /// <remarks>
    /// Declares the exchanges of the published contracts, the exchanges of the subscribed ones (passively when this user may
    /// not configure another system's exchange: it must exist then), and per subscription its quorum queue, its retry queue
    /// and the bindings.
    /// </remarks>
    public async Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(subscriptions);
        var connection = await Connection(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            foreach (var exchange in topics.Select(ExchangeOf).Distinct(StringComparer.Ordinal))
                await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // another system's exchange: declare it if this user may configure it, else it must exist (its publisher declares it)
        foreach (var exchange in subscriptions.SelectMany(static s => s.Topics).Select(ExchangeOf).Except(topics.Select(ExchangeOf), StringComparer.Ordinal).Distinct(StringComparer.Ordinal))
            await DeclareForeign(connection, exchange, cancellationToken).ConfigureAwait(false);
        channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            foreach (var subscription in subscriptions)
            {
                var queue = QueueOf(subscription.Name);
                await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken).ConfigureAwait(false);
                await channel.QueueDeclareAsync(queue + ".retry", durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object?>
                    {
                        ["x-queue-type"] = "quorum",
                        ["x-dead-letter-exchange"] = "",
                        ["x-dead-letter-routing-key"] = queue,
                        ["x-dead-letter-strategy"] = "at-least-once",
                        ["x-overflow"] = "reject-publish",
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var topic in subscription.Topics)
                    await channel.QueueBindAsync(queue, ExchangeOf(topic), topic, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeclareForeign(IConnection connection, string exchange, CancellationToken cancellationToken)
    {
        try
        {
            var active = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (active.ConfigureAwait(false))
                await active.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException e) when (e.ShutdownReason?.ReplyCode == 403)
        {
            var passive = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (passive.ConfigureAwait(false))
                await passive.ExchangeDeclarePassiveAsync(exchange, cancellationToken).ConfigureAwait(false);   // 404 until its publisher declared it: the host retries
        }
    }

    /// <inheritdoc />
    /// <exception cref="OperationInterruptedException">The broker refused the publish (e.g. <c>ACCESS_REFUSED</c>: this user may not write the exchange).</exception>
    public async Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Connection(cancellationToken).ConfigureAwait(false);
        var properties = new BasicProperties
        {
            MessageId = message.DedupId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            UserId = _options.StampUserId ? _options.User : null,
            Headers = message.Headers().ToDictionary(static h => h.Key, static h => (object?)h.Value),
        };
        try
        {
            await _publish!.BasicPublishAsync(ExchangeOf(message.Type), message.Type, mandatory: true, properties, message.Data, cancellationToken).ConfigureAwait(false);
        }
        catch (PublishException e) when (e.IsReturn)
        {
            Interlocked.Increment(ref _unroutable);
            Unrouted(_log, message.Type, message.DedupId);                               // nobody subscribes: delivered to nobody
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Consumes the subscription's queue with <see cref="Subscription.Concurrency"/> handlers at once (prefetch = concurrency).
    /// A broken channel ends the call with an exception; the host consumes again after its delay.
    /// </remarks>
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        var connection = await Connection(cancellationToken).ConfigureAwait(false);
        var queue = QueueOf(subscription.Name);
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: (ushort)subscription.Concurrency),
            cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            var retry = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
            await using (retry.ConfigureAwait(false))
            {
                var broken = new TaskCompletionSource<ShutdownEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, args) =>
                {
                    broken.TrySetResult(args);
                    return Task.CompletedTask;
                };
                await channel.BasicQosAsync(0, (ushort)Math.Max(1, subscription.Concurrency), false, cancellationToken).ConfigureAwait(false);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => Handle(delivery, channel, retry, queue, handler, cancellationToken);
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
                var stopped = Task.Delay(Timeout.Infinite, cancellationToken);
                if (await Task.WhenAny(stopped, broken.Task).ConfigureAwait(false) == broken.Task && !cancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException($"The channel of {queue} closed: {broken.Task.Result.ReplyText}");
            }
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

    private async Task Handle(BasicDeliverEventArgs delivery, IChannel channel, IChannel retry, string queue, MessageHandler handler, CancellationToken cancellationToken)
    {
        var data = delivery.Body.ToArray();                                                    // the body is valid inside this handler only
        var headers = delivery.BasicProperties.Headers;
        string? Header(string name) => headers is not null && headers.TryGetValue(name, out var value) ? value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            null => null,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        } : null;
        var message = Message.From(Header, data);
        var attempt = int.TryParse(Header(AttemptHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var counted) ? counted : 1;
        if (delivery.Redelivered)
            attempt++;                                                                         // a consumer died or a connection broke
        var topic = delivery.RoutingKey;
        if (topic == queue && Header(MessageHeaders.Type) is { } type)
            topic = type;                                                                      // back from the retry queue
        Handled handled;
        try
        {
            handled = await handler(new Incoming(message, attempt, delivery.BasicProperties.UserId, data, topic), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;                                                                            // unacknowledged: delivered again
        }
        try
        {
            if (handled.Ack == Ack.Retry)
            {
                var copy = new BasicProperties(delivery.BasicProperties)
                {
                    Expiration = Math.Max(1L, (long)handled.RetryAfter.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                    Headers = new Dictionary<string, object?>(headers ?? new Dictionary<string, object?>())
                    {
                        [AttemptHeader] = (attempt + 1).ToString(CultureInfo.InvariantCulture),
                    },
                };
                await retry.BasicPublishAsync("", queue + ".retry", mandatory: true, copy, data, CancellationToken.None).ConfigureAwait(false);
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AlreadyClosedException or OperationInterruptedException or PublishException)
        {
            AckFailed(_log, queue, e);                                                         // unacknowledged: delivered again
        }
    }

    private async Task<IConnection> Connection(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open && _publish is { IsOpen: true })
            return open;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } again && _publish is { IsOpen: true })
                return again;
            if (_connection is not { IsOpen: true })
                _connection = await new ConnectionFactory { Uri = _options.Uri, ClientProvidedName = _options.ClientName }
                    .CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _publish = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(EventId = 11301, Level = LogLevel.Information, Message = "{Contract} {DedupId} is unroutable: no queue is bound to it (published to nobody)")]
    private static partial void Unrouted(ILogger logger, string contract, string dedupId);

    [LoggerMessage(EventId = 11302, Level = LogLevel.Warning, Message = "Acknowledging a delivery of {Queue} failed; the broker delivers it again")]
    private static partial void AckFailed(ILogger logger, string queue, Exception exception);
}
