using System.Data.Common;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Queries.Npgsql.AotSmoke;
using Tarol.Results;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

static Page<AuctionRow> Ok(Result<Page<AuctionRow>> result) => result.TryGetValue(out Page<AuctionRow>? page) ? page : throw new InvalidOperationException(result.ToString());

var script = NpgsqlReadModelSchema.Script<AuctionRow>("smoke");
Check("the DDL from the generated column map", script.Contains("\"ends_at\" timestamptz NOT NULL", StringComparison.Ordinal)
                                              && script.Contains("\"title\" text COLLATE \"C\" NOT NULL", StringComparison.Ordinal)
                                              && script.Contains("ON smoke.rm_auction_list_v1 (tenant, \"highest_bid\", id)", StringComparison.Ordinal));

var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"queries_smoke_{Environment.ProcessId}";
    var json = new JsonSerializerOptions(SmokeJson.Default.Options);
    var store = new NpgsqlAggregateStore(db, new EventCodec(json).Add<Auction>(), TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, NotifyFeed = true });
    try
    {
        await store.EnsureSchema(default);
        var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var start = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var last = GlobalPosition.Start;
        for (var i = 0; i < 25; i++)
        {
            var auction = Auction.Open($"Lot {i}", start.AddHours(i + 1), start);
            for (var b = 1; b <= i % 3; b++)
                _ = auction.Bid(b * 10m);
            var appended = await store.Append(auction, 0, context, Option.None, default);
            if (appended.TryGetValue(out GlobalPosition position))
                last = position;
        }
        Check("Append returns the commit's position", last > GlobalPosition.Start);

        var readModels = new NpgsqlReadModels(db, json, schema, pushCheckpoints: true);           // the worker sends the checkpoint NOTIFY
        await using var pushed = new NpgsqlProjectionPositions(db, schema) { PollInterval = TimeSpan.FromHours(1) };   // another API process listens
        _ = await pushed.Checkpoint<AuctionRow>(default);
        for (var wait = 0; !pushed.IsListening && wait < 3000; wait++)
            await Task.Delay(10);
        await using var signal = new NpgsqlFeedSignal(db, store.FeedChannel);
        var host = new ProjectionHost<AuctionRow>(store, readModels.Store<AuctionRow>(), new ProjectionOptions { Batch = 10 }, signal: signal);
        await host.StartAsync(default);
        var api = new ProjectionPositions(new NpgsqlReadModels(db, json, schema));                  // the API process: no host, polls the row
        Check("read-your-writes from another process", await api.WaitFor<AuctionRow>(last, TimeSpan.FromSeconds(30), default));
        var more = Auction.Open("Woken", start.AddDays(3), start);
        var woken = await store.Append(more, 0, context, Option.None, default);
        Check("NotifyFeed wakes the host", woken.TryGetValue(out GlobalPosition wokenAt) && await host.WaitFor(wokenAt, TimeSpan.FromSeconds(30), default));
        Check("a pushed checkpoint wakes WaitFor in another process",
            pushed.IsListening && await pushed.WaitFor<AuctionRow>(wokenAt, TimeSpan.FromSeconds(30), default)
                               && (await pushed.Checkpoint<AuctionRow>(default)).Position >= wokenAt);
        await host.StopAsync(default);

        var reads = readModels.Store<AuctionRow>();
        var checkpoint = await reads.Checkpoint(default);
        Check("checkpoint stored and current", checkpoint.State == ProjectionState.Current && checkpoint.Position >= wokenAt);
        var query = new ListQuery(Guid.Empty) { Sort = "ends_at", Limit = 6 };
        var seen = new List<AuctionRow>();
        for (var q = query; ;)
        {
            var page = Ok(await reads.Search(q, default));
            seen.AddRange(page.Items);
            if (page.Next is null)
                break;
            q = q with { After = page.Next };
        }
        Check("cursor pages over the read table", seen.Count == 26 && seen.Select(r => r.EndsAt).SequenceEqual(seen.Select(r => r.EndsAt).Order()));
        var top = Ok(await reads.Search(new ListQuery(Guid.Empty) { Sort = "highest_bid", Descending = true, Limit = 3, Count = true }, default));
        Check("descending sort and count", top.Total == 26 && top.Items[0].HighestBid == 20m && top.ETag.StartsWith("W/", StringComparison.Ordinal));
        Check("a bad cursor is Invalid", (await reads.Search(query with { After = "x" }, default)).IsFailure);
        var scoped = Ok(await reads.Search(new ListQuery(Guid.Empty) { Count = true }.Within(ListScope.In("bidders", ["b1"])), default));
        var none = Ok(await reads.Search(new ListQuery(Guid.Empty) { Count = true }.Within(ListScope.None), default));
        var firstDay = seen[0].EndsOn;
        var days = Ok(await reads.Search(new ListQuery(Guid.Empty) { Sort = "ends_on", Descending = true, Filters = [new ColumnFilter("ends_on", firstDay)], Count = true }, default));
        Check("a DateOnly filter and sort, a TimeOnly column (TN-7)", days.Total > 0 && days.Items.All(r => r.EndsOn == firstDay)
                                                                   && script.Contains("\"ends_on\" date NOT NULL", StringComparison.Ordinal)
                                                                   && script.Contains("\"ends_at_time\" time NOT NULL", StringComparison.Ordinal));
        Check("set filter over a text[] column and an empty scope (TN-6)", scoped.Total > 0 && scoped.Items.All(r => r.Bidders.Contains("b1")) && none.Total == 0);

        var titles = new TitleCount(schema);
        var sql = new SqlProjectionHost(titles, store, readModels);
        await sql.CatchUp(default);
        await using (var count = db.CreateCommand($"SELECT count FROM {schema}.title_count"))
            Check("a SQL projection in the batch transaction", await count.ExecuteScalarAsync() is 26L);
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "Tarol.Queries.Npgsql AOT smoke: all checks passed" : $"Tarol.Queries.Npgsql AOT smoke: {failures} check(s) failed");
return failures == 0 ? 0 : 1;

internal sealed class TitleCount(string schema) : ISqlProjection
{
    public string Name => "title-count";

    public int Version => 1;

    public IReadOnlyCollection<string>? StreamTypes => ["auction"];

    public async ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {schema}.title_count (one integer PRIMARY KEY, count bigint NOT NULL)",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<int> Apply(IReadOnlyList<PositionedEvent> events, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {schema}.title_count (one, count) VALUES (1, $1)
            ON CONFLICT (one) DO UPDATE SET count = title_count.count + excluded.count
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = (long)events.Count(e => e.Event.Event.Event is Auction.Opened) });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
