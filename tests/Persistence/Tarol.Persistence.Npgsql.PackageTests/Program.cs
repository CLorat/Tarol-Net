using System.Text.Json;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Persistence.Npgsql.PackageTests;

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
await using var db = new NpgsqlSlimDataSourceBuilder("Host=localhost;Database=unused").EnableArrays().Build();
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = "app_events" });

Check("schema script from the package", NpgsqlSchema.Script(store.Schema).Contains("CREATE TABLE IF NOT EXISTS app_events.streams", StringComparison.Ordinal));
Check("generated stream type from the package", Wallet.StreamType == "wallet" && codec.StreamTypeOf<Wallet>() == "wallet");
Check("schema name validated", Throws(() => new NpgsqlStoreOptions { Schema = "x; DROP" }));

static bool Throws(Func<object> action)
{
    try { _ = action(); return false; }
    catch (ArgumentException) { return true; }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
