---
name: tarol-endpoints
description: >-
  Use when exposing Tarol changes over HTTP: [Endpoint] on a change or an IView<TAggregate, TSelf> (GET by id), the
  generated MapChanges(), AddEndpoints, ChangeEndpointOptions (claims, actor and tenant hooks), Idempotency-Key, If-Match
  and ETag, problem details, [Webhook] factories with WebhookOptions, and OpenAPI (AddTarolEndpoints); fixing TWEB001-TWEB006.
  Do not hand-write MapPost lambdas per change, do not decompile the generator, do not web-search ASP.NET Core binding.
---
# Tarol.Changes.AspNetCore: HTTP entries

## Rules

1. **An endpoint is an attribute on the change**: `[Endpoint("POST /auctions/{auction}/bids", Policy = "auctions:bid")]`
   on the `partial record`. Route values bind the members of the same name (`Id<T>`, `Guid`, `string`, `int`, `long`); the
   others come from the JSON body, every missing (`Required`) or mistyped (`Json`) member reported at once. List the record
   in the app's `JsonSerializerContext`.
2. **Register once, map once**: `AddTarol(…).UseNpgsqlStore(…).AddSecurity(roles => …).AddEndpoints()`, then
   `app.MapChanges()` (a route group: `app.MapGroup("/v1").MapChanges(names: "V1")`). `AddSecurity` is required.
3. **Two checks**: the endpoint `Policy` (or the default policy: authenticated) and the change's authorization (skill
   `tarol-security`: role map + row rules). `Anonymous = true` skips only the first.
4. **Authentication is the app's** (JwtBearer, …). The edge reads `sub`, `client_id`, `tenant`; an identity provider with
   other shapes sets `ChangeEndpointOptions.Actor` / `.Tenant` (or maps claims in an `IClaimsTransformation`).
5. **Retries**: clients send `Idempotency-Key` (the same key with another body → 422) and `If-Match: "<version>"` from the
   `ETag` (stale → 409). Creations without a key are not deduplicated.
6. **GET by id is a view**, never the aggregate: `[Endpoint("GET /auctions/{auction}")]` on a record implementing
   `IView<Auction, TSelf>` with `static TSelf From(Auction)`; grant `Read<Auction>`.
7. **Webhooks**: `[Webhook(sender, type, Version = "2")]` on `public static TChange From(TPayload)`; configure
   `WebhookOptions.Senders[sender].Secrets`; a duplicate *business* event must be a success in the domain.

## Example

```csharp
var builder = WebApplication.CreateSlimBuilder();
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseInMemoryStore()                                    // .UseNpgsqlStore(connectionString) in production
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
    })
    .AddEndpoints();
builder.Services.AddAuthentication().AddJwtBearer();       // authentication stays the app's
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser())
    .AddPolicy("auctions:bid", p => p.RequireAuthenticatedUser())
    .AddPolicy("auctions:read", p => p.RequireAuthenticatedUser());
builder.Services.AddOpenApi(o => o.AddTarolEndpoints());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapChanges();                                          // every [Endpoint] and [Webhook] of this assembly
```

```http
POST /auctions/0193…/bids  {"amount":150}   → 204  ETag: "2"
POST /auctions/0193…/bids  {"amount":120}   → 409  {"code":"Bid.TooLow",…}
POST /auctions/0193…/bids  {}               → 400  {"errors":{"amount":["Required"]}}
```

## Common mistakes

| Mistake | What happens |
|---|---|
| `[Endpoint]` on the aggregate | TWEB002: declare a view |
| a `{value}` with no member of that name | TWEB003 |
| `MapChanges()` without `AddSecurity` | `InvalidOperationException` at startup (fail closed) |
| a second `MapChanges()` without `names:` | "Duplicate endpoint name" at the first request |
| the input not in the JSON context | `NotSupportedException` for its member types at the first request |
| a role claim the identity does not use (`IsInRole` false) | 403 `Auth.Forbidden`: set `RoleClaimType` or map claims |
| `If-Match: W/"2"` | 400 `If-Match: Format` (strong ETags only) |
| a webhook factory that throws for a duplicate payment | 503 until the sender gives up; return success in the domain |
| expecting a renamed member's value-rule errors under its JSON name | value rules report C# paths (converted by the naming policy); the binder reports `[JsonPropertyName]` names |

## AOT

The delegates are generated (no reflection, no RDG); JSON goes through the app's context; OpenAPI needs the delegate's
`MethodInfo`, which the generator adds. Publish with `PublishAot`: 0 IL warnings.
