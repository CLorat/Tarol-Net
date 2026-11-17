# Sample: ArtFlow and billing under an Aspire AppHost

The runnable sample of steps 9 to 14 (D-15, M-15, QR-7, J-1). It runs on the real packages:
- **`Worker/`** is ArtFlow's reaction worker (`Tarol.Sagas`, `Tarol.Persistence.Npgsql`, the generated
  `GeneratedReactions.Set`, the settlement saga of `docs/architecture/sagas.md` §5).
- **`Api/`** is its HTTP API (`Tarol.Changes.AspNetCore`, `Tarol.Security`, `Tarol.Security.Zitadel`). It links the
  worker's domain and changes and adds their entries as partial declarations (`[Endpoint]`, `[Webhook]`), a creation,
  a view and two lists over the auction list (`GET /auctions`, `GET /my/auctions`, `Tarol.Queries.Npgsql`).
- **The auction list** (`Worker/AuctionList.cs`, step 12): a `[Projection("auction-list")]` row record the worker hosts
  (`AddProjections`, woken by the store's feed `NOTIFY`) and the API lists; change answers carry `Tarol-Position`, and
  the script reads the list after each bid with `Tarol-Read-After` (docs/queries/README.md).
- **The settlement report** (`Worker/Reports.cs`, step 14): an `IJob` in the worker (`AddJobs`, `Tarol.Jobs`), nightly at
  02:15 Vienna time per tenant (`PerTenant`, with `AuctionTenants` as the `ITenantDirectory`). It counts the tenant's settled
  auctions in the auction list and records a `DayReport` keyed by the occurrence. The AppHost overrides its schedule to every
  minute (`Tarol__Jobs__SettlementReport__Cron`), so a run shows it (docs/jobs/README.md).
- **`Billing/`** is the second system (`Tarol.Messaging`): it consumes ArtFlow's `artflow.auction-settled`
  (`IssueInvoice`), pays each invoice in a reaction (a stand-in payment provider, `PayInvoice`) and publishes
  `billing.invoice-paid` from its outbox. ArtFlow's worker consumes it into `RecordPayment` (the same change as the
  payment provider's webhook), which completes the settlement saga. Each system owns its contract records
  (`Worker/Messaging.cs`, `Billing/Domain.cs`).
- **`AppHost/`** starts these resources:
  - PostgreSQL (databases `tarol` and `billingdb`: Aspire's resource names are global, so the database is not `billing`),
    **Zitadel** and **NATS JetStream** (`ARTFLOW_BROKER=rabbitmq`: RabbitMQ) as containers;
  - `zitadel-setup` (project `artflow`, roles `seller`/`bidder`, machine users alice and bob);
  - the API (after the setup completed), the worker and billing;
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
dotnet publish samples/Sagas/Billing -c Release -r linux-x64 -o /tmp/artflow-billing
ARTFLOW_API_NATIVE=/tmp/artflow-api/ArtFlow.Api ARTFLOW_WORKER_NATIVE=/tmp/artflow-worker/ArtFlow.Worker \
  ARTFLOW_BILLING_NATIVE=/tmp/artflow-billing/ArtFlow.Billing \
  samples/Sagas/apphost.sh "$(dirname "$(find samples/Sagas/AppHost/bin -name ArtFlow.AppHost.dll | head -1)")" 5

# RabbitMQ instead of NATS
ARTFLOW_BROKER=rabbitmq samples/Sagas/apphost.sh "$(dirname "$(find samples/Sagas/AppHost/bin -name ArtFlow.AppHost.dll | head -1)")" 5
```

`apphost.sh` runs the AppHost without dev certificates (`ASPIRE_ALLOW_UNSECURED_TRANSPORT`, anonymous dashboard on
`http://localhost:18888`). For each auction:
1. it gets client-credentials tokens from Zitadel (`http://localhost:8080`);
2. alice creates the auction, bob bids and reads the auction list after his bid (`Tarol-Read-After` with the bid's
   `Tarol-Position`), bob tries to settle (403, the seller-only rule), alice settles;
3. it reads the list (total, a page with its cursor, alice's and bob's own auctions, the page ETag and a 304), the
   projection's checkpoint, and the result from both databases (ArtFlow's and billing's) and every resource's spans from the dashboard's
   telemetry API;
4. it waits (at most 100 s) for a settlement report that saw the settled auctions and prints the job rows and the reports;
5. finally it removes the containers.

Interactively: `dotnet run --project samples/Sagas/AppHost`, then open the dashboard link it prints.

Result on SDK 11.0.100, Aspire 13.6.5, Zitadel v4.20.3, NATS (Aspire's image) and RabbitMQ 4 (2026-11-15). JIT projects
with NATS, JIT projects with RabbitMQ and the three Native AOT binaries with NATS give the same result:
- the API is healthy after 14–27 s;
- auction 1: bid 204, settle by bob 403, settle by alice 204, and the view;
- 5 payouts and 5 settlement sagas, recorded as `System Reactions.MarkPayoutDue` on behalf of alice's Zitadel user id;
- billing: 5 invoices issued (`System Consumers.IssueInvoice` for alice) and 5 paid (`System Reactions.PayInvoice` for alice);
- 5 payments recorded back in ArtFlow (`System Consumers.RecordPayment` for alice) and 5 settlement sagas completed;
- every event has a `traceparent`; both outboxes are empty but for the 5 deadline rows; no dead letters in either system;
- the dashboard lists `api`, `worker` and `billing` with the request, reaction, publish and consume spans; every reaction,
  publish and consume span is linked (`publish artflow.auction-settled` to the request that settled, `consume …` to the
  publish).

Step 14 (2026-11-17, JIT, NATS): the job rows are `SettlementReport (a tenant) [* * * * * Europe/Vienna]` and
`tarol.purge-keys [every 3600s]`, no dead jobs; one report by `System Jobs.SettlementReport` that counted 5 settled
auctions, one key `job:SettlementReport`, one `job SettlementReport` span; the run took 91 s.

The three binaries publish with 0 IL warnings (api 24.8 MB, worker 20.0 MB, billing 19.8 MB, each with NATS.Net and
RabbitMQ.Client).

Two lessons of the probe hold here (docs/migration/messaging.md §5 items 9 and 12): the API traces
`Microsoft.AspNetCore` too, or the parent-based sampler drops its spans; and nothing in the loop drops a version conflict.
Every step is a reaction or a consumer with its key, and the payment provider's webhook answers 503 so its sender retries.
A caller that runs a change itself must retry `Stream.VersionConflict` (C-7).
