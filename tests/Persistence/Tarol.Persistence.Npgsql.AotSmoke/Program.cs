using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Persistence.Npgsql.AotSmoke;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var codec = new EventCodec(new JsonSerializerOptions(SmokeJson.Default.Options)).Add<Wallet>();
var lookups = new StoreLookups().Define<Wallet>("owner", w => w.Owner);
Check("schema script", NpgsqlSchema.Script("smoke").Contains("CREATE TABLE IF NOT EXISTS smoke.events", StringComparison.Ordinal));

// The database checks need a PostgreSQL: TAROL_PG=<connection string> (the CI aot-smoke job provides one).
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"aot_smoke_{Environment.ProcessId}";
    var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Lookups = lookups });
    try
    {
        await store.EnsureSchema(default);
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
        var key = new IdempotencyKey(context.Tenant, "smoke", "k-1");

        var wallet = Wallet.Open("alice");
        _ = wallet.Add(5);
        Check("append", (await store.Append(wallet, 0, context, key, default)).IsSuccess);
        AggregateChanges.Committed(wallet);
        var loaded = (await store.Load(wallet.Id, context, default)).GetValueOrThrow();
        Check("load from stored state", loaded.Total == 5 && loaded.Version == 2);
        Check("key stored", (await store.Seen(key, default)).GetValueOrThrow() == new StoredOutcome(wallet.Id.Value, 2, 2));
        Check("lookup", (await store.Find<Wallet>("owner", "alice", context, default)).GetValueOrThrow() == wallet.Id);
        var rival = (await store.Load(wallet.Id, context, default)).GetValueOrThrow();
        _ = loaded.Add(1);
        _ = rival.Add(2);
        Check("first writer commits", (await store.Append(loaded, 2, context, Option.None, default)).IsSuccess);
        Check("second writer: version conflict", StoreErrors.IsVersionConflict(await store.Append(rival, 2, context, Option.None, default)));
        var feed = new List<PositionedEvent>();
        await foreach (var e in store.ReadAll(GlobalPosition.Start, 10, default))
            feed.Add(e);
        Check("feed with context", feed.Count == 3 && feed.All(e => e.Event.Context == context && e.StreamType == "wallet"));
        Check("rebuild states", await store.RebuildStates<Wallet>(default) == 0);
        Check("purge keys", await store.PurgeKeys(TimeProvider.System.GetUtcNow().AddDays(1), 100, default) == 1);
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
