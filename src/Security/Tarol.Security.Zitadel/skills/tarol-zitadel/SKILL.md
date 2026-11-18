---
name: tarol-zitadel
description: >-
  Use when a Tarol application authenticates with Zitadel: AddZitadel (ZitadelOptions: Authority, ProjectId,
  RequireHttpsMetadata, ServiceRole), ZitadelClaimsTransformation (project roles to roles, organisation to tenant),
  ZitadelTenants.Of, the scopes a client requests, and running Zitadel in a container for tests and Aspire. Do not write
  your own JWT validation or parse Zitadel's role claim by hand.
---
# Tarol.Security.Zitadel

## Rules

1. **One call after `AddEndpoints`**: `.AddEndpoints().AddZitadel(z => { z.Authority = …; z.ProjectId = …; })`. It sets
   JwtBearer (issuer discovery, audience = project, `sub` as name, `role` as role claim) and the claims transformation.
2. **Roles are the project's roles**, granted to users in Zitadel; map them onto changes with `AddSecurity(roles => …)`
   (skill `tarol-security`). Roles of other projects are ignored.
3. **The tenant is the user's organisation**: `ZitadelTenants.Of(organisationId)` (stable `Id<ITenant>`); use it where an
   organisation must be named as a tenant (webhook sender entries, seed data).
4. **Services are machine users with the `service` role** (`ZitadelOptions.ServiceRole`): recorded as `Actor.Service(client_id)`.
5. **Clients request the scopes** `openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner
   urn:zitadel:iam:org:project:id:<project>:aud`; without the last one the token is for another audience (401), without
   the second the user has no tenant (another tenant's 404s).
6. **Endpoint policies**: Zitadel's access tokens carry no `scope` claim; let `[Endpoint(Policy = …)]` require an
   authenticated user and the role map decide.
7. **Local containers** have no TLS: `RequireHttpsMetadata = false`, and the issuer is the external URL (fixed port).

## Example

```csharp
var builder = WebApplication.CreateSlimBuilder();
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseInMemoryStore()                                    // .UseNpgsqlStore(connectionString) in production
    .AddSecurity(roles => roles.Role("seller").May<CreateAuction>().May<CloseAuction>())   // Zitadel project roles
    .AddEndpoints()
    .AddZitadel(zitadel =>
    {
        zitadel.Authority = "https://auth.example.com";
        zitadel.ProjectId = "399724483650256899";         // the project: the audience and the roles
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("auctions:write", p => p.RequireAuthenticatedUser());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapChanges();
```

## Common mistakes

| Mistake | What happens |
|---|---|
| `AddZitadel` before `AddEndpoints` | `InvalidOperationException` |
| the token requested without the project's `:aud` scope | 401 |
| roles granted on another project (also in the generic `urn:zitadel:iam:org:project:roles` claim) | the role map sees none: 403 `Auth.Forbidden` |
| a role granted in another organisation than the user's | not a role in the user's tenant |
| a machine user without the `service` role | recorded as `Actor.User(<user id>)` |
| `RequireHttpsMetadata` left on for `http://localhost` | the metadata is not fetched: every request 401 |
| the container's issuer port differs from the URL clients use | issuer mismatch: 401 |

## AOT

JwtBearer, discovery, the JWKS and the transformation run natively with 0 IL warnings.
