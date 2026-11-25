---
name: tarol-queries
description: >-
  Use when adding a read model, list or search over a Tarol event-sourced application: a [Projection] row record with
  When(event) methods, its ProjectionHost (AddProjections), list endpoints ([Endpoint("GET …")] on an IQuery<TRow>
  record with filters, a Sort enum, [FromClaim], IListScope, cursor, Tarol-Read-After), ListQuery pages with ETag, read-your-writes,
  ISqlProjection, and testing with ProjectionTest; fixing TQRY001-TQRY006. Do not query streams.state, read a projection
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
* `?sort=HighestBid` names an enum member (case-insensitive); a member without `[Descending]` ascends. The default is
  the `Sort` member's default value.
* `[FromClaim("sub")]` binds the caller's claim (403 without it); never add a request parameter for "my" rows. The row's
  column must hold the same value (store the actor's id, e.g. `context.Operation.Actor.Id`, in the event).
* Paging keys: `limit` (default 50, ≤ 200), `after` = the previous page's `next`, `offset` (≤ 1,000), `count=true`. Answer:
  `{ "items": […], "next": "…" | null, "total": n }`, weak `ETag` (304), `Tarol-Position`.
* Read your own write: send the change answer's `Tarol-Position` (text `<transaction>.<position>`, e.g. `7341.1000412`;
  copy it as it is) as `Tarol-Read-After`; after 2 s the list answers with
  `Tarol-Stale: true`. 503 + `Retry-After` while a new projection version builds. A test or start-up step waits for all hosts of
  the process with `ProjectionHosts.WaitUntilCurrent(timeout, ct)` (`Building` names the rest) instead of retrying on 503;
  `p.ReadyWhenCurrent()` keeps readiness down until then. A host that keeps failing names why in `host.LastFailure`
  (`ProjectionHosts.Failing`, also in the readiness text).
* Grant the list with the role map: `roles.Role("bidder").May<Read<Auction>>()`. Row rules do not run on lists.
* Rows that depend on domain data ("a parent sees only their children's entries"): implement `IListScope<TQuery>` and
  mark the entry `RequiresScope = true` (startup fails without the registration):

```csharp
[Endpoint("GET /logs", RequiresScope = true)]
public sealed record Logs(Guid? Child) : IQuery<LogRow>;          // LogRow(Guid Id, Guid Child, Guid[] Tagged, …)

public interface IChildren                                         // your port: null = every child
{
    ValueTask<IReadOnlySet<Guid>?> Visible(ClaimsPrincipal user, DateTimeOffset now, CancellationToken ct);
}

public sealed class ChildScope(IChildren children) : IListScope<Logs>
{
    public async ValueTask<ListScope> Scope(ChangeContext context, ClaimsPrincipal principal, CancellationToken ct)
        => await children.Visible(principal, context.Now, ct) is { } visible
            ? ListScope.In(nameof(LogRow.Child), visible)           // = ANY(…); on Guid[] Tagged: overlap (&&)
            : ListScope.All;                                       // null: the owner sees every row
}
// services.AddScoped<IListScope<Logs>, ChildScope>();
```
  An empty set (or `ListScope.None`) answers an empty page, never an unfiltered one. `SetFilter.IdColumn` ("id") filters
  the row id. The scope binds the cursor (another scope's cursor is a 400) and the page cache key.
* Array members (`Guid[]`, `string[]`, `int[]`, `long[]`, `short[]`, an int enum's array, or `IReadOnlyList<T>` of them)
  are array columns: set filters only, never a sort or an equality filter.
* Only the row goes in your `JsonSerializerContext`; the query record is bound by generated code. An `ISqlProjection`'s
  tables are not served by list endpoints: read them behind your own port.
* Register the read models in the API process too (`UseNpgsqlReadModels()` / `UseInMemoryReadModels()`).

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
| a scope that returns `ListScope.All` when the caller's set is empty | the caller sees every row: return the empty set (or `ListScope.None`) |
| `RequiresScope = true` without the registration, or on a change | `InvalidOperationException` at `MapChanges`; TWEB002 |
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
