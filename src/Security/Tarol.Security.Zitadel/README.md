# Tarol.Security.Zitadel

Optional: [Zitadel](https://zitadel.com) as the identity provider of a Tarol application. One call configures JwtBearer
against the Zitadel issuer and project and maps Zitadel's token shape onto what Tarol reads.

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .UseNpgsqlStore(connectionString)
    .AddSecurity(roles => roles.Role("seller").May<CreateAuction>().May<CloseAuction>())   // Zitadel project roles
    .AddEndpoints()
    .AddZitadel(zitadel =>
    {
        zitadel.Authority = "https://auth.example.com";
        zitadel.ProjectId = "399724483650256899";
    });
```

| Zitadel issues | Tarol gets |
|---|---|
| `urn:zitadel:iam:org:project:<project>:roles` = `{"seller":{"<org>":"…"}}` (one JSON-object claim) | `role` claims → the `Tarol.Security` role map; only roles granted in the user's organisation; the generic `urn:zitadel:iam:org:project:roles` is not read |
| `urn:zitadel:iam:user:resourceowner:id` (the user's organisation, numeric) | the `tenant` claim: `ZitadelTenants.Of(organisation)`, a stable `Id<ITenant>`; it replaces a `tenant` claim the token carried |
| a machine user (`sub` = its user id, `client_id` = its name) with the project role `service` | `Actor.Service(client_id)` (others: `Actor.User(sub)`) |
| `aud` = the project id (when requested) | JwtBearer's audience; another audience → 401 |
| no `scope` claim | scope-based endpoint policies do not apply: let the role map decide |

* Clients request the scopes `openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner
  urn:zitadel:iam:org:project:id:<project>:aud`.
* `ZitadelOptions`: `Authority`, `ProjectId`, `RequireHttpsMetadata` (default true; false for a local container),
  `ServiceRole` (default `service`), `HubPath` (default `/tarol/messages`: under it the token may come from the query string
  `access_token`, because a browser cannot set headers on a WebSocket; everywhere else a query token is ignored; `null` turns
  it off).
* `AddZitadel` comes after `AddEndpoints` (it sets the actor rule unless the app set one).
* Discovery and the signing keys (RS256) come from the issuer; Native-AOT ready (0 IL warnings).
* Not a dependency of any other Tarol package; Aspire and tests run Zitadel as a container (see `samples/Sagas`).
