using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Tarol.Messaging.Nats;

/// <summary>
/// The transport port over NATS JetStream (candidate T-2): one stream per source system (subjects <c>&lt;source&gt;.&gt;</c>,
/// file storage, a duplicate window), publish waits for the stream's ack and carries <c>Nats-Msg-Id</c> = (event, contract,
/// version); one durable pull consumer per subscription, explicit ack, <c>Nak(delay)</c> for a retry, <c>AckWait</c> = the lease.
/// NATS stamps no publisher identity: subject permissions per user decide who may publish under a prefix.
/// </summary>
public sealed class NatsTransport : IMessageTransport
{
    private readonly NatsConnection _connection;
    private readonly INatsJSContext _js;

    /// <summary>Connects (lazily) to <paramref name="url"/>, optionally as a user.</summary>
    public NatsTransport(string url, string? user = null, string? password = null)
    {
        _connection = new NatsConnection(NatsOpts.Default with
        {
            Url = url,
            Name = "tarol-probe",
            AuthOpts = user is null ? NatsAuthOpts.Default : NatsAuthOpts.Default with { Username = user, Password = password },
        });
        _js = new NatsJSContext(_connection);
    }

    /// <summary>The duplicate window of the streams (JetStream default 2 minutes).</summary>
    public TimeSpan Duplicates { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Probe: publish with <c>Nats-Msg-Id</c> = the event id only (shows the per-stream dedup trap).</summary>
    public bool DedupByEventIdOnly { get; init; }

    /// <summary>How many publishes the stream reported as duplicates.</summary>
    public int Duplicated { get; private set; }

    /// <inheritdoc />
    public string Name => "nats";

    private static string StreamOf(string topic) => topic[..topic.IndexOf('.', StringComparison.Ordinal)].ToUpperInvariant();

    /// <inheritdoc />
    public async Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(subscriptions);
        foreach (var stream in topics.Concat(subscriptions.SelectMany(s => s.Topics)).Select(StreamOf).Distinct(StringComparer.Ordinal))
            await _js.CreateOrUpdateStreamAsync(new StreamConfig(stream, [stream.ToLowerInvariant() + ".>"])
            {
                Storage = StreamConfigStorage.File,
                DuplicateWindow = Duplicates,
            }, cancellationToken).ConfigureAwait(false);
        foreach (var s in subscriptions)
            await _js.CreateOrUpdateConsumerAsync(StreamOf(s.Topics[0]), new ConsumerConfig(Durable(s))
            {
                FilterSubjects = [.. s.Topics],
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = s.Lease,
                MaxAckPending = s.Concurrency,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            }, cancellationToken).ConfigureAwait(false);
    }

    private static string Durable(Subscription s) => s.Name.Replace('.', '_');

    /// <inheritdoc />
    public async Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var headers = new NatsHeaders();
        foreach (var (k, v) in message.Headers())
            headers[k] = v;
        var ack = await _js.PublishAsync(message.Type, message.Data.ToArray(), headers: headers,
            opts: new NatsJSPubOpts { MsgId = DedupByEventIdOnly ? message.Id.ToString("N") : message.DedupId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        ack.EnsureSuccess();
        if (ack.Duplicate)
            Duplicated++;
    }

    /// <inheritdoc />
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        var consumer = await _js.GetConsumerAsync(StreamOf(subscription.Topics[0]), Durable(subscription), cancellationToken).ConfigureAwait(false);
        using var slots = new SemaphoreSlim(subscription.Concurrency);
        var running = new List<Task>();
        try
        {
            await foreach (var msg in consumer.ConsumeAsync<byte[]>(opts: new NatsJSConsumeOpts { MaxMsgs = Math.Max(16, subscription.Concurrency * 2) }, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        var data = msg.Data ?? [];
                        var message = Message.From(h => msg.Headers is { } hs && hs.TryGetValue(h, out var v) ? v.ToString() : null, data);
                        var attempt = (int)(msg.Metadata?.NumDelivered ?? 1);
                        var handled = await handler(new Incoming(message, attempt, null, data, msg.Subject), cancellationToken).ConfigureAwait(false);
                        if (handled.Ack == Ack.Ack)
                            await msg.AckAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        else
                            await msg.NakAsync(delay: handled.RetryAfter, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Raw publish on core NATS, for the permission probe.</summary>
    public async Task<string> TryPublishRaw(string subject, CancellationToken cancellationToken)
    {
        try
        {
            var ack = await _js.PublishAsync(subject, "forged"u8.ToArray(), opts: new NatsJSPubOpts { MsgId = Guid.NewGuid().ToString("N") }, cancellationToken: cancellationToken).ConfigureAwait(false);
            ack.EnsureSuccess();
            return "accepted (stream " + ack.Stream + ")";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "refused: " + e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>Messages in a stream.</summary>
    public async Task<long> Count(string stream, CancellationToken cancellationToken)
        => (await _js.GetStreamAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)).Info.State.Messages;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
