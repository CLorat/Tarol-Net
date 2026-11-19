# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) and are taken from git tags (`v1.2.3`) by MinVer.
Generator diagnostics are tracked per generator in `AnalyzerReleases.Shipped.md` / `Unshipped.md`.

## [Unreleased]

### Added

* `Tarol.Search.Npgsql` (step 18C): PostgreSQL full-text search in the store's database: a table per index version, a GIN led by
  the tenant, ranking within the first 1,000 matches, German/English/simple with accents folded, a trigram typo fallback, counts,
  snippets, blind tokens; documents and checkpoint in one transaction; the previous version answers while a new one builds.
* `Tarol.Search` (step 18B): global search over aggregates and their entities: `[Searchable]`, `[SearchTitle]`,
  `[SearchAudience]`, `[SearchLanguage]` with a generator (TSRCH001–TSRCH005), the port `ISearchIndex`, `SearchHost` on the
  projection machinery, `ISearch`, the in-memory index, `SearchTest`, `SearchEraser` (erasure, retention, export); in the
  meta-package `Tarol`.
* Design of step 18, global search: `docs/architecture/search.md`, `docs/migration/search.md` (GS-0 … GS-12) and the probes under
  `docs/migration/probes/search/` (PostgreSQL full-text search and `pg_trgm`, Meilisearch, Typesense, OpenSearch at 10k–1M
  documents). No package code yet.

* `IOutbox.Release` (default member) hands a claimed row back unrun and uncounted; `IPageCache.Clears` (default member) counts
  cache clears; `NpgsqlTracing.AutoPrepareUnlessSet`; `ViolationJsonConverter`.
* Store schema version 4 (compatible from 1): the index `streams_retention` on `streams (type, updated_at)`.

### Changed

* `ProjectionHost` can be derived from outside `Tarol.Queries` (step 18A): its constructor and `Prepare`, `LoadCheckpoint` and
  `Write` are `protected` (were `private protected`); `Write`'s last position parameter is named `through`. Behaviour unchanged.

* `NpgsqlSchema.Version` is 4. The data sources Tarol builds set `Max Auto Prepare=64` unless the connection string sets it.
* The data-subject endpoints (`…/subjects/{id}/export|erase`) require `?tenant=` (400 without it).
* `Violation` JSON is read in any member order and accepts only `ErrorType` names for `type`.
* Zitadel: roles come only from this project's claim and only when granted in the user's organisation, which also sets the tenant.

### Fixed

* Messaging: one failed subscription restarts all of them instead of staying dead; a re-drive never loses its dead letter, and a
  `SourceMismatch` letter stays dead; the Npgsql bus hands a batch's tail back once half its lease has passed; RabbitMQ nacks a
  delivery whose retry copy the broker refused, and its retries go to six fixed delay queues (`.retry.1s` … `.retry.1h`) so a
  short retry never waits behind a long one.
* Jobs: a failed lease extension is retried, and the run is cancelled before its lease could run out.
* Persistence: an undecodable outbox row no longer stalls its batch; commits ignore a late cancellation; an erased key is never
  cached across a read; each SQL text is built once; `RebuildStates` pages in primary-key order.
* Sagas: a slow batch no longer runs its tail after the lease.
* Changes: a concurrent run under the same idempotency key returns the stored outcome instead of a 409.
* Caching: a page read before an erasure is not cached after it.
* Kernel: actor kinds are matched by name only (`"1"`, `"User,Service"` refused); `OperationContext.Actor` is never null.
* Generators and analyzers: no id registration for markers nested in generic types; a record deriving from an input that adds
  parameters is reported (TVAL002); TRES002 does not report the awaited `Tap` extensions.
* CI runs every integration-test project (DataProtection and Operations were left out).
* SignalR: a push connection that closes again during the client's own restart is started again instead of staying disconnected.

## [0.1.0]

The first release of Tarol-Net: 25 packages (24 and the meta-package `Tarol`), the public API baseline
(`PublicAPI.Shipped.txt`) and 83 diagnostics (below).

### Added

* `Tarol` (step 17C), the meta-package: one reference for the 17 in-process packages (every generator included); the
  store, the read tables, the transports, SignalR and Zitadel are referenced separately. Agent skill `tarol-overview`.
* Release (step 17E, 17F): `release.yml` (a tag `v*` re-runs CI, packs, checks, writes these notes and publishes to
  GitHub Packages), `eng/release.py`; PDBs embedded in the DLLs (no `.snupkg`).
* The public API baseline (step 17B): every runtime package lists its public API in `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt` (3,694 lines), checked by `Microsoft.CodeAnalysis.PublicApiAnalyzers`; `eng/public-api.py`
  writes the files from the analyzer's own output.
* CI (step 17A): actions pinned to commit SHAs, CodeQL code scanning for C#, every .NET security rule an error except
  CA2100 and CA5394.
* Repository foundation: layered build configuration, central package management, packaging conventions
  (README, agent skills and bundled generators in every runtime package), CI.
* `Tarol.StronglyTypedId`, migrated from CLorat/Tarol with: open generic JSON converter (no reflection fallback),
  allocation-free JSON reads, `Id<T>.New(TimeProvider)`, `IComparisonOperators`, generator registration of ids
  inherited from generic bases, diagnostic TSID001, agent skill `tarol-strongly-typed-id`.
* `Tarol.Results`, migrated from CLorat/Tarol and redesigned on C# 15 unions: `Result<T>` and `Result` are
  non-boxing `[Union]` structs (16/24/8 B instead of 48–64 B); failures are a closed `Violation` with exactly two
  kinds, `Invalid` (`FieldError`s) and `Error` (`ErrorType`, code, detail); `default` is `Error.Uninitialized`;
  `ValueTask` pipelines with `CancellationToken`; AOT-safe JSON envelope; bundled analyzers TRES001–TRES003;
  agent skill `tarol-results`.
* `Tarol.Options` (step 3), a package of its own with no dependencies: `Option<T>`, a non-boxing `[Union]` struct of
  `T` and `None`, the same API for value and reference types, `default` = `None`; `Map`, `Bind`, `Filter`, `Match`,
  `GetValueOrDefault`, `GetValueOrThrow`; `T?.ToOption()`, `ToNullable()`; JSON = the value or `null` (the `T?` wire
  format); bundled analyzer TOPT001–TOPT004; agent skill `tarol-options`. `Tarol.Results` depends on it and adds
  `option.ToResult(violation)` and `Result<T>.ToOption()`.
