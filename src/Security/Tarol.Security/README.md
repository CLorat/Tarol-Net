# Tarol.Security

Authorization of Tarol changes with ASP.NET Core's own authorization: **the change is the requirement, the aggregate is the
resource**. The application maps its roles onto the changes and reads they may run; row rules on the loaded (or new)
aggregate grant or deny. Allowed = something granted it and nothing denied it; nothing granted = denied (fail closed).

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
        roles.Authenticated().May<WatchAuction>();
    });
builder.Services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();

public sealed class CloseAuctionRule : ChangeHandler<CloseAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CloseAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller && !context.User.IsInRole("admin"))
            Deny(context, "Auction.NotSeller");                 // 403, wins over the seller role's grant
        return Task.CompletedTask;
    }
}
```

| Outcome | `Result` |
|---|---|
| granted, no rule denies | success |
| a rule denied | `Error.Forbidden(<its code>)` |
| nothing granted | `Error.Forbidden("Auth.Forbidden")` |
| anonymous caller, not granted | `Error.Unauthorized("Auth.Required")` |
| another tenant's aggregate | `Error.NotFound` from the load (never authorized) |

* **Who is checked**: `ChangePipeline` asks only for runs that carry a principal (`ChangeOptions.Principal`). HTTP entries
  always pass one; jobs, reactions and sagas pass none: the intent was authorized where it entered. A system action that is a
  new privilege opts in with `SystemPrincipal.For(actor, "refunds")`.
* **Order**: after the load, before the expected version (`If-Match`) and before `Execute`, so a refused caller never learns
  the aggregate's version.
* **Reads**: `Read<TAggregate>` is the intent of a GET; grant it in the map or with an ownership rule
  (`ChangeHandler<Read<Auction>, Auction>` + `context.Succeed(requirement)`).
* **Context**: `requirement.Context` holds the operation, the time and the change's `[Read]` aggregates.
* **Audit**: every denial is a warning log (event 1001: change, aggregate, id, actor kind and id, reason) and the counter
  `tarol.authorization.denied` (tags `change`, `reason`, `actor.kind`) on the meter `Tarol.Security`. No events.
* **Authentication** stays the application's (JwtBearer, schemes, claims transformations); roles are read with
  `ClaimsPrincipal.IsInRole`.
* Works without a web host (`AddAuthorizationCore`); Native-AOT ready, no reflection. Handlers are singletons.

Guide: [docs/security/README.md](../../../docs/security/README.md). Design: `docs/architecture/rest.md` §3.
