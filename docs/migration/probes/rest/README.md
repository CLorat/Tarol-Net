# Probes: Security and REST (step 10 design)

Evidence for [docs/migration/rest.md](../../rest.md) and [docs/architecture/rest.md](../../../architecture/rest.md).
The probes are copied and adapted, never referenced, and stay out of `Tarol.slnx`. `*.csproj.txt` become project files
in a work directory (`run.sh`); `TarolRoot` points at this repository, so the probes build against **today's** packages
(`Tarol.Changes`, `Tarol.Persistence(.Npgsql)`, `Tarol.Sagas` and their generators), not copies.

| Folder | What it is |
|---|---|
| `Web/` | the would-be `Tarol.Changes.AspNetCore`: `[Endpoint]`, `[Webhook]`, `IView<,>`, `Read<T>`; `ChangeEndpoints` (edge context, binding B1, outcome → HTTP, problem details by hand or through `IProblemDetailsService`, GET by id), `Webhooks` (raw body, HMAC, window, rotation, envelope), `TarolBuilder` (one registration) |
| `Security/` | the would-be `Tarol.Security`: `ChangeRequirement`, `ChangeHandler<TChange, TAggregate>`, `ChangeRoles` (AZ-D role map), `ChangeAuthorization` (`IChangeAuthorization` over `IAuthorizationService`, log + `tarol.authorization.denied`) |
| `Gen/` | the would-be generator: one `RequestDelegate` per `[Endpoint]` change/aggregate/view and per webhook sender, `MapChanges(names)`; `ProbeBinding=record` (B1) or `members` (B2); `ProbeOpenApiMethod=true` adds the delegate's `MethodInfo` |
| `App/` | ArtFlow over HTTP: JwtBearer, policies, resource handlers, webhook contracts V1/V2, OpenAPI, OTLP; modes `selftest`, `bench`, `startup`, `serve`, `worker` |
| `Aspire/` | AppHost: PostgreSQL resource, `api` and `worker` (the same app, two modes), dashboard, optionally Zitadel + its setup (`PROBE_ZITADEL_DIR`); `apphost.sh` drives it with a JWT (minted, or from Zitadel) |
| `Zitadel/` | `start.sh` (PostgreSQL + Zitadel v4.20.3 in Docker on port 8080), `setup.py` (project, roles, machine users, grants through the management API); the app's `oidc-selftest` mode and `App/Oidc.cs` (claims transformation, actor rule) |

```bash
dockerd > /tmp/dockerd.log 2>&1 &            # D-15
docs/migration/probes/rest/all.sh /tmp/rest  # writes output.txt (about 30 minutes on 4 cores); ALL_FROM=<n> resumes at section n
# Zitadel's image is on ghcr.io: its layers come from pkg-containers.githubusercontent.com (allow it in the environment)
docs/migration/probes/rest/run.sh /tmp/w App run -- selftest      # one run; PROBE_PG=<connection string> for Npgsql
```
