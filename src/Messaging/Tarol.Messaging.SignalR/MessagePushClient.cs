using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tarol.Messaging.SignalR;

/// <summary>
/// The .NET client of the <see cref="MessageHub"/> (a desktop app, a test, a service that shows a UI). It keeps an
/// immutable map topic → handlers, replays every subscription after <see cref="Start"/> and on every reconnect (F7, F8),
/// starts again by itself when the connection closed for good without <see cref="Stop"/>,
/// removes a handler whose server subscription failed (F9), disposes one handler without touching the others (F11), runs
/// every handler isolated from the others (F18), and owns the <c>On</c> registration it makes (F17).
/// </summary>
/// <example>
/// <code>
/// await using var push = MessagePushClient.Connect(new Uri("https://artflow.example/tarol/messages"), () => GetTokenAsync());
/// await using var settled = await push.Subscribe("artflow.auction-settled", (m, ct) => { Show(m.Data); return ValueTask.CompletedTask; });
/// await push.Start();
/// </code>
/// </example>
public sealed partial class MessagePushClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly bool _ownsConnection;
    private readonly ILogger _log;
    private readonly IDisposable _on;
    private readonly CancellationTokenSource _stopped = new();
    private int _restarting;
    private ImmutableDictionary<string, ImmutableArray<Handler>> _topics = ImmutableDictionary.Create<string, ImmutableArray<Handler>>(StringComparer.Ordinal);

    /// <summary>Wraps a connection (built with the <see cref="SignalRJson"/> protocol and, for replays, <c>WithAutomaticReconnect</c>).</summary>
    /// <param name="connection">The connection; not started yet, or started (then call <see cref="Start"/> anyway to replay).</param>
    /// <param name="logger">Logs failing handlers and refused replays; <see langword="null"/> = none.</param>
    /// <param name="ownsConnection">Dispose the connection with this client.</param>
    public MessagePushClient(HubConnection connection, ILogger? logger = null, bool ownsConnection = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
        _log = logger ?? NullLogger.Instance;
        _on = connection.On<PushedMessage>(MessageHub.MessageMethod, Dispatch);
        connection.Reconnected += OnReconnected;
        connection.Closed += OnClosed;
    }

    /// <summary>The connection's state.</summary>
    public HubConnectionState State => _connection.State;

    /// <summary>The topics with at least one handler.</summary>
    public IReadOnlyCollection<string> Topics => [.. Volatile.Read(ref _topics).Keys];

    /// <summary>
    /// A client of its own connection to <paramref name="hub"/>: the source-generated hub JSON, automatic reconnect that never
    /// gives up (0, 1, 2, 5 s, then every 10 s with jitter), the access token from <paramref name="accessToken"/> on every
    /// (re)connect. The client owns the connection.
    /// </summary>
    /// <param name="hub">The hub's URL, e.g. <c>https://host/tarol/messages</c>.</param>
    /// <param name="accessToken">Supplies the bearer token; <see langword="null"/> = none (cookies, or a test handler).</param>
    /// <param name="configure">More connection options (headers, transports, a test handler).</param>
    /// <param name="logger">Logs failing handlers and refused replays.</param>
    /// <returns>The client (not started).</returns>
    public static MessagePushClient Connect(Uri hub, Func<Task<string?>>? accessToken = null, Action<HttpConnectionOptions>? configure = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var connection = new HubConnectionBuilder()
            .WithUrl(hub, options =>
            {
                if (accessToken is not null)
                    options.AccessTokenProvider = accessToken;
                configure?.Invoke(options);
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .AddJsonProtocol(static o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, SignalRJson.Default))
            .Build();
        return new MessagePushClient(connection, logger, ownsConnection: true);
    }

    /// <summary>Starts the connection (unless it is started) and subscribes every topic that has handlers.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task.</returns>
    public async Task Start(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        Volatile.Write(ref _userStopped, false);
        if (_connection.State == HubConnectionState.Disconnected)
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        await Replay(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="handler"/> for every message of <paramref name="topic"/>. The first handler of a topic subscribes on
    /// the server when connected (else at <see cref="Start"/> or the next reconnect); dispose the result to remove this
    /// handler only.
    /// </summary>
    /// <param name="topic">The topic.</param>
    /// <param name="handler">The handler; an exception is logged and does not reach the others.</param>
    /// <param name="cancellationToken">Cancels the server call.</param>
    /// <returns>The handle that removes the handler.</returns>
    /// <exception cref="HubException">The server refused (e.g. <c>Forbidden</c>); the handler is not kept.</exception>
    public async Task<IAsyncDisposable> Subscribe(string topic, Func<PushedMessage, CancellationToken, ValueTask> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);
        var entry = new Handler(handler);
        var first = Update(topic, entry, add: true);
        if (first && _connection.State == HubConnectionState.Connected)
        {
            try
            {
                await _connection.InvokeAsync(nameof(MessageHub.Subscribe), topic, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _ = Update(topic, entry, add: false);                                  // F9: no handler without a server subscription
                throw;
            }
        }
        return new Handle(this, topic, entry);
    }

    /// <summary>Sends <paramref name="data"/> to the subscribers of <paramref name="topic"/> in this caller's tenant (the server stamps the sender).</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="data">The payload.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="HubException">The server refused (e.g. <c>Forbidden</c>).</exception>
    public Task Publish(string topic, JsonElement data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return _connection.InvokeAsync(nameof(MessageHub.Publish), topic, data, cancellationToken);
    }

    /// <summary>Stops the connection (subscriptions stay in the map and are replayed by the next <see cref="Start"/>).</summary>
    /// <param name="cancellationToken">Cancels the stop.</param>
    /// <returns>A task.</returns>
    public Task Stop(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _userStopped, true);
        return _connection.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, true);
        Volatile.Write(ref _userStopped, true);
        await _stopped.CancelAsync().ConfigureAwait(false);
        _connection.Closed -= OnClosed;
        _connection.Reconnected -= OnReconnected;
        _on.Dispose();
        if (_ownsConnection)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnReconnected(string? connectionId) => Replay(CancellationToken.None);

    /// <summary>
    /// The connection closed for good without <see cref="Stop"/> (the reconnect policy gave up, or the server closed it and
    /// disallowed a reconnect): start again with a backoff until it works or the client is stopped.
    /// </summary>
    private Task OnClosed(Exception? error)
    {
        if (Volatile.Read(ref _userStopped) || Interlocked.Exchange(ref _restarting, 1) == 1)
            return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; !Volatile.Read(ref _userStopped); attempt++)
                {
                    await Task.Delay(ForeverRetry.Delay(attempt), _stopped.Token).ConfigureAwait(false);
                    try
                    {
                        if (_connection.State == HubConnectionState.Disconnected)
                            await _connection.StartAsync(_stopped.Token).ConfigureAwait(false);
                        await Replay(_stopped.Token).ConfigureAwait(false);
                        // the server may close the new connection right away (a server shutting down): its Closed came while this
                        // loop ran and was not acted on, so look again instead of reporting a restart that did not hold
                        if (_connection.State == HubConnectionState.Disconnected)
                            continue;
                        Restarted(_log, attempt + 1);
                        return;
                    }
                    // Only the client's own stop ends the loop. An HTTP timeout is a TaskCanceledException too, and ending the
                    // loop on it left the client disconnected for good (docs/migration/messaging.md §20).
                    catch (Exception e) when (e is not OutOfMemoryException && !_stopped.IsCancellationRequested)
                    {
                        RestartFailed(_log, attempt + 1, e);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Volatile.Write(ref _restarting, 0);
            }

            // a Closed between the last look and the flag's reset was dropped by the check above: act on it now
            if (!Volatile.Read(ref _userStopped) && !_stopped.IsCancellationRequested && _connection.State == HubConnectionState.Disconnected)
                _ = OnClosed(null);
        });
        return Task.CompletedTask;
    }

    private async Task Replay(CancellationToken cancellationToken)
    {
        foreach (var topic in Volatile.Read(ref _topics).Keys)
        {
            try
            {
                await _connection.InvokeAsync(nameof(MessageHub.Subscribe), topic, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ReplayFailed(_log, topic, e);                                             // the next reconnect tries again
            }
        }
    }

    private async Task Dispatch(PushedMessage message)
    {
        if (!Volatile.Read(ref _topics).TryGetValue(message.Topic, out var handlers))
            return;
        foreach (var handler in handlers)
        {
            try
            {
                await handler.Run(message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                HandlerFailed(_log, message.Topic, message.Id, e);                         // F18: the other handlers still run
            }
        }
    }

    /// <summary>Adds or removes one handler atomically; returns whether the topic got its first handler or lost its last.</summary>
    private bool Update(string topic, Handler handler, bool add)
    {
        while (true)
        {
            var current = Volatile.Read(ref _topics);
            var handlers = current.TryGetValue(topic, out var found) ? found : [];
            ImmutableDictionary<string, ImmutableArray<Handler>> next;
            bool edge;
            if (add)
            {
                next = current.SetItem(topic, handlers.Add(handler));
                edge = handlers.IsEmpty;
            }
            else
            {
                if (!handlers.Contains(handler))
                    return false;
                var rest = handlers.Remove(handler);
                next = rest.IsEmpty ? current.Remove(topic) : current.SetItem(topic, rest);
                edge = rest.IsEmpty;
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _topics, next, current), current))
                return edge;
        }
    }

    private async ValueTask Remove(string topic, Handler handler)
    {
        if (!Update(topic, handler, add: false) || _connection.State != HubConnectionState.Connected)
            return;
        try
        {
            await _connection.InvokeAsync(nameof(MessageHub.Unsubscribe), topic).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            UnsubscribeFailed(_log, topic, e);                                                // best effort: a dead connection forgets it anyway
        }
    }

    private bool _userStopped;
    private bool _disposed;

    /// <summary>Reconnects forever: 0, 1, 2, 5 s, then every 10 s with ±20 % jitter.</summary>
    private sealed class ForeverRetry : IRetryPolicy
    {
        public static TimeSpan Delay(long attempt)
        {
            var seconds = attempt switch { 0 => 0, 1 => 1, 2 => 2, 3 => 5, _ => 10 };
            return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * Random.Shared.NextDouble()));
        }

        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);
    }

    private sealed class Handler(Func<PushedMessage, CancellationToken, ValueTask> run)
    {
        public Func<PushedMessage, CancellationToken, ValueTask> Run { get; } = run;
    }

    private sealed class Handle(MessagePushClient client, string topic, Handler handler) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? client.Remove(topic, handler) : ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 11411, Level = LogLevel.Error, Message = "A handler of {Topic} failed on message {MessageId}; the other handlers ran")]
    private static partial void HandlerFailed(ILogger logger, string topic, Guid messageId, Exception exception);

    [LoggerMessage(EventId = 11412, Level = LogLevel.Warning, Message = "Subscribing {Topic} again failed; the next reconnect tries again")]
    private static partial void ReplayFailed(ILogger logger, string topic, Exception exception);

    [LoggerMessage(EventId = 11414, Level = LogLevel.Information, Message = "The message push connection started again after {Attempts} attempt(s)")]
    private static partial void Restarted(ILogger logger, int attempts);

    [LoggerMessage(EventId = 11415, Level = LogLevel.Warning, Message = "Starting the message push connection again failed (attempt {Attempt}); trying again")]
    private static partial void RestartFailed(ILogger logger, int attempt, Exception exception);

    [LoggerMessage(EventId = 11413, Level = LogLevel.Warning, Message = "Unsubscribing {Topic} failed")]
    private static partial void UnsubscribeFailed(ILogger logger, string topic, Exception exception);
}