* `Tarol.Kernel` (step 4): `Actor` (`Anonymous`, `User`, `Service`, `System` on behalf of a user; no personal data),
  `OperationContext` (actor, `Option<Id<ITenant>>`, `Id<ICorrelation>`, `Option<Id<ICausation>>`; `Start`, `CausedBy`,
  `AsSystem`) with a fixed AOT-safe JSON contract; bundled analyzer TKRN001 (system clock read, inject `TimeProvider`);
  agent skill `tarol-kernel`. Concept notes on time, identity, tracing and security in `docs/architecture/`.
* `Tarol.Validations` (step 5): rule attributes `Range`, `Length`, `Pattern` (timeout), `Email`, `NotEmpty`, `Count`
  declared once on `readonly partial record struct` value objects; the bundled generator writes `Create(…) : Result<T>`,
  `Check`, limit constants, a conversion to the field, comparisons, an invariant `ToString` and a trusting JSON converter
  (old events always load), and implements `IValidatable` on input records (`Validated()`: all field errors with input
  paths such as `Lines[1].Quantity`, 0 B when valid). `ValueObjectJson` for source-generated contexts in the same
  assembly; diagnostics TVAL001–TVAL009; agent skill `tarol-validations`. Concept note `docs/architecture/validation.md`.
* `Tarol.Primitives` (step 6, part A): event-sourced `AggregateRoot<T>` and child `Entity<TRoot, T>` (`EntitySet`,
  `EntityRef`, children with several owner types via `CreatedIn<TParent>`); events are pure-payload records deriving from
  the library's nested `Event`/`Created`/`Deleted` bases (`sealed record Repriced(decimal Price) : Event;`), routed with a
  `StreamEvent` (version, entity id, parent id) beside them; a bundled generator writes the dispatch (target, owners,
  root), identity maps, cascade, strict replay, `Rehydrate`, `EventTypeOf` and the invariant check; `[Invariant]` rules
  returning `Result` run at the hand-off (`AggregateChanges.Prepare`), never on replay; a throwing `On` handler poisons
  the instance; `RecordedEvent` for stored events with Kernel's `OperationContext`; `AggregateTest` given/when/then
  helper; diagnostics TPRM001–TPRM015 (including "state assigned outside an On handler" and "rule, throw, clock or Raise
  inside an On handler"); agent skill `tarol-primitives`. Concept note `docs/architecture/domain-core.md`.
* `Tarol.Primitives` (step 6, part B): stored state. Every aggregate gets generated `StateSchema`,
  `WriteState(Utf8JsonWriter, aggregate, options)` and `ReadState(id, ref Utf8JsonReader, options) : Option<T>` on
  `IAggregateRoot<T>`: every instance field of the root and its entities as queryable camelCase JSON (children nested by
  id, `id`/`version`/`schema` on the root, `null` written, enums by name), read back without running a handler,
  invariant or rule; `StateSchema` is a stable FNV-1a hash of the shape, `None` on another shape (the store replays).
  `AggregateState.Write/Read`, `AggregateTest.StoredStateMatchesReplay`, `[StateName]`, `[LoadByReplay]`; diagnostics
  TPRM024–TPRM027 (a field the state cannot hold, a state name used twice or reserved, a state attribute without effect,
  a `[Transient]` member filled in `On`).
* `Tarol.Primitives` (step 6, part C): lifecycles. `[Lifecycle]` on a state enum and `[To(...)]` on its members declare
  the allowed steps; the generator writes `<Enum>Lifecycle` (`Initial`, Mermaid `Diagram`, `IsEdge`, `IsTerminal`,
  `Edges`, extension members `CanTransitionTo(to) : bool` and `RequireTransitionTo(to) : Result` with
  `Error.Conflict("<Enum>.NotAllowed", "<From> -> <To>")`) and, inside the aggregate's dispatch, a live-only check of
  every step of a lifecycle field: a step no `[To]` allows, or a created root/entity not in the initial state, is
  returned by `AggregateChanges.Prepare` before the invariants as
  `Error.Internal("Lifecycle.ForbiddenStep", "<Type>.<Field>: <From> -> <To> by <Event>")`. Replay and state loads never
  check. `[Transition<TEnum>(from, to)]` labels an edge in the diagram; `LifecycleViolation`; the protected,
  generated-use-only `ApplyLive`/`ApplyRaised`/`ForbiddenStep` on `AggregateRoot<T>`; diagnostics TPRM016–TPRM023;
  suppressor TPRMSPR01 (CS8524 on complete switches over lifecycle enums). Concept note
  `docs/architecture/state-transitions.md`.
* `Tarol.Persistence` (step 7, PR 7A): the aggregate store port `IAggregateReader` (`Load`, `Find` by unique lookup)
  and `IAggregateStore` (`Append` with expected version and `IdempotencyKey`, `Seen`), `StoredOutcome`, `StoreErrors`
  (`Stream.VersionConflict`, `Idempotency.DuplicateKey`, `Lookup.Taken`), and `InMemoryAggregateStore`: tenant
  isolation, atomic appends, `RecordedEvent` with the operation context and commit time, stored-state loads, defined
  lookups; agent skill `tarol-persistence`. Concept and decisions of step 7 in `docs/architecture/changes.md`.
* `Tarol.Changes` (step 7, PR 7B): `IChange<T>`, `ICreation<T>`, `ILoads<T>`, `[Read]`, `ChangeContext`,
  `ChangePipeline` (validation before any read, automatic load, permission hook `IChangeAuthorization`, time read once,
  `Prepare`, append with expected version and idempotency key, discard on failure, version conflicts returned),
  `ChangeOptions`, `ChangeOutcome`, `ChangeTest`; bundled generator (target, `[Read]` loads, typed
  `GeneratedChanges.Dispatch`) with diagnostics TCHG001–TCHG005; agent skill `tarol-changes`.

* Persistence step 8A: generated `T.StreamType` (`[StreamType]`, TPRM028 duplicate, TPRM029 empty) and
  `T.EventTypes` (`EventCatalog`) on every aggregate; `EventCodec` with `IEventUpcaster` chains in `Tarol.Persistence`;
  `IEventFeed.ReadAll(GlobalPosition, limit)`; `StoreLookups` shared by stores; `InMemoryStoreOptions` (state options,
  codec round trip, lookups). Design: docs/architecture/persistence.md, docs/migration/persistence.md §20.

* `Tarol.Persistence.Npgsql` (step 8B): `NpgsqlAggregateStore` (`IAggregateStore`, `IEventFeed`) on plain Npgsql with
  one guarded statement per commit, stored state as queryable JSON, key-function lookups, idempotency keys and
  `PurgeKeys`, `RebuildStates<T>`, the (transaction, position) feed, `NpgsqlSchema.Script` and `EnsureSchema`;
  integration tests on Testcontainers PostgreSQL and the CI `integration` job; agent skill `tarol-persistence-npgsql`.

* Persistence step 8C: `ReactionTable` (`Tarol.Persistence`); outbox rows per (event, reaction) in the commit
  (`NpgsqlStoreOptions.Reactions`) and `NpgsqlAggregateStore.Outbox` (`Claim` with `FOR UPDATE SKIP LOCKED`, the event,
  a lease and a claim id; `Complete`, `Fail`, `Pending`); one stream's rows reach a reaction in version order.

* Changes step 9A (reactions to own events): `[OnEvent<T>]` on a static factory of a change (`Name`, `DueAt`),
  `ReactionSet` (table, schedule, runner, `Combine`, the reaction's `Context` and `Options`: system actor on behalf of
  the user, causation = event id, key (reaction, event id)); the generator emits a public `GeneratedReactions.Set` per
  assembly, diagnostics TCHG006–TCHG008. Validations: TVAL006 no longer fires inside a static entry factory of an
  `IValidatable` change (C-13). Design: docs/architecture/sagas.md, docs/migration/sagas.md §16.

* Persistence step 9B: the outbox port `IOutbox` (`Claim`, `Complete`, `Retry`, `Bury`, `DeadLetters`, `Redrive`,
  `Pending`, `WaitForRows`) on both stores, `IOutboxReactions` (implemented by `ReactionSet`), `DeadLetter`; the
  in-memory store writes outbox rows; the Npgsql claim uses a maintained `head` flag (constant cost per claim), scheduled
  reactions run by due time, dead letters move to `outbox_dead`, claims carry the `traceparent`, `Notify` is opt-in.
  Changed: `OutboxRow` moved to `Tarol.Persistence`; `NpgsqlStoreOptions.Reactions` takes `IOutboxReactions`;
  `NpgsqlOutbox.Fail` is `Retry`. Design: docs/migration/sagas.md §17.

* `Tarol.Sagas` (step 9C): `ReactionWorker` (claim → `ReactionSet.Run` → complete, retry with backoff or bury; a Consumer
  activity per row linked to the committing trace; source-generated logs), `DeliveryPolicy` (C-9 with NotFound retries,
  10 attempts, 1 s … 5 min, deterministic jitter), `ReactionOptions`, `ReactionHost` (`BackgroundService` with the key
  purge), `AddTarolReactions`, `ReactionTest`; agent skill `tarol-sagas`; integration tests on PostgreSQL; an Aspire
  AppHost sample (`samples/Sagas`). Design: docs/architecture/sagas.md, docs/migration/sagas.md §18.

* `Tarol.Security` (step 10A): the change is ASP.NET Core's requirement (`ChangeRequirement` with the run's
  `ChangeContext`), the aggregate the resource; `ChangeRoles` maps the app's roles onto changes and `Read<T>` intents,
  row rules `ChangeHandler<TChange, TAggregate>` grant or `Deny` (deny wins), nothing granted is
  `Error.Forbidden("Auth.Forbidden")`, anonymous `Error.Unauthorized("Auth.Required")`; denials logged (event 1001) and
  counted (`tarol.authorization.denied`); `SystemPrincipal`; `AddSecurity` / `AddTarolSecurity`; agent skill
  `tarol-security`. Design: docs/architecture/rest.md §3, docs/migration/rest.md §14.
