using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.SignalR;

/// <summary>What the UI push forwards and how the hub reads the caller.</summary>
public sealed class UiPushOptions
{
    private readonly List<string> _topics = [];

    /// <summary>The contracts forwarded to UI clients (from this or a trusted system).</summary>
    public IReadOnlyList<string> Topics => _topics;

    /// <summary>The name of the forwarding consumer: its subscription is <c>&lt;application&gt;.&lt;name&gt;</c> (default <c>UiPush</c>).
    /// Every node of a scaled-out web tier needs every message: give each node its own name (e.g. with the machine name).</summary>
    public string Name { get; set; } = "UiPush";

    /// <summary>The hub's path (default <c>/tarol/messages</c>).</summary>
    public string HubPath { get; set; } = "/tarol/messages";

    /// <summary>
    /// Closes a hub connection when its authentication expires (default <see langword="true"/>; BC-15): a client whose token
    /// ran out, or a user who was revoked, stops receiving pushes at the token's expiry instead of at its next reconnect. The
    /// client reconnects with a fresh token. Applies to bearer schemes, whose ticket carries the token's expiry.
    /// </summary>
    public bool CloseOnAuthenticationExpiration { get; set; } = true;

    /// <summary>The claim of the caller's tenant (default <c>tenant</c>, as the HTTP entries).</summary>
    public string TenantClaim { get; set; } = "tenant";

    /// <summary>Maps the tenant claim's value to the tenant (e.g. an identity provider's organisation id); absent: it must parse as an <c>Id&lt;ITenant&gt;</c>.</summary>
    public Func<string, Option<Id<ITenant>>>? Tenant { get; set; }

    /// <summary>The claim stamped as the sender of a client's publish (default <c>sub</c>).</summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>Who may subscribe and publish (default: nobody; <see cref="Rules"/> or your own <see cref="ITopicAuthorizer"/> registered in DI).</summary>
    public TopicRules Rules { get; } = new();

