# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) and are taken from git tags (`v1.2.3`) by MinVer.
Generator diagnostics are tracked per generator in `AnalyzerReleases.Shipped.md` / `Unshipped.md`.

## [Unreleased]

### Added

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
* `Tarol.Changes`: `IOutboxRowHandler` (an outbox row kind the reaction worker runs) and `OutboxRows` (handlers joined);
  `ReactionSet` implements it (`ActivityName`), `ReactionSet.Empty`.
* `Tarol.Persistence`: `OutboxReactions.Combine`, `TarolBuilder.WithOutboxRows` and `TarolBuilder.OutboxTable` (the
  store's table: reactions and added rows).

### Fixed

* `Tarol.Sagas`: a reaction that ran while the host was shutting down was not completed (the verdict used the stopping
  token) and ran again after its lease; the verdict is now recorded regardless. Three timing-dependent tests made
  deterministic (docs/migration/sagas.md §18.3).

### Changed

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
