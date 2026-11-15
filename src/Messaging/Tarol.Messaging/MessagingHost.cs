using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Primitives;

namespace Tarol.Messaging;

/// <summary>
/// The hosted service of consumers: declares the subscriptions on the transport, then consumes them until the host stops.
/// A transport that fails (unreachable broker) is retried after a delay; deliveries stay with the transport meanwhile.
/// </summary>
public sealed partial class MessagingHost : BackgroundService
{
    private readonly IMessageTransport _transport;
    private readonly MessageConsumers _consumers;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>Creates the host.</summary>
    /// <param name="transport">The transport.</param>
    /// <param name="consumers">The consumers.</param>
    /// <param name="time">The clock of the restart delay.</param>
    /// <param name="logger">Logs transport failures.</param>
    public MessagingHost(IMessageTransport transport, MessageConsumers consumers, TimeProvider time, ILogger<MessagingHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(time);
        _transport = transport;
        _consumers = consumers;
        _time = time;
        _log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>How long to wait before declaring and consuming again after the transport failed (default 5 s).</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _transport.Prepare([], _consumers.Subscriptions, stoppingToken).ConfigureAwait(false);
                await _consumers.Run(_transport, stoppingToken).ConfigureAwait(false);
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
                await Task.Delay(RestartDelay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 11004, Level = LogLevel.Error, Message = "The message transport failed; the consumers declare and consume again after a delay")]
    private static partial void TransportFailed(ILogger logger, Exception exception);
}

/// <summary>What <see cref="MessagingTarolBuilderExtensions.AddMessaging"/> configures.</summary>
public sealed class MessagingBuilder
{
    private readonly List<ConsumerSet> _consumers = [];
    private readonly List<PublicationSet> _publications = [];
    private readonly List<Action<IServiceCollection>> _effectServices = [];

    internal MessagingBuilder(TarolBuilder tarol, string application)
    {
        Tarol = tarol;
        Options = new MessagingOptions { Application = application };
    }

    /// <summary>The Tarol builder.</summary>
    public TarolBuilder Tarol { get; }

    /// <summary>Application name, trust, policy, concurrency.</summary>
    public MessagingOptions Options { get; }

    internal Func<IServiceProvider, IMessageTransport>? Transport { get; private set; }

    internal Func<IServiceProvider, IMessageDeadLetters>? DeadLetters { get; private set; }

    internal IReadOnlyList<ConsumerSet> ConsumerSets => _consumers;

    internal IReadOnlyList<PublicationSet> PublicationSets => _publications;

    internal EffectSet Effects { get; private set; } = new();

    internal IReadOnlyList<Action<IServiceCollection>> EffectServices => _effectServices;

    /// <summary>
    /// Publishes these publications (<c>GeneratedPublications.Set</c>; call once per assembly): the store writes their outbox
    /// rows in each commit and the reaction worker (<c>AddReactionWorker</c>) publishes them on the transport.
    /// </summary>
    /// <param name="publications">The publications.</param>
    /// <returns>This.</returns>
    public MessagingBuilder Publish(PublicationSet publications)
    {
        ArgumentNullException.ThrowIfNull(publications);
        _publications.Add(publications);
        return this;
    }

    /// <summary>
    /// Runs <typeparamref name="THandler"/> once per commit of <typeparamref name="TEvent"/>, at least once, from an
    /// <c>effect:&lt;name&gt;</c> outbox row (S-2). The handler is resolved from a service scope per row (registered as
    /// transient unless you registered it already).
    /// </summary>
    /// <typeparam name="TEvent">The event.</typeparam>
    /// <typeparam name="THandler">The handler.</typeparam>
    /// <param name="name">The effect's stable name (stored in the rows).</param>
    /// <returns>This.</returns>
    public MessagingBuilder Effect<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(string name)
        where TEvent : DomainEvent
        where THandler : class, IEffect<TEvent>
    {
        Effects = Effects.Add<TEvent>(name, static services => services.GetRequiredService<THandler>());
        _effectServices.Add(static services => services.TryAddTransient<THandler>());
        return this;
    }

    /// <summary>Runs an effect created by <paramref name="handler"/> once per commit of <typeparamref name="TEvent"/> (S-2).</summary>
    /// <typeparam name="TEvent">The event.</typeparam>
    /// <param name="name">The effect's stable name (stored in the rows).</param>
    /// <param name="handler">Creates the handler from the row's service scope.</param>
    /// <returns>This.</returns>
    public MessagingBuilder Effect<TEvent>(string name, Func<IServiceProvider, IEffect<TEvent>> handler)
        where TEvent : DomainEvent
    {
        Effects = Effects.Add(name, handler);
        return this;
    }

