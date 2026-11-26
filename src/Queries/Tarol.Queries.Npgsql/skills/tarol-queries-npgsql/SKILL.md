---
name: tarol-queries-npgsql
description: >-
  Use when storing Tarol projections in PostgreSQL: UseNpgsqlReadModels, the read tables rm_<name>_v<n> and the
  projections checkpoint table, NotifyFeed with the LISTEN wake-up, PushCheckpoints and NpgsqlProjectionPositions
  (pushed checkpoints for read-after and the page cache), rebuilds by Version and DropVersion, SQL projections
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
   migration tool. To change a table, raise `Version`; drop the old one later from a process that no longer uses it,
   on the registered instance: `provider.GetRequiredService<NpgsqlReadModels>().DropVersion("auction-list", 1, ct)`.
5. A list answers `Error.Unavailable` (`NpgsqlReadModelStore<TRow>.NotPreparedCode`) until a host prepared the table,
   and the checkpoint reports `ProjectionState.Building` until the projection reached the head of the feed.
6. `UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })` in **both** the worker and the API
   process: every batch sends `NOTIFY <schema>_checkpoint` in its transaction (delivered after the commit), and
   `IProjectionPositions` becomes `NpgsqlProjectionPositions` (one `LISTEN` connection, the checkpoints in memory, a 1 s
   poll and a poll after every reconnect as the fallback). `Checkpoint<TRow>` then costs no round trip and
   `WaitFor` (`Tarol-Read-After`) wakes on the notification instead of polling every 5 ms. `Tarol.Caching` needs it.
   `IsListening` tells a health check whether the connection is up.
7. **One runner per projection version** (step 15, on by default, `NpgsqlReadModelOptions.SingleRunner`): every host
   asks `NpgsqlProjectionGate` (a session `pg_try_advisory_lock`; all locks of a process share one connection); the others
   wait (`IsRunner` false) and take over within a second when the runner's session ends. Scale workers freely; the
   checkpoint guard stays. A process needs its pool plus about five listener/gate sessions (README "Connections").
8. **`DropVersion`** has a `lock_timeout` (`DropLockTimeout` 200 ms, `DropAttempts` 5) and throws `TimeoutException` while
   another session still reads the table; drop only after no pod of the old version runs (else readers fail with `42P01`).
9. **Lag**: the snapshot signal `tarol.projections.lag` (seconds, tag `tarol.projection`) and the meter `"Tarol.Queries"`
   (`tarol.projections.events`, `.batches`, `.batch.duration`, outcome written | refused). A data source the application
   provides to `UseNpgsqlStore(Func)` is registered as `NpgsqlDataSource` for the read models.
10. An `ISqlProjection` runs its statements on the `connection` and `transaction` it is given, keys its rows by tenant
   first, and creates its tables in `Prepare` with `CREATE TABLE IF NOT EXISTS`. Queryable rows: `ISqlProjection<TRow>` with
   `new NpgsqlRows<TRow>(json, schema)`; its `Prepare`/`Write`/`Remove` take the given connection and transaction.

## Example

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { NotifyFeed = true })
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })   // the same in the API process
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
| `PushCheckpoints` only in the API process | the worker sends nothing: `Checkpoint` moves only with the 1 s poll, cached lists lag up to 1 s |
| `PushCheckpoints` behind a pooler in transaction mode (PgBouncer) | `LISTEN` needs a session: the positions fall back to the 1 s poll; give the read models a direct data source |
| an `ISqlProjection` that opens its own connection | its writes commit apart from the checkpoint: no longer exactly once |
| `SingleRunner = false` with several workers | every host runs, the guard refuses all but one batch (75 % wasted with 4) |
| `DropVersion` while old pods still run | `TimeoutException` (they read it), or later `42P01` in those pods |
| your own SQL against a read table with a member `From`, `Order`, `Limit` | the host quotes every column (`"from"`); unquoted in your SQL it is `42601` — quote it too |
| a projection that stays building in a test or on start | `host.LastFailure` / `ProjectionHosts.Failing` name the cause (log 12004 when the table cannot be created) |
| `DateTimeOffset` parameters with an offset in your own SQL | Npgsql refuses them for `timestamptz`: pass `ToUniversalTime()` |

## AOT

No reflection: SQL text is built from the generated column map, rows go through your `JsonSerializerContext`. Use
`NpgsqlSlimDataSourceBuilder` with `EnableArrays()` (what `UseNpgsqlStore(connectionString)` builds).
