---
name: tarol-list-endpoints
description: >-
  Use when a Tarol projection is read over HTTP: [Endpoint("GET …")] on an IQuery<TRow> record (filters, a Sort enum,
  [FromClaim], paging with after/limit/offset/count, Tarol-Read-After, 503 while building), one row with IRowQuery<TRow>, rows
  scoped by domain data with IListScope<TQuery> and RequiresScope, and fixing TQRY005, TQRY006 and TWEB002 for queries. The
  projection itself is skill tarol-queries. Do not add a request parameter for "my" rows, and do not write a Minimal API
  endpoint that reads IReadModels.
---
# List and row endpoints (Tarol.Changes.AspNetCore)

A query record names a projection row (`IQuery<TRow>`: a page, `IRowQuery<TRow>`: one row) and carries `[Endpoint("GET …")]`; the
generator writes the endpoint, its access check (the role map's `Read<T>`), and its OpenAPI operation. Rows come from the
projection of skill `tarol-queries`.

## Rules and example


```csharp
public enum AuctionSort { EndsAt, [Descending] HighestBid }

[Endpoint("GET /auctions", Policy = "auctions:read")]
public sealed record AuctionSearch(AuctionStatus? Status, string? Seller, AuctionSort Sort = AuctionSort.EndsAt) : IQuery<AuctionRow>;

[Endpoint("GET /my/auctions")]
public sealed record MyAuctions([FromClaim("sub")] string Seller) : IQuery<AuctionRow>;
// one row (TN-54): the caller's tenant's row of a [PerTenant] aggregate, or by a route id; 404 X.NotFound, ETag = row version
[Endpoint("GET /house/row")] public sealed record TenantHouseRow : IRowQuery<HouseRow>;
[Endpoint("GET /auctions/{auction}/row")] public sealed record AuctionRowById(Guid Auction) : IRowQuery<AuctionRow>;
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

## Common mistakes

| Mistake | What happens |
|---|---|
| a scope that returns `ListScope.All` when the caller's set is empty | the caller sees every row: return the empty set (or `ListScope.None`) |
| `RequiresScope = true` without the registration, or on a change | `InvalidOperationException` at `MapChanges`; TWEB002 |
| a non-nullable filter, a member naming no column, a sort naming a non-`[Sort]` member | TQRY005 |
| `[FromClaim]` on a member the route also names | TQRY006 (warning: the claim wins) |
| `IQuery<TRow>` with POST | TWEB002 |
| a list answer 403 for a caller who may read single auctions by a row rule | lists need the role map's `Read<T>` |
| a list of one row and `items[0]` in the client | `IRowQuery<TRow>`: one row, 404 when missing, `ETag` = the row's version |

## AOT

The endpoints are generated; the row goes through your `JsonSerializerContext`, the query record is bound by generated code.
