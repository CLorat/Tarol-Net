using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.SignalR;

/// <summary>
/// The UI push's hub (docs/architecture/messaging.md §10): a plain <see cref="Hub"/> (a typed hub needs Reflection.Emit under
/// Native AOT) that only authenticated callers reach (<c>[Authorize]</c>, F3). Every subscribe and publish is asked of the
/// <see cref="ITopicAuthorizer"/>; the group is <c>&lt;tenant&gt;/&lt;topic&gt;</c> with the tenant from the caller's principal, so a
/// client never reaches another tenant; a client's publish is stamped with the caller's identity by the server.
/// </summary>
/// <remarks>Clients receive <see cref="PushedMessage"/> on <see cref="MessageMethod"/>; <see cref="MessagePushClient"/> is the .NET client.</remarks>
[Authorize]
public sealed partial class MessageHub : Hub
{
    /// <summary>The client method messages arrive on.</summary>
    public const string MessageMethod = "message";

    private readonly ITopicAuthorizer _authorizer;
    private readonly HubConnections _connections;
    private readonly UiPushOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>Creates the hub (per call, by SignalR).</summary>
    /// <param name="authorizer">Decides every subscribe and publish.</param>
    /// <param name="connections">The open connections, aborted when the application stops.</param>
    /// <param name="options">The tenant and subject claims.</param>
    /// <param name="time">The clock of a client publish.</param>
    /// <param name="logger">Logs refusals.</param>
    public MessageHub(ITopicAuthorizer authorizer, HubConnections connections, UiPushOptions options, TimeProvider time, ILogger<MessageHub> logger)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _authorizer = authorizer;
        _connections = connections;
        _options = options;
        _time = time;
        _log = logger;
    }

    /// <summary>The SignalR group of a topic in a tenant: <c>&lt;tenant&gt;/&lt;topic&gt;</c>, <c>-/&lt;topic&gt;</c> without a tenant.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="topic">The topic.</param>
    /// <returns>The group name.</returns>
    public static string Group(Option<Id<ITenant>> tenant, string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return (tenant.TryGetValue(out Id<ITenant> id) ? id.Value.ToString("D", CultureInfo.InvariantCulture) : "-") + "/" + topic;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        _connections.Add(Context);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Remove(Context);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Receives <paramref name="topic"/>'s messages of the caller's tenant.</summary>
    /// <param name="topic">The topic.</param>
    /// <returns>A task.</returns>
    /// <exception cref="HubException">The topic is not valid, the tenant claim does not parse, or the authorizer refused.</exception>
    public async Task Subscribe(string topic)
    {
        var group = await Authorized(TopicOperation.Subscribe, topic).ConfigureAwait(false);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <summary>Stops receiving <paramref name="topic"/> (no authorization: it only narrows).</summary>
    /// <param name="topic">The topic.</param>
    /// <returns>A task.</returns>
    /// <exception cref="HubException">The topic is not valid, or the tenant claim does not parse.</exception>
    public Task Unsubscribe(string topic)
    {
        Valid(topic);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(Tenant(), topic), Context.ConnectionAborted);
    }

    /// <summary>Sends <paramref name="data"/> to the subscribers of <paramref name="topic"/> in the caller's tenant, the caller included.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="data">The payload.</param>
    /// <returns>A task.</returns>
    /// <exception cref="HubException">The topic is not valid, the tenant claim does not parse, or the authorizer refused.</exception>
    public async Task Publish(string topic, JsonElement data)
    {
        var group = await Authorized(TopicOperation.Publish, topic).ConfigureAwait(false);
        var sender = Context.User?.FindFirst(_options.SubjectClaim)?.Value ?? Context.UserIdentifier ?? "anonymous";   // never the client's word
        var message = new PushedMessage(topic, Guid.CreateVersion7(_time.GetUtcNow()), 1, null, _time.GetUtcNow(), sender, data,
            Revision: null, Position: null);                                                     // no stream, no feed: a notification only
        await Clients.Group(group).SendAsync(MessageMethod, message, Context.ConnectionAborted).ConfigureAwait(false);
    }

    private async Task<string> Authorized(TopicOperation operation, string topic)
    {
        Valid(topic);
        var tenant = Tenant();
        var user = Context.User ?? new ClaimsPrincipal();
        if (!await _authorizer.Authorize(user, operation, topic, Context.ConnectionAborted).ConfigureAwait(false))
        {
            Refused(_log, user.FindFirst(_options.SubjectClaim)?.Value ?? "anonymous", operation, topic);
            throw HubErrors.Exception(HubErrors.Forbidden, $"{operation} {topic}");
        }
        return Group(tenant, topic);
    }

    private Option<Id<ITenant>> Tenant()
    {
        if (Context.User?.FindFirst(_options.TenantClaim)?.Value is not { Length: > 0 } claimed)
            return Option.None;
        if (_options.Tenant is { } map)
            return map(claimed).TryGetValue(out Id<ITenant> mapped) ? mapped : throw HubErrors.Exception(HubErrors.Tenant, "the tenant claim is not known");
        return Guid.TryParse(claimed, out var id) ? (Id<ITenant>)id : throw HubErrors.Exception(HubErrors.Tenant, "the tenant claim is not a tenant id");
    }

    private static void Valid(string topic)
    {
        if (topic is not { Length: > 0 and <= 200 } || Message.SystemOf(topic).Length == 0 || topic.Any(static c => char.IsWhiteSpace(c) || c == '/'))
            throw HubErrors.Exception(HubErrors.Topic, "a topic is <system>.<name>, at most 200 characters");
    }

    [LoggerMessage(EventId = 11401, Level = LogLevel.Warning, Message = "The message hub refused {Subject}: {Operation} {Topic}")]
    private static partial void Refused(ILogger logger, string subject, TopicOperation operation, string topic);
}

