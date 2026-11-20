# Tarol.Queries

Projections and read models for [Tarol](https://github.com/CLorat/Tarol-Net): a row record with `When(event)` methods
under `[Projection(name, Version)]` gets a generated switch and column map; a hosted `ProjectionHost` per projection reads
the store's event feed after its checkpoint and writes the rows and the checkpoint in one guarded batch, idempotent by
stream version; list queries with equality filters, whitelisted sorts, an opaque cursor, a bounded offset and a page ETag;
read-your-writes waits; an in-memory read store and `ProjectionTest`. Native-AOT ready.

The PostgreSQL read tables are in `Tarol.Queries.Npgsql`; the GET endpoints over query records in `Tarol.Changes.AspNetCore`.

## Install

```bash
dotnet add package Tarol.Queries          # brings Tarol.Persistence; the generator is bundled
```

## 30 seconds

```csharp
[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt,
    AuctionStatus Status, [Sort] decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r)                 // creates the stream's row
        => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0);
    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1 };
    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

// the worker process hosts the projection; every process that reads registers the read store
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseInMemoryStore()
    .UseInMemoryReadModels()                                                          // UseNpgsqlReadModels() in production
    .AddProjections(p => p.Add<AuctionRow>());

// a page: tenant first, equality filters, a [Sort] column, the cursor of the previous page
var page = await readModels.Store<AuctionRow>().Search(new ListQuery(tenant)
{
    Filters = [new ColumnFilter("status", AuctionStatus.Open)],
    Sort = "ends_at",
    After = previous?.Next,
}, ct);
```

## What runs

| Piece | What it does |
|---|---|
| generator | implements `IProjection<TRow>` explicitly: `Name`, `Version`, `StreamType` (of the one aggregate the `When`s read), `Columns` (every scalar member except `Id`/`Tenant`/`Version`/`Doc`, in snake case), `Apply` (static `When` without a row, instance `When` with one, otherwise the same row) |
| `ProjectionHost<TRow>` | per batch (default 2,000 events): the feed after the checkpoint, the events of its stream type, the stored rows of their streams, `Apply` per event skipping versions ≤ the row's, then one `Write`: checkpoint moved from the position it read (guarded: a second runner is refused and re-reads), rows upserted. `Building` until it reaches the feed's head, then `Current`. Backoff on errors, a span `project <name>` of `Tarol.Queries`, logs 12001–12003 |
| `ISqlProjection` / `SqlProjectionHost` | your own SQL in the batch transaction (cross-stream tables), on a store that implements `ISqlProjectionStore` |
| `ListQuery` / `Page<T>` / `Cursor` | tenant always, equality filters (a `null` value: `IS NULL`), one sort (a `[Sort]` column or the id) with the id as tie-break, `Limit` 1–200, `After` (cursor bound to sort and filters) or `Offset` ≤ 1,000, `Count` opt-in; the page has `Next`, a weak `ETag` over (id, version), `Next` and `Total`, the checkpoint `Position` |
| `IProjectionPositions` | `WaitFor<TRow>(position, timeout)`: the host's signal in its process, the stored checkpoint polled elsewhere |
| `InMemoryReadModels`, `ProjectionTest<TRow>` | the same semantics in memory (ordinal text, uuids in PostgreSQL order) for tests |

## Rules

* A projection never decides and never reads another aggregate: `When` methods are pure functions of the row and the
  event. A decision never reads a projection (it lags the commit).
* Rows are per stream, keyed (tenant, stream id). Totals across streams are an `ISqlProjection`.
* Changing the row or its logic means `Version + 1`: the new version builds from the start next to the old one.
* Lists are tenant-scoped; row rules (`ChangeHandler<,>`) do **not** run on lists.

## Diagnostics

| Id | When |
|---|---|
| TQRY001 | `[Projection]` on a type that is not a non-generic `partial record` class (containing types partial), a bad name or version |
| TQRY002 | no static `When` creating the row, or two `When`s (of one kind) for one event |
| TQRY003 | a `When` that takes no event of the projection's one aggregate, a second parameter other than `RecordedEvent`, or returns another type |
| TQRY004 | `[Sort]` on a member without a non-nullable column |

## Operations (step 15)

Meter `"Tarol.Queries"`: `tarol.projections.events`, `tarol.projections.batches` (outcome written | refused),
`tarol.projections.batch.duration`. `IProjectionGate` lets one host of a projection version run (`IsRunner`); the
PostgreSQL read stores provide one.

* **Personal data (step 16)**: `IReadModelStore.Replace`/`Remove` and `ProjectionEraser<TRow>`, the erasure participant `AddProjections` registers for every projection: it re-folds an erased subject's streams without moving the checkpoint.

## Links

* Concept: `docs/architecture/queries.md`; decisions QR-1 … QR-7: `docs/migration/queries.md`.
* Agent skill: `skills/tarol-queries/SKILL.md` (in this package).
