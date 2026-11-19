# Tarol.Changes.AspNetCore

HTTP entries for Tarol changes. Put `[Endpoint]` on a change record (or on a view) and the generator writes a
`RequestDelegate` for it; `app.MapChanges()` maps them all. Native-AOT ready (the Request Delegate Generator does not see
generated code, so the delegates bind by themselves).

```csharp
[Endpoint("POST /auctions/{auction}/bids", Policy = "auctions:bid")]
public sealed partial record PlaceBid(Id<Auction> Auction, Amount Amount) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.PlaceBid(context.Operation.Actor.Id, Amount, context.Now).ToResult();
}

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddSecurity(roles => roles.Role("bidder").May<PlaceBid>())
    .AddEndpoints();
app.MapChanges();
```

| Request | Answer |
|---|---|
| a change committed | 204, `ETag: "<version>"`, `Tarol-Position: <tx>.<pos>` (the commit's place in the feed) |
| a creation committed | 201, `Location`, `{"id":…,"version":…}`, `ETag` |
| the same `Idempotency-Key` again | the first outcome (200 for a creation), `Idempotent-Replayed: true`; another body → 422 |
| invalid input (binder or value rules) | 400 problem details, `errors` keyed by JSON paths, every field at once |
| `If-Match` stale | 409 `Stream.VersionConflict` |
| not authenticated / endpoint policy | 401 / 403 |
| refused by the change's authorization (Tarol.Security) | 403 with the rule's `code` |
| missing, or another tenant's | 404 |
| a guard or lifecycle step | 409 with the guard's `code` |
| not JSON (also a body without `Content-Type`) / too large | 415 / 413 |

* **Binding**: route values bind the members of the same name (`Id<T>`, `Guid`, `string`, `int`, `long`); body members are
  read one by one through the app's JSON context (value objects included), `[JsonPropertyName]` and defaults honoured.
* **Context**: actor from `sub` / `client_id` (or the app's `ChangeEndpointOptions.Actor`), tenant from the `tenant` claim
  (or `ChangeEndpointOptions.Tenant`; a `{tenant}` route value must equal it), a new correlation, the principal for the
  authorization. Authentication itself is the app's.
* **Views**: `[Endpoint("GET /auctions/{auction}")]` on a record implementing `IView<Auction, TSelf>` (`static TSelf
  From(Auction)`): load, `Read<Auction>` authorization, `ETag`, 304 on `If-None-Match`
  (RFC 9110 weak comparison, a list of tags or `*`: the same matcher for views, lists and search).
* **Lists** (step 12): `[Endpoint("GET /auctions", Policy = "auctions:read")]` on a record implementing `IQuery<TRow>`
  over a `[Projection]` row (`Tarol.Queries`): nullable members named like a row column are equality filters from the query
  string (`?status=Open`), a `Sort` enum member names `[Sort]` columns (`[Descending]`; the id breaks ties), route members
  filter too, `[FromClaim("sub")]` members come from the caller only. Paging: `limit` (≤ 200), `after` (the opaque cursor of
  the previous page's `next`), `offset` (≤ 1,000), `count=true`. The answer is `{"items":[…],"next":…,"total":…}` with a weak
  `ETag` (304 on `If-None-Match`) and `Tarol-Position`; `Tarol-Read-After: <position>` waits up to 2 s for the projection,
  then answers with `Tarol-Stale: true`; 503 + `Retry-After` while a projection version builds. A list needs the role map's
  `Read<TAggregate>`, is always tenant-scoped, and runs no row rules. Limits in `ChangeEndpointOptions.Queries`.
  Diagnostics TQRY005–TQRY006.
* **Cache headers** (step 13): list answers (200, 304, problems) and view answers (200, 304) carry
  `Cache-Control: private, no-cache` and `Vary: Authorization` (`CacheHeaders`). No output caching; for fast lists,
  `Tarol.Caching`'s `AddCaching()` caches the pages behind `CheckList` and `Tarol-Read-After`.
* **Webhooks**: `[Webhook("payments", "payment.succeeded", Version = "2")]` on a `static From(Payload)` factory; served at
  `POST /webhooks/payments` (and `/webhooks/payments/{tenant}`); HMAC-SHA256 over `timestamp.body`, ±5 minutes, secrets from
  `WebhookOptions` (rotation), delivery-id key; 200 / 202 (unknown type) / 401 / 422 / 503 (retry).
* **OpenAPI**: with `Microsoft.AspNetCore.OpenApi`, `AddOpenApi(o => o.AddTarolEndpoints())` describes every entry
  (path and header parameters, 2xx with `ETag`, problem responses, value-object rules).
* **Problem details** through `IProblemDetailsService` when the app registered one, else written directly.
* **Browsers on another origin** (CORS): `TarolHeaders.Allowed` are the request headers a browser client sends,
  `TarolHeaders.Exposed` the response headers it must read (`ETag`, `Location`, `Tarol-Position`, `Tarol-Stale`,
  `Idempotent-Replayed`, `Retry-After`, `WWW-Authenticate`); without them a script cannot revalidate or read its own writes.
  The recipe (origins and credentials stay yours; `UseCors` before `UseAuthentication`, a preflight carries no token):

  ```csharp
  builder.Services.AddCors(cors => cors.AddPolicy("browser", policy => policy
      .WithOrigins("https://app.example.com").AllowAnyMethod().WithTarolHeaders()));
  var app = builder.Build();
  app.UseCors("browser");
  app.UseAuthentication();
  app.UseAuthorization();
  ```
* Fails closed: an entry without `Policy` needs an authenticated caller, the change is always authorized, `MapChanges`
  refuses to start without `AddSecurity`. Diagnostics TWEB001–TWEB006.

Guide: [docs/changes/README.md](../../../docs/changes/README.md) (HTTP entries). Design: `docs/architecture/rest.md`.