* One registration (step 10A): `services.AddTarol(jsonContext, codec => …)` returns a `TarolBuilder` (codec, clock,
  pipeline) with `UseInMemoryStore`, `UseNpgsqlStore` (EnsureSchema as a hosted service), `WithReactions`,
  `AddReactionWorker`, `AddSecurity`; `IIdempotencyKeyPurge` (the worker purges through the store).
* Idempotency keys carry an optional `Fingerprint`, stored with the outcome (`StoredOutcome.Fingerprint`, column
  `idempotency_keys.fingerprint`, added by `EnsureSchema` to existing tables); another fingerprint under the same key is
  `Error.UnprocessableEntity("Idempotency.KeyReused")` (step 10A, R-9).

* `Tarol.Changes.AspNetCore` (step 10B, + generator TWEB001–TWEB006):
  * `[Endpoint]` on change records and `IView<TAggregate, TSelf>` views becomes generated `RequestDelegate`s, mapped by
    `MapChanges(names)`;
  * a generated member binder reports every `Json`/`Required` error;
  * the edge context comes from the principal, with actor and tenant hooks and a `{tenant}` route check;
  * `Idempotency-Key` is fingerprinted, `If-Match` and `ETag`/304 are mapped, problem details are written by hand or
    through `IProblemDetailsService`;
  * `[Webhook]` factories: HMAC-SHA256 with rotation, a timestamp window, a delivery-id key, contract versions and the
    answer table;
  * `GeneratedOpenApi.AddTarolEndpoints` when the app references `Microsoft.AspNetCore.OpenApi`;
  * `AddEndpoints` on the builder; agent skill `tarol-endpoints`.
* `Tarol.Security.Zitadel` (step 10B, optional): `AddZitadel(…)` sets JwtBearer against the Zitadel issuer and
  project and a claims transformation (project roles → roles, organisation → tenant via `ZitadelTenants.Of`, the
  service role → `Actor.Service`); agent skill `tarol-zitadel`.
* `samples/Sagas`: ArtFlow's API and worker with Zitadel under the AppHost.
* `Tarol.Messaging` (step 11A, + generator TMSG001–TMSG003, TMSG006, TMSG007): consumers of other systems' messages.
  `[Consumes(contract, version)]` on a static `From` factory of a change, one per contract version (the anti-corruption
  layer); the generated `GeneratedConsumers.Set` (a switch per consumer, contract and version; malformed data, an unknown
  version and an unknown contract are distinguished); `MessageConsumers` checks the source against the contract prefix
  and the tenant against the source's allow-list (`MessagingOptions.Trust`, `SourceTrust`), runs the change as
  `System("Consumers.<name>")` on behalf of the sender's actor with the key `(tenant, consumer:<name>, message id)` (the
  inbox), classifies with `Delivery.Classify` and the reaction worker's `DeliveryPolicy`, and keeps dead letters with
  their headers and bytes (`IMessageDeadLetters`, `Redrive`); the envelope `Message` is CloudEvents 1.0 binary mode
  (`MessageHeaders`); the transport port `IMessageTransport` with `InMemoryTransport`; `MessagingHost` and
  `AddMessaging(…)` on the `TarolBuilder`; `MessageTest`; `ActivitySource "Tarol.Messaging"`; agent skill
  `tarol-messaging`. TVAL006 is exempt inside `[Consumes]` factories.
