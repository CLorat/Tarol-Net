using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Nats;
using Tarol.Messaging.RabbitMq;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Sagas;

namespace ArtFlow;

internal static class Lab
{
    public static int Failures;
    public static string Runtime => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";
    public static string Pg => Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Database=postgres;Maximum Pool Size=150";
    public static string NatsUrl => Environment.GetEnvironmentVariable("PROBE_NATS") ?? "nats://localhost:54222";
    public static string RabbitUri => Environment.GetEnvironmentVariable("PROBE_RABBIT") ?? "amqp://guest:guest@localhost:55672/";

    private static NpgsqlDataSource? _db;
    public static NpgsqlDataSource Db => _db ??= new NpgsqlSlimDataSourceBuilder(Pg).EnableArrays().Build();

    public static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : "  — " + detail)}");
        if (!ok)
            Failures++;
    }

    public static void Info(string text) => Console.WriteLine("INFO " + text);

    public static IMessageTransport Transport(string kind, TimeProvider time, string bus) => kind switch
    {
        "memory" => new InMemoryTransport(time),
        "postgres" => new PgTransport(Db, time, bus),
        "postgres+notify" => new PgTransport(Db, time, bus) { Notify = true },
        "nats" => new NatsTransport(NatsUrl),
        "rabbit" => new RabbitTransport(RabbitUri),
        _ => throw new ArgumentException("transport: memory | postgres | postgres+notify | nats | rabbit"),
    };

    /// <summary>Removes what an earlier run left on a broker (streams, queues), so durable subscriptions start empty.</summary>
    public static async Task Reset(string kind, IEnumerable<string> subscriptions)
    {
        if (kind == "nats")
        {
            await using var nats = new NATS.Client.Core.NatsConnection(NATS.Client.Core.NatsOpts.Default with { Url = NatsUrl });
            var js = new NATS.Client.JetStream.NatsJSContext(nats);
            foreach (var stream in new[] { "ARTFLOW", "BILLING" })
                try { await js.DeleteStreamAsync(stream); } catch (NATS.Client.JetStream.NatsJSApiException) { }
        }
        else if (kind == "rabbit")
        {
            var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(RabbitUri) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var s in subscriptions)
            {
                await channel.QueueDeleteAsync(s, false, false);
                await channel.QueueDeleteAsync(s + ".retry", false, false);
            }
        }
    }

    public static async Task<T> Until<T>(Func<T?> probe, TimeSpan timeout, string what)
        where T : class
    {
        var end = TimeProvider.System.GetUtcNow() + timeout;
        while (TimeProvider.System.GetUtcNow() < end)
        {
            if (probe() is { } found)
                return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("waited " + timeout.TotalSeconds + " s for " + what);
    }

    public static async Task<bool> Until(Func<bool> probe, TimeSpan timeout)
    {
        var end = TimeProvider.System.GetUtcNow() + timeout;
        while (TimeProvider.System.GetUtcNow() < end)
        {
            if (probe())
                return true;
            await Task.Delay(20);
        }
        return probe();
    }
}

/// <summary>One application: store, pipeline, the reaction worker (reactions + publications + effects), consumers, dead letters.</summary>
internal sealed class Sys : IAsyncDisposable
{
    public required string Name { get; init; }
    public required IAggregateStore Store { get; init; }
    public required IEventFeed Feed { get; init; }
    public required IOutbox Outbox { get; init; }
    public required ChangePipeline Pipeline { get; init; }
    public required ReactionWorker Worker { get; init; }
    public required ConsumerHost Consumers { get; init; }
    public required IMessageDeadLetters Dead { get; init; }
    public required PublicationSet Publications { get; init; }
    public required ConsumerOptions Options { get; init; }
    public required IMessageTransport Transport { get; init; }
    public ConcurrentQueue<ConsumerAttempt> Attempts { get; } = new();
    public ConcurrentQueue<(Message Message, DateTimeOffset At)> Published { get; } = new();
    private CancellationTokenSource? _stop;
    private readonly List<Task> _loops = [];

    public static readonly DeliveryPolicy Quick = new() { MaxAttempts = 4, FirstDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(400) };

    public static async Task<Sys> Create(string name, string suffix, bool postgres, TimeProvider time, IMessageTransport transport, PublicationSet publications,
        ConsumerSet consumers, JsonSerializerOptions json, Func<EventCodec, EventCodec> codec, ReactionSet[] more, Action<ConsumerOptions> trust,
        DeliveryPolicy? policy = null, bool notify = false, NpgsqlDataSource? db = null, bool fresh = true)
    {
        db ??= Lab.Db;
        var rows = new OutboxRows([publications.AsReactions(transport), .. more]);
        IAggregateStore store;
        IEventFeed feed;
        IOutbox outbox;
        IMessageDeadLetters dead;
        var c = codec(new EventCodec(new JsonSerializerOptions(json)));
        if (postgres)
        {
            var schema = name + "_" + suffix;
            if (fresh)
                await using (var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
                    await drop.ExecuteNonQueryAsync();
            var pg = new NpgsqlAggregateStore(db, c, time, new NpgsqlStoreOptions { Schema = schema, Reactions = rows, Notify = notify });
            await pg.EnsureSchema(default);
            var d = new NpgsqlDeadLetters(db, time, schema);
            await d.Ensure(default);
            (store, feed, outbox, dead) = (pg, pg, pg.Outbox, d);
        }
        else
        {
            var mem = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = c, Reactions = rows });
            (store, feed, outbox, dead) = (mem, mem, mem.Outbox, new InMemoryDeadLetters(time));
        }
        var pipeline = new ChangePipeline(store, time);
        var options = new ConsumerOptions { Application = name, Policy = policy ?? Quick };
        trust(options);
        var worker = new ReactionWorker(outbox, rows.Set, pipeline, time, new ReactionOptions { Policy = policy ?? Quick, Lease = TimeSpan.FromSeconds(30) });
        var sys = new Sys
        {
            Name = name, Store = store, Feed = feed, Outbox = outbox, Pipeline = pipeline, Worker = worker, Dead = dead, Publications = publications,
            Options = options, Transport = transport,
            Consumers = new ConsumerHost(consumers, pipeline, dead, options, json, time),
        };
        sys.Consumers.Observed = sys.Attempts.Enqueue;
        publications.Published = m => sys.Published.Enqueue((m, time.GetUtcNow()));
        return sys;
    }

    public void Start(int publishers = 1, int concurrency = 1, bool consume = true)
    {
        _stop = new CancellationTokenSource();
        for (var i = 0; i < publishers; i++)
            _loops.Add(Worker.Run(_stop.Token));
        if (consume)
            _loops.Add(Consumers.Run(Transport, _stop.Token, concurrency));
    }

    public async Task Stop()
    {
        if (_stop is null)
            return;
        await _stop.CancelAsync();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { }
        _loops.Clear();
        _stop.Dispose();
        _stop = null;
    }

    public async Task<List<RecordedEvent>> Events()
    {
        var all = new List<RecordedEvent>();
        await foreach (var e in Feed.ReadAll(GlobalPosition.Start, 1_000_000, default))
            all.Add(e.Event);
        return all;
    }

    public IEnumerable<ConsumerAttempt> For(string consumer, Guid message) => Attempts.Where(a => a.Consumer == consumer && a.Incoming.Message?.Id == message);

    public async ValueTask DisposeAsync() => await Stop();
}
