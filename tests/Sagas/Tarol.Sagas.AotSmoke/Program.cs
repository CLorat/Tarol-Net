using System.Text.Json;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;
using Tarol.Sagas.AotSmoke;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

// in memory: the chain and the scheduled reaction with a standing clock
var test = ReactionTest.At(start, GeneratedReactions.Set, SmokeJson.Default.Options);
var order = Order.Place(start);
_ = await test.Pipeline.Run(new PlaceGiven(order), test.Context());
var now = await test.Drain();
Check("reaction runs (ship), scheduled one waits", now is [{ Row.Reaction: "ShipOrder", Verdict: Verdict.Done }]);
var later = await test.Later(TimeSpan.FromHours(1)).Drain();
Check("scheduled reaction runs an hour later (no-op: shipped)", later is [{ Row.Reaction: "CancelUnshipped", Verdict: Verdict.Done }]
                                                               && test.Load(order.Id) is { Shipped: true, Cancelled: false });
Check("reaction context", test.Store.Recorded.Single(e => e.Event.Event is Order.WasShipped).Context.Actor.Id == "Reactions.ShipOrder");
Check("policy", DeliveryPolicy.Classify(Tarol.Results.Error.Conflict("x")) == Verdict.Retry && new DeliveryPolicy().Backoff(1, 1) > TimeSpan.Zero);

// the worker against PostgreSQL, through the hosted service: TAROL_PG=<connection string>
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"sagas_smoke_{Environment.ProcessId}";
    var codec = new EventCodec(new JsonSerializerOptions(SmokeJson.Default.Options)).Add<Order>();
    var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Reactions = GeneratedReactions.Set });
    try
    {
        await store.EnsureSchema(default);
        var pipeline = new ChangePipeline(store, TimeProvider.System);
        var host = new ReactionHost(store.Outbox, GeneratedReactions.Set, pipeline, TimeProvider.System,
            new ReactionOptions { Workers = 2, PollInterval = TimeSpan.FromMilliseconds(20) });
        await host.StartAsync(default);
        var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var placed = Order.Place(TimeProvider.System.GetUtcNow());
        Check("commit with outbox rows", (await store.Append(placed, 0, context, Option.None, default)).IsSuccess);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        // done = the reaction committed and its row was completed (only the scheduled row is left)
        while (await store.Outbox.Pending(default) > 1 && waited.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20);
        await host.StopAsync(default);
        Check("hosted worker ran the reaction on PostgreSQL", (await store.Load(placed.Id, context, default)).GetValueOrThrow().Shipped);
        Check("the scheduled row waits", await store.Outbox.Pending(default) == 1);
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

/// <summary>Commits a placed order through the pipeline (an ILoads that hands back the new instance).</summary>
internal sealed record PlaceGiven(Order New) : ILoads<Order>
{
    public ValueTask<Tarol.Results.Result<Order>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken) => ValueTask.FromResult<Tarol.Results.Result<Order>>(New);
    public Tarol.Results.Result Execute(Order order, ChangeContext context) => Tarol.Results.Result.Success();
}
