using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tarol.Persistence;

/// <summary>
/// One registration for a Tarol application (docs/architecture/rest.md §10, R-14): <c>services.AddTarol(…)</c>
/// (<c>Tarol.Changes</c>) creates it with the codec and the JSON options; each package adds its part:
/// <see cref="UseInMemoryStore"/> here, <c>UseNpgsqlStore</c> (<c>Tarol.Persistence.Npgsql</c>), <c>AddReactionWorker</c>
/// (<c>Tarol.Sagas</c>), <c>AddSecurity</c> (<c>Tarol.Security</c>). Stores read <see cref="Reactions"/> when they are
/// resolved, so <see cref="WithReactions"/> may follow the store; the worker needs the reactions when it is added.
/// </summary>
public sealed class TarolBuilder
{
    private readonly List<IOutboxReactions> _rows = [];

    /// <summary>Creates the builder; called by <c>AddTarol</c>.</summary>
    /// <param name="services">The application's services.</param>
    /// <param name="json">The JSON options of the event payloads and the stored state (the app's context, value objects first).</param>
    /// <param name="codec">The event codec with the application's aggregates.</param>
    public TarolBuilder(IServiceCollection services, JsonSerializerOptions json, EventCodec codec)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(codec);
        Services = services;
        Json = json;
        Codec = codec;
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(codec);
    }

    /// <summary>The application's services.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The JSON options of payloads and stored state.</summary>
    public JsonSerializerOptions Json { get; }

    /// <summary>The event codec.</summary>
    public EventCodec Codec { get; }

    /// <summary>The reactions the store writes outbox rows for, if any (<see cref="WithReactions"/>).</summary>
    public IOutboxReactions? Reactions { get; private set; }

    /// <summary>
    /// Everything the store writes outbox rows for: <see cref="Reactions"/> and the rows other packages add with
    /// <see cref="WithOutboxRows"/> (publications and effects of <c>Tarol.Messaging</c>). Stores read it when they are resolved.
    /// </summary>
    public IOutboxReactions? OutboxTable => OutboxReactions.Combine([Reactions, .. _rows]);

    /// <summary>Whether a store was registered.</summary>
    public bool HasStore { get; private set; }

    /// <summary>The store writes an outbox row per (event, reaction) in each commit; the API process needs this too, the
    /// worker (<c>AddReactionWorker</c>) runs the rows.</summary>
    /// <param name="reactions">The reactions (<c>GeneratedReactions.Set</c>).</param>
    /// <returns>The builder.</returns>
    public TarolBuilder WithReactions(IOutboxReactions reactions)
    {
        ArgumentNullException.ThrowIfNull(reactions);
        Reactions = reactions;
        return this;
    }

    /// <summary>
    /// Adds outbox rows next to the reactions (publications, effects): the store writes them in the same commit and the
    /// reaction worker runs them with the handler the package registers. Order-independent with <see cref="WithReactions"/>.
    /// </summary>
    /// <param name="rows">The rows' table.</param>
    /// <returns>The builder.</returns>
    public TarolBuilder WithOutboxRows(IOutboxReactions rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows.Add(rows);
        return this;
    }

    /// <summary>The in-memory store (tests, prototypes): <see cref="IAggregateStore"/>, <see cref="IAggregateReader"/>,
    /// <see cref="IEventFeed"/> and <see cref="IOutbox"/>.</summary>
    /// <param name="lookups">The unique lookups, if any.</param>
    /// <returns>The builder.</returns>
    public TarolBuilder UseInMemoryStore(StoreLookups? lookups = null)
        => UseStore(provider => new InMemoryAggregateStore(provider.GetRequiredService<TimeProvider>(), new InMemoryStoreOptions
        {
            Codec = Codec,
            StateOptions = Json,
            Reactions = OutboxTable,
            Lookups = lookups ?? new StoreLookups(),
        }), static store => store.Outbox);

    /// <summary>Registers a store: itself, <see cref="IAggregateStore"/>, <see cref="IAggregateReader"/>, its
    /// <see cref="IOutbox"/>, and <see cref="IEventFeed"/> / <see cref="IIdempotencyKeyPurge"/> when it implements them.</summary>
    /// <typeparam name="TStore">The store type.</typeparam>
    /// <param name="factory">Creates the store (once, as a singleton).</param>
    /// <param name="outbox">The store's outbox.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">A store was registered already.</exception>
    public TarolBuilder UseStore<TStore>(Func<IServiceProvider, TStore> factory, Func<TStore, IOutbox> outbox)
        where TStore : class, IAggregateStore
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(outbox);
        if (HasStore)
            throw new InvalidOperationException("A store is registered already; a Tarol application has one store.");
        HasStore = true;
        Services.AddSingleton(factory);
        Services.AddSingleton<IAggregateStore>(static provider => provider.GetRequiredService<TStore>());
        Services.AddSingleton<IAggregateReader>(static provider => provider.GetRequiredService<TStore>());
        Services.AddSingleton(provider => outbox(provider.GetRequiredService<TStore>()));
        if (typeof(IEventFeed).IsAssignableFrom(typeof(TStore)))
            Services.AddSingleton(static provider => (IEventFeed)provider.GetRequiredService<TStore>());
        if (typeof(IIdempotencyKeyPurge).IsAssignableFrom(typeof(TStore)))
            Services.AddSingleton(static provider => (IIdempotencyKeyPurge)provider.GetRequiredService<TStore>());
        return this;
    }
}
