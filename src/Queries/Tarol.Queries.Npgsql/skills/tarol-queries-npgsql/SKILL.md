---
name: tarol-queries-npgsql
description: >-
  Use when storing Tarol projections in PostgreSQL: UseNpgsqlReadModels, the read tables rm_<name>_v<n> and the
  projections checkpoint table, NotifyFeed with the LISTEN wake-up, rebuilds by Version and DropVersion, SQL projections
  (ISqlProjection) on Npgsql, and the integration test setup. Do not write DDL for read tables by hand, alter or truncate
  them, or poll the events table yourself.
---
# Tarol.Queries.Npgsql: read tables on PostgreSQL

## Rules

1. Register `UseNpgsqlReadModels()` after `UseNpgsqlStore(…)` in every process that reads or hosts projections; it takes
   the store's data source and schema. Host projections only in the worker: `.AddProjections(p => p.Add<AuctionRow>())`.
2. The row type must be in the application's `JsonSerializerContext` (`[JsonSerializable(typeof(AuctionRow))]`): the
   row is stored as `jsonb` and read back from it. A missing entry throws `InvalidOperationException` naming the attribute.
3. Turn on `NpgsqlStoreOptions { NotifyFeed = true }` for millisecond lag (p50 3.6 ms); the read models then `LISTEN`
   by themselves. Without it the hosts poll every 100 ms. Keep the poll: an open transaction anywhere hides later
   commits from the feed until it ends, after their notification.
4. The tables are generated from the row record: `NpgsqlReadModelSchema.Script<TRow>(schema)` gives the script for a
   migration tool. To change a table, raise `Version`; drop the old one later with
   `NpgsqlReadModels.DropVersion(name, oldVersion, ct)` from a process that no longer uses it.
5. A list answers `Error.Unavailable` (`NpgsqlReadModelStore<TRow>.NotPreparedCode`) until a host prepared the table,
   and the checkpoint reports `ProjectionState.Building` until the projection reached the head of the feed.
6. An `ISqlProjection` runs its statements on the `connection` and `transaction` it is given, keys its rows by tenant
   first, and creates its tables in `Prepare` with `CREATE TABLE IF NOT EXISTS`.

## Example

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { NotifyFeed = true })
    .UseNpgsqlReadModels()
    .AddProjections(p => p.Add<AuctionRow>().Add(new SellerTotals("tarol")));

public sealed class SellerTotals(string schema) : ISqlProjection
{
    public string Name => "seller-totals";
    public int Version => 1;
    public IReadOnlyCollection<string>? StreamTypes => ["auction"];

    public async ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {schema}.seller_totals (tenant uuid, seller text, auctions integer, PRIMARY KEY (tenant, seller))",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<int> Apply(IReadOnlyList<PositionedEvent> events, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var opened = events.Where(e => e.Event.Event.Event is Auction.Opened).ToList();
        if (opened.Count == 0)
            return 0;
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {schema}.seller_totals AS t (tenant, seller, auctions)
            SELECT tenant, seller, count(*) FROM unnest($1::uuid[], $2::text[]) AS u(tenant, seller) GROUP BY tenant, seller
            ON CONFLICT (tenant, seller) DO UPDATE SET auctions = t.auctions + excluded.auctions
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = opened.Select(e => e.Event.Context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = opened.Select(e => ((Auction.Opened)e.Event.Event.Event).Seller).ToArray() });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

## Common mistakes

| Mistake | What happens |
|---|---|
| `ALTER TABLE rm_auction_list_v1 …` after adding a member | the host writes the generated column list: missing column, `42703`; raise `Version` instead |
| a test that calls `CatchUp()` once and asserts the newest rows on a shared database | an older transaction of another test hides commits: catch up until `host.Position >= ` the position `Append` returned |
| `Listen = true` without `NotifyFeed` on the store | nothing is ever notified; the 1 s fallback poll is all that wakes the host |
| an `ISqlProjection` that opens its own connection | its writes commit apart from the checkpoint: no longer exactly once |
| `DateTimeOffset` parameters with an offset in your own SQL | Npgsql refuses them for `timestamptz`: pass `ToUniversalTime()` |

## AOT

No reflection: SQL text is built from the generated column map, rows go through your `JsonSerializerContext`. Use
`NpgsqlSlimDataSourceBuilder` with `EnableArrays()` (what `UseNpgsqlStore(connectionString)` builds).
