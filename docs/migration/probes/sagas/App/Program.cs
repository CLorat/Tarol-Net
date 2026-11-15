using System.Diagnostics;
using System.Text.Json;
using ArtFlow;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

var mode = args.FirstOrDefault() ?? "functional";
Console.WriteLine($"# mode {mode}, runtime {Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT"}");
return mode switch
{
    "functional" => await Functional.Run(),
    "crash-child" => await Crash.Child(args[1]),
    "throughput" => await Perf.Run(args.Skip(1).ToArray()),
    "serve" => await Serve.Run(args.Skip(1).ToArray()),
    _ => throw new ArgumentException(mode),
};

internal static class Lab
{
    public static int Failures;
    public static readonly ActivitySource Http = new("Probe.Http");

    public static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(detail is null ? "" : "  — " + detail)}");
        if (!ok)
            Failures++;
    }

    public static string ConnectionString => Environment.GetEnvironmentVariable("PROBE_PG")
        ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=150";

    private static NpgsqlDataSource? _db;
    public static NpgsqlDataSource Db => _db ??= new NpgsqlSlimDataSourceBuilder(ConnectionString).EnableArrays().Build();

    public static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(AppJson.Default.Options)).Add<Auction>().Add<Payout>().Add<Settlement>();

    public static OperationContext Alice(TimeProvider time) => OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), time);
}

/// <summary>A schema with the step 8 store and the probe's outbox additions.</summary>
internal sealed class Env : IAsyncDisposable
{
    public required string Schema { get; init; }
    public required NpgsqlAggregateStore Store { get; init; }
    public required PgOutbox Outbox { get; init; }
    public required ChangePipeline Pipeline { get; init; }
    public required TimeProvider Time { get; init; }
    public required ReactionSet Reactions { get; init; }

    public static async Task<Env> Create(string name, TimeProvider time, DeadLetterDesign design, bool notify = false, ReactionSet? reactions = null, string[]? unordered = null)
    {
        var schema = $"sagas_{name}_{Environment.ProcessId}";
        var set = reactions ?? GeneratedReactions.Set;
        var codec = Lab.Codec();
        var store = new NpgsqlAggregateStore(Lab.Db, codec, time, new NpgsqlStoreOptions { Schema = schema, Reactions = set.Table });
        await using (var drop = Lab.Db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
            await drop.ExecuteNonQueryAsync();
        await store.EnsureSchema(default);
        var outbox = new PgOutbox(Lab.Db, codec, time, schema, design, unordered);
        await outbox.Install(notify, default);
        return new Env { Schema = schema, Store = store, Outbox = outbox, Pipeline = new ChangePipeline(store, time), Time = time, Reactions = set };
    }

    public ReactionWorker Worker(WorkerOptions? options = null, Action<ClaimedRow, Verdict, bool>? observed = null, Func<CancellationToken, Task>? wait = null)
        => new(Outbox, Reactions, Pipeline, Time, options ?? new()) { Observed = observed, Wait = wait };

    public async Task<long> Scalar(string sql, params object[] values)
    {
        await using var command = Lab.Db.CreateCommand(sql.Replace("{s}", Schema, StringComparison.Ordinal));
        foreach (var v in values)
            command.Parameters.Add(new NpgsqlParameter { Value = v });
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<RecordedEvent>> Events()
    {
        var all = new List<RecordedEvent>();
        await foreach (var e in Store.ReadAll(GlobalPosition.Start, 10_000, default))
            all.Add(e.Event);
        return all;
    }

    /// <summary>An auction with a winning bid by bob, committed by alice (no reaction runs for these events).</summary>
    public async Task<Id<Auction>> AuctionWithBid(OperationContext alice, decimal amount = 120)
    {
        var auction = Auction.Create("alice", 100, Time.GetUtcNow());
        _ = auction.PlaceBid("bob", amount, Time.GetUtcNow());
        (await Store.Append(auction, 0, alice, Option.None, default)).GetValueOrThrow();
        return auction.Id;
    }

    public async ValueTask DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("PROBE_KEEP") is not null)
            return;
        await using var drop = Lab.Db.CreateCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

internal static class Extensions
{
    public static T GetValueOrThrow<T>(this ValueTask<Result<T>> pending) where T : notnull => pending.AsTask().GetAwaiter().GetResult().GetValueOrThrow();

    public static void GetValueOrThrow(this Result result)
    {
        if (result.TryGetValue(out Violation? v))
            throw new InvalidOperationException(v.ToString());
    }
}
