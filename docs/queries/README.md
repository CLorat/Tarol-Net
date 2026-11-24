# Queries and read models

User guide for `Tarol.Queries`, `Tarol.Queries.Npgsql` and the list entries of `Tarol.Changes.AspNetCore`. Concept:
[architecture/queries.md](../architecture/queries.md); decisions and measurements: [migration/queries.md](../migration/queries.md)
(QR-1 … QR-7, implementation §15–§18). The runnable example is the auction list of [`samples/Sagas`](../../samples/Sagas).

## Design in six points

1. **A projection is a row record.** `[Projection(name, Version)]` on a `sealed partial record` with one static `When`
   that creates a stream's row and instance `When`s that return the next row. The generator writes the switch and the
   column map; there is no reflection and no hand-written mapping.
2. **One host per projection reads the feed.** A `ProjectionHost` in the worker process reads the store's global feed
   after its checkpoint and writes the changed rows and the checkpoint in one transaction. The checkpoint moves first,
   guarded by the position the batch read from, so a second runner is refused and re-reads; events at or below a row's
   version are skipped, so a replay changes nothing.
3. **A table per projection version.** Typed columns (filters, sorts) next to the row as `jsonb` (what the API returns),
   the tenant first in every key and index. A new `Version` is the rebuild: it is built from the start next to the old one,
   and lists answer 503 until it is current. Old versions are dropped by an explicit `DropVersion`.
4. **A list is a query record.** `[Endpoint("GET …")]` on an `IQuery<TRow>` record: nullable members are equality filters,
   a `Sort` enum whitelists the order, the cursor pages, the offset is bounded. Nothing from the request becomes SQL text.
5. **Lists lag the commit and say by how much.** Every change answer carries `Tarol-Position`; every list answer carries
   the projection's position. A client that needs its own write sends `Tarol-Read-After`; after 2 s it gets the list with
   `Tarol-Stale: true`. A decision never reads a projection.
6. **Who sees which rows.** A list needs the role map's `Read<TAggregate>` and is always tenant-scoped; "my rows" is a
   `[FromClaim("sub")]` member. Row rules authorize one loaded aggregate and do **not** run on lists; rows that depend on
   domain data (a parent's children, a substitute's children for two weeks) come from an `IListScope<TQuery>` (TN-6): set
   filters over a column, an array column or the row id; an empty set is an empty page.

## Packages

| Package | Step 12 parts |
|---|---|
| `Tarol.Queries` | `[Projection]`, `[Sort]`, `IProjection<TRow>`, `ProjectionHost`, `ISqlProjection`, `ListQuery`, `Page<T>`, `Cursor`, `IProjectionPositions`, the in-memory read store, `ProjectionTest` (TQRY001–TQRY004) |
| `Tarol.Queries.Npgsql` | read tables, the `projections` checkpoint table, the guarded batch, SQL projections, `DropVersion`, `NpgsqlFeedSignal` |
| `Tarol.Persistence(.Npgsql)` | `Append` returns the commit's `GlobalPosition`; `NpgsqlStoreOptions.NotifyFeed` |
| `Tarol.Changes(.AspNetCore)` | `ChangeOutcome.Position`, `Tarol-Position`; list entries on `IQuery<TRow>`, `[FromClaim]`, `Tarol-Read-After`, page ETag/304, 503 while building, OpenAPI (TQRY005–TQRY006) |
| `Tarol.Security` | `CheckList<T>`: the role map's `Read<T>` without a resource |
| `Tarol.Caching` (step 13) | cached list pages in the API process, valid while the pushed checkpoint stands: [caching.md](caching.md) |

## Guide: a list from scratch

### 1. The row

```csharp
[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Seller, decimal Reserve, [Sort] decimal HighestBid, int Bids, bool Settled,
    decimal Paid, [Sort] DateTimeOffset OpenedAt)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Seller, e.Reserve, 0m, 0, false, 0m, r.RecordedAt);
    public AuctionRow When(Bid.Placed e) => this with { HighestBid = Math.Max(HighestBid, e.Amount), Bids = Bids + 1 };
    public AuctionRow When(Auction.Settled e) => this with { Settled = true };
    public AuctionRow When(Auction.PaymentRecorded e) => this with { Paid = Paid + e.Amount };
}
```

