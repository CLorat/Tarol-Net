# Tarol

Tarol is a .NET 11 framework for event-sourced Domain-Driven Design:

* **Every change is an event**, so state can always be rebuilt from the event stream.
* **User code stays small**: the plumbing is source-generated, and each generator ships inside its runtime package.
* **Native-AOT ready**: no reflection on the hot path; every package that claims AOT has a smoke app in CI.
* **Persistence on Npgsql**: a thin event store instead of a document database: events, queryable current state,
  lookups and idempotency keys in one transaction, the tenant in every key.

Tarol-Net is the successor of [CLorat/Tarol](https://github.com/CLorat/Tarol). Packages move over one family at a
time; see the [migration plan](docs/migration/plan.md).

## Packages

| Package | Description | Status |
|---|---|---|
| `Tarol` | the meta-package: the 17 in-process packages in one reference, every generator included; add a store (`Tarol.Persistence.Npgsql`, `Tarol.Queries.Npgsql`) and a transport yourself | new (step 17C) |
| `Tarol.StronglyTypedId` | `Id<TEntity>` over UUIDv7, JSON/binding/formatting built in, generator included | migrated |
| `Tarol.Operations` | operations: one cached snapshot of the database numbers (oldest due reaction, dead letters, overdue jobs, projection lag, bus backlog) polled every 15 s and published as gauges on the meter `Tarol.Operations`; health checks in three groups (`live`, `ready`, `tarol`) with `MapTarolHealth`; the admin entry for dead reactions, jobs and messages (keyset pages, re-drive, discard, every act audited) and projection versions with `MapTarolAdmin` behind a required policy; optional retention as `tarol.purge-dead` |
| `Tarol.Options` | `Option<T>` (`T` \| `None`) as a non-boxing C# 15 union, the same for value and reference types; AOT-safe JSON (the value or `null`), analyzer included; no dependencies | migrated |
| `Tarol.Results` | `Result<T>` / `Result` as non-boxing C# 15 unions of a value and a `Violation` (`Invalid` field errors or one `Error`), conversions to and from `Option<T>`; AOT-safe JSON, analyzers included | migrated |
| `Tarol.Kernel` | `OperationContext`: who acts (`Actor`), tenant, correlation and causation as `Id<T>`, AOT-safe JSON; analyzer that keeps the system clock out of your code (inject `TimeProvider`) | migrated |
| `Tarol.Validations` | rules declared once on value objects (`record struct Quantity([Range(1, 100)] int Value)`): generated `Create`, trusting JSON converter, comparisons; `IValidatable` inputs validated with all field errors and input paths as `Invalid`, zero allocation when valid; generator and analyzer included | migrated |
| `Tarol.Primitives` | event-sourced aggregates with child entities: pure-payload events (`sealed record Repriced(decimal Price) : Event;`), generated dispatch, cascade and strict replay, invariants at the hand-off, a given/when/then test helper, stored state (the current state as queryable JSON with every commit, loaded without replay), lifecycles (state enums with declared transitions, generated guards, every step checked at commit); generator and analyzers included | migrated (step 6 done, parts A–C) |
| `Tarol.Persistence` | the aggregate store port: tenant-scoped loads, appends with an expected version and an idempotency key stored in the same commit, unique lookups; an in-memory store for tests that records every event with its operation context and exercises the stored state | new (step 7, PR 7A) |
| `Tarol.Changes` | changes as the entries of an actor into the domain: an input record (`IChange<T>`, `ICreation<T>`) that calls one behaviour; one pipeline (validate before any read, load or `ILoads<T>`, `[Read]` aggregates, permission hook, time read once, invariants, expected version, idempotency key, discard on failure); generated target, reads and typed dispatcher; `ChangeTest` helper; reactions to own events (`[OnEvent<T>]`, generated `GeneratedReactions.Set`) | new (step 7, PR 7B; reactions 9A) |
| `Tarol.Persistence.Npgsql` | the PostgreSQL store on plain Npgsql: one guarded statement per commit (conflicts are results), stored state as queryable JSON, unique lookups, idempotency keys, a gap-free global feed, a shipped SQL schema; the outbox (head-flag claims, scheduled rows, dead letters, opt-in NOTIFY) | new (step 8, PR 8B; outbox 8C, 9B) |
| `Tarol.Sagas` | reactions to own events and sagas: one outbox worker runs every reaction through the change pipeline as the system on behalf of the user (effect once by its key), retries conflicts with backoff, dead-letters what keeps failing; a hosted service with key purging, tracing links and logs; `ReactionTest` for the whole chain in memory; sagas are aggregates whose steps are reactions, timeouts are scheduled reactions | new (step 9, PRs 9A–9C) |
| `Tarol.Security` | authorization of changes with ASP.NET Core: the app's roles mapped onto changes and reads, row rules on the loaded or new aggregate (deny wins), fail closed, checked only for runs with a principal; denials logged and counted; works without a web host | new (step 10, PR 10A) |
| `Tarol.Changes.AspNetCore` | HTTP entries: `[Endpoint]` on a change or a view becomes a generated `RequestDelegate` (route + body bound member by member, context from the principal, `Idempotency-Key`, `If-Match`, `ETag`, problem details), GET by id through views, list queries over projections (filters, sorts, cursor, ETag, read-your-writes), signed webhooks, complete OpenAPI metadata; fails closed | new (step 10, PR 10B; lists 12C) |
| `Tarol.Security.Zitadel` | optional: Zitadel as the identity provider (JwtBearer against the issuer and project; project roles, organisation and machine users mapped onto the role map, the tenant and the actor) | new (step 10, PR 10B) |
| `Tarol.Messaging` | messages between systems: `[Consumes(contract, version)]` on a change's factory turns another system's message into a change through the same pipeline, deduplicated by its key (the inbox), retried and dead-lettered like a reaction; `[Publishes<TEvent>(contract, version)]` mapping methods and `IEffect<TEvent>` effects leave through the outbox, run by the reaction worker; CloudEvents envelope, trust per source system and tenant, a transport port with an in-memory transport, `MessageTest`, `MessagingTest` | new (step 11, PRs 11A, 11B) |
| `Tarol.Messaging.Nats` | the NATS JetStream transport: a stream per source, durable pull consumers, dedup by (event, contract, version), delayed retries, subject permissions per system | new (step 11, PR 11D) |
| `Tarol.Messaging.Npgsql` | the in-database message bus: systems on one PostgreSQL exchange messages through a `bus` schema (publish in one statement, deduplicated; claim with `SKIP LOCKED` in publish order; NOTIFY opt-in; the publisher's role stamped by the database, row-level security per contract prefix); dead letters in `messages_dead` | new (step 11, PR 11C) |
| `Tarol.Messaging.RabbitMq` | the RabbitMQ transport: a topic exchange per source, quorum queues, publisher confirms, TTL retry queues, `user_id` as the authenticated publisher | new (step 11, PR 11D) |
| `Tarol.Messaging.SignalR` | the UI push: messages forwarded to SignalR groups per tenant and topic, an authenticated hub that authorizes every subscribe and publish, a .NET client that replays its subscriptions on reconnect | new (step 11, PR 11E) |
| `Tarol.Queries` | projections and read models: a row record with `When(event)` methods under `[Projection]` (generated switch and column map), a hosted projection per read model on the event feed (rows and checkpoint in one guarded batch, idempotent by stream version, rebuild by version), list queries with filters, whitelisted sorts, an opaque cursor, a bounded offset and a page ETag, read-your-writes waits; `ProjectionTest` in memory | new (step 12, PR 12A) |
| `Tarol.Queries.Npgsql` | PostgreSQL read tables: a table per projection version with typed columns and the row as `jsonb`, tenant first in every key and an index per sort, the checkpoint guarded in the same transaction as the rows, building/current while a new version rebuilds, SQL projections in the batch transaction, the `LISTEN` wake-up on the store's feed `NOTIFY` | new (step 12, PR 12B) |
| `Tarol.DataProtection` | data protection: erasure of a data subject everywhere Tarol wrote its data (the key's tombstone, projection re-folds, page caches in every process, personal lookups, an audit row), export of a subject's data opened with that subject's key only, retention that deletes whole streams of a type after N days (`tarol.retention`), the configured master key; `[PersonalData]` itself is sealed by `Tarol.Persistence` |
| `Tarol.Caching` | list page caching in the API process: a decorator of `IReadModels` whose `Search` answers from an in-process page cache, keyed by tenant, projection version and the canonical query, valid exactly while the checkpoint it was read at is current (pushed by `Tarol.Queries.Npgsql`); bounded by entries, bytes and an idle expiry; the meter `Tarol.Caching`; no HybridCache, no output caching |
| `Tarol.Jobs` | scheduled jobs: `[Scheduled(cron)]` change entries and `IJob` classes run as the system once per occurrence across any number of workers, from one row per (tenant, job) with leased claims, a heartbeat and the occurrence as the idempotency key; our own 5-field Unix cron with IANA zones and Vixie DST rules, missed-run policies, retries and dead jobs, the `Tarol:Jobs:<name>` override, Tarol's maintenance as system jobs, `JobTest` |
| … | | planned (see plan) |

Packages are private: they are published only to the owner's GitHub Packages feed, never to nuget.org.

## Quick start

```csharp
using Tarol.StronglyTypedId;

public sealed class Customer { public Id<Customer> Id { get; } = Id<Customer>.New(); }

app.MapGet("/customers/{id}", (Id<Customer> id) => …);   // binds, serializes, no configuration
```

```csharp
using Tarol.Results;

Result<Customer> Find(Id<Customer> id) => store.TryGet(id, out var c) ? c : Error.NotFound<Customer, Id<Customer>>(id);

var response = Find(id) switch
{
    Customer c => TypedResults.Ok(c),
    Invalid i  => TypedResults.ValidationProblem(…),
    Error e    => TypedResults.Problem(statusCode: e.ToHttpStatusCode(), title: e.Code),
};
```

## Build

Requires the .NET SDK 11.0.x (`global.json`).

```bash
dotnet build Tarol.slnx
```

```bash
dotnet test --solution Tarol.slnx
```

```bash
eng/verify-packages.sh
```

```bash
eng/aot-smoke.sh
```

See [CONTRIBUTING.md](CONTRIBUTING.md) and, for coding agents, [AGENTS.md](AGENTS.md).

## Licence

Proprietary, for the copyright holder's use only. All rights reserved; see [LICENSE](LICENSE).
