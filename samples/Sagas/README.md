# Sample: the ArtFlow reaction worker under an Aspire AppHost

The first runnable prototype of step 9 (D-15): `Worker/` is ArtFlow's reaction worker on the real packages
(`Tarol.Sagas` hosted worker, `Tarol.Persistence.Npgsql`, the generated `GeneratedReactions.Set`, the settlement saga
of `docs/architecture/sagas.md` §5); `AppHost/` starts a PostgreSQL container, the worker and the Aspire dashboard. The
worker settles 20 auctions, one per second: each `Bid.Won` marks a payout due and starts a settlement saga whose
3-day deadline waits in the outbox.

Not part of `Tarol.slnx` and not built by CI (the AppHost needs Docker and Aspire's orchestrator); it has its own
`Directory.Build.props` and package versions. The full Aspire ArtFlow sample is step 13.

```bash
dockerd > /tmp/dockerd.log 2>&1 &                        # if Docker is not running
dotnet build samples/Sagas/AppHost
samples/Sagas/apphost.sh "$(dirname "$(find samples/Sagas/AppHost/bin -name ArtFlow.AppHost.dll | head -1)")"

# the same with the Native AOT worker
dotnet publish samples/Sagas/Worker -c Release -r linux-x64 -o /tmp/artflow-worker
ARTFLOW_WORKER_NATIVE=/tmp/artflow-worker/ArtFlow.Worker samples/Sagas/apphost.sh "$(dirname "$(find samples/Sagas/AppHost/bin -name ArtFlow.AppHost.dll | head -1)")"
```

`apphost.sh` runs the AppHost without dev certificates (`ASPIRE_ALLOW_UNSECURED_TRANSPORT`, anonymous dashboard on
`http://localhost:18888`), reads the result from the PostgreSQL resource and the worker's spans from the dashboard's
telemetry API, and removes the containers. Interactively: `dotnet run --project samples/Sagas/AppHost` and open the
dashboard link it prints.

Result on SDK 11.0.100, Aspire 13.6.5 (2026-11-15), JIT and Native AOT worker alike: 20 payouts, 20 sagas (40 events),
every event with a `traceparent`, 20 deadline rows waiting; the dashboard lists the `worker` resource with the request
spans and one `reaction …` span per row, each with an `ActivityLink` to its request. The native worker publishes with
0 IL warnings.