Columns are the scalar members in snake case (`HighestBid` → `highest_bid`; `Id`, `Tenant`, `Version` and `Doc` are the
key). Entity events count as their root's (`Bid.Placed` is an `Auction` event). Add the row to your `JsonSerializerContext`.

### 2. Test it

```csharp
var test = ProjectionTest<AuctionRow>.Create().Given(auction, tenant);   // or ProjectionTest<AuctionRow>.Over(changeTest.Store)
await test.CatchUp();
test.Row(auction.Id.Value, tenant)!.Bids.ShouldBe(1);
var page = await test.Search(new ListQuery(tenantId) { Sort = "highest_bid", Descending = true, Limit = 20 });
```

### 3. Host it in the worker, read it in the API

```csharp
// worker
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { NotifyFeed = true })
    .UseNpgsqlReadModels()
    .AddProjections(p => p.Add<AuctionRow>());

// API
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .UseNpgsqlReadModels()
    .AddSecurity(roles => roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>())
    .AddEndpoints();
```

### 4. The list entries

```csharp
public enum AuctionSort { [Descending] HighestBid, [Descending] OpenedAt }

[Endpoint("GET /auctions")]
public sealed record AuctionSearch(bool? Settled, string? Seller, AuctionSort Sort = AuctionSort.HighestBid) : IQuery<AuctionRow>;

[Endpoint("GET /my/auctions")]
public sealed record MyAuctions([FromClaim("sub")] string Seller, bool? Settled = null) : IQuery<AuctionRow>;
```

### 5. The client

```http
POST /auctions/0196…/bids            → 204, Tarol-Position: 7341.1000412
GET /auctions?settled=false&sort=OpenedAt&limit=20
Tarol-Read-After: 7341.1000412       → 200 {"items":[…],"next":"MXxv…"}, ETag: W/"…", Tarol-Position: 7341.1000415
GET /auctions?settled=false&sort=OpenedAt&limit=20&after=MXxv…     → the next page
GET /auctions?…  If-None-Match: W/"…"                              → 304 while the page's rows are unchanged
```

| Request | Answer |
|---|---|
| a filter value that does not parse, a key twice, an unknown `sort` | 400, `errors` per parameter, all at once |
| `limit` > 200, `offset` > 1,000, `offset` with `after`, a cursor of another sort or filter | 400 on that parameter |
| `Tarol-Read-After` not passed within 2 s | 200 with `Tarol-Stale: true` |
| the projection version is still building (or not prepared yet) | 503 with `Retry-After` |
| no `Read<TAggregate>` in the role map / anonymous | 403 / 401 |
| a `[FromClaim]` claim missing | 403 `Auth.Claim` |

### 6. Change a list

Add the member and raise `Version` to 2. The worker builds `rm_auction_list_v2` from the start while the old code keeps
reading v1; the new API answers 503 until v2 is current. Once no running code reads v1:
`await readModels.DropVersion("auction-list", 1, ct)`.

### 7. Totals across streams

An `ISqlProjection` gets each batch's events and the batch transaction; it writes its own tables (keyed by tenant first)
on the connection it is given, so its rows and the checkpoint commit together. See the skill `tarol-queries-npgsql`.

## What not to do

* Do not query `streams.state` for lists: its paths are the aggregate's internals (enums as names, a wrong path is
  silently empty), and it is not an API (R-8).
* Do not read a projection inside a change's `Execute`: it lags the commit. Load the aggregate.
* Do not page deep with `offset`: it is slow (3.2 s at 900,000) and repeats a row when one is inserted before the page.
* Do not test with one `CatchUp()` on a shared database: an open transaction elsewhere hides newer commits from the feed;
  wait for the position `Append` returned.

## Measurements

Catch-up 133k events/s (batch 2,000); lag with the feed NOTIFY p50 3.6 / p99 6.1 ms; a page of 50 at depth 900,000:
offset 3.2 s, cursor 0.6 ms; total count 62 ms at 1e6 rows ([migration/queries.md](../migration/queries.md) §4).
