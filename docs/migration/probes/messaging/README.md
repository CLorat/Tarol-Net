# Probes: Messaging (step 11 design)

Evidence for [docs/migration/messaging.md](../../messaging.md) and [docs/architecture/messaging.md](../../../architecture/messaging.md).
The probes are copied and adapted, never referenced, and stay out of `Tarol.slnx`. `*.csproj.txt` become project files
in a work directory (`run.sh`); `TarolRoot` points at this repository, so the probes build against **today's** packages
(`Tarol.Changes`, `Tarol.Persistence(.Npgsql)`, `Tarol.Sagas` and their generators).

| Folder | What it is |
|---|---|
| `Messaging/` | the would-be `Tarol.Messaging`: `Message` (CloudEvents binary), `IMessageTransport`, `InMemoryTransport`, `[Consumes]` + `ConsumerHost` (trust, context, key, classification, dead letters, re-drive), `[Publishes<T>]` + `PublicationSet` (as reaction-worker rows), effects, the PostgreSQL bus `PgTransport` and `NpgsqlDeadLetters` |
| `Nats/`, `Rabbit/` | the would-be `Tarol.Messaging.Nats` (NATS.Net 3.3.2) and `Tarol.Messaging.RabbitMq` (RabbitMQ.Client 7.2.2) |
| `App/` | ArtFlow and billing (two systems, two stores) with hand-written "generated" consumers and publications; modes `selftest <memory\|postgres\|postgres+notify\|nats\|rabbit>`, `security <nats\|rabbit\|postgres>`, `legacy` (the old backplane, copied), `bench`, `startup`, and the Aspire roles `api`, `worker`, `billing` |
| `Size/` | one transport per Native AOT binary (`-p:ProbeTransport=postgres\|nats\|rabbit`) |
| `Aspire/` | AppHost: PostgreSQL (databases `artflow`, `billing`), NATS (JetStream), api + worker + billing; `apphost.sh` drives it and reads the dashboard |
| `Brokers/` | `nats.conf` (two users with subject permissions), `rabbit-users.sh` (two users with permissions), `claim-order.sql` (`UPDATE … RETURNING` order) |
| `inventory.md` | the old Messaging code, file:line, its tests run on SDK 11, every review finding re-verified |
| `output.txt` | the last `all.sh` run; `output-before-claim-order-fix.txt` the run that found §5 item 6 of the evaluation |

```bash
dockerd > /tmp/dockerd.log 2>&1 &                          # D-15; images postgres:17, nats:2.11, rabbitmq:4-management
docs/migration/probes/messaging/all.sh /tmp/msg            # writes output.txt; ALL_FROM=<n> / ALL_TO=<m> run sections n…m
OLD_TAROL=<checkout of CLorat/Tarol@6a62f81> …             # section 10: the old tests (default: ../Tarol next to this repository)
docs/migration/probes/messaging/run.sh /tmp/w App run -- selftest memory   # one run; PROBE_PG, PROBE_NATS, PROBE_RABBIT for the others
```