    /// <summary>Forwards <paramref name="topics"/> to UI clients.</summary>
    /// <param name="topics">Contracts, e.g. <c>artflow.auction-settled</c>.</param>
    /// <returns>This.</returns>
    public UiPushOptions Forward(params string[] topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        foreach (var topic in topics)
        {
            if (Message.SystemOf(topic).Length == 0)
                throw new ArgumentException($"The topic '{topic}' is not <system>.<name>.", nameof(topics));
            if (!_topics.Contains(topic, StringComparer.Ordinal))
                _topics.Add(topic);
        }
        return this;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException($"UiPushOptions.Name '{Name}' is letters, digits, '-' and '_'.");
        if (!HubPath.StartsWith('/'))
            throw new InvalidOperationException("UiPushOptions.HubPath starts with '/'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantClaim);
        ArgumentException.ThrowIfNullOrWhiteSpace(SubjectClaim);
    }
}

/// <summary>
/// The consumer subscription of the UI push: every message of <see cref="UiPushOptions.Topics"/> goes to the SignalR group
/// <c>&lt;tenant&gt;/&lt;topic&gt;</c> of its envelope's tenant. Only messages of this application or of a trusted source, for a
/// tenant that source may name (M-13), are forwarded; the rest is dropped and logged. A UI client that is not connected
/// misses the message: the push is a notification, the store is the truth.
/// </summary>
public sealed partial class UiPushForwarder : BackgroundService
{
    private readonly IMessageTransport _transport;
    private readonly IHubContext<MessageHub> _hub;
    private readonly MessagingOptions _messaging;
    private readonly UiPushOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>Creates the forwarder.</summary>
    /// <param name="transport">The transport.</param>
    /// <param name="hub">The hub's context.</param>
    /// <param name="messaging">The application name and the trusted sources.</param>
    /// <param name="options">The forwarded topics.</param>
    /// <param name="time">The clock of the restart delay.</param>
    /// <param name="logger">Logs dropped messages and failures; <see langword="null"/> = none.</param>
    public UiPushForwarder(IMessageTransport transport, IHubContext<MessageHub> hub, MessagingOptions messaging, UiPushOptions options, TimeProvider time,
        ILogger<UiPushForwarder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _transport = transport;
        _hub = hub;
        _messaging = messaging;
        _options = options;
        _time = time;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        Subscription = new Subscription(messaging.Application + "." + options.Name, options.Topics) { Concurrency = 4, Lease = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The forwarder's subscription.</summary>
    public Subscription Subscription { get; }

    /// <summary>Forwards one delivery; never throws but for cancellation (a failed send is retried twice, then dropped).</summary>
    /// <param name="incoming">The delivery.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>Ack, or a retry after a failed send.</returns>
    public async ValueTask<Handled> Forward(Incoming incoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.Message is not { } message)
            return Drop(incoming.Topic, null, "no CloudEvents headers");
        var source = Message.SystemOf(message.Type);
        if (source != message.Source || (incoming.AuthenticatedAs is { } who && who != source))
            return Drop(message.Type, message.Id, "the source does not match the contract's prefix");
        if (source != _messaging.Application && !(_messaging.Sources.TryGetValue(source, out var trust) && trust.Allows(message.Tenant)))
            return Drop(message.Type, message.Id, $"source {source} is not trusted for its tenant");
        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(message.Data);
            data = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Drop(message.Type, message.Id, "the data is not JSON");
        }
        try
        {
            await _hub.Clients.Group(MessageHub.Group(message.Tenant, message.Type))
                .SendAsync(MessageHub.MessageMethod, new PushedMessage(message.Type, message.Id, message.Version, message.Subject, message.Time, source, data,
                    message.StreamVersion, message.Position), cancellationToken)
                .ConfigureAwait(false);
            return new Handled(Ack.Ack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (incoming.Attempt >= 3)
                return Drop(message.Type, message.Id, "sending failed: " + e.Message);
            SendFailed(_log, message.Type, message.Id, e);
            return new Handled(Ack.Retry, TimeSpan.FromSeconds(incoming.Attempt));
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _transport.Prepare([], [Subscription], stoppingToken).ConfigureAwait(false);
                await _transport.Consume(Subscription, Forward, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                TransportFailed(_log, e);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Handled Drop(string topic, Guid? id, string reason)
    {
        Dropped(_log, topic, id, reason);
        return new Handled(Ack.Ack);
    }

    [LoggerMessage(EventId = 11402, Level = LogLevel.Warning, Message = "The UI push dropped {Topic} {MessageId}: {Reason}")]
    private static partial void Dropped(ILogger logger, string topic, Guid? messageId, string reason);

    [LoggerMessage(EventId = 11403, Level = LogLevel.Warning, Message = "The UI push could not send {Topic} {MessageId}; it is tried again")]
    private static partial void SendFailed(ILogger logger, string topic, Guid messageId, Exception exception);

    [LoggerMessage(EventId = 11404, Level = LogLevel.Error, Message = "The UI push's transport failed; it consumes again after a delay")]
    private static partial void TransportFailed(ILogger logger, Exception exception);
}

/// <summary>The UI push on the <see cref="MessagingBuilder"/> and the hub on the endpoints.</summary>
public static class UiPushExtensions
{
    /// <summary>
    /// Pushes messages to UI clients over SignalR: SignalR with the source-generated hub JSON (<see cref="SignalRJson"/>), the
    /// <see cref="UiPushForwarder"/> for <see cref="UiPushOptions.Topics"/>, and the <see cref="ITopicAuthorizer"/> of
    /// <see cref="UiPushOptions.Rules"/> unless one is registered, and <see cref="UiPushTopics"/> (the topics for the OpenAPI
    /// document's <c>x-tarol-topics</c>, BC-25). Map the hub with <see cref="MapMessageHub"/>.
    /// </summary>
    /// <param name="messaging">The messaging builder (its transport and trust are used).</param>
    /// <param name="configure">Topics, rules, claims, the hub's path.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder PushToUi(this MessagingBuilder messaging, Action<UiPushOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new UiPushOptions();
        configure(options);
        options.Validate();
        var services = messaging.Tarol.Services;
        services.AddSingleton(options);
        services.TryAddSingleton<ITopicAuthorizer>(options.Rules);
        var tarol = messaging.Tarol;                       // BC-25: the publications are read when resolved (WithOutboxRows may follow)
        services.TryAddSingleton(provider => new UiPushTopics(provider.GetRequiredService<UiPushOptions>(),
            tarol.OutboxRows.OfType<PublicationSet>().SelectMany(static set => set.Publications),
            ReferenceEquals(provider.GetService<ITopicAuthorizer>(), options.Rules)));
        services.TryAddSingleton<HubConnections>();
        services.AddSignalR().AddJsonProtocol(static o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, SignalRJson.Default));
        if (options.Topics.Count > 0)
            services.AddHostedService(static provider => new UiPushForwarder(provider.GetRequiredService<IMessageTransport>(), provider.GetRequiredService<IHubContext<MessageHub>>(),
                provider.GetRequiredService<MessagingOptions>(), provider.GetRequiredService<UiPushOptions>(), provider.GetRequiredService<TimeProvider>(),
                provider.GetService<ILogger<UiPushForwarder>>()));
        return messaging;
    }

    /// <summary>Maps the <see cref="MessageHub"/> at <see cref="UiPushOptions.HubPath"/> (after <see cref="PushToUi"/>).</summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <returns>The hub's endpoint, for more conventions.</returns>
    /// <exception cref="InvalidOperationException"><see cref="PushToUi"/> was not called.</exception>
    public static HubEndpointConventionBuilder MapMessageHub(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetService<UiPushOptions>()
            ?? throw new InvalidOperationException("Call AddMessaging(…, m => m.PushToUi(…)) before MapMessageHub().");
        return endpoints.MapHub<MessageHub>(options.HubPath, dispatcher => dispatcher.CloseOnAuthenticationExpiration = options.CloseOnAuthenticationExpiration);
    }
}
