# Inventory: personal data in Tarol (step 16 design) **[verified by reading]**

Old code: `CLorat/Tarol@6a62f81` (2026-11-12). New code: `CLorat/Tarol-Net` `main` at `eabacb5` (step 15 done). Line numbers are
from those commits.

## 1. The old code: nothing to migrate

The old code has no data-protection code: no field or event encryption, no key store, no `[PersonalData]`-like attribute, no erasure,
no retention, no export, no redaction. Its events carry no actor and no tenant metadata.

| Where (`CLorat/Tarol`) | What | Verdict |
|---|---|---|
| `src/Sagas/Sagas/Internals/SagaInternals.cs:2,37` | `SHA1.HashData` for name-based saga/outbox ids | ids, not protection |
| `src/TestUtility/Sagas.Testing/FakeChangeDispatcher.cs:225` | `SHA256.HashData` for deterministic event ids in a fake | test helper |
| `src/Contexts/Contexts/IUserContext.cs:6-22` | `UserId`, `TenantId`, `Roles`; no implementation, no consumer | stub |
| `src/Persistence/Persistence/IRepositoryInterceptor.cs` | staged/committed hooks, default no-ops ("audit" in the docs) | the only hook point; unused |
| `src/Primitives/Primitives/DomainEvent.cs:99-108` | `DeleteDomainEvent<…>`: removes a child entity from the state | domain deletion, not erasure |
| `src/Persistence/Persistence.Marten/MartenSnapshotRepository.cs:55,100-140` | the stream as "audit trail"; cites a missing `MartenAuditTrail` | comment only |
| `src/Results/Results/Result[T].cs:378` | doc example `LogInformation("Found user: {Email}", user.Email)` | an unmasked email in a doc |
| `examples/ArtFlow/…/Bidder.cs`, `…/Expert.AggregateRoot.cs` | `Bidder.Name`, `Expert.Name`: names of persons, unmarked | step 18's sample holds personal data |
| `src/Validations/Validations.Value/Attributes/Text.EmailAttribute.cs` | `[Email]` format validation | validation only |
| `docs/review/*` | no mention of GDPR, erasure, encryption | — |

## 2. Tarol-Net: where personal data lands

### 2.1 Store (`src/Persistence/Tarol.Persistence.Npgsql/NpgsqlSchema.cs`, schema version 2)

