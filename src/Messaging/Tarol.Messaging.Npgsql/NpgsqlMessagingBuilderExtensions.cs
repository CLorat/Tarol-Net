using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;

namespace Tarol.Messaging.Npgsql;

/// <summary>The in-database bus and the PostgreSQL dead letters on the <see cref="MessagingBuilder"/>.</summary>
public static class NpgsqlMessagingBuilderExtensions
{
    /// <summary>
    /// Uses the in-database bus on the application's registered <see cref="NpgsqlDataSource"/> (the store's: what
    /// <c>UseNpgsqlStore</c> registers, or Aspire's), so the bus shares its pool instead of opening a second one (Tatalu
    /// FB-INT3-3: separate pools of the store, the bus and other components exhausted <c>max_connections</c>). Use this when the
    /// bus lives in the store's database; a separate bus database takes the connection string overload.
    /// </summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="options">Schema, NOTIFY, poll interval, roles; <see langword="null"/> = defaults.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNpgsqlBus(this MessagingBuilder messaging, NpgsqlBusOptions? options = null)
        => UseNpgsqlBus(messaging, static provider => provider.GetRequiredService<NpgsqlDataSource>(), options);

    /// <summary>
    /// Uses the in-database bus on its own data source built from <paramref name="connectionString"/> (the bus database every
    /// system reaches; it may be the store's database). The data source is disposed with the bus. It is a pool of its own
    /// (up to <c>Maximum Pool Size</c>, default 100, plus the LISTEN connection); for the store's database prefer
    /// <see cref="UseNpgsqlBus(MessagingBuilder, NpgsqlBusOptions?)"/>.
    /// </summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="connectionString">The bus database, connected as this system's role.</param>
    /// <param name="options">Schema, NOTIFY, poll interval, roles; <see langword="null"/> = defaults.</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNpgsqlBus(this MessagingBuilder messaging, string connectionString, NpgsqlBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options ??= new NpgsqlBusOptions();
        options.Validate();
        AddRetention(messaging, options);
        AddBacklog(messaging);
        return messaging.UseTransport(provider =>
        {
            var source = new NpgsqlSlimDataSourceBuilder(connectionString).AutoPrepareUnlessSet();
            if (!options.TraceRootCommands)
                source.TraceOnlyInActivities();                                                     // O-6: no root spans from idle polls
            return new NpgsqlBus(source.Build(), ownsDataSource: true, provider.GetRequiredService<TimeProvider>(), options,
                provider.GetService<ILogger<NpgsqlBus>>());
        });
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
        options ??= new NpgsqlBusOptions();
        options.Validate();
        AddRetention(messaging, options);
        AddBacklog(messaging);
        return messaging.UseTransport(provider => new NpgsqlBus(dataSource(provider), provider.GetRequiredService<TimeProvider>(), options,
            provider.GetService<ILogger<NpgsqlBus>>()));
    }

    /// <summary>The bus backlog per subscription as a snapshot signal (step 15): a count of the deliveries, read once per poll.</summary>
    private static void AddBacklog(MessagingBuilder messaging)
        => messaging.Tarol.Services.AddSingleton(new OperationsSignal(OperationsSignals.Meter, OperationsSignals.MessagesBacklog, "{message}",
            "Deliveries waiting on the PostgreSQL bus, per subscription of this application.", static async (provider, ct) =>
            {
                if (provider.GetRequiredService<IMessageTransport>() is not NpgsqlBus bus)
                    return [];
                var application = provider.GetRequiredService<MessagingOptions>().Application;
                return [.. (await bus.Backlog(application, ct).ConfigureAwait(false)).Select(static b => new SignalValue(b.Count, "tarol.subscription", b.Subscription))];
            }));

    /// <summary>The name of the bus retention's system job (J-9).</summary>
    public const string PurgeMessagesJob = "tarol.purge-messages";

    /// <summary>The bus retention as a maintenance task: a job host runs it as <see cref="PurgeMessagesJob"/> every hour.</summary>
    private static void AddRetention(MessagingBuilder messaging, NpgsqlBusOptions options)
        => messaging.Tarol.Services.AddSingleton(new MaintenanceTask(PurgeMessagesJob, TimeSpan.FromHours(1), async (provider, at, ct) =>
        {
            if (provider.GetRequiredService<IMessageTransport>() is not NpgsqlBus bus)
                return 0;
            var total = 0;
            for (int deleted; (deleted = await bus.PurgeMessages(at - options.Retention, options.PurgeBatch, ct).ConfigureAwait(false)) > 0;)
            {
                total += deleted;
                if (deleted < options.PurgeBatch)
                    break;
            }
            return total;
        }));

    /// <summary>
    /// Keeps dead letters in <c>&lt;schema&gt;.messages_dead</c> of the consumer's database: by default the
    /// <see cref="NpgsqlDataSource"/> registered by <c>UseNpgsqlStore(connectionString)</c> and schema <c>tarol</c>. The table
    /// is created when the host starts.
    /// </summary>
    /// <param name="messaging">The messaging builder.</param>
    /// <param name="schema">The schema of the table (default <c>tarol</c>, the store's default).</param>
    /// <param name="dataSource">Resolves the data source; <see langword="null"/> = the registered <see cref="NpgsqlDataSource"/>.</param>
    /// <param name="ensureSchema">Create or upgrade the table when the host starts (version table <c>messages_dead_version</c>).</param>
    /// <returns>The builder.</returns>
    public static MessagingBuilder UseNpgsqlDeadLetters(this MessagingBuilder messaging, string schema = "tarol", Func<IServiceProvider, NpgsqlDataSource>? dataSource = null,
        bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        NpgsqlBusSchema.Validate(schema);
        messaging.UseDeadLetters(provider => new NpgsqlMessageDeadLetters(
            dataSource?.Invoke(provider) ?? provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<TimeProvider>(), schema,
            provider.GetService<ILogger<NpgsqlMessageDeadLetters>>()));
        messaging.Tarol.Services.AddSingleton(new OperationsSignal(OperationsSignals.Meter, OperationsSignals.MessagesDead, "{message}",
            "Dead consumer messages (messages_dead).", static async (provider, ct) =>
                provider.GetRequiredService<IMessageDeadLetters>() is NpgsqlMessageDeadLetters dead
                    ? [new SignalValue(await dead.Count(ct).ConfigureAwait(false))]
                    : []));
        messaging.Tarol.Services.AddSingleton<IDeadLetterRetention>(static provider =>
            (NpgsqlMessageDeadLetters)provider.GetRequiredService<IMessageDeadLetters>());
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
