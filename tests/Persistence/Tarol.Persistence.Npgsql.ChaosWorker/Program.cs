using System.Globalization;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Contract;
using Tarol.Persistence.Npgsql;
using Tarol.StronglyTypedId;

// args: <connection string> <schema> <crash after every n-th commit>
// Every job fires every second; a claim runs every occurrence up to now (Missed.All without a cap), moving the cursor
// after each. "Crash" = FailFast right after a commit, before the cursor moves: the next claim replays its key.
var (connectionString, schema, crashEvery) = (args[0], args[1], int.Parse(args[2], CultureInfo.InvariantCulture));
var lease = TimeSpan.FromSeconds(2);
await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
var codec = new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = schema });
var rows = store.Jobs;
var commits = 0;
Console.WriteLine("ready");

while (true)
{
    Option<JobRow> claimed;
    try
    {
        claimed = await rows.Claim(lease, default);
    }
    catch (NpgsqlException)
    {
        await Task.Delay(200);
        continue;
    }
    if (!claimed.TryGetValue(out JobRow? row))
    {
        await Task.Delay(50);
        continue;
    }
    using var stop = new CancellationTokenSource();
    var heartbeat = Beat(row, stop.Token);
    var now = TimeProvider.System.GetUtcNow();
    var cursor = row.NextAt;
    var lost = false;
    while (cursor <= now && !lost)
    {
        var key = new IdempotencyKey(row.Tenant, "job:" + row.Name, cursor.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        if (!(await store.Seen(key, default)).TryGetValue(out StoredOutcome _))
        {
            var context = OperationContext.Start(Actor.System("Jobs." + row.Name), row.Tenant, TimeProvider.System);
            var member = Member.Join(Id<Member>.New(), row.Name + "@" + key.Key);
            _ = await store.Append(member, 0, context, key, default);   // a concurrent duplicate fails on the key: the commit is once
            if (++commits % crashEvery == 0)
                Environment.FailFast("chaos: crash after the commit, before the cursor moves");
        }
        cursor = cursor.AddSeconds(1);
        lost = !await rows.Move(row, cursor, default);
    }
    await stop.CancelAsync();
    await heartbeat;
    if (!lost)
        _ = await rows.Advance(row, cursor, default);
}

async Task Beat(JobRow row, CancellationToken stop)
{
    try
    {
        while (await rows.Extend(row, lease, CancellationToken.None))
            await Task.Delay(lease / 3, stop);
    }
    catch (OperationCanceledException)
    {
    }
    catch (NpgsqlException)
    {
    }
}
