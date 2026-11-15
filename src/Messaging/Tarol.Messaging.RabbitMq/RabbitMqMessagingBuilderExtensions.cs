using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tarol.Messaging.RabbitMq;

/// <summary>RabbitMQ on the <see cref="MessagingBuilder"/>.</summary>
public static class RabbitMqMessagingBuilderExtensions
{
    /// <summary>Uses RabbitMQ at <paramref name="uri"/> (e.g. Aspire's connection string <c>rabbitmq</c>).</summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="uri">The broker, <c>amqp://user:password@host:port/vhost</c>.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseRabbitMq(this MessagingBuilder messaging, string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return messaging.UseRabbitMq(new RabbitMqTransportOptions { Uri = new Uri(uri) });
    }

    /// <summary>Uses RabbitMQ with these options (prefix, user id).</summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="options">The options.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseRabbitMq(this MessagingBuilder messaging, RabbitMqTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return messaging.UseTransport(provider => new RabbitMqTransport(options, provider.GetService<ILogger<RabbitMqTransport>>()));
    }
}
