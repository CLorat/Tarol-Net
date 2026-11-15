# Security

User guide for `Tarol.Security` (with the permission step of `Tarol.Changes`). Concept: [architecture/rest.md](../architecture/rest.md)
§3 and [architecture/security.md](../architecture/security.md); decisions: [migration/rest.md](../migration/rest.md) §9.

## Design in five points

1. **The change is the requirement, the aggregate the resource.** "May bob close auction 42?" is ASP.NET Core resource
   authorization with `CloseAuction` as the requirement and the loaded auction as the resource; a creation is checked on
   the new aggregate, a read on `Read<Auction>`.
2. **The app's roles are mapped onto changes and reads.** `AddSecurity(roles => roles.Role("seller").May<CloseAuction>())`.
   Row rules (`ChangeHandler<TChange, TAggregate>`) add what roles cannot say: "only the auction's seller", "a reserve
   above 100 000 needs the dealer role", "the seller reads their own auction".
3. **Allowed = a grant and no deny.** The map and ownership rules grant, `Deny(context, code)` refuses and wins; nothing
   granted is refused (fail closed). Anonymous callers get 401, refused ones 403 with the rule's code.
4. **Authorize the intent where it enters.** Only runs that carry a principal are checked: HTTP always passes one; jobs,
   reactions and sagas act later as the system on behalf of the user and are not checked again. A new privilege of the
   system opts in with `SystemPrincipal.For(actor, roles)`.
5. **Permissions are not invariants.** "Who" is decided here; "what the state allows" is a guard in the aggregate (409).
   Another tenant's aggregate is never loaded (404), so no rule ever sees it.

## Packages

| Package | Types |
|---|---|
| `Tarol.Changes` | `IChangeAuthorization`, `ChangeOptions.Principal`, the order: load → permission → expected version → `Execute` |
| `Tarol.Security` | `ChangeRoles`, `RoleGrants`, `ChangeRequirement`, `ChangeHandler<TChange, TAggregate>`, `Read<T>`, `ChangeAuthorization`, `SystemPrincipal`, `AddSecurity` |

## Register

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(builder.Configuration.GetConnectionString("tarol")!)
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
        roles.Role("admin").May<CloseAuction>().May<Read<Auction>>();
        roles.Authenticated().May<WatchAuction>();
    });
builder.Services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
```

Authentication stays the application's: JwtBearer (or any scheme) produces the `ClaimsPrincipal`; roles are read with
`IsInRole`, so set `RoleClaimType` or map an identity provider's role claim in an `IClaimsTransformation` (Zitadel's roles
arrive as one JSON-object claim, rest.md §11).

## Results

| Case | Result | HTTP (step 10B) |
|---|---|---|
| granted, no deny | success | 2xx |
| a rule denied | `Error.Forbidden(<code>)` | 403 problem details with `code` |
| nothing granted | `Error.Forbidden("Auth.Forbidden")` | 403 |
| anonymous | `Error.Unauthorized("Auth.Required")` | 401 |
| principal, but no `AddSecurity` | `Error.Forbidden("Auth.NotConfigured")` | 403 |
| another tenant | `Error.NotFound` (the load) | 404 |

## Audit

A denial is a warning log (event id 1001: change, aggregate type and id, actor kind and id, reason) and one count of
`tarol.authorization.denied` (tags `change`, `reason`, `actor.kind`) on the meter `Tarol.Security`. Nothing changed, so
nothing is written to the event store.

## Testing

A rule is a pure function of principal, input and aggregate: test it with `IAuthorizationService` in a unit test, or
through the pipeline with `ChangeTest.At(now, json, authorization)` and `new ChangeOptions(Principal: user)`. A test
without a principal is an internal run and checks nothing.