/// <summary>
/// The hub's open connections. When the application stops they are aborted at once, so a graceful shutdown does not wait
/// its whole timeout for long-lived UI connections; a client that reconnects while the server still stops is aborted again.
/// The clients reconnect (to another node, or to this one when it is back) and replay their subscriptions.
/// </summary>
public sealed class HubConnections : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HubCallerContext> _open = new(StringComparer.Ordinal);
    private readonly CancellationTokenRegistration _stopping;
    private bool _stopped;

    /// <summary>Creates the registry.</summary>
    /// <param name="lifetime">Aborts the connections when the application stops.</param>
    public HubConnections(Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _stopping = lifetime.ApplicationStopping.Register(AbortAll);
    }

    /// <summary>How many connections are open.</summary>
    public int Count => _open.Count;

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    internal void Add(HubCallerContext context)
    {
        if (Volatile.Read(ref _stopped))
        {
            context.Abort();                                                // a client reconnecting into the shutdown: not kept open
            return;
        }
        _open[context.ConnectionId] = context;
    }

    internal void Remove(HubCallerContext context) => _open.TryRemove(context.ConnectionId, out _);

    private void AbortAll()
    {
        Volatile.Write(ref _stopped, true);
        foreach (var context in _open.Values)
            context.Abort();
    }
}


/// <summary>
/// The message hub's refusals (BC-12): every <see cref="HubException"/> message is <c>&lt;code&gt;: &lt;detail&gt;</c>, the code
/// one of these constants, the same contract as an HTTP problem's <c>code</c>. A client reads the code with <see cref="CodeOf"/>
/// (or the text before the first <c>": "</c>).
/// </summary>
public static class HubErrors
{
    /// <summary>The topic authorizer refused the subscribe or publish.</summary>
    public const string Forbidden = "Hub.Forbidden";

    /// <summary>The topic is not <c>&lt;system&gt;.&lt;name&gt;</c> of at most 200 characters.</summary>
    public const string Topic = "Hub.Topic";

    /// <summary>The caller's tenant claim is not a tenant (as the HTTP entries' <c>Auth.Tenant</c>).</summary>
    public const string Tenant = "Auth.Tenant";

    /// <summary>The exception with the message <c>&lt;code&gt;: &lt;detail&gt;</c>.</summary>
    /// <param name="code">One of the constants.</param>
    /// <param name="detail">What was refused.</param>
    /// <returns>The exception to throw.</returns>
    public static HubException Exception(string code, string detail) => new($"{code}: {detail}");

    /// <summary>The code of a hub error's message (SignalR clients may prefix it, e.g. "An unexpected error occurred invoking
    /// 'Subscribe' on the server. HubException: "), or <see langword="null"/> when it carries none.</summary>
    /// <param name="message">The error message as the client received it.</param>
    /// <returns>The code.</returns>
    public static string? CodeOf(string? message)
    {
        if (message is null)
            return null;
        foreach (var code in (ReadOnlySpan<string>)[Forbidden, Topic, Tenant])
            if (message.Contains(code + ": ", StringComparison.Ordinal))
                return code;
        return null;
    }
}
