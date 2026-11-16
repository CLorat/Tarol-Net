# Tarol.Queries.Npgsql

PostgreSQL read tables for [Tarol](https://github.com/CLorat/Tarol-Net) projections on plain Npgsql: a table per projection
version with typed columns from the generated column map and the row as `jsonb`, the tenant first in every key and index,
an index per sort, the checkpoint moved first and guarded in the same transaction as the rows, the building/current state
of a rebuild, list queries over the columns, SQL projections in the batch transaction, and the `LISTEN` wake-up on the
store's feed `NOTIFY`. Native-AOT ready.

## Install

```bash
dotnet add package Tarol.Queries.Npgsql     # brings Tarol.Queries and Tarol.Persistence.Npgsql
```

## 30 seconds

```csharp
// every process that reads or hosts projections
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { NotifyFeed = true })   // wake projections at each commit
    .UseNpgsqlReadModels()                                                             // the store's data source and schema
    .AddProjections(p => p.Add<AuctionRow>());                                         // only in the worker

[JsonSerializable(typeof(AuctionRow))]                                                 // the row is stored as jsonb
public sealed partial class AppJson : JsonSerializerContext;
```

## What it stores

```sql
CREATE TABLE tarol.projections (name text, version integer, tx xid8, pos bigint, state text, updated_at timestamptz,
                                PRIMARY KEY (name, version));                -- the checkpoint, 'building' / 'current'
CREATE TABLE tarol.rm_auction_list_v1 (tenant uuid, id uuid, version bigint, title text COLLATE "C", …, ends_at timestamptz,
                                       status integer, highest_bid numeric, doc jsonb, PRIMARY KEY (tenant, id));
CREATE INDEX rm_auction_list_v1_s3 ON tarol.rm_auction_list_v1 (tenant, ends_at, id);       -- one per [Sort]
```

`NpgsqlReadModelSchema.Script<TRow>(schema)` returns the script (idempotent) for a migration tool; the host runs it under an
advisory lock when it starts. Text columns are `COLLATE "C"` so pages sort as in memory.

| Piece | Behaviour |
|---|---|
| `NpgsqlReadModelStore<TRow>` | `Write`: one transaction, first `UPDATE projections … WHERE tx = $from AND pos = $from` (a second runner waits on the row lock, finds 0 rows and re-reads), then one `INSERT … SELECT unnest(…) ON CONFLICT DO UPDATE … WHERE t.version < excluded.version`; `Search`: the checkpoint, the page (`(sort, id) > ($v, $id)` for the cursor) and the count in one round trip; before any host prepared the table, `Checkpoint` is `Building` at the start and `Search` is `Error.Unavailable("Projection.NotPrepared")` |
| `NpgsqlReadModels` | `IReadModels` and `ISqlProjectionStore` (SQL projections: checkpoint guard, then your `Apply` in the same transaction); `DropVersion(name, version)` drops an old version's table and checkpoint (never automatic; refused for a version this process uses) |
| `NpgsqlFeedSignal` | one `LISTEN <schema>_feed` connection, reconnected after a failure; the hosts keep a 1 s poll because the feed shows a commit only after every older transaction ended |
| `NpgsqlProjectionPositions` | with `PushCheckpoints`: every batch (row and SQL projections) sends `NOTIFY <schema>_checkpoint, '<name>:<version>:<tx>:<pos>:<state>'` in its transaction after the guarded update (delivered after the commit); this `IProjectionPositions` holds one `LISTEN` connection per process, answers `Checkpoint` from memory and wakes `WaitFor` on the notification; it polls every checkpoint every 1 s and after a reconnect (backoff 250 ms … 10 s) |
| `UseNpgsqlReadModels(options, dataSource)` | registers the above and `IProjectionPositions` (`NpgsqlProjectionPositions` with `PushCheckpoints = true`, set in the worker and the API process); `Schema` and `Listen` default to the store's |

## Rebuild

Raise `Version` on the row record and deploy: the host builds `rm_<name>_v<n+1>` from the start of the feed while the old
version's table keeps serving the old code; the new version reports `Building` until it reaches the head. Drop the old one
with `DropVersion` once no running code reads it. Never `ALTER` or `TRUNCATE` a read table.

## Links

* Concept: `docs/architecture/queries.md`; decisions and measurements: `docs/migration/queries.md`.
* Agent skill: `skills/tarol-queries-npgsql/SKILL.md` (in this package).