    /// <summary>Consumes with these consumers (<c>GeneratedConsumers.Set</c>; call once per assembly).</summary>
    /// <param name="consumers">The consumers.</param>
    /// <returns>This.</returns>
    public MessagingBuilder Consume(ConsumerSet consumers)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        _consumers.Add(consumers);
        return this;
    }

    /// <summary>Accepts messages of <paramref name="source"/>'s contracts (M-13).</summary>
    /// <param name="source">The source system, e.g. <c>billing</c>.</param>
    /// <param name="configure">Which tenants it may name (default: none).</param>
    /// <returns>This.</returns>
    public MessagingBuilder Trust(string source, Action<SourceTrust>? configure = null)
    {
        Options.Trust(source, configure);
        return this;
    }

    /// <summary>Uses a transport (the broker packages add <c>UseNats</c>, <c>UseRabbitMq</c>, <c>UseNpgsqlBus</c>).</summary>
    /// <param name="transport">Creates the transport once.</param>
    /// <returns>This.</returns>
    /// <exception cref="InvalidOperationException">A transport is set already.</exception>
    public MessagingBuilder UseTransport(Func<IServiceProvider, IMessageTransport> transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (Transport is not null)
            throw new InvalidOperationException("A message transport is set already; an application has one.");
        Transport = transport;
        return this;
    }

    /// <summary>Uses the in-process <see cref="InMemoryTransport"/> (tests, single-process apps).</summary>
    /// <returns>This.</returns>
    public MessagingBuilder UseInMemoryTransport()
        => UseTransport(static provider => new InMemoryTransport(provider.GetRequiredService<TimeProvider>()));

    /// <summary>Keeps dead letters in a store of your choice (<c>Tarol.Messaging.Npgsql</c> adds the database table).</summary>
    /// <param name="deadLetters">Creates the store once.</param>
    /// <returns>This.</returns>
    public MessagingBuilder UseDeadLetters(Func<IServiceProvider, IMessageDeadLetters> deadLetters)
    {
        ArgumentNullException.ThrowIfNull(deadLetters);
        DeadLetters = deadLetters;
        return this;
    }

    /// <summary>Keeps dead letters in memory: they are lost when the process ends (tests and samples).</summary>
    /// <returns>This.</returns>
    public MessagingBuilder UseInMemoryDeadLetters()
        => UseDeadLetters(static provider => new InMemoryMessageDeadLetters(provider.GetRequiredService<TimeProvider>()));
}

/// <summary>Registers messaging on the <see cref="TarolBuilder"/>.</summary>
public static class MessagingTarolBuilderExtensions
{
    /// <summary>
    /// Adds messaging for <paramref name="application"/>: consumers (<see cref="MessageConsumers"/> and the
    /// <see cref="MessagingHost"/> over the configured transport and dead letters), publications and effects (outbox rows the
    /// store writes in each commit, run by the reaction worker: add <c>AddReactionWorker</c> where they should run). The
    /// pipeline and the JSON are the builder's (<c>AddTarol</c>).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="application">This application's name, the prefix of its subscriptions, e.g. <c>billing</c>.</param>
    /// <param name="configure">Consumers, trust, transport, dead letters.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">Called twice, no transport was chosen, or consumers have no dead-letter store.</exception>
    public static TarolBuilder AddMessaging(this TarolBuilder builder, string application, Action<MessagingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        if (builder.Services.Any(static d => d.ServiceType == typeof(Marker)))
            throw new InvalidOperationException("AddMessaging was called twice; configure everything in one call.");
        var messaging = new MessagingBuilder(builder, application);
        configure(messaging);
        messaging.Options.Validate();
        if (messaging.Transport is null)
            throw new InvalidOperationException("Choose a message transport: UseInMemoryTransport(), UseNpgsqlBus(…), UseNats(…) or UseRabbitMq(…).");
        if (messaging.DeadLetters is null && messaging.ConsumerSets.Count > 0)
            throw new InvalidOperationException("Choose where dead letters go: UseInMemoryDeadLetters() or UseNpgsqlDeadLetters(…) (Tarol.Messaging.Npgsql).");
        var consumers = messaging.ConsumerSets.Count == 1 ? messaging.ConsumerSets[0] : ConsumerSet.Combine([.. messaging.ConsumerSets]);
        var services = builder.Services;
        var json = builder.Json;
        services.AddSingleton<Marker>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(messaging.Options);
        services.AddSingleton(consumers);
        services.AddSingleton(messaging.Transport);
        services.AddSingleton(messaging.DeadLetters ?? (static provider => new InMemoryMessageDeadLetters(provider.GetRequiredService<TimeProvider>())));

        // M-10: publications and effects are outbox rows of the store's table, run by the reaction worker
        if (messaging.PublicationSets.Count > 0)
        {
            var publications = messaging.PublicationSets.Count == 1 ? messaging.PublicationSets[0] : PublicationSet.Combine([.. messaging.PublicationSets]);
            builder.WithOutboxRows(publications);
            services.AddSingleton(publications);
            services.AddSingleton<IOutboxRowHandler>(provider => new MessagePublisher(provider.GetRequiredService<PublicationSet>(),
                provider.GetRequiredService<IMessageTransport>(), json, provider.GetService<ILogger<MessagePublisher>>()));
        }
        if (messaging.Effects.Names.Count > 0)
        {
            var effects = messaging.Effects;
            builder.WithOutboxRows(effects);
            foreach (var register in messaging.EffectServices)
                register(services);
            services.AddSingleton<IOutboxRowHandler>(provider => effects.WithServices(provider));
        }
        services.AddSingleton(provider => new MessageConsumers(provider.GetRequiredService<ConsumerSet>(), provider.GetRequiredService<ChangePipeline>(),
            provider.GetRequiredService<IMessageDeadLetters>(), provider.GetRequiredService<MessagingOptions>(), json,
            provider.GetService<ILogger<MessageConsumers>>()));
        if (consumers.Consumers.Count > 0)
            services.AddHostedService(static provider => new MessagingHost(provider.GetRequiredService<IMessageTransport>(),
                provider.GetRequiredService<MessageConsumers>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<ILogger<MessagingHost>>()));
        return builder;
    }

    private sealed class Marker;
}
