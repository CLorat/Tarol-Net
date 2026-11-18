using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Tarol.Messaging.Nats;

/// <summary>How the NATS transport connects and names its streams.</summary>
public sealed record NatsTransportOptions
{
    /// <summary>The server, e.g. <c>nats://localhost:4222</c>; required.</summary>
    public required string Url { get; init; }

    /// <summary>The user this system connects as (subject permissions per user decide who may publish under a prefix).</summary>
    public string? Username { get; init; }

    /// <summary>The user's password.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// A namespace in front of every subject, stream and durable consumer (e.g. <c>staging</c> → subject
    /// <c>staging.artflow.auction-settled</c>, stream <c>STAGING_ARTFLOW</c>), for environments that share a server;
    /// <see langword="null"/> (default) = none. Permissions then name the prefixed subjects.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>The streams' duplicate window (default 2 minutes, JetStream's default): a republish within it is dropped.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a stream keeps its messages (JetStream <c>MaxAge</c>): 7 days by default, the bus retention (DP-5, step 16C).
    /// Before step 16 streams kept them for ever. <see cref="TimeSpan.Zero"/> = for ever (a contract must then carry no
    /// personal data).
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long a publish waits for the stream's acknowledgement (default 5 s). A publish to a subject the user may not
    /// publish on is dropped by the server and ends in this timeout (docs/migration/messaging.md §5 item 7).</summary>
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The connection's name shown by the server (default <c>tarol</c>).</summary>
    public string ClientName { get; init; } = "tarol";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Url);
        if (Prefix is { } prefix && (prefix.Length == 0 || prefix.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            throw new ArgumentException($"The prefix '{prefix}' is letters, digits, '-' and '_'.", nameof(Prefix));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DuplicateWindow, TimeSpan.Zero, nameof(DuplicateWindow));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAge, TimeSpan.Zero, nameof(MaxAge));
        if (MaxAge != TimeSpan.Zero && MaxAge < DuplicateWindow)
            throw new ArgumentOutOfRangeException(nameof(MaxAge), "MaxAge must be at least the DuplicateWindow (or zero for no limit).");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PublishTimeout, TimeSpan.Zero, nameof(PublishTimeout));
    }
}

/// <summary>
/// The transport port over NATS JetStream (docs/architecture/messaging.md §6, T-2): one stream per source system (subjects
/// <c>&lt;source&gt;.&gt;</c>, file storage, a duplicate window); a publish waits for the stream's acknowledgement and carries
/// <c>Nats-Msg-Id</c> = <see cref="Message.DedupId"/>; one durable pull consumer per subscription, explicit acknowledgement,
/// <c>AckWait</c> = the lease, <c>MaxAckPending</c> = the concurrency, a retry is <c>Nak(delay)</c>.
/// </summary>
/// <remarks>
/// NATS stamps no publisher identity (<see cref="Incoming.AuthenticatedAs"/> is <see langword="null"/>): subject permissions per
/// user decide who may publish under a prefix. A duplicate publish is a success (the acknowledgement's <c>Duplicate</c>);
/// <c>EnsureSuccess()</c> is never used, it throws for one. A publish to a forbidden subject is a timeout: the server's
/// <c>-ERR</c> on the connection is logged as a permission error.
/// </remarks>
public sealed partial class NatsTransport : IMessageTransport, IAsyncDisposable
{
    private readonly NatsConnection _connection;
    private readonly NatsJSContext _js;
    private readonly NatsTransportOptions _options;
    private readonly ILogger _log;
    private readonly string _prefix;
    private long _duplicates;

    /// <summary>Creates the transport; it connects on first use.</summary>
    /// <param name="options">Server, user, prefix.</param>
    /// <param name="logger">Logs permission errors, duplicates and disconnects; <see langword="null"/> = none.</param>
    public NatsTransport(NatsTransportOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _log = logger ?? NullLogger.Instance;
        _prefix = options.Prefix is { } p ? p + "." : "";
        _connection = new NatsConnection(NatsOpts.Default with
        {
            Url = options.Url,
            Name = options.ClientName,
            RequestTimeout = options.PublishTimeout,
            AuthOpts = options.Username is null ? NatsAuthOpts.Default : NatsAuthOpts.Default with { Username = options.Username, Password = options.Password },
        });
        _connection.ServerError += OnServerError;
        _js = new NatsJSContext(_connection);
    }

    /// <summary>How many publishes the streams reported as duplicates (dropped, counted as success).</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>The stream of a contract's source: <c>[PREFIX_]SOURCE</c>, uppercase.</summary>
    /// <param name="contract">A contract such as <c>artflow.auction-settled</c>.</param>
    /// <returns>The stream name.</returns>
    public string StreamOf(string contract)
    {
        var source = Message.SystemOf(contract);
        if (source.Length == 0)
            throw new ArgumentException($"The contract '{contract}' has no system prefix.", nameof(contract));
        return ((_options.Prefix is { } p ? p + "_" : "") + source).ToUpperInvariant().Replace('-', '_');
    }

