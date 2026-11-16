using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tarol.Messaging.Npgsql;

/// <summary>The in-database bus and the PostgreSQL dead letters on the <see cref="MessagingBuilder"/>.</summary>
public static class NpgsqlMessagingBuilderExtensions
{
    /// <summary>
    /// Uses the in-database bus on its own data source built from <paramref name="connectionString"/> (the bus database every
    /// system reaches; it may be the store's database). The data source is disposed with the bus.
    /// </summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="connectionString">The bus database, connected as this system's role.</param>
    /// <param name="options">Schema, NOTIFY, poll interval, roles; <see langword="null"/> = defaults.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNpgsqlBus(this MessagingBuilder messaging, string connectionString, NpgsqlBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        (options ?? new NpgsqlBusOptions()).Validate();
        return messaging.UseTransport(provider => new NpgsqlBus(new NpgsqlSlimDataSourceBuilder(connectionString).Build(), ownsDataSource: true,
            provider.GetRequiredService<TimeProvider>(), options, provider.GetService<ILogger<NpgsqlBus>>()));
    }

    /// <summary>Uses the in-database bus on a data source the application provides (e.g. the store's, or Aspire's).</summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="dataSource">Resolves the data source (not disposed by the bus).</param>
    /// <param name="options">Schema, NOTIFY, poll interval, roles; <see langword="null"/> = defaults.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNpgsqlBus(this MessagingBuilder messaging, Func<IServiceProvider, NpgsqlDataSource> dataSource, NpgsqlBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(dataSource);
        (options ?? new NpgsqlBusOptions()).Validate();
        return messaging.UseTransport(provider => new NpgsqlBus(dataSource(provider), provider.GetRequiredService<TimeProvider>(), options,
            provider.GetService<ILogger<NpgsqlBus>>()));
    }

    /// <summary>
    /// Keeps dead letters in <c>&lt;schema&gt;.messages_dead</c> of the consumer's database: by default the
    /// <see cref="NpgsqlDataSource"/> registered by <c>UseNpgsqlStore(connectionString)</c> and schema <c>tarol</c>. The table
    /// is created when the host starts.
    /// </summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="schema">The schema of the table (default <c>tarol</c>, the store's default).</param>
    /// <param name="dataSource">Resolves the data source; <see langword="null"/> = the registered <see cref="NpgsqlDataSource"/>.</param>
    /// <param name="ensureSchema">Create the table when the host starts.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNpgsqlDeadLetters(this MessagingBuilder messaging, string schema = "tarol", Func<IServiceProvider, NpgsqlDataSource>? dataSource = null,
        bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        NpgsqlBusSchema.Validate(schema);
        messaging.UseDeadLetters(provider => new NpgsqlMessageDeadLetters(
            dataSource?.Invoke(provider) ?? provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<TimeProvider>(), schema));
        if (ensureSchema)
            messaging.Tarol.Services.AddHostedService(static provider => new EnsureDeadLetters((NpgsqlMessageDeadLetters)provider.GetRequiredService<IMessageDeadLetters>()));
        return messaging;
    }

    private sealed class EnsureDeadLetters(NpgsqlMessageDeadLetters deadLetters) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => deadLetters.EnsureSchema(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
