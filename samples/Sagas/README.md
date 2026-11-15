# Sample: ArtFlow's API and reaction worker under an Aspire AppHost

The runnable sample of steps 9 and 10 (D-15). It runs on the real packages:
- **`Worker/`** is ArtFlow's reaction worker (`Tarol.Sagas`, `Tarol.Persistence.Npgsql`, the generated
  `GeneratedReactions.Set`, the settlement saga of `docs/architecture/sagas.md` §5).
- **`Api/`** is its HTTP API (`Tarol.Changes.AspNetCore`, `Tarol.Security`, `Tarol.Security.Zitadel`). It links the
  worker's domain and changes and adds their entries as partial declarations (`[Endpoint]`, `[Webhook]`), a creation and
  a view.
- **`AppHost/`** starts these resources:
  - PostgreSQL and **Zitadel** as containers;
  - `zitadel-setup` (project `artflow`, roles `seller`/`bidder`, machine users alice and bob);
  - the API (after the setup completed) and the worker;
  - the Aspire dashboard.

Not part of `Tarol.slnx` and not built by CI: the AppHost needs Docker and Aspire's orchestrator, and Zitadel's image comes from ghcr.io.
The sample has its own `Directory.Build.props` and package versions. The full Aspire ArtFlow sample is step 18.

```bash
dockerd > /tmp/dockerd.log 2>&1 &                        # if Docker is not running
dotnet build samples/Sagas/AppHost
samples/Sagas/apphost.sh "$(dirname "$(find samples/Sagas/AppHost/bin -name ArtFlow.AppHost.dll | head -1)")" 5

# the same with the Native AOT API and worker
dotnet publish samples/Sagas/Api -c Release -r linux-x64 -o /tmp/artflow-api
dotnet publish samples/Sagas/Worker -c Release -r linux-x64 -o /tmp/artflow-worker
ARTFLOW_API_NATIVE=/tmp/artflow-api/ArtFlow.Api ARTFLOW_WORKER_NATIVE=/tmp/artflow-worker/ArtFlow.Worker \
  samples/Sagas/apphost.sh "$(dirname "$(find samples/Sagas/AppHost/bin -name ArtFlow.AppHost.dll | head -1)")" 5
```

`apphost.sh` runs the AppHost without dev certificates (`ASPIRE_ALLOW_UNSECURED_TRANSPORT`, anonymous dashboard on
`http://localhost:18888`). For each auction:
1. it gets client-credentials tokens from Zitadel (`http://localhost:8080`);
2. alice creates the auction, bob bids, bob tries to settle (403, the seller-only rule), alice settles;
3. it reads the result from the PostgreSQL resource and both resources' spans from the dashboard's telemetry API;
4. finally it removes the containers.

Interactively: `dotnet run --project samples/Sagas/AppHost`, then open the dashboard link it prints.

Result on SDK 11.0.100, Aspire 13.6.5, Zitadel v4.20.3 (2026-11-15). JIT projects and Native AOT binaries give the same
result:
- the API is healthy after about 14 s;
- auction 1: bid 204, settle by bob 403, settle by alice 204, and the view;
- 5 payouts and 5 settlement sagas, recorded as `System Reactions.MarkPayoutDue` on behalf of alice's Zitadel user id;
- every event has a `traceparent`;
- 5 deadline rows are waiting;
- the dashboard lists `api` and `worker` with the request spans and the reaction spans, each reaction span linked to its request.

Both binaries publish with 0 IL warnings.
