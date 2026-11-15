using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore;

/// <summary>Question 7: one registration for codec, store, pipeline, reactions and endpoints (candidate H1, a builder).</summary>
public sealed class TarolBuilder
{
    internal TarolBuilder(IServiceCollection services, JsonSerializerOptions json, EventCodec codec)
        => (Services, Json, Codec) = (services, json, codec);

    public IServiceCollection Services { get; }
    public JsonSerializerOptions Json { get; }
    public EventCodec Codec { get; }
    internal ReactionSet? Reactions { get; set; }

    /// <summary>The store writes outbox rows for these reactions (the API process too); the worker runs them.</summary>
    public TarolBuilder WithReactions(ReactionSet reactions)
    {
        Reactions = reactions;              // not registered here: AddTarolReactions takes a ReactionSet registration as "called twice"
        return this;
    }

    public TarolBuilder UseInMemoryStore()
    {
        Services.AddSingleton(s => new InMemoryAggregateStore(s.GetRequiredService<TimeProvider>(),
            new InMemoryStoreOptions { Codec = Codec, StateOptions = Json, Reactions = Reactions }));
        Services.AddSingleton<IAggregateStore>(s => s.GetRequiredService<InMemoryAggregateStore>());
        Services.AddSingleton<IOutbox>(s => s.GetRequiredService<InMemoryAggregateStore>().Outbox);
        return Common();
    }

    public TarolBuilder UseNpgsqlStore(string connectionString)
    {
        Services.AddSingleton(_ => new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build());
        Services.AddSingleton(s => new NpgsqlAggregateStore(s.GetRequiredService<NpgsqlDataSource>(), Codec, s.GetRequiredService<TimeProvider>(),
            new NpgsqlStoreOptions { Reactions = Reactions, StateOptions = Json }));
        Services.AddSingleton<IAggregateStore>(s => s.GetRequiredService<NpgsqlAggregateStore>());
        Services.AddSingleton<IOutbox>(s => s.GetRequiredService<NpgsqlAggregateStore>().Outbox);
        Services.AddHostedService(s => new EnsureSchema(s.GetRequiredService<NpgsqlAggregateStore>()));
        return Common();
    }

    /// <summary>Hosts the reaction worker (Tarol.Sagas) in this process.</summary>
    public TarolBuilder AddReactionWorker(Action<ReactionOptions>? configure = null)
    {
        var reactions = Reactions ?? throw new InvalidOperationException("WithReactions first.");
        Services.AddTarolReactions(reactions, configure);
        return this;
    }

    /// <summary>The HTTP entries: ChangeEndpoints, webhooks, and the resolver in ASP.NET Core's JSON options.</summary>
    public TarolBuilder AddEndpoints(Action<ChangeEndpointOptions>? configure = null)
    {
        var options = new ChangeEndpointOptions { Json = Json, StateJson = Json };
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton(s => new ChangeEndpoints(options, s.GetRequiredService<ChangePipeline>(), s.GetRequiredService<IAggregateStore>(),
            s.GetRequiredService<TimeProvider>(), s.GetService<IChangeAuthorization>()));
        Services.AddSingleton<Webhooks>();
        Services.AddOptions<WebhookOptions>();
        Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, ValueObjectJson.Resolver);
            foreach (var resolver in Json.TypeInfoResolverChain)
                if (resolver != ValueObjectJson.Resolver)
                    o.SerializerOptions.TypeInfoResolverChain.Add(resolver);
        });
        return this;
    }

    private TarolBuilder Common()
    {
        Services.AddSingleton<IAggregateReader>(s => s.GetRequiredService<IAggregateStore>());
        Services.AddSingleton(s => new ChangePipeline(s.GetRequiredService<IAggregateStore>(), s.GetRequiredService<TimeProvider>(),
            s.GetService<IChangeAuthorization>()));
        return this;
    }

    private sealed class EnsureSchema(NpgsqlAggregateStore store) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchema(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public static class TarolServiceCollectionExtensions
{
    /// <summary>Codec and JSON from the app's context (ValueObjectJson.Resolver first); TimeProvider.System unless the app registered one.</summary>
    public static TarolBuilder AddTarol(this IServiceCollection services, JsonSerializerContext json, Action<EventCodec> aggregates)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(aggregates);
        services.TryAddSingleton(TimeProvider.System);
        var options = ValueObjectJson.Options(json);
        var codec = new EventCodec(options);
        aggregates(codec);
        return new TarolBuilder(services, options, codec);
    }
}
