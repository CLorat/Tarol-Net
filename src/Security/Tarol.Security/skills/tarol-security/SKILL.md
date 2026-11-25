---
name: tarol-security
description: >-
  Use when deciding who may run a Tarol change or read an aggregate: mapping the app's roles onto changes and reads
  (AddSecurity, ChangeRoles, Read<T>), row rules on the loaded or new aggregate (ChangeHandler<TChange, TAggregate>, Deny),
  which runs are checked (ChangeOptions.Principal, SystemPrincipal), and the results (Auth.Forbidden, Auth.Required).
  Do not write a policy engine, check permissions inside aggregates, or decompile ASP.NET Core authorization.
---
# Tarol.Security: who may run a change

## Rules

1. The **change is the requirement**, the aggregate the resource. Grant changes and reads to the app's roles with
   `AddSecurity(roles => roles.Role("seller").May<CloseAuction>())`; `roles.Authenticated()` grants to every signed-in caller.
2. **Allowed = a grant and no deny.** The role map grants; a row rule `ChangeHandler<TChange, TAggregate>` grants
   (`context.Succeed(requirement)`, an ownership grant) or denies (`Deny(context, "Auction.NotSeller")`, wins over every
   grant). Nothing granted = `Error.Forbidden("Auth.Forbidden")`; an anonymous caller gets `Error.Unauthorized("Auth.Required")`.
3. **Only runs with a principal are checked.** HTTP passes `ChangeOptions.Principal` always; jobs, reactions and sagas pass
   none and are not checked. A privileged system action opts in: `new ChangeOptions(Principal: SystemPrincipal.For(actor, "refunds"))`.
4. A GET is the intent `Read<TAggregate>`: grant it like a change; it grants every view, list and row query of the aggregate
   except a restricted one. `[Endpoint("GET …", Restricted = true)]` needs `roles.Role("tpp").MayRead<Caregiver, TaxView>()`,
   which grants that one view (or list) and nothing else. A row rule for one view: `ViewHandler<TView, TAggregate>`
   (`HandleView`); `ChangeHandler<Read<T>, T>` rules still run for every view, so a restricted view narrows, never widens.
5. **Who** is a permission (here); **what state allows** is a guard in the aggregate (409). Never read roles in an aggregate.
6. Another tenant's aggregate is never loaded (404), so rules never see it.
7. Register rules as **singleton** `IAuthorizationHandler`s; `requirement.Context` has `Now`, `Operation` and `Read<T>()`.

## Example

```csharp
var services = new ServiceCollection();
services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>())
    .UseInMemoryStore()
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CloseAuction>();
        roles.Role("admin").May<CloseAuction>();
    });
services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
await using var provider = services.BuildServiceProvider();
var pipeline = provider.GetRequiredService<ChangePipeline>();

// alice's auction, created without a principal (an internal run: not checked)
var alice = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
var auction = (Id<Auction>)(await pipeline.Run(new CreateAuction(100), alice)).GetValueOrThrow().StreamId;

// bob is a seller, but not this auction's seller: the row rule denies
var bob = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "bob"), new Claim("role", "seller")], "jwt", "sub", "role"));
var refused = await pipeline.Run(new CloseAuction(auction), OperationContext.Start(Actor.User("bob"), Option.None, TimeProvider.System),
    new ChangeOptions(Principal: bob));
// refused: Error.Forbidden("Auction.NotSeller")

public sealed class CloseAuctionRule : ChangeHandler<CloseAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CloseAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller && !context.User.IsInRole("admin"))
            Deny(context, "Auction.NotSeller");
        return Task.CompletedTask;
    }
}
```

## Common mistakes

| Mistake | What happens |
|---|---|
| a change no role grants (and no rule succeeds) | 403 `Auth.Forbidden`: fail closed |
| a rule that only calls `Succeed` for the seller, expecting it to restrict | the seller role still grants others; use `Deny` to restrict |
| `context.Fail()` without a reason | 403 `Auth.Forbidden` (no code); use `Deny(context, code)` |
| a principal on a pipeline without `AddSecurity` | 403 `Auth.NotConfigured` |
| passing a user principal from a job or reaction | the job is checked as that user; pass none (or `SystemPrincipal`) |
| forgetting `Read<TAggregate>` in the map | GET answers 403 |
| roles not arriving (`IsInRole` false) | the identity's role claim type differs: set `RoleClaimType` in JwtBearer or map claims in an `IClaimsTransformation` |
| a scoped handler | captive dependency of the singleton authorization; make it a singleton |

## AOT

No reflection: the map keys on `change.GetType()`, handlers are plain classes. `Microsoft.AspNetCore.Authorization`
comes from the ASP.NET Core shared framework and works without a web host (`AddAuthorizationCore`).