* `Tarol.Messaging` (step 11B, + generator TMSG004, TMSG005; TMSG006 also for contract records): publications and
  effects from the outbox. `[Publishes<TEvent>(contract, version)]` on a static mapping method (`null` = not published);
  the generated `GeneratedPublications.Set` (`PublicationSet`, `PublicationInfo`, rows `publish:<contract>@<version>`, the map
  through the app's JSON options); `MessagePublisher` runs the rows on the reaction worker (`Message.Of`: message id = event
  id, `traceparent` = the `publish <contract>` span); effects `IEffect<TEvent>` with `EffectContext` (key = the event id),
  `EffectSet`, rows `effect:<name>`; `AddMessaging(…).Publish(…)`, `.Effect<TEvent, THandler>(name)`; `MessagingTest`
  (several systems in memory). TVAL006 is exempt inside `[Publishes<T>]` methods.
* `Tarol.Messaging.Npgsql` (step 11C): the in-database bus. `NpgsqlBus` (`IMessageTransport`; `EnsureSchema` under an
  advisory lock, publish in one statement deduplicated by (event, contract, version) with one delivery per subscription and
  an opt-in NOTIFY, a claim with `FOR UPDATE SKIP LOCKED` ordered after `RETURNING`, acknowledge and retry presenting the
  claim, the publisher's role stamped by the database and mapped to its source, `PurgeMessages`, `Pending`);
  `NpgsqlBusOptions`; `NpgsqlBusSchema` (`Script`, `RowLevelSecurityScript` with policy `publish_own_prefix`,
  `GrantScript`, `DeadLettersScript`); `NpgsqlMessageDeadLetters`; `UseNpgsqlBus(…)`, `UseNpgsqlDeadLetters(…)`; agent skill
  `tarol-messaging-npgsql`.
* `Tarol.Messaging.Nats` (step 11D): the NATS JetStream transport. `NatsTransport` (a stream per source with a duplicate
  window, `Nats-Msg-Id` = the dedup id and a duplicate counted as success, a durable pull consumer per subscription,
  `Nak(delay)`, the server's permission errors logged, `StreamOf`, `Duplicates`), `NatsTransportOptions` (`Prefix`,
  `PublishTimeout`, …), `UseNats(…)`; agent skill `tarol-messaging-nats`.
* `Tarol.Messaging.RabbitMq` (step 11D): the RabbitMQ transport. `RabbitMqTransport` (a topic exchange per source, quorum
  queues, publisher confirms with `mandatory` publishes: unroutable is logged and counts as delivered, retries through a TTL
  retry queue with `x-tarol-attempt`, `user_id` as `AuthenticatedAs`, a passive declare of another system's exchange,
  `ExchangeOf`, `QueueOf`, `Unroutable`), `RabbitMqTransportOptions` (`Prefix`, `StampUserId`, …), `UseRabbitMq(…)`; agent
  skill `tarol-messaging-rabbitmq`.
* `Tarol.Messaging.SignalR` (step 11E): the UI push. `PushToUi(…)` (`UiPushOptions`: `Forward`, `Rules`, `Name`, `HubPath`,
  claims) with `UiPushForwarder` (a consumer subscription → SignalR groups `<tenant>/<topic>`, trust as for consumers);
  `MessageHub` (a plain `[Authorize]` hub: `Subscribe`, `Unsubscribe`, `Publish`, the tenant from the principal, the sender
  stamped by the server, `Group`), `ITopicAuthorizer`, `TopicRules`, `TopicOperation`, `HubConnections` (connections aborted
  when the application stops), `PushedMessage`, `SignalRJson`; `MapMessageHub()`; the .NET client `MessagePushClient`
  (`Connect`, `Start`, `Subscribe` with handles, `Publish`, `Stop`; replays subscriptions on reconnect); agent skill
  `tarol-messaging-signalr`. Replaces the old Messaging projects (Protocol, Transport, Backplane, Gateway; review findings
  F1–F23, docs/migration/messaging.md §19).
* `samples/Sagas`: billing as a second system (consumes `artflow.auction-settled`, publishes `billing.invoice-paid`), NATS
  JetStream under the AppHost (RabbitMQ with `ARTFLOW_BROKER=rabbitmq`).
* `Tarol.Changes`: `IOutboxRowHandler` (an outbox row kind the reaction worker runs) and `OutboxRows` (handlers joined);
  `ReactionSet` implements it (`ActivityName`), `ReactionSet.Empty`.
* `Tarol.Persistence`: `OutboxReactions.Combine`, `TarolBuilder.WithOutboxRows` and `TarolBuilder.OutboxTable` (the
  store's table: reactions and added rows).

* `Tarol.Queries` (step 12A): projections and read models. `[Projection(name, Version)]` on a `sealed partial record`
  row with static/instance `When(event)` methods and `[Sort]` members; the bundled generator implements
  `IProjection<TRow>` explicitly (`Name`, `Version`, `StreamType`, `Columns`, `Apply`); `ProjectionColumn<TRow>` /
  `ColumnType` / `ColumnValues` (typed columns in snake case, enums as numbers, times in UTC microseconds, uuids in
  PostgreSQL order); `ProjectionHost<TRow>` (`BackgroundService`: feed after the checkpoint, batch 2,000, versions ≤ the
  row's skipped, one guarded write of rows + checkpoint, `Building` → `Current`, backoff, a span per batch of
  `Tarol.Queries`, logs 12001–12003), `ISqlProjection` / `ISqlProjectionStore` / `SqlProjectionHost`, `ProjectionOptions`,
  `IFeedSignal`; `IReadModelStore<TRow>` / `IReadModels`, `RowKey`, `Versioned<TRow>`, `ProjectionCheckpoint`,
  `ProjectionState`, `ProjectionBatch<TRow>`; list queries `ListQuery` (tenant, `ColumnFilter`s, sort, `After` cursor,
  `Offset` ≤ 1,000, `Limit` ≤ 200, `Count`), `ListQueryPlan<TRow>`, `Page<T>` (`Next`, weak `ETag` over (id, version),
  `Position`, `Total`), `Cursor`; `IProjectionPositions` / `ProjectionPositions` (`WaitFor`); `InMemoryReadModels`,
  `InMemoryReadModelStore<TRow>`, `ProjectionTest<TRow>`; `TarolBuilder.UseInMemoryReadModels()`, `AddProjections(…)`,
  `AddProjectionPositions()`; `IQuery<TRow>` and `[Descending]` for the list endpoints of 12C; diagnostics TQRY001–TQRY004;
  agent skill `tarol-queries`.

* `Tarol.Queries.Npgsql` (step 12B): PostgreSQL read tables. `NpgsqlReadModelStore<TRow>` (a table
  `rm_<name>_v<n>` per projection version: tenant, id, version, the typed columns of the generated map with text
  `COLLATE "C"`, the row as `jsonb`, an index (tenant, column, id) per sort; the guarded batch: checkpoint first, then one
  `unnest` upsert above the stored version; list queries, checkpoint and count in one round trip; `NotPreparedCode`),
  `NpgsqlReadModels` (`IReadModels`, `ISqlProjectionStore`, `DropVersion`), `NpgsqlReadModelSchema` (`ProjectionsTable`,
  `Script<TRow>`, `SqlType`), `NpgsqlFeedSignal` (`LISTEN <schema>_feed`), `NpgsqlReadModelOptions`,
  `TarolBuilder.UseNpgsqlReadModels(…)`; the checkpoint table `projections` with the state `building` / `current`;
  agent skill `tarol-queries-npgsql`.
* `Tarol.Persistence.Npgsql`: `NpgsqlStoreOptions.NotifyFeed` (a `NOTIFY` on `NpgsqlAggregateStore.FeedChannel` with
  every commit, off by default), `NpgsqlAggregateStore.FeedChannel`, `NotifiesFeed`.
* `Tarol.Persistence`: `StoreErrors.IsVersionConflict<T>(Result<T>)`.

* `Tarol.Changes.AspNetCore` (step 12C): list entries. `[Endpoint("GET …")]` on an `IQuery<TRow>` record over a
  projection: nullable filter members from the query string, a `Sort` enum over `[Sort]` columns (`[Descending]`, the id
  as tie-break), route members, `[FromClaim(type)]` members from the caller; `limit`/`after`/`offset`/`count`; the page
  as `{items, next, total}` with a weak `ETag` and 304, `Tarol-Position`, `Tarol-Read-After` (waits, then
  `Tarol-Stale: true`), 503 + `Retry-After` while the projection builds; `Positions` (header names, `Format`, `TryParse`),
  `QueryEndpointOptions` (`ChangeEndpointOptions.Queries`: `MaxLimit`, `DefaultLimit`, `MaxOffset`, `ReadAfterTimeout`,
  `RetryAfter`), `FromClaimAttribute`, `QueryParameter`, `TarolEndpointMetadata.Query`, `ChangeEndpoints.BuildingCode`;
  OpenAPI for lists (query parameters with enum values, `Tarol-Read-After`, the page schema, 304, 503); diagnostics
  TQRY005–TQRY006; the queries part of the skill `tarol-queries`.
* `Tarol.Changes`: `ChangeOutcome.Position` (the commit's feed position) and `IChangeAuthorization.CheckList<T>` (a
  default method that refuses); `Tarol.Security`: `ChangeAuthorization.CheckList<T>` (the role map's `Read<T>`, no row rules).

* `samples/Sagas` (step 12D): the auction list: a `[Projection("auction-list")]` row hosted by the worker (feed
  `NOTIFY`), `GET /auctions` and `GET /my/auctions` (claim-bound) in the API, read-your-writes after each bid in
  `apphost.sh`; the user guide `docs/queries/README.md`.

* `Tarol.Queries.Npgsql` (step 13A): pushed checkpoints. `NpgsqlReadModelOptions.PushCheckpoints` makes every
  projection batch (row and SQL projections) send `NOTIFY <schema>_checkpoint, '<name>:<version>:<tx>:<pos>:<state>'`
  in its transaction (delivered after the commit), and registers `NpgsqlProjectionPositions` as `IProjectionPositions`:
  one `LISTEN` connection per process, the checkpoints in memory (`Checkpoint<TRow>` without a round trip), `WaitFor`
  woken by the notification, a 1 s poll and a poll after every reconnect as the fallback, `IsListening`; logs 12101–12103.
* `Tarol.Caching` (step 13B, new): cached list pages. `CachedReadModels` decorates `IReadModels`; its stores answer
  `Search` from an `IPageCache` (`TryGet`, `Put`, `Clear`), keyed by `PageKey` (the tenant, the projection's name and
  version, the canonical query with `[FromClaim]` values as filters) and valid only while
  `IProjectionPositions.Checkpoint<TRow>()` equals the position read before the search; nothing cached while building;
  `Prepare`, `Checkpoint`, `Load`, `Write`, `Find` pass through. `MemoryPageCache` (`CacheOptions`: 10,000 entries,
  64 MB estimated, 5 min idle expiry on `TimeProvider`; no eviction thread), `AddCaching(configure)`, the meter
  `Tarol.Caching` (hits, misses, evictions, entries, bytes); agent skill `tarol-caching`.
* `Tarol.Changes.AspNetCore` (step 13C): `CacheHeaders` (`private, no-cache`, `Authorization`, `Apply`). `samples/Sagas`
  caches the auction list (`AddCaching()` in the API, `PushCheckpoints` in both processes); the guide
  `docs/queries/caching.md` (what is cached, how a request goes, why there is no output caching).
* `Tarol.Changes` (step 14A): scheduled change entries. `[Scheduled(cron, TimeZone, Name, Missed, PerTenant)]` on a static
  `From(JobOccurrence)` factory of a change; the generated `GeneratedJobs.Set` (`JobSet`: `Definitions`, `Run`, `Combine`,
  `Context`, `Key`, `Options`) runs an occurrence as `Actor.System("Jobs.<name>")` with the key (tenant, `job:<name>`,
  occurrence UTC); `JobOccurrence` (whole seconds, `Local`), `JobDefinition`, `Missed` (`Latest`, `All`, `Skip`);
  diagnostics TCHG009 (factory shape), TCHG010 (invalid cron literal), TCHG011 (duplicate job name).
* `Tarol.Persistence` (step 14A): the job rows port `IJobRows` (`Sync`, one-row `Claim` with a lease, `Move`, `Advance`,
  `Extend`, `Retry`, `Bury`, `DeadJobs`, `Redrive`, `Rows`, `Overdue`; records `JobSchedule`, `JobSyncChange`, `JobRow`,
  `JobState`, `DeadJob`), in-memory rows as `InMemoryAggregateStore.Jobs`, `TarolBuilder.UseStore(factory, outbox, jobs)`.
* `Tarol.Persistence.Npgsql` (step 14B): the job rows on PostgreSQL. `jobs` (one row per (tenant, job): schedule text,
  `enabled`, the cursor `next_at`, claim id, lease, attempts, last error) and `jobs_dead` (with a pending `redrive`) in the
  version 1 script; `NpgsqlAggregateStore.Jobs` (`NpgsqlJobRows`): one-row claims with `FOR UPDATE SKIP LOCKED` (a pending
  re-drive first), claim-guarded single statements, `Sync` in one transaction under an advisory lock; `UseNpgsqlStore`
  registers `IJobRows`.
* `Tarol.Jobs` (step 14C, new): scheduled jobs. `IJob` classes (`JobRun.Change`/`Create` keyed by occurrence and target or
  part), `AddJobs(j => j.Add(GeneratedJobs.Set).Add<TJob>(cron), o => …)` (`JobsBuilder`, `JobOptions`: zone, `Missed`,
  `MaxCatchUp`, `Grace`, `PerTenant`, `Enabled`, `Policy`; `JobHostOptions`: workers, poll, lease, tenant sync), `JobHost`
  (syncs the rows at startup, one-row claims, the cursor moved per occurrence, a heartbeat every lease/3, retries with
  `DeliveryPolicy`, dead jobs, re-drives), our own 5-field cron `Cron` (Vixie OR, names, DST: end of the gap, once in the
  repeated hour) and `Schedule.Every`, `ITenantDirectory`, the override `Tarol:Jobs:<name>:Cron`/`:TimeZone`/`:Enabled`
  validated at startup, the system jobs `tarol.purge-keys` and every registered `MaintenanceTask`, `JobTest` (standing clock,
  `Advance`, `Down`, `Runs`, dead jobs, tenants, configuration, drained reactions), logs 14001–14010,
  `ActivitySource "Tarol.Jobs"`; agent skill `tarol-jobs`. `Tarol.Persistence`: `MaintenanceTask`, `IMaintenanceHost`.
  `Tarol.Messaging.Npgsql`: `NpgsqlBusOptions.Retention` (7 days) and `PurgeBatch`, the system job `tarol.purge-messages`.
  `samples/Sagas`: the nightly settlement report in the worker; the guide `docs/jobs/README.md`.

* Step 15A (docs/migration/operations.md §15, O-4): versioned schema upgrades in `Tarol.Persistence.Npgsql`
  (`UpgradeStep`, `NpgsqlSchemaDefinition`, `NpgsqlSchemaUpgrader`, `SchemaMode.VerifyOnly`, `SchemaState`,
  `NpgsqlSchema.Steps/Definition/UpgradeScript/AuditScript`, `NpgsqlStoreOptions.SchemaMode/SchemaLockTimeout`, logs
  8001–8005): a polled session advisory lock per schema, steps with `lock_timeout` and retries, `CONCURRENTLY` steps with
  invalid-index cleanup, `schema_version.compatible_from`. Store schema version 2: `operations_audit`, `dead_at` indexes on
  `outbox_dead` and `jobs_dead`. `Tarol.Messaging.Npgsql`: the bus and `messages_dead` on the same upgrader
  (`NpgsqlBusSchema.Definition/UpgradeScript`, `DeadLettersVersion` 2 with `messages_dead_version`, `NpgsqlBusOptions.SchemaMode`).

* Step 15B (docs/migration/operations.md §16, O-1, O-3, O-6, O-8): operations contracts in `Tarol.Persistence`
  (`OperationsSignal`, `IDeadLetterSource`, `DeadLetterQuery` keyset paging, `DeadLetterPage`); `Discard` and paged dead letters
  on `IOutbox`, `IJobRows`, `IMessageDeadLetters`, `IOutbox.OldestDue`; audited re-drive and discard on PostgreSQL
  (`operations_audit`); meters `Tarol.Changes`, `Tarol.Sagas`, `Tarol.Jobs`, `Tarol.Queries`, `Tarol.Messaging` from
  `IMeterFactory`; `ActivitySource "Tarol.Changes"` (one span per pipeline run) and logs 7001–7003; the snapshot signals of the
  stores, the bus and the read models; `IProjectionGate` and `NpgsqlProjectionGate`; `NpgsqlTracing.TraceOnlyInActivities`.

* Step 15C (docs/migration/operations.md §17, O-1, O-2, O-3, O-5): `Tarol.Operations`, a new package on the ASP.NET Core
  shared framework: `AddOperations` (the snapshot poll and its gauges on the meter `Tarol.Operations`), health checks in the
  groups `live`, `ready` and `tarol` with `MapTarolHealth`, `IDeadLetterAdmin` and `MapTarolAdmin(prefix, policy)` (paged
  lists, re-drive, discard, projection versions, every act audited), the optional `DeadRetention` as `tarol.purge-dead`; logs
  15001–15005; skill `tarol-operations`. `Tarol.Persistence` gains `ReadinessCheck`, `IReadModelAdmin` and
  `IDeadLetterRetention`, which the Npgsql packages implement.

* Step 15D (docs/migration/operations.md §18): the guide `docs/operations/README.md`; `samples/Sagas` with `AddOperations`,
  `MapTarolHealth` in the API and the worker, `MapTarolAdmin` behind the Zitadel role `tarol-admin`, traces with `Tarol.*` and
  `Npgsql`, metrics to OTLP, and the AppHost waiting on `/health/ready`. **Step 15 (operations) done.**

* Step 16A (docs/migration/data-protection.md §15, DP-1 … DP-4):
  * `Tarol.Primitives`: `[PersonalData]` (`Subject`) and `[Retention(Days)]`; the generator emits `PersonalShape` on
    `T.EventTypes` (`EventCatalogEntry.Personal`, `EventCatalog.State`, `RetentionDays`); diagnostics TPRM030–TPRM033;
  * `Tarol.Persistence`:
    * sealed fields in the `EventCodec` (`tdp1.` envelopes, AES-256-GCM bound to tenant and subject; `Seal`/`Decode` with
      a `KeyRing`, `SealState`/`OpenState`);
    * the key-store port `IKeyStore` with `InMemoryKeyStore`, `IKeyEncryptionKey` with `ConfiguredKeyEncryptionKey`;
    * blind-indexed personal lookups (`StoreLookups.DefinePersonal`);
    * `IDataSubjects`, `ErasureParticipant`, `RetentionPolicy`; `InMemoryStoreOptions.Keys`;
    * `DataProtection.SubjectErased`.
* Step 16B (§16, DP-10): `Tarol.Persistence.Npgsql`:
  * schema version 3 (`tenant_keys`, `subject_keys`, `subjects` columns, a GIN index);
  * `NpgsqlKeyStore` (cached keys, the tombstone and `NOTIFY` in one statement, `LISTEN` eviction) and `UseNpgsqlKeyStore`;
  * `NpgsqlStoreOptions.Keys` seals on append and opens on every read;
  * the subject queries, `DeleteStreams` and `SealHistory<T>`; logs 8006–8008.
* Step 16C (§17, DP-5 … DP-7, DP-9): `Tarol.DataProtection`, a new package:
  * `AddDataProtection` (the master key from `Tarol:DataProtection:MasterKey`, retention from
    `Tarol:Retention:<type>:Days`), `UseInMemoryKeyStore`;
  * `DataSubjects`: an erasure writes the tombstone, re-folds the projections, drops the page cache in every process,
    deletes personal lookups and audits; export as one JSON document opened with the subject's key only;
  * the system job `tarol.retention`; the meter `Tarol.DataProtection`; logs 16001–16008; skill `tarol-data-protection`.

  Additions elsewhere: `IDataProtectionStore` (both stores); `IReadModelStore.Replace`/`Remove`, `ProjectionEraser<TRow>`;
  `IPageCache.Clear(tenant)`, `PageCacheEraser`; the admin endpoints `…/subjects/{id}/export|erase`;
  `OperationsOptions.AuditRetention` → `tarol.purge-audit`; `NatsTransportOptions.MaxAge`.
* Step 16D (§18): the guide `docs/data-protection/README.md`; `samples/Sagas` with a `BidderProfile` whose name and email
  are sealed, exported and erased through the admin entry, and a retention on `Payout`. **Step 16 (data protection) done.**

### Fixed

* `Tarol.Messaging.SignalR`: a restart attempt that timed out (`TaskCanceledException`) ended `MessagePushClient`'s restart loop,
  leaving the client disconnected for good; only the client's own stop ends it now (docs/migration/messaging.md §20).
* Step 16: `AllocationTests` measured one loop and failed under the parallel suite when a tier-up landed in it; both
  allocation tests assert the least allocating of five loops (#59).

* Step 15: `PushTests` waited a fixed 300 ms for the SignalR client's replay and an upcast test rewrote events of the shared
  schema (15-0); the `checkpoint-listen` readiness reported a LISTEN that had not started yet as down (15C).
* `Tarol.Sagas`: a reaction that ran while the host was shutting down was not completed (the verdict used the stopping
  token) and ran again after its lease; the verdict is now recorded regardless. Three timing-dependent tests made
  deterministic (docs/migration/sagas.md §18.3).

### Changed

* **Licence** (step 17D): Tarol is licensed under the GNU AGPL-3.0-only (was: proprietary, owner-only); the copyright
  holder also offers a commercial licence (`NOTICE`). Packages declare `PackageLicenseExpression` `AGPL-3.0-only` and carry
  `LICENSE` and `NOTICE`; contributions need a CLA (`CONTRIBUTING.md`).
* Step 16 (announced in docs/migration/data-protection.md §10):
  * the store's schema is version 3. Its steps are compatible from 1; a store with keys raises `compatible_from` to 3,
    so pods from before step 16 refuse such a schema;
  * the outbox claim decodes with keys, so reactions see the fields opened;
  * the plain `EventCodec.Decode` reads sealed fields as `null`;
  * `IReadModelStore` and `IPageCache` gain members (implementers must add them);
  * **NATS JetStream streams keep messages 7 days** (`MaxAge`; they kept them for ever before).

* Step 15B (announced in docs/migration/operations.md §10): the data sources `UseNpgsqlStore(connectionString)` and
  `UseNpgsqlBus(connectionString)` build trace Npgsql commands only inside an activity (`TraceRootCommands` opts out);
  `UseNpgsqlReadModels` lets one host per projection version run (`SingleRunner`, on by default); `DropVersion` gives up
  after its lock timeouts (`TimeoutException`); `UseNpgsqlStore(Func)` registers its data source; the ports `IOutbox`,
  `IJobRows`, `IMessageDeadLetters` gain members (implementers must add them); `tarol.authorization.denied` has the unit
  `{request}`.
* Step 15A (announced in docs/migration/operations.md §10): `EnsureSchema` of the store, the bus and `messages_dead` takes a
  polled advisory lock (6 concurrent first starts no longer fail), runs the version 1 script only on an unversioned schema,
  and **accepts a newer schema while it is compatible** (`compatible_from` ≤ the package's version) instead of refusing any
  newer one. `NpgsqlSchema.Version` is 2; code of step 14 refuses a version 2 schema. `NpgsqlSchema.Script` is the version 1
  baseline (it stores 1); a migration tool runs `NpgsqlSchema.UpgradeScript(schema, 0)`. `Tarol.Messaging.Npgsql` depends on
  `Tarol.Persistence.Npgsql`.
* Step 14C (announced in docs/migration/jobs.md §10, J-9): with a job host (`AddJobs`), `AddReactionWorker` no longer sets
  `ReactionOptions.PurgeKeys` from the store: the system job `tarol.purge-keys` purges once per hour across the cluster
  instead of in every worker process. Without a job host nothing changes.
* Step 14A (announced in docs/migration/jobs.md §10): TVAL006 is not reported inside a static `[Scheduled]` factory of an
  `IValidatable` change (C-13), as for `[OnEvent<T>]`, `[Webhook]` and `[Consumes]`.
* Step 13C (announced in docs/migration/caching.md §10): list answers of `Tarol.Changes.AspNetCore` (200, 304 and the
  problems) and view answers (200, 304) carry `Cache-Control: private, no-cache` and `Vary: Authorization`: shared caches
  no longer store them, browsers revalidate by `ETag`.

* Step 12C (announced in docs/migration/queries.md §10): change answers carry `Tarol-Position`; `ChangeOutcome` gains
  `Position` as a last positional member with a default (source compatible; a replayed outcome has `GlobalPosition.Start`).
  `IChangeAuthorization` gains `CheckList<T>` as a default interface method (existing implementations compile and refuse
  lists). `Tarol.Changes.AspNetCore` depends on `Tarol.Queries`.
* Step 12B (announced in docs/migration/queries.md §10): `IAggregateStore.Append` returns `Result<GlobalPosition>`
  (the commit's position in the feed: its transaction and the position of its last event; `GlobalPosition.Start` when
  nothing was appended) instead of `Result`, in `InMemoryAggregateStore` and `NpgsqlAggregateStore`. Callers that only
  test `IsSuccess` or `TryGetValue(out Violation?)` compile unchanged; `Result` comparisons need `.ToResult()`.

* Step 11B: the reaction worker (`ReactionWorker`, `ReactionHost`) takes an `IOutboxRowHandler` instead of a
  `ReactionSet` (source compatible: a `ReactionSet` is one) and names each row's span by its handler; the hosted worker
  joins every `IOutboxRowHandler` registered in DI with the reactions. `AddReactionWorker()` without reactions runs with
  `ReactionSet.Empty` (it threw). The stores read `TarolBuilder.OutboxTable` (reactions + added rows). `AddMessaging`
  needs a dead-letter store only with consumers. TMSG003's message names the attribute (`[Consumes(…)]` or
  `[Publishes<T>(…)]`). Full list: docs/migration/messaging.md §16.

* Changes (step 10A): `IChangeAuthorization.Check` takes the `ChangeContext` and a non-null principal and is called
  only for runs with `ChangeOptions.Principal`; it runs before the expected version is compared (a refused caller no
  longer gets 409 with the current version); a principal without an authorization is `Error.Forbidden("Auth.NotConfigured")`.
  `Verdict` and the delivery classification moved from `Tarol.Sagas` to `Tarol.Changes` (`Delivery.Classify`;
  `DeliveryPolicy.Classify` forwards). `AddTarolReactions` guards a second call by its own marker. Full list:
  docs/migration/rest.md §14.2.

* Validations: `[GenerateValidation]`, `ValidateValues()`, `ValidateInvariants()`, `[Invariant]` `bool` rules and
  `IPrerequisite`/`IPrerequisiteAsync` are gone. Rules live on value objects and inputs; invariants return to step 6 as
  `[Invariant]` rules returning `Result`; prerequisites are guard clauses returning `Result` with `now` as a parameter.
  Full list: docs/migration/validations.md §10.

* `Tarol.Results`: the typed `Result<T>.Value` is replaced by patterns, `TryGetValue` and `GetValueOrThrow()`
  (C# unions require `object? Value`); `Combine<T1,T2>` now combines failures like `Combine(params)`; `TapError` is
  `TapFailure`. Full list: docs/migration/results.md §7.
* Options: the C# 14 extension members on every `T?` (`IsSome`, `IsNone`, `Value`, `T.Some`, `T.None`, `Or`, `Match`,
  `Map`, `Bind`, `Filter`) are replaced by `Option<T>`. Full list: docs/migration/options.md §8.
* `Tarol.Results` analyzers: new TRES005 (ignored `TryGetValue`) and TRES006 (`ok ? value : default`) for
  `Result<T>`.
* Step 3 follow-up (after PR #5): `Option<T>` moved from `Tarol.Results` (namespace `Tarol.Results`) into
  `Tarol.Options` (namespace `Tarol.Options`); `Option<T>.ToResult` is now an extension in `Tarol.Results`; the Option
  rules TRES003 (Option part), TRES004, TRES005 (Option part) and TRES006 (Option part) are TOPT001–TOPT004.
  Mapping: docs/migration/options.md §7.1.

* Primitives (step 6): the envelope left the event (no `Version`/`Timestamp`/ids on event objects; `StreamEvent` beside
  it), `DomainClock` is gone, deletes are raised by the entity itself, the old generic event bases
  (`DomainEvent<R, X>`, `CreateDomainEvent`, `DeleteDomainEvent`) and the generated `closed` bases are replaced by the
  library's nested bases with the same user syntax. Full list: docs/migration/primitives.md §10.
* `InMemoryAggregateStore` keys streams by `T.StreamType` instead of the CLR name, copies the history only to replay,
  and falls back to a replay when a stored state does not parse (it threw `JsonException`); docs/migration/persistence.md §20.

### Removed

* Kernel (step 4): the ambient `Tarol.Clock`, the markers `IUser` and `ITrace`, `IUserContext`/`ContextUserType`
  and `Security.IAuthorizer` are not carried over (docs/migration/kernel.md §8). `ITenant` stays as a phantom type.
* Primitives (step 6): `Value`, `TarolPrimitivesJson`, `AggregateRootChangeAccessor`, `IDomainEvent*`, the writable
  `FindRef` lookups and `GlobalUsings.cs`; the event codec (`IEventCodec`, `EventColumns`, `IEventUpcaster`) moves to
  step 8 and snapshots become stored state in step 6 part B (docs/migration/primitives.md §10).

* `IdJsonConverterFactory` (see docs/migration/stronglytypedid.md).
* `Tarol.StronlgyTypedId.EntityFrameworkCore` is not carried over: EF Core is not Tarol's persistence provider.
* `Tarol.Results`: `IResult`/`IResult<T>`, `Violation.None`, `ViolationType` (replaced by `Invalid` + `ErrorType`),
  `Violation.Aggregate`, `Switch`, `MatchAsync`, `TryGetError` and every `Task`-based overload.
* The old `Tarol.Options` `T?` extensions are not carried over; neither are `Options.Persistence.Relational` (EF Core)
  and `Options.Serialization.Json`, which were already removed in CLorat/Tarol `e4722c4`.

### Upgrade notes

* **Store schema version 3** (step 16B). `EnsureSchema` applies the version 3 steps (key tables, `subjects` columns), which are
  compatible from version 1: code built before step 16B keeps running on the new schema.
* **Rolling deploys into sealing (DP-10).** A store with keys raises `schema_version.compatible_from` to 3 on its first
  `EnsureSchema`, so pods built before step 16 refuse to start instead of reading sealed envelopes as values. Roll out the new
  version everywhere, then turn keys on.
* **Existing plaintext is not sealed automatically.** Fields written before a member was marked `[PersonalData]` stay plaintext
  until you run `store.SealHistory<T>(500, ct)` once per aggregate, after every pod runs the new version. It is the one rewrite
  of `events` Tarol does (about 4.4 minutes per million events in the measurement).
* **Backups hold the wrapped keys.** An erasure is complete once the backups older than it have expired. Keep the backup
  retention short, or keep the key tables in a database of their own with shorter backups
  (`docs/data-protection/README.md`).
* **Licence**: AGPL-3.0-only from this release on (`LICENSE`, `NOTICE`); a commercial licence is available from the copyright
  holder.

