---
name: tarol-queries
description: >-
  Use when adding a read model over a Tarol event-sourced application: a [Projection] row record with When(event) methods (and
  Empty(RecordedEvent) for a stream whose first event varies), its ProjectionHost (AddProjections, ProjectionHosts,
  ReadyWhenCurrent), ListQuery pages with ETag, ISqlProjection, personal data in rows, and testing with ProjectionTest; fixing
  TQRY001-TQRY004. HTTP lists and one-row reads are skill tarol-list-endpoints. Do not query streams.state, read a projection
  inside a change, or decompile the package.
---
# Tarol.Queries: projections and list queries

## Rules

1. A projection is a `sealed partial record` under `[Projection("kebab-name", Version = n)]`. A **static**
   `When(TEvent e, RecordedEvent r)` (or `When(TEvent e)`) creates the stream's row; **instance** `When(TEvent e)` (or
   `When(TEvent e, RecordedEvent r)`) methods return the next row (`this with { … }`). An entity's event names its entity in
   `r.Event.EntityId` and its parent in `r.Event.ParentId` (`r.StreamId` is the root's). All events come from **one** aggregate (entity events count as their root's).
   Events no `When` handles leave the row unchanged. When the first event varies, `public static TRow Empty(RecordedEvent r)`
   starts the row for every instance `When` (no static `When` needed; TN-54).
2. `When` methods are pure: no services, no clock, no I/O, no rules. Take times and ids from the event or `RecordedEvent`
   (`r.StreamId`, `r.RecordedAt`, `r.EventId`).
3. Every scalar member (`Guid`, `string`, `bool`, `short`, `int`, `long`, `decimal`, `double`, `DateTimeOffset`,
   `DateOnly`, `TimeOnly`, enums, nullable ones) is a typed column named in snake case (`EndsAt` → `ends_at`); enums are
   stored as numbers, times in UTC to the microsecond, `DateOnly` as `date`, `TimeOnly` as `time` (to the microsecond).
   A calendar day (attendance, a closing day, `ValidUntil`) is a `DateOnly`, never a `DateTimeOffset` at midnight.
   Query string: `?day=2026-11-24` (`yyyy-MM-dd` only), `?at=08:15` or `08:15:30` (no zone); other forms are `Format`. Members named `Id`, `Tenant`, `Version`, `Doc` and non-scalars live only in the row's JSON.
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

## Endpoints

Lists, one-row reads, filters, `IListScope`, paging and `Tarol-Read-After` over HTTP: skill `tarol-list-endpoints`
(`Tarol.Changes.AspNetCore`). An `ISqlProjection`'s tables are not served by those endpoints: read them behind your own port.

## Personal data (step 16)

* A projection copies personal fields into its rows. `AddProjections(p => p.Add<TRow>())` also registers
  `ProjectionEraser<TRow>`: on an erasure it re-folds the subject's streams (the fields now read as `null`) and replaces
  the rows with `IReadModelStore.Replace`, without moving the checkpoint. Retention removes rows with `Remove`.
* Never rebuild a projection (a new `Version`) to forget one person. A rebuild replays the whole feed; the re-fold reads
  only the subject's streams.
* A personal row member stays nullable. It cannot be `[Sort]`: sort columns are non-nullable (TQRY004).
* An `ISqlProjection` (your own SQL tables) has no automatic participant: register your own `ErasureParticipant`.

## A host of your own (step 18)
A package that feeds something other than a read table (search) derives from `ProjectionHost` and implements `Prepare`,
`LoadCheckpoint` and `Write(events, from, through, state, ct)`; the loop, the guard, the gate, the meters, `CatchUp` and `WaitFor`
come with it. `Write` returns `(false, 0)` when another runner moved the checkpoint (the host reads again). Applications use
`[Projection]` or `ISqlProjection` instead.

## Common mistakes

| Mistake | What happens |
|---|---|
| `[Projection]` on a non-partial record, a class, a generic type, `"AuctionList"` as the name | TQRY001 |
| only instance `When`s, or two static `When`s for one event | TQRY002 |
| a `When` for another aggregate's event, `When(e, int x)`, returning `void` | TQRY003 |
| `[Sort]` on `string?`, `int[]` or a member named `Version` | TQRY004 |
| `new ColumnFilter("tagged", id)` on an array column, a `null` in a set | `ArgumentException` (use a `SetFilter`; nulls need a `ColumnFilter`) |
| a `When` that does not use `this` | CA1822 in your build: make it `static` only if it creates the row, otherwise use the row |
| `Sort = "EndsAt"` or a filter on a member without a column | `ArgumentException` (column names are snake case) |
| a cursor reused with another sort or filter, `Limit = 500`, `Offset = 5000` | `Invalid` with field `after`, `limit` or `offset` (HTTP 400) |
| reading `ProjectionHost.Position` in the API process | it is only this process's view: use `IProjectionPositions` |
| an `ISqlProjection` without `UseNpgsqlReadModels()` | `InvalidOperationException` when the host is resolved |

## AOT

Everything is generated: no reflection on the projection path. The in-memory store keeps rows as objects; the Npgsql
store writes the row as `jsonb` and needs the row type in your `JsonSerializerContext`.
