using Microsoft.Extensions.Configuration;
using Tarol.Messaging;
using Tarol.Messaging.Nats;
using Tarol.Messaging.RabbitMq;

namespace ArtFlow.Samples;

/// <summary>The AppHost's broker: connection string <c>nats</c> (default) or <c>rabbitmq</c> (<c>ARTFLOW_BROKER=rabbitmq</c>).</summary>
internal static class Broker
{
    public static MessagingBuilder UseBroker(this MessagingBuilder messaging, IConfiguration configuration)
        => configuration.GetConnectionString("rabbitmq") is { Length: > 0 } rabbit
            // both systems share the AppHost's one broker user: user_id would name it, not the system, so it is not stamped
            ? messaging.UseRabbitMq(new RabbitMqTransportOptions { Uri = new Uri(rabbit), StampUserId = false })
            : configuration.GetConnectionString("nats") is { Length: > 0 } nats
                ? messaging.UseNats(nats)
                : throw new InvalidOperationException("No connection string 'nats' or 'rabbitmq' (run it through the AppHost).");

    /// <summary>The OpenTelemetry sources of a system: reactions, publications and effects, and consumed messages.</summary>
    public static readonly string[] Sources = [Tarol.Sagas.ReactionWorker.ActivitySourceName, MessageConsumers.ActivitySourceName];
}