    /// <inheritdoc />
    /// <remarks>Creates or updates the stream of every source involved and one durable consumer per subscription.</remarks>
    /// <exception cref="ArgumentException">A subscription receives contracts of two sources (one stream per consumer).</exception>
    public async Task Prepare(IReadOnlyList<string> topics, IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(subscriptions);
        foreach (var source in topics.Concat(subscriptions.SelectMany(static s => s.Topics)).Select(Message.SystemOf).Distinct(StringComparer.Ordinal))
            await _js.CreateOrUpdateStreamAsync(new StreamConfig(StreamOf(source + ".x"), [_prefix + source + ".>"])
            {
                Storage = StreamConfigStorage.File,
                DuplicateWindow = _options.DuplicateWindow,
                MaxAge = _options.MaxAge,
            }, cancellationToken).ConfigureAwait(false);
        foreach (var subscription in subscriptions)
        {
            var stream = StreamOfSubscription(subscription);
            await _js.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig(Durable(subscription))
            {
                FilterSubjects = [.. subscription.Topics.Select(t => _prefix + t)],
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = subscription.Lease,
                MaxAckPending = subscription.Concurrency,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="NatsJSApiException">The stream refused the message (not a duplicate).</exception>
    /// <exception cref="TimeoutException">No acknowledgement within <see cref="NatsTransportOptions.PublishTimeout"/>: no stream for the subject, or the user may not publish on it.</exception>
    public async Task Publish(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var headers = new NatsHeaders();
        foreach (var (name, value) in message.Headers())
            headers[name] = value;
        PubAckResponse ack;
        try
        {
            ack = await _js.PublishAsync(_prefix + message.Type, message.Data.ToArray(), headers: headers, opts: new NatsJSPubOpts { MsgId = message.DedupId },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NatsNoRespondersException or NatsJSPublishNoResponseException or TimeoutException or NatsTimeoutException)
        {
            throw new TimeoutException($"No acknowledgement for {message.Type} within {_options.PublishTimeout}: is there a stream for '{_prefix}{Message.SystemOf(message.Type)}.>', and may this user publish on it?", e);
        }
        if (ack.Error is { } error)                                                          // never EnsureSuccess(): it throws for a duplicate
            throw new NatsJSApiException(error);
        if (ack.Duplicate)
        {
            Interlocked.Increment(ref _duplicates);
            Duplicate(_log, message.Type, message.DedupId);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Pulls with the durable consumer and runs up to <see cref="Subscription.Concurrency"/> handlers at once; the attempt is
    /// the server's delivery count. A delivery that outlives <c>AckWait</c> is delivered again.
    /// </remarks>
    public async Task Consume(Subscription subscription, MessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(handler);
        var consumer = await _js.GetConsumerAsync(StreamOfSubscription(subscription), Durable(subscription), cancellationToken).ConfigureAwait(false);
        using var slots = new SemaphoreSlim(subscription.Concurrency);
        var running = new List<Task>();
        try
        {
            await foreach (var msg in consumer.ConsumeAsync<byte[]>(opts: new NatsJSConsumeOpts { MaxMsgs = Math.Max(16, subscription.Concurrency * 2) },
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                running.RemoveAll(static t => t.IsCompleted);
                running.Add(Handle(msg, handler, slots, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _connection.ServerError -= OnServerError;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task Handle(INatsJSMsg<byte[]> msg, MessageHandler handler, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var data = msg.Data ?? [];
            var headers = msg.Headers;
            var message = Message.From(h => headers is not null && headers.TryGetValue(h, out var v) ? v.ToString() : null, data);
            var attempt = (int)Math.Min(int.MaxValue, msg.Metadata?.NumDelivered ?? 1);
            var topic = _prefix.Length > 0 && msg.Subject.StartsWith(_prefix, StringComparison.Ordinal) ? msg.Subject[_prefix.Length..] : msg.Subject;
            var handled = await handler(new Incoming(message, attempt, null, data, topic), cancellationToken).ConfigureAwait(false);
            // the handler has run: record its answer even while stopping
            if (handled.Ack == Ack.Ack)
                await msg.AckAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            else
                await msg.NakAsync(delay: handled.RetryAfter, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // AckWait brings the message back
        }
        catch (Exception e) when (e is NatsException or ObjectDisposedException)
        {
            AckFailed(_log, msg.Subject, e);                                              // AckWait brings it back
        }
        finally
        {
            slots.Release();
        }
    }

    private string StreamOfSubscription(Subscription subscription)
    {
        var streams = subscription.Topics.Select(StreamOf).Distinct(StringComparer.Ordinal).ToArray();
        return streams.Length == 1
            ? streams[0]
            : throw new ArgumentException($"The subscription '{subscription.Name}' receives contracts of {streams.Length} sources; on NATS a consumer reads one stream.", nameof(subscription));
    }

    private string Durable(Subscription subscription) => (_options.Prefix is { } p ? p + "_" : "") + subscription.Name.Replace('.', '_');

    private ValueTask OnServerError(object? sender, NatsServerErrorEventArgs args)
    {
        if (args.Error.Contains("Permissions Violation", StringComparison.OrdinalIgnoreCase))
            PermissionError(_log, _options.Username ?? "(anonymous)", args.Error);
        else
            ServerFailed(_log, args.Error);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 11201, Level = LogLevel.Error, Message = "NATS refused user {User}: {Error} (a publish on that subject times out; check the user's subject permissions)")]
    private static partial void PermissionError(ILogger logger, string user, string error);

    [LoggerMessage(EventId = 11202, Level = LogLevel.Warning, Message = "NATS server error: {Error}")]
    private static partial void ServerFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 11203, Level = LogLevel.Debug, Message = "{Contract} {DedupId} was a duplicate within the stream's window (dropped, counted as published)")]
    private static partial void Duplicate(ILogger logger, string contract, string dedupId);

    [LoggerMessage(EventId = 11204, Level = LogLevel.Warning, Message = "Acknowledging a delivery of {Subject} failed; it is delivered again after AckWait")]
    private static partial void AckFailed(ILogger logger, string subject, Exception exception);
}
