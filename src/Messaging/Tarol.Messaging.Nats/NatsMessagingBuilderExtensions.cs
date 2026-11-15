using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tarol.Messaging.Nats;

/// <summary>NATS JetStream on the <see cref="MessagingBuilder"/>.</summary>
public static class NatsMessagingBuilderExtensions
{
    /// <summary>Uses NATS JetStream at <paramref name="url"/> (e.g. Aspire's connection string <c>nats</c>).</summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="url">The server URL; a <c>nats://user:password@host:port</c> URL carries the credentials.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNats(this MessagingBuilder messaging, string url)
        => messaging.UseNats(new NatsTransportOptions { Url = url });

    /// <summary>Uses NATS JetStream with these options (user, prefix, duplicate window, publish timeout).</summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="options">The options.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNats(this MessagingBuilder messaging, NatsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return messaging.UseTransport(provider => new NatsTransport(options, provider.GetService<ILogger<NatsTransport>>()));
    }
}
