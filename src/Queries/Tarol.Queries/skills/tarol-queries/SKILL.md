---
name: tarol-queries
description: >-
  Use when adding a read model, list or search over a Tarol event-sourced application: a [Projection] row record with
  When(event) methods, its ProjectionHost (AddProjections), list endpoints ([Endpoint("GET …")] on an IQuery<TRow>
  record with filters, a Sort enum, [FromClaim], cursor, Tarol-Read-After), ListQuery pages with ETag, read-your-writes,
  ISqlProjection, and testing with ProjectionTest; fixing TQRY001-TQRY006. Do not query streams.state, read a projection
  inside a change, or decompile the package.
---
# Tarol.Queries: projections and list queries

## Rules

1. A projection is a `sealed partial record` under `[Projection("kebab-name", Version = n)]`. A **static**
   `When(TEvent e, RecordedEvent r)` (or `When(TEvent e)`) creates the stream's row; **instance** `When(TEvent e)` methods
   return the next row (`this with { … }`). All events come from **one** aggregate (entity events count as their root's).
   Events no `When` handles leave the row unchanged.
2. `When` methods are pure: no services, no clock, no I/O, no rules. Take times and ids from the event or `RecordedEvent`
   (`r.StreamId`, `r.RecordedAt`, `r.EventId`).
3. Every scalar member (`Guid`, `string`, `bool`, `short`, `int`, `long`, `decimal`, `double`, `DateTimeOffset`, enums,
   nullable ones) is a typed column named in snake case (`EndsAt` → `ends_at`); enums are stored as numbers, times in
   UTC to the microsecond. Members named `Id`, `Tenant`, `Version`, `Doc` and non-scalars live only in the row's JSON.
   `[Sort]` marks a non-nullable column as a sort (an index per sort).
4. Rows are per stream and keyed (tenant, stream id). For totals across streams write an `ISqlProjection`.
5. To change a row's shape or logic, raise `Version`: the new version builds from the start of the feed next to the
   old one (`ProjectionState.Building` until it is `Current`). Never alter or truncate a read table.
6. Host projections in the worker: `builder.UseInMemoryReadModels()` (or `UseNpgsqlReadModels()`) in every process that
   reads, `.AddProjections(p => p.Add<AuctionRow>())` in the process that projects.
7. Lists are always tenant-scoped: set `new ListQuery(tenant)` from the caller, never from the request. Row rules do not
   run on lists; filter by the caller instead (a seller column equal to the caller's subject).
8. Page with the cursor: pass `page.Next` as `After`. `Offset` is bounded (≤ 1,000) and repeats a row when one is
   inserted before the page. `Count = true` only when the UI shows a total.
9. A decision (a change's `Execute`) never reads a projection: it lags the commit. Read-your-writes is opt-in:
   `IProjectionPositions.WaitFor<TRow>(position, timeout, ct)`.

## Example

```csharp
public enum AuctionStatus { Open, Closed }

[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt,
    AuctionStatus Status, [Sort] decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r)
        => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0);
    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1 };
    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

// test without a database
var test = ProjectionTest<AuctionRow>.Create().Given(Auction.Open("ada", "Vase", endsAt, now), tenant);
await test.CatchUp();
var result = await test.Search(new ListQuery(tenant.GetValueOrThrow().Value)
{
    Filters = [new ColumnFilter("status", AuctionStatus.Open)],
    Sort = "ends_at",
    Limit = 20,
});
result.TryGetValue(out Page<AuctionRow>? page).ShouldBeTrue();   // page.Items, page.Next, page.ETag, page.Position
```

`ProjectionTest<TRow>.Over(changeTest.Store)` projects the events a `ChangeTest` committed.

## List endpoints (Tarol.Changes.AspNetCore)

```csharp
public enum AuctionSort { EndsAt, [Descending] HighestBid }

[Endpoint("GET /auctions", Policy = "auctions:read")]
public sealed record AuctionSearch(AuctionStatus? Status, string? Seller, AuctionSort Sort = AuctionSort.EndsAt) : IQuery<AuctionRow>;

[Endpoint("GET /my/auctions")]
public sealed record MyAuctions([FromClaim("sub")] string Seller) : IQuery<AuctionRow>;
```

* Filter members are **nullable** and named like a row member (`Status` → column `status`); `?status=Open` (enum names,
  case-insensitive). A member named `Sort` of an enum whose members name `[Sort]` members (or `Id`) is the order.
* `[FromClaim("sub")]` binds the caller's claim (403 without it); never add a request parameter for "my" rows.
* Paging keys: `limit` (≤ 200), `after` = the previous page's `next`, `offset` (≤ 1,000), `count=true`. Answer:
  `{ "items": […], "next": "…" | null, "total": n }`, weak `ETag` (304), `Tarol-Position`.
* Read your own write: send the change answer's `Tarol-Position` as `Tarol-Read-After`; after 2 s the list answers with
  `Tarol-Stale: true`. 503 + `Retry-After` while a new projection version builds.
* Grant the list with the role map: `roles.Role("bidder").May<Read<Auction>>()`. Row rules do not run on lists.
* Register the read models in the API process too (`UseNpgsqlReadModels()` / `UseInMemoryReadModels()`).

## Common mistakes

| Mistake | What happens |
|---|---|
| `[Projection]` on a non-partial record, a class, a generic type, `"AuctionList"` as the name | TQRY001 |
| only instance `When`s, or two static `When`s for one event | TQRY002 |
| a `When` for another aggregate's event, `When(e, int x)`, returning `void` | TQRY003 |
| `[Sort]` on `string?`, `int[]` or a member named `Version` | TQRY004 |
| a `When` that does not use `this` | CA1822 in your build: make it `static` only if it creates the row, otherwise use the row |
| `Sort = "EndsAt"` or a filter on a member without a column | `ArgumentException` (column names are snake case) |
| a cursor reused with another sort or filter, `Limit = 500`, `Offset = 5000` | `Invalid` with field `after`, `limit` or `offset` (HTTP 400) |
| reading `ProjectionHost.Position` in the API process | it is only this process's view: use `IProjectionPositions` |
| an `ISqlProjection` without `UseNpgsqlReadModels()` | `InvalidOperationException` when the host is resolved |
| a non-nullable filter, a member naming no column, a sort naming a non-`[Sort]` member | TQRY005 |
| `[FromClaim]` on a member the route also names | TQRY006 (warning: the claim wins) |
| `IQuery<TRow>` with POST | TWEB002 |
| a list answer 403 for a caller who may read single auctions by a row rule | lists need the role map's `Read<T>` |

## AOT

Everything is generated: no reflection on the projection path. The in-memory store keeps rows as objects; the Npgsql
store writes the row as `jsonb` and needs the row type in your `JsonSerializerContext`.