| Table (lines) | Columns that can hold personal data | Written by | Erasable today |
|---|---|---|---|
| `events` (43-63) | `payload jsonb` (the event), `actor_id`, `on_behalf_of` (the OIDC `sub`), `traceparent` | `NpgsqlAggregateStore.Commit` (240-330; SQL 531-536) | **no**: no `UPDATE`/`DELETE` on `events` anywhere |
| `streams` (33-41) | `state jsonb` (the aggregate's whole state) | `Commit` (255), `RebuildStates<T>` (421-455) | rewritten by the next commit or `RebuildStates` |
| `lookups` (67-74) | `key text` (a value of the aggregate, e.g. an email); keys are app functions (`StoreLookups.Define<T>(name, Func<T,string?>)`) | `Commit` (295-300) | released when the key changes |
| `idempotency_keys` (76-87) | `scope` = `http:{actor kind}:{actor id}:{method} {path}` (`ChangeEndpoints.cs:107`), `key`, `fingerprint` (SHA-256 of method, path, body) | `Commit` (291-293) | `tarol.purge-keys` (age) |
| `outbox`, `outbox_dead` (91-120) | ids; `last_error text` (free text) | the reaction worker | dead rows: `tarol.purge-dead` (optional, age) |
| `jobs`, `jobs_dead` (124-147) | `last_error text` | the job worker | as above |
| `operations_audit` (198-210) | `actor text` (the operator's principal name), `detail jsonb` (tenant, stream, event ids) | same statement as each admin act | **never deleted** |
| `schema_version` | — | | |

No snapshot table: the "snapshot" is `streams.state`. The codec (`Codec/EventCodec.cs`) is synchronous: `Encode<T>(DomainEvent,
IBufferWriter<byte>)` (118) and `Decode(streamType, EventType, ReadOnlySpan<byte>)` (138), with a `JsonObject` path for upcasters
(`IEventUpcaster.Upcast(JsonObject)`). State: the generated `WriteState`/`ReadState` (`IAggregateRoot.cs:57,70`); a state that does
not parse is replayed (`NpgsqlAggregateStore.cs:159-176`).

### 2.2 Bus and transports

| Where | Personal data | Retention today |
|---|---|---|
| `bus.messages` (`NpgsqlBusSchema.cs:40-50`) | `data bytea` (the contract JSON), `headers jsonb` with `ce-actor` = `Kind:id[:onBehalfOf]` (`Message.cs:183`) | `tarol.purge-messages`: 7 days after delivery (`NpgsqlBusOptions.cs:59`) |
| `messages_dead` (`NpgsqlBusSchema.cs:144-153`) | `data`, `headers`, `reason` | kept; `tarol.purge-dead` optional |
| NATS JetStream (`NatsTransport.cs:112-116`) | the contract JSON | **no `MaxAge`: kept forever by default** |
| RabbitMQ (`RabbitMqTransport.cs:121-131`) | the contract JSON, until consumed | until acknowledged |
| `events.on_behalf_of` via consumers | a remote user's `sub` from `ce-actor` (`MessageConsumers.cs:202`) | as `events` |

### 2.3 Read side

| Where | Personal data | Forgettable today |
|---|---|---|
| `rm_<name>_v<n>` (`NpgsqlReadModelSchema.cs:43-63`) | typed columns from `TRow.Columns` and `doc jsonb` (the whole row) | **no**: `IProjection<TRow>.Apply(TRow?, RecordedEvent)` (`IProjection.cs:27-34`) cannot delete; a new `[Projection(Version)]` rebuilds from the feed |
| `SqlProjection` tables (`Hosting/SqlProjection.cs:25`) | the app's own | the app's own |
| `Tarol.Caching` (`PageKey.cs:19`, `IPageCache.cs`) | cached pages; the key's query string holds filter values (31, 42), e.g. an email filter | `Clear()` only; entries miss after the next checkpoint |

### 2.4 Logs and traces

`[LoggerMessage]` arguments that can carry personal data: 1001/1002 `actorId`; 7001 `detail` (free text); 9001/9002, 11001/11002,
11402, 14001/14002 `reason` (exception or failure text); 11201 NATS `user`; 11401 SignalR `subject`; 15001–15004 the operator
`actor`, 15002 the `tenant`. Activity tags: `tarol.tenant` (`JobWorker.cs:186`), `tarol.stream.id`, `messaging.message.id`, and
`SetStatus(Error, reason)` with free text. No tag carries the actor. Metrics have no tenant tag (O-1). Npgsql spans do not tag
parameters.

### 2.5 Identity and tenancy

* `Actor(Kind, Id, OnBehalfOf)` (`Actor.cs:17`); `Actor.User(subject)` takes the OIDC `sub` (`ChangeEndpointOptions.cs:17-24`), a
  pseudonymous id, never a name or an email in Tarol's own code.
* The tenant is a uuid on every table (`Guid.Empty` = none). Zitadel's tenant is `SHA256("zitadel-organisation:" + org)`
  (`ZitadelClaims.cs:71`). `ITenantDirectory` (`Job.cs:161`) lists tenants for per-tenant jobs.

### 2.6 What exists to build on

* Member attributes → generated metadata: `[Transient]`, `[StateName]` in the aggregate generator (`StateDiscovery.cs:40,98,130`,
  with `[JsonPropertyName]` at 324); `[Sort]` in the projection generator; per-event metadata could ride on
  `EventCatalogEntry(EventType Type, Type ClrType)` (`EventCatalog.cs:6`).
* Versioned schema steps (`UpgradeStep(Version, CompatibleFrom, Sql, Concurrently, Index)`, O-4); the store is at version 2.
* System jobs as `MaintenanceTask(name, every, run)` (`Maintenance.cs:11`): `tarol.purge-keys`, `tarol.purge-messages`,
  `tarol.purge-dead`.
* The admin entry `IDeadLetterAdmin` + `MapTarolAdmin(prefix, policy)` with `operations_audit` (O-3).
* Cryptography in use: `SHA256` (Zitadel tenant id), `IncrementalHash` (idempotency fingerprint), `HMACSHA256` + `FixedTimeEquals`
  (webhooks). **No encryption anywhere**; no `IDataProtector`, no Data Protection package.

## 3. Free names

* `[LoggerMessage]` ids in use: 1001–1002, 7001–7003, 8001–8005, 9001–9004, 9010–9011, 11001–11004, 11010, 11101–11102,
  11201–11204, 11301–11302, 11401–11404, 11411–11415, 12001–12003, 12010–12011, 12101–12103, 14001–14010, 15001–15005.
  **16001+ is free.** The registry is `docs/operations/README.md` (the log-event table).
* Decision prefixes in use: C-, CA-, D-, J-, M-, O-, P-, Q-, QR-, R-, S-, T- (plus single letters in probe code). **`DP-` is free.**
