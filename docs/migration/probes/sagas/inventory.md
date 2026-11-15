# Old saga code: inventory (CLorat/Tarol @ 6a62f81)

Read-only inventory of `CLorat/Tarol@6a62f81`. All paths are relative to that repository root. Everything here comes from reading the source. Nothing was built or run for this inventory, and no `obj/**/generated` output is checked in. Where this file shows emitted code, it is rebuilt from `SagaEmitter.cs`, not taken from a build.

Global build settings that affect every project (`Directory.Build.props:3-12`): `net11.0`, `TreatWarningsAsErrors`, `LangVersion 15`, and `AssemblyName`/`RootNamespace`/`PackageId` = `Tarol.$(MSBuildProjectName)`. No Sagas project sets `IsAotCompatible` or `IsTrimmable` (grep finds nothing).

---

## 1. Projects

| Project (assembly) | Lines (cs + csproj) | Purpose | References |
|---|---|---|---|
| `src/Sagas/Sagas` (Tarol.Sagas) | 1270 + 20 | Programming model, framework events, descriptor/registry, store and outbox abstractions, serializer | ProjectRef `Results` (`Sagas.csproj:9`); Pkg `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options` (`:12-13`); IVT `Tarol.Sagas.Engine` (`:6`) and `Sagas.Tests` (`:18`) |
| `src/Sagas/Sagas.Engine` (Tarol.Sagas.Engine) | 603 + 13 | `SagaEngine`, outbox processor and worker, inspector, `AddSagas`/`SagaBuilder` | ProjectRef `Sagas.Persistence.InMemory`, `Sagas` (`Sagas.Engine.csproj:5-6`); Pkg `Microsoft.Extensions.Hosting.Abstractions` (`:10`) |
| `src/Sagas/Sagas.Hosting` | 0 + 4 | Empty: a csproj with no sources (`Sagas.Hosting.csproj:1-4`) | none |
| `src/Sagas/Sagas.Persistence.InMemory` | 186 + 8 | `InMemorySagaStore` (store and outbox) | ProjectRef `Sagas` (`:5`) |
| `src/Sagas/Sagas.Persistence.Marten` | 341 + 9 | `MartenSagaStore`, documents, `UseMarten()` | ProjectRef `Sagas.Engine`, `Sagas` (`:3-4`); Pkg `Marten` (`:7`) |
| `gen/Sagas/Sagas.Generators` | 1240 + 40 (+7 md) | Incremental generator, `netstandard2.0`, packed as its **own** package (`GeneratePackageOnBuild`, `DevelopmentDependency`, `Sagas.Generators.csproj:4-10`) | Pkg `Microsoft.CodeAnalysis.Analyzers`, `Microsoft.CodeAnalysis.CSharp` (`:21-25`); packs a non-existent `Tarol.Sagas.targets`, listed twice (`:29-33`) |
| `tests/Sagas/Sagas.Tests` (Tarol.Sagas.Tests) | 1644 + 43 | xUnit v3 tests | Pkg coverlet, Test.Sdk, Shouldly (referenced, never used), xunit.analyzers, runner, xunit.v3 (`Sagas.Tests.csproj:10-24`); ProjectRef `Sagas`, `TestUtility/Sagas.Testing`, and as analyzers `Primitives.Generators` and `Sagas.Generators` (`:32-39`) |
| `tests/Sagas/Sagas.Tests/Tarol.Sagas.Testing.Tests.csproj` | 28 | Leftover second project in the same folder. It is not in `Tarol.sln` and its references point to non-existent paths (`:18-21`) | — |

Out of scope but used by every engine test: `src/TestUtility/Sagas.Testing` (1038 lines, `SagaTestHost`, `SagaScenario`, `FakeChangeDispatcher`, `SagaTestClock`). It references Engine and `xunit.v3.extensibility.core` (`Sagas.Testing.csproj:8-14`).

### 1.1 Public API: Tarol.Sagas

| Type (file:line) | Kind | Members / notes |
|---|---|---|
| `SagaStatus` (Saga.cs:9) | enum | `NotStarted, Running, Completed, Compensated, Faulted` |
| `Saga` (Saga.cs:41) | abstract class | `Guid Id {get; internal set}` :47, `SagaStatus Status` :50, `long Version` :53, `Violation? Fault` :56, `DateTimeOffset? StartedAt/FinishedAt` :59/:62, `bool IsFinished` :65, `protected static SagaStep Step` :70, `protected internal abstract void ApplyState(object)` :77, `protected internal abstract IEnumerable<CorrelationKey> GetCorrelationKeys()` :82. Internal: `HasHandled` :84, `Apply(frameworkEvent, resolveState)` :89-128, `RegisteredCompensations` :72 |
| `Saga<TSelf>` (Saga.cs:149) | abstract class | CRTP marker with no members |
| `SagaStep` (SagaStep.cs:23) | sealed class | ctor `internal` :29. `Apply<TEvent>` :46, `Execute<TChange>` :61, `CompensateWith<TChange>` :73, `Schedule<TMessage>(msg, TimeSpan)` :93, `ScheduleAt<TMessage>(msg, DateTimeOffset)` :105 (`TMessage : class, ISagaMessage`), `Complete()` :114, `Compensate()` :120, `Fail(Violation, bool compensate = true)` :128. Internal: `Events/Changes/Schedules/Outcome/Violation` :33-41, `StepOutcome` :144, `PlannedChange` :153, `PlannedSchedule` :155 |
| `SagaHandler` (SagaDescriptor.cs:4) | delegate | `ValueTask<SagaStep>(Saga, object, SagaContext)` |
| `CorrelationExtractor` (SagaDescriptor.cs:7) | delegate | `CorrelationKey?(object)` |
| `ISagaDefinition` (SagaDescriptor.cs:13) | interface | `static abstract SagaDescriptor Descriptor` |
| `SagaSubscription` (SagaDescriptor.cs:22) | sealed class | ctor(messageType, startsSaga, correlate, handler); `MessageType`, `StartsSaga`, `Correlate`, `Handler` |
| `SagaDescriptor` (SagaDescriptor.cs:49) | sealed class | ctor(name, sagaType, factory, subscriptions, stateEventTypes, messageTypes) :56; `Name`, `SagaType`, `Subscriptions`, `StateEventTypes`, `MessageTypes`, `FindSubscription(Type)` :89, `AppliesState(Type)` :95; internal `Create(Guid)` :100 |
| `SagaRegistry` (SagaDescriptor.cs:111) | sealed class | ctor(IEnumerable<SagaDescriptor>) :123 throws on a duplicate name, an alias shared by two types, or two aliases for one type (:129-153). `Sagas`, `Find(string)` :164, `Find(Type)` :170, `FindMessageType(Type)` :176, `GetMessageType(Type)` :183, `GetMessageType(string alias)` :194, `SubscriptionsFor(Type)` :201 |
| `SagaMessageType` (SagaMessageType.cs:10) | sealed class | `Type`, `Alias`, `static Reference<T>(alias)` :29, `static Value<T>(alias)` :37, `CreateChangeFailed(object, Violation)` :44 |
| `ISagaMessage` (Primitives.cs:15) | marker | Directly addressed messages, never correlated |
| `IChangeFailed` (Primitives.cs:20), `ChangeFailed<TChange>(Change, Violation)` (:34) | interface / record | Delivered when a change returns a failed `Result` |
| Attributes (Primitives.cs) | | `[StartedBy]` :45 (method); `[CorrelateBy(sagaProperty) { MessageProperty }]` :59 (method); `[Correlation]` :73 (property); `[NotCorrelated]` :79 (property); `[SagaName(name)]` :88 (class); `[SagaMessage(typeof)]` :100 (class, multiple); `[SagaMessageAlias(alias)]` :111 (class/struct); `[assembly: SagaJsonContext(typeof)]` :132 |
| `CorrelationKey(string Type, string Value)` (Primitives.cs:148) | record struct | `static CorrelationKey? From<T>(type, value)` :154, which returns null for null, default or empty values (`:156-163`) and formats with the invariant culture |
| `SagaEnvelope(MessageId, Message, CorrelationId?, CausationId?)` (Primitives.cs:187) | record | |
| `SagaContext` (Primitives.cs:196) | sealed class | internal ctor :200; `SagaId, MessageId, CorrelationId (?? SagaId), CausationId, IsNewSaga, UtcNow, Services, CancellationToken`, `GetRequiredService<T>()` :241 |
| Framework events (SagaEvents.cs:14-35) | records | `SagaStarted(SagaType, StartKey, At)`, `SagaMessageHandled(MessageId, MessageType, At)`, `SagaStateChanged(SerializedMessage)`, `SagaCompensationRegistered(SerializedMessage)`, `SagaCompensationsTriggered(At)`, `SagaCompleted(At)`, `SagaCompensated(At)`, `SagaFaulted(Violation, At)`; `SagaFrameworkEvents.All` :38-51 |
| `SagaOptions` (SagaOptions.cs:8) | class | `Func<DateTimeOffset> UtcNow` :14 (default `TimeProvider.System.GetUtcNow()`), `MaxConcurrencyRetries = 5` :17, `JsonSerializerOptions` (Web) :23, `Outbox` :26 |
| `SagaOutboxOptions` (SagaOptions.cs:32) | class | `RunWorker = true`, `PollInterval = 1s`, `BatchSize = 50`, `LeaseDuration = 1m`, `MaxAttempts = 10`, `RetryBackoff = 2^n s, max 300` (:35-51) |
| `Abstractions.ISagaStore` (Abstractions.cs:17) | interface | `FindByCorrelationAsync(sagaType, key)`, `LoadAsync(sagaId)`, `CommitAsync(SagaCommit)` |
| `Abstractions.SagaStreamData(Events)` :32, `SagaCommit(SagaType, SagaId, ExpectedVersion, Events, NewCorrelationKeys, Outbox)` :42, `SagaConcurrencyException` :51 | records / exception | `Version => Events.Count` :38 |
| `Abstractions.SagaOutboxKind {Change, Timeout}` :63; `SagaOutboxMessage(Id, Kind, SagaType, SagaId, Payload, DueAt, CorrelationId, CausationId, Attempts, LastError, Position)` :78 | | |
| `Abstractions.ISagaOutbox` (Abstractions.cs:94) | interface | `ClaimDueAsync(now, maxCount, lease)`, `CompleteAsync(id)`, `RetryAsync(id, dueAt, error)`, `DeadLetterAsync(id, error)` |
| `Abstractions.IChangeDispatcher` (:130), `ChangeContext(MessageId, CorrelationId, CausationId, SagaType, SagaId)` (:142) | | Implemented by the application |
| `Internal.SagaIds` (SagaInternals.cs:10) | static | `ForStart(sagaType, key)` :18 (UUIDv5 via SHA-1, :29-41), `Derive(source, purpose)` :24 |
| `Internal.SagaDiagnostics` (SagaInternals.cs:47) | static | `SourceName = "Tarol.Sagas"`, internal `ActivitySource` :52 |
| `Internal.SagaOutboxSignal` (SagaInternals.cs:58) | class | `Notify()`, `WaitAsync(timeout, ct)` on `SemaphoreSlim(0,1)` |
| `Serialization.SerializedMessage(Type, Data)` :14, `ISagaMessageSerializer` :19, `JsonSagaMessageSerializer` :38, `SagaJsonResolvers` :113, `SagaJsonContext` :134 | | See §4.3 |

### 1.2 Public API: Tarol.Sagas.Engine

| Type (file:line) | Members |
|---|---|
| `SagaDeliveryResult` (SagaEngine.cs:12) | `Handled, Duplicate, SagaNotFound, SagaFinished, NoSubscription` |
| `ISagaDispatcher` (SagaEngine.cs:33) | `DispatchAsync(SagaEnvelope)`; `DeliverAsync(sagaType, sagaId, envelope) : SagaDeliveryResult` |
| `SagaEngine` (SagaEngine.cs:46) | primary ctor(registry, store, serializer, scopeFactory, outboxSignal, options) |
| `SagaOutboxProcessor` (SagaOutboxProcessor.cs:19) | `ProcessDueAsync() : int` :30, `ProcessAllDueAsync()` :66 |
| `SagaOutboxWorker` (:127) | **internal** `BackgroundService` |
| `SagaInspector` (SagaInspector.cs:9) | `LoadAsync<TSaga>(sagaId)` |
| `SagaBuilder` (namespace `Tarol.Sagas`, SagaServiceCollectionExtensions.cs:16) | `Services`, `Add(SagaDescriptor)` :34, `Add<TSaga>()` :44, `AddJsonTypeInfoResolver` :54, `Configure(Action<SagaOptions>)` :64, `UseInMemoryStore()` :71 |
| `SagaServiceCollectionExtensions.AddSagas(services, Action<SagaBuilder>)` (:100) | Throws when no store is registered (:108-113). Registers everything with `TryAddSingleton` (:116-123) plus `AddHostedService<SagaOutboxWorker>` (:124) |

### 1.3 Public API: stores

| Type | Members |
|---|---|
| `Tarol.Sagas.Persistence.InMemory.InMemorySagaStore` (InMemorySagaStore.cs:10) | `ISagaStore`, `ISagaOutbox`, plus inspection: `GetSagaIds()` :139, `GetStream(id)` :148, `GetPendingOutbox()` :155, `GetDeadLetters()` :171 |
| `Tarol.Sagas.Marten.MartenSagaStore(IDocumentStore)` (MartenSagaStore.cs:15) | `ISagaStore`, `ISagaOutbox` |
| `SagaCorrelationDocument` (:151), `SagaOutboxDocument` (:182) | Public mutable documents |
| `SagaBuilderMartenExtensions.UseMarten(this SagaBuilder)` (:328) | `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` (:326-327) |
| internal `MartenConcurrency` (:270), `SagaMartenConfiguration : IConfigureMarten` (:298) | |

---

## 2. Programming model (user view)

| Topic | How it works | Evidence |
|---|---|---|
| Declaration | `public partial class X : Saga<X>`. Handlers are instance methods named `When(TMessage, …extra)` returning `SagaStep`, `Task<SagaStep>` or `ValueTask<SagaStep>`. State changes are `void On(TEvent)`. Both may be private. There is no `SagaDescriptor` written by hand: the generator emits it | Saga.cs:131-152; SagaModelBuilder.cs:68-95, 317-333 |
| Extra handler parameters | `SagaContext`, `CancellationToken`, or any other type, which is resolved as a DI service from a per-message scope | SagaModelBuilder.cs:284-290; SagaEmitter.cs:120-127; SagaEngine.cs:175-177 |
| Start | `[StartedBy]` on a `When`. The saga id is `SagaIds.ForStart(sagaName, key)`, a deterministic UUIDv5 | Primitives.cs:45; SagaEngine.cs:77-85; SagaInternals.cs:18-21 |
| Correlation properties | Saga properties of type `Id<T>` (`Tarol.StronglyTypedId.Id`1`) or properties marked `[Correlation]`, excluding `[NotCorrelated]`. `Nullable<T>` is unwrapped | SagaModelBuilder.cs:335-353, 518-523, 590 |
| Correlation of a message | Convention: exactly one public message property whose type equals a saga correlation key type. Otherwise `[CorrelateBy(nameof(SagaProp), MessageProperty = …)]`. For `DomainEvent<TRoot>` there is a name-based "prefer root" filter (see S1) | SagaModelBuilder.cs:355-437, 458-465 |
| Lookup at runtime | `DispatchAsync`: for each subscription of the exact runtime type, extract the key, then `store.FindByCorrelationAsync(sagaName, key)`. If nothing is found and the subscription starts the saga, use the deterministic id. `ISagaMessage` subscriptions (`Correlate == null`) are skipped | SagaEngine.cs:58-90 |
| Keys learned later | After the evolve step, `GetCorrelationKeys()` (generated) is diffed against the keys before, and new keys go into the commit. Keys are never removed | SagaEngine.cs:189, 279-281 |
| Direct messages | `ISagaMessage` (timeouts, `ChangeFailed<T>`) is delivered with `DeliverAsync(sagaType, sagaId, …)` and cannot be `[StartedBy]` (TSAGA008) | SagaEngine.cs:93-102; SagaModelBuilder.cs:293-304 |
| Steps | `Step.Apply(evt)` records state events, which require an `On`. `Execute(change)` puts the change in the outbox. `CompensateWith(c)` attaches to the preceding `Execute`. `Schedule`/`ScheduleAt` create a timeout outbox entry. The trigger message itself is recorded as state if `On(TMessage)` exists | SagaStep.cs:46-111; SagaEngine.cs:244-259 |
| Completion | `Complete()` records `SagaCompleted`. A finished saga ignores everything and returns `SagaFinished` | SagaStep.cs:114; SagaEngine.cs:161-164, 263-265 |
| Compensation | `Compensate()` enqueues every registered compensation in reverse order, then records `SagaCompensationsTriggered` and **immediately** `SagaCompensated`. `Fail(v)` does the same, then `SagaFaulted`. `Fail(v, false)` only faults. There is **no** `CompensationFailed` type anywhere (grep finds nothing) | SagaEngine.cs:225-235, 266-276 |
| Change failure | The outbox calls `IChangeDispatcher`. On a failed `Result` it builds `ChangeFailed<T>` and delivers it. With no handler, the engine applies `Fail(violation)`, which compensates | SagaOutboxProcessor.cs:87-97; SagaEngine.cs:179-182 |
| Time source | `SagaOptions.UtcNow` is a `Func<DateTimeOffset>` that defaults to `TimeProvider.System.GetUtcNow()`. It is not an injected `TimeProvider`. Exceptions: Marten `CommittedAt = DateTimeOffset.UtcNow`, and engine and worker delays use `Task.Delay` and `SemaphoreSlim.WaitAsync` on real time | SagaOptions.cs:14; MartenSagaStore.cs:249; SagaEngine.cs:127; SagaOutboxProcessor.cs:143,151 |
| Restart after completion | Not supported. The old key still maps to the finished id, and `ForStart` gives the same id again, so the result is `SagaFinished` | SagaEngine.cs:75-85, 161-164 |
| Persisted names | Saga: `[SagaName]` or `ToDisplayString()`. Messages: `[SagaMessageAlias]` or the full name. Message types are discovered from `On` parameters, `[SagaMessage]`, and the type arguments of `Apply/Execute/CompensateWith/Schedule/ScheduleAt` | SagaModelBuilder.cs:116, 126-203 |
| Registration | `services.AddSagas(s => s.Add<X>() / .Add{Assembly}Sagas() .UseInMemoryStore() / .UseMarten())`, plus a user-supplied `IChangeDispatcher` | SagaServiceCollectionExtensions.cs:90-127 |

Verbatim example (tests/Sagas/Sagas.Tests/Domain.cs:124-134, then LifecycleTests.cs:135-152):

```csharp
    [StartedBy]
    private SagaStep When(TransferRequested e)
    {
        return Step
            .Apply(new ReferenceAssigned(ReferenceFor(e.TransferId)))
            .Execute(new Debit(e.TransferId, e.Amount))
            .CompensateWith(new UndoDebit(e.TransferId))
            .Execute(new Credit(e.TransferId, e.Amount))
            .CompensateWith(new UndoCredit(e.TransferId))
            .Schedule(new TransferExpired(), Expiry);
    }
// ---
    [Fact]
    public async Task Compensate_runs_compensations_in_reverse_order()
    {
        await using var host = Hosts.Transfer();
        await host.PublishAsync(new TransferRequested(_transferId, 100m),
            cancellationToken: TestContext.Current.CancellationToken);
        host.Changes.ClearDispatched();

        await host.PublishAsync(new BankRejected(Reference, "insufficient funds"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new object[] { new UndoCredit(_transferId), new UndoDebit(_transferId) },
            host.Changes.Changes);
```

---

## 3. Generator (`gen/Sagas/Sagas.Generators`)

### 3.1 Pipeline and inputs

| Step | Detail | Evidence |
|---|---|---|
| Candidate filter | `CreateSyntaxProvider`: any `ClassDeclarationSyntax` with a base list. This is not `ForAttributeWithMetadataName` | SagaGenerator.cs:32-35; SagaModelBuilder.cs:18-21 |
| Transform | `Build`: resolves the symbol, creates `KnownTypes` (15 `GetTypeByMetadataName` calls) **per candidate**, requires inheritance from `Saga`1` (`OriginalDefinition` only), and runs only on the first partial declaration that has a base list | SagaModelBuilder.cs:23-35, 479-499, 569-591 |
| Per-saga inputs | Declared members only (`saga.GetMembers()`; base classes are not scanned): `When`/`On` methods, properties, class attributes, and every invocation in every partial part. Other trees use `compilation.GetSemanticModel` with RS1030 suppressed | SagaModelBuilder.cs:61-96, 160-187 |
| Assembly inputs | `CompilationProvider` gives the assembly name and `ReadJsonContexts` (`[assembly: SagaJsonContext]` plus their `[JsonSerializable]` types), re-evaluated on every compilation | SagaGenerator.cs:55-59; SagaModelBuilder.cs:209-248 |
| Outputs | One `<FQN>.Saga.g.cs` per valid saga, plus `TarolSagaRegistration.g.cs` (`Combine` of the collected registrations, assembly name and JSON contexts) | SagaGenerator.cs:37-48, 50-63, 107-114; SagaEmitter.cs:183-190 |
| Models / incrementality | Records of strings and `EquatableArray<T>`, with no symbols (comment at Models.cs:15-16). **They do carry locations**: `DiagnosticInfo.Location` and `JsonContextModel.Location` are `LocationInfo(FilePath, TextSpan, LinePositionSpan)`, so an edit that shifts lines changes model equality for sagas that have diagnostics and for the registration output. There are no incrementality tests, so the actual caching behaviour is unverified | Models.cs:18-30, 39-43, 72-105 |
| Silent skips | A `When` with zero parameters or a generic `When` is dropped with no diagnostic | SagaModelBuilder.cs:270-271 |

### 3.2 Emitted shape (rebuilt from SagaEmitter.cs:12-179)

```csharp
// <auto-generated/>  #nullable enable  #pragma warning disable CS1591
namespace Ns {
partial class X : global::Tarol.Sagas.ISagaDefinition {
    [GeneratedCode("Tarol.Sagas.Generators","1.0.0")]
    public static global::Tarol.Sagas.SagaDescriptor Descriptor { get; } = new(
        name: "Ns.X", sagaType: typeof(global::Ns.X), factory: static () => new global::Ns.X(),
        subscriptions: new SagaSubscription[] {
            new SagaSubscription(messageType: typeof(M), startsSaga: true,
                correlate: static message => CorrelationKey.From("System.Guid", ((M)message).Prop),   // or null
                handler: static (saga, message, context) =>
                    new ValueTask<SagaStep>(((global::Ns.X)saga).When((M)message, context, context.GetRequiredService<S>()))),
        },
        stateEventTypes: new Type[] { typeof(E), ... },              // most-derived first (SagaModelBuilder.cs:105-110)
        messageTypes: new SagaMessageType[] { SagaMessageType.Reference<E>("alias"), ... });
    protected override void ApplyState(object @event) { switch (@event) { case E e0: On(e0); break; } }   // no default
    protected override IEnumerable<CorrelationKey> GetCorrelationKeys() {
        if (CorrelationKey.From("System.Guid", this.TransferId) is { } key0) yield return key0; ... yield break; }
}}
// TarolSagaRegistration.g.cs
namespace Tarol.Sagas { public static class {Asm}SagaRegistration {
    public static SagaBuilder Add{Asm}Sagas(this SagaBuilder builder) { builder.Add(X.Descriptor); builder.AddJsonTypeInfoResolver(Ctx.Default); return builder; } } }
```

A `Task<SagaStep>` return is wrapped with `new ValueTask<SagaStep>(task)`, and a `ValueTask` return is passed through (SagaEmitter.cs:132-137).

### 3.3 Diagnostics (SagaDiagnostics.cs:9-80, category `Tarol.Sagas`)

| Id | Severity | Message (abridged) |
|---|---|---|
| TSAGA001 | Error | Saga '{0}' must be declared partial |
| TSAGA002 | Error | must be a top-level, non-generic class |
| TSAGA003 | Error | requires a parameterless constructor (it may be private) |
| TSAGA004 | Error | When-handler for '{0}' must return SagaStep, Task<SagaStep> or ValueTask<SagaStep> |
| TSAGA005 | Error | '{0}' cannot be correlated with saga '{1}': no message property matches ({2}) |
| TSAGA006 | Error | Correlation for '{0}' is ambiguous ({1}). Use [CorrelateBy(...)] |
| TSAGA007 | Error | [CorrelateBy] on the handler for '{0}' is invalid: {1} |
| TSAGA008 | Error | '{0}' implements ISagaMessage … cannot be [StartedBy] |
| TSAGA009 | Warning | Saga '{0}' has no [StartedBy] handler |
| TSAGA010 | Error | more than one When-handler for '{1}' |
| TSAGA011 | Warning | On-method '{0}' is ignored: must return void and take exactly one parameter |
| TSAGA012 | Error | Saga message '{0}' is missing in the registered JSON contexts ({1}). Only checked if at least one `[SagaJsonContext]` exists (SagaGenerator.cs:85-86), and reported at the first context's location (:92) |
| TSAGA013 | Error | '{0}' registered via [SagaJsonContext] must be a source-generated JsonSerializerContext |
| TSAGA014 | Warning | '{0}' is an interface or abstract type and cannot be resolved when persisted |

`AnalyzerReleases.Unshipped.md:8` lists only TSAGA001, and no `Shipped.md` exists. An error-severity diagnostic suppresses the saga's output (`SagaModel.IsValid`, Models.cs:29; SagaGenerator.cs:42-43).

---

## 4. Engine

### 4.1 `SagaEngine.ProcessAsync` / `ProcessOnceAsync` (SagaEngine.cs:104-293)

1. Starts an `Activity` (`saga {name}`, Consumer) with tags `saga.type/id`, `messaging.message.id` and `saga.message_type` (:111-115).
2. Retry loop: on `SagaConcurrencyException` with `attempt < MaxConcurrencyRetries` it waits `Random.Shared.Next(5, 25*attempt)` ms and retries. This is linear jitter, not exponential. Any other exception sets the activity error status and rethrows (:117-134).
3. **Load**: `store.LoadAsync`, creates a fresh instance, and replays every framework event through `Saga.Apply` with `serializer.Deserialize` for `SagaStateChanged`. There is no snapshot (:149-152).
4. Guards in order: new saga without a start key or start subscription gives `SagaNotFound`; finished gives `SagaFinished`; `HasHandled(MessageId)` gives `Duplicate` (:154-167). The idempotency inbox is the set of `SagaMessageHandled` ids in the stream (Saga.cs:97-99).
5. **Decide**: `now = options.UtcNow()`. If a subscription exists, the handler runs in a new async DI scope with a `SagaContext`. Otherwise an `IChangeFailed` becomes `new SagaStep().Fail(violation)`, and anything else gives `NoSubscription` (:170-186).
6. **Evolve** (each event is applied to the live instance *and* collected): `SagaStarted` if new, then `SagaMessageHandled` (alias, or the CLR full name as fallback), then the trigger as `SagaStateChanged` if `On(TMessage)` exists, then step events (missing `On` throws `InvalidOperationException`), then per change an outbox entry plus `SagaCompensationRegistered`, then timeouts (`DueAt ?? now + Delay`), then the outcome events (:189-277). The live path reuses the in-memory event object rather than the deserialized copy (:195-200, 208).
7. Outbox entries: `Id = Guid.CreateVersion7(now)`, `CorrelationId = envelope.CorrelationId ?? sagaId`, `CausationId = envelope.MessageId`, `Position = index` (:211-223).
8. New keys = the generated keys after the step minus the keys before, plus the start key when new (:279-281).
9. **Commit** `SagaCommit(name, id, stream.Version, events, newKeys, outbox)` as one call, then `outboxSignal.Notify()` if anything was enqueued (:284-289).
10. Fan-out: `DispatchAsync` handles the saga types and instances one after another. The first exception aborts the rest (:63-89).

**Own observation (N1, not in the review):** a second `[StartedBy]` message for an *existing* key with a *new* `MessageId` is found by correlation and handled as an ordinary message. The start handler runs again (`isNew == false`, subscription present, :154-178), so it executes its changes and registers compensations a second time. Only an identical `MessageId` is deduplicated (DeliveryTests.cs:34-46). `Parallel_start_messages_create_a_single_saga` (DeliveryTests.cs:164-174) checks only the instance count, with `processOutbox: false`, and does not check how many changes are dispatched.

### 4.2 `SagaOutboxProcessor` and `SagaOutboxWorker` (SagaOutboxProcessor.cs)

| Aspect | Behaviour | Line |
|---|---|---|
| Claim | `ClaimDueAsync(UtcNow(), BatchSize, LeaseDuration)` | 33-34 |
| Per entry | Deserialize the payload. **Timeout**: `DeliverAsync(sagaType, sagaId, new SagaEnvelope(entry.Id, payload, corr, cause))`, and the result is ignored. **Change**: dispatch in a new scope with `ChangeContext(entry.Id, …)`. On failure, `CreateChangeFailed` is delivered with `MessageId = SagaIds.Derive(entry.Id, "change-failed")` and the result is ignored, because the critical log is commented out | 75-110, 113-120 |
| Success | `CompleteAsync(entry.Id)` in a separate store call, after the saga commit | 43 |
| Failure | `attempts = entry.Attempts + 1`. At `>= MaxAttempts` it calls `DeadLetterAsync(id, ex.ToString())`. Otherwise `RetryAsync(id, UtcNow() + RetryBackoff(attempts), ex.Message)` | 45-58 |
| Cancellation | An OCE caused by the token propagates, and the entry stays leased until the lease expires | 45-46 |
| Lease ownership | Not checked: Complete, Retry and DeadLetter act by id only, so a slow processor whose lease expired can still complete or retry an entry another node has re-claimed | 43, 52, 57 |
| Idempotency | Timeouts use the outbox id as the message id, and the change failure uses a derived stable id, so a re-run deduplicates through the saga inbox. Changes are at-least-once, and `IChangeDispatcher` is asked to dedupe by `ChangeContext.MessageId` (Abstractions.cs:121-124) | 83, 96, 118 |
| Worker | Returns at once if `!RunWorker`. Loop: process, and if 0 entries were claimed, wait for the signal or `PollInterval`. `catch (Exception)` then `Task.Delay(PollInterval)`, with no log | 132-153 |
| Logging | No `ILogger` anywhere in Sagas, Generators or Sagas.Testing (grep) | — |

### 4.3 Serializer (SagaMessageSerializer.cs)

* `Serialize` looks up the alias by **exact** `message.GetType()` in the registry and serializes with `JsonSerializer.Serialize(message, JsonTypeInfo)` (:51-57). `Deserialize` goes from alias to type to `JsonTypeInfo` (:60-66).
* Resolver chain: a copy of `SagaOptions.JsonSerializerOptions`, then the registered contexts, then `SagaJsonContext.Default`, then `DefaultJsonTypeInfoResolver` **only if** `JsonSerializer.IsReflectionEnabledByDefault`, with IL2026 and IL3050 suppressed (:82-106). Missing metadata becomes an `InvalidOperationException` with guidance (:68-80).
* `SagaJsonContext` covers the 8 framework events, `SerializedMessage` and `CorrelationKey` (:123-134), but no store uses it at runtime. InMemory keeps CLR objects (InMemorySagaStore.cs:119), and Marten uses its own serializer (MartenSagaStore.cs:302).
* Verdict: AOT-safe only when every message is covered by a registered context. Without any `[SagaJsonContext]`, TSAGA012 is silent and a Native-AOT host fails at runtime. This is inferred from :85-86 and :104 and was not run.

---

## 5. Stores

| Aspect | InMemorySagaStore | MartenSagaStore |
|---|---|---|
| Stream shape | `Dictionary<Guid, List<object>>` of framework-event CLR objects (InMemorySagaStore.cs:15) | Marten event stream with a Guid id and the framework events registered as event types (MartenSagaStore.cs:104, 117-119, 302). User payloads are a JSON **string** inside `SerializedMessage` |
| Correlation index | `Dictionary<(sagaType, key), HashSet<Guid>>` (:12) | `SagaCorrelationDocument{Id = "type\|keyType\|keyValue\|sagaId:N", SagaType, KeyType, KeyValue, SagaId}` stored with upsert (`session.Store`), indexed on `KeyValue` only (:151-179, 122, 304-305) |
| Outbox shape | `OutboxEntry{Message, Sequence, LockedUntil, DeadLettered}` (:177-186) | `SagaOutboxDocument` with payload type/data, DueAt, CorrelationId, CausationId, Position, `CommittedAt = DateTimeOffset.UtcNow`, Attempts, LastError, LockedUntil, DeadLettered, `[Version] Guid` (:182-251), with optimistic concurrency and an index on DueAt (:307-309) |
| Append concurrency | Under one `Lock`: `count != ExpectedVersion` throws `SagaConcurrencyException` before any mutation (:110-114) | `StartStream` when expected is 0, else `Append(id, ExpectedVersion + n, events)`. Conflicts are mapped by **exception type name** (:116-119, 127-134, 273-294). The expected-version semantics follow Marten's API and are not verified by any test |
| Atomicity of commit | Events, keys and outbox in one lock section (:108-134) | Events, correlation docs and outbox docs in one `LightweightSession.SaveChangesAsync` (:113-129) |
| Claim | Under the lock: due, not dead, lease free; order by DueAt then commit sequence; set `LockedUntil = now + lease` (:19-38) | Query candidates (DueAt, CommittedAt, Position), then **one session and SaveChanges per document** with an expected version, skipping conflicts (:21-52) |
| Complete / Retry / DeadLetter | Remove / reschedule with `Attempts+1`, `LockedUntil = null` / flag. Unknown ids are ignored (:41-83) | Delete by id / load-modify-`Update` in a separate session (:58-87, 137-147). Whether the `Update` enforces the loaded version was not verified |
| Key removal | Never | Never |
| Not transactional with each other | — | Claim, Complete and the saga commit triggered by a delivery are separate transactions, held together by inbox idempotency |
| Namespace | `Tarol.Sagas.Persistence.InMemory` (:3) | `Tarol.Sagas.Marten` (:9), which differs from the default root namespace `Tarol.Sagas.Persistence.Marten` |
| Tests | 18 cases (§6) | none |

---

## 6. Tests (tests/Sagas/Sagas.Tests)

There are 78 active `[Fact]`/`[Theory]` methods (79 cases). `SagaStepTests.cs` is fully commented out (14 methods, 15 cases). All engine tests run through `SagaTestHost` with the InMemory store and `FakeChangeDispatcher`.

**InMemorySagaStoreTests** (InMemorySagaStoreTests.cs)
| Test | Behaviour |
|---|---|
| Missing_stream_is_empty :18 | An absent stream has version 0 |
| Commits_append_to_the_stream :27 | Appends across commits |
| Wrong_expected_version_is_a_concurrency_conflict (1,3) :42 | Version check, stream unchanged |
| Failed_commit_writes_neither_keys_nor_outbox :55 | Atomicity |
| Loaded_streams_are_snapshots :69 | Copy on load |
| Correlation_keys_find_their_sagas :83 | A shared key maps to several ids |
| Correlation_keys_are_scoped_per_saga_type :99 | Scoping |
| Correlation_key_type_is_part_of_the_key :108 | Type is part of the key |
| Only_due_entries_are_claimed :120 | DueAt filter |
| Claims_are_ordered_by_due_time_then_commit_order :133 | Ordering |
| Claims_respect_the_batch_size :147 | maxCount |
| Claimed_entries_are_leased_until_the_lease_expires :157 | Lease |
| Completed_entries_are_removed :168 | Complete |
| Retry_reschedules_and_releases_the_lease :180 | Retry, Attempts, LastError |
| Dead_letters_are_parked :197 | Dead letter |
| Operations_on_unknown_entries_are_ignored :212 | No throw |
| GetSagaIds_lists_every_stream :223 | Inspection |

**SerializationTests / RegistryTests / PrimitiveTests** (RegistryAndSerializationTests.cs)
| Test | Behaviour |
|---|---|
| Messages_round_trip :23 | Serialize/deserialize |
| Persisted_type_defaults_to_the_full_type_name :34 | Default alias |
| Persisted_type_uses_the_alias_attribute :41 | `[SagaMessageAlias]` |
| Payload_uses_the_configured_json_options :50 | camelCase web defaults |
| Unknown_message_type_is_rejected_with_guidance :58 | Error text |
| Unknown_alias_is_rejected :67 | Error text |
| Framework_context_covers_the_framework_events :76 | Metadata exists |
| Finds_sagas_by_name_and_type :88 | Registry lookups |
| Lists_every_saga_subscribed_to_a_message_type :99 | Fan-out index |
| Registering_the_same_descriptor_twice_is_harmless :111 | Dedupe |
| Duplicate_saga_names_are_rejected :119 | Throws |
| One_alias_for_two_types_is_rejected :127 | Throws |
| Two_aliases_for_one_type_are_rejected :136 | Throws |
| Shared_message_types_with_the_same_alias_are_allowed :144 | OK |
| Unknown_message_lookups_throw :152 | Throws |
| Value_type_messages_cannot_create_ChangeFailed :162 | Throws |
| CorrelationKey_ignores_null_and_default_values :178 | null/default give no key |
| CorrelationKey_uses_the_invariant_culture :186 | de-DE |
| CorrelationKey_formats_as_type_and_value :202 | ToString |
| Start_ids_are_deterministic_version_5_guids :208 | UUIDv5 |
| Derived_ids_are_stable_per_purpose :224 | Derive |
| ChangeFailed_exposes_the_change_untyped :232 | IChangeFailed |

**CorrelationTests** (Engine/CorrelationTests.cs): Saga_id_is_derived_deterministically_from_the_start_key :11; Key_learned_in_On_correlates_later_messages :25; Learned_keys_are_registered_in_the_index :39; Unknown_key_reaches_no_saga :58; Non_start_message_before_the_start_is_ignored :72; CorrelateBy_with_explicit_message_property_picks_the_right_value :84; Different_keys_start_different_instances :99; Same_message_starts_every_subscribed_saga_type :112; Saga_messages_are_not_correlated_by_DispatchAsync :130; Default_correlation_value_starts_no_saga :144.

**DeliveryTests** (Engine/DeliveryTests.cs): Redelivered_message_is_handled_once :17; Redelivered_start_message_creates_no_second_saga_and_no_second_change :34; DeliverAsync_reports_each_outcome :51 (NotFound, NoSubscription, Handled, Finished); DeliverAsync_detects_duplicates_on_a_running_saga :78; DeliverAsync_cannot_start_a_saga :95; DeliverAsync_to_an_unknown_saga_type_throws :107; Concurrency_conflicts_are_retried :119; Concurrency_conflicts_surface_after_the_last_retry :133; Parallel_messages_to_one_saga_are_all_applied :147 (20 parallel, 200 retries); Parallel_start_messages_create_a_single_saga :164.

**LifecycleTests** (Engine/LifecycleTests.cs): Start_message_creates_a_running_saga_with_applied_state :26; Start_message_dispatches_changes_in_step_order :45; Start_message_schedules_the_timeout :58; Stream_records_the_framework_events_of_a_start :72; Complete_finishes_the_saga_without_compensation :95; Finished_saga_ignores_further_messages_and_timeouts :113; Compensate_runs_compensations_in_reverse_order :136; Compensations_are_triggered_only_once :155; Fail_compensates_by_default :169; Fail_without_compensation_dispatches_nothing :188; Timeout_is_delivered_to_its_saga_at_the_due_time :207; ScheduleAt_uses_the_absolute_due_time :222; Unhandled_change_failure_faults_the_saga_with_the_change_violation :238 (asserts that **UndoCredit**, the compensation of the failed change, is dispatched, :250); Failing_compensation_of_a_finished_saga_is_not_retried_forever :254 (asserts no dead letter, :267); Handler_exception_persists_nothing :274; Applying_an_event_without_On_method_is_rejected :288; Async_handler_receives_context_and_services :304; Correlation_id_defaults_to_the_saga_id :327; Envelope_correlation_id_flows_into_changes :343.

**Not tested**
* Marten store, in any form.
* The generator: no snapshot tests, no diagnostic tests for TSAGA001-014, no incrementality tests. It is covered only by compiling `Domain.cs`.
* `SagaOutboxWorker`; the processor's retry, backoff and dead-letter path (no test makes the dispatcher throw); poison payloads.
* A user `When(ChangeFailed<T>)` handler (none in `Domain.cs`); the `Task<SagaStep>` return kind; `[SagaName]`, `[NotCorrelated]`, `[SagaMessage]`, `[SagaJsonContext]` and TSAGA012.
* `Id<T>` and Primitives `DomainEvent<TRoot>` correlation. Test sagas use `Guid`/`string` with `[Correlation]` (Domain.cs:111-113). Primitives is referenced only as a generator, not as a runtime (Sagas.Tests.csproj:34).
* Restart after finish; a correlation key changing value; replay after `On` is removed or an alias is renamed.
* A second start message with a new id for an existing key (N1); cancellation; `SagaInspector` with a custom name; AOT.

---

## 7. Review findings (docs/review/sagas.md:141-170) re-verified

| # | Claim (one sentence) | Verdict | Evidence |
|---|---|---|---|
| S1 | Convention correlation on root `DomainEvent<TRoot>` always gives TSAGA006, because `AggregateRootId` and `EntityId` are both `Id<TRoot>` and the root filter checks the saga key type | **Confirmed** | `DomainEvent<TRoot> : DomainEvent<TRoot,TRoot>` exposes public `AggregateRootId` and `EntityId`, both `Id<TRoot>` (src/Primitives/Primitives/DomainEvent.cs:57-69). Public getters are collected (SagaModelBuilder.cs:439-456). `rootPairs` filters on `p.Saga.KeyType` (:413), so both pairs survive and :431-433 reports TSAGA006. With `[CorrelateBy]` and no `MessageProperty`, `matches.Count == 2` (:386-397). `FindDomainEventRoot` matches by name (:461) |
| S2 | A failed compensation is silently lost | **Confirmed** | ChangeFailed is delivered to a finished saga, which returns `SagaFinished` (SagaEngine.cs:161-164). The result is unused and the log is commented out (SagaOutboxProcessor.cs:93-104). The entry is completed (:43). The test pins this behaviour (LifecycleTests.cs:254-269) |
| S3 | `Compensated`/`Faulted` are recorded when compensations are enqueued, not when they succeed | **Confirmed** | SagaEngine.cs:266-276. There is no success or failure event type (SagaEvents.cs:14-51) |
| S4 | An unhandled `ChangeFailed` compensates the failed change too | **Confirmed** | Compensation is registered at decision time (SagaEngine.cs:254-255), and the fallback is `Fail(violation)`, which compensates (:179-182, 273-275). The test asserts that UndoCredit is dispatched after Credit failed (LifecycleTests.cs:241,250) |
| S5 | `SagaInspector` looks up by `typeof(TSaga).FullName`, so `[SagaName]` sagas throw | **Confirmed** | SagaInspector.cs:18 vs `Find(Type)` at SagaDescriptor.cs:170. The default name is `ToDisplayString()` (SagaModelBuilder.cs:116). SagaTestHost uses the inspector (SagaTestHost.cs:264, 287) |
| S6 | The correlation index only grows | **Confirmed** | Only additions: SagaEngine.cs:279-281; InMemorySagaStore.cs:121-127; MartenSagaStore.cs:121-122. No delete API exists in `ISagaStore` (Abstractions.cs:17-29) |
| S7 | A business key can never start a second saga after the first finished | **Confirmed** | SagaEngine.cs:75-85 (the old key or `ForStart` gives the same id), then :161-164 |
| S8 | Full replay on every message, no snapshots, unbounded `_handledMessages` | **Confirmed** | SagaEngine.cs:149-152; Saga.cs:44, 97-99; no snapshot member in `ISagaStore` |
| S9 | Generated `ApplyState` has no default, so a state event whose `On` was removed is silently ignored on replay, or replay fails late on an unknown alias | **Partly**: the code fact holds; which outcome you get depends on the registry | No default branch (SagaEmitter.cs:145-158). Deserialization happens first (Saga.cs:100-101), and `GetMessageType(alias)` throws if the alias is gone (SagaDescriptor.cs:194-198). The alias survives if the type is still discovered by another path (`Apply<T>`, `[SagaMessage]`, or **another saga**, because the alias table is registry-wide, :143-157), and then the event is silently ignored |
| S10 | The worker swallows exceptions and there is no logging anywhere | **Confirmed** | SagaOutboxProcessor.cs:149-152; grep `ILogger` finds nothing |
| S11 | `DispatchAsync` fan-out is sequential, and the first exception aborts the rest | **Confirmed** | SagaEngine.cs:63-89, with no try/catch |
| S12 | Saga events share the Marten event store with aggregates and assume Guid stream identity | **Partly**: confirmed for shared registration and Guid ids; the effect on projections is not visible in this code | MartenSagaStore.cs:302 (`AddEventTypes`), :104, 117-119 (Guid overloads) |
| S13 | Marten claims take one round trip per candidate, and nodes contend on the same top N | **Confirmed** | MartenSagaStore.cs:24-52 |
| S14 | Marten conflicts are detected by exception type name | **Confirmed** | MartenSagaStore.cs:273-294 |
| S15 | `CommittedAt` uses `DateTimeOffset.UtcNow` instead of the clock | **Confirmed** | MartenSagaStore.cs:249 |
| S16 | The Marten namespace `Tarol.Sagas.Marten` is inconsistent | **Confirmed** | MartenSagaStore.cs:9 vs InMemorySagaStore.cs:3; default `RootNamespace` (Directory.Build.props:11) |
| S17 | Engine references InMemory and hosts `UseInMemoryStore` | **Confirmed** | Sagas.Engine.csproj:5; SagaServiceCollectionExtensions.cs:7, 71-79 |
| S18 | `Sagas.Hosting` is empty; the worker lives in Engine and is always added | **Confirmed** | Sagas.Hosting.csproj:1-4; SagaOutboxProcessor.cs:127; SagaServiceCollectionExtensions.cs:124 |
| S19 | A second `AddSagas` silently drops its sagas and resolvers | **Confirmed** | `TryAddSingleton(new SagaRegistry(...))` and `TryAddSingleton(new SagaJsonResolvers(...))` (SagaServiceCollectionExtensions.cs:116-117) |
| S20 | The IVT `Sagas.Tests` does not match the assembly `Tarol.Sagas.Tests`, which is why `SagaStepTests` is commented out | **Confirmed** for the mismatch; the reason for the commenting-out is plausible but not provable | Sagas.csproj:18 vs `AssemblyName=Tarol.$(MSBuildProjectName)` (Directory.Build.props:12). `SagaStepTests` uses the internal ctor, `Outcome` and `StepOutcome` (SagaStepTests.cs:12-14, 105-106) |
| S21 | Runtime lookup by exact `GetType()`, while discovery records static type arguments; no polymorphic dispatch | **Confirmed** | SagaMessageSerializer.cs:54; SagaModelBuilder.cs:182-185; SagaDescriptor.cs:68, 89-92, 138-141; SagaEngine.cs:61-63 |
| S22 | AnalyzerReleases lists only TSAGA001; `Tarol.Sagas.targets` is referenced twice but missing | **Confirmed** | AnalyzerReleases.Unshipped.md:8; Sagas.Generators.csproj:29-33; the file is absent. Whether it is an `AdditionalFiles` item: it is not in the csproj, and `Directory.Build.props` has no item groups (grep) |
| S23 | Expensive pipeline: broad predicate, `KnownTypes` per class, `GetSemanticModel` on other trees, `GetSymbolInfo` on every invocation | **Confirmed** | SagaModelBuilder.cs:18-21, 29, 166-187, 571-591 |
| S24 | Base-class `On`/`When` are ignored, and CRTP misuse is not diagnosed | **Confirmed** | SagaModelBuilder.cs:61 (`GetMembers()` on the saga only); `InheritsFrom` compares `OriginalDefinition` only (:492-499) |
| S25 | `FakeChangeDispatcher` runs user callbacks under its lock | **Confirmed** | src/TestUtility/Sagas.Testing/FakeChangeDispatcher.cs:79-104 (`createException`, `respond` and `produce` run inside `lock (_gate)`) |
| S26 | `xunit.v3.extensibility.core` is referenced but unused | **Confirmed** | Sagas.Testing.csproj:14; grep `Xunit` in Sagas.Testing/*.cs finds nothing |
| S27 | Concurrency retry is jitter, not exponential | **Confirmed** | SagaEngine.cs:127 |
| Lead box | "79 tests pass" vs "70 active tests" (sagas.md:13, 118, 197) | Count: **78 methods / 79 cases** active; "70" is wrong | Attribute counts per file (§6) |
| B3 two csproj | `Tarol.Sagas.Testing.Tests.csproj` is broken and not in the solution | **Confirmed** | Its references point to non-existent paths (:18-21); explicit `Version=` (:12-14); `Tarol.sln` lists only `Sagas.Tests` (:286) |

---

## 8. ROADMAP Phase 4 (docs/review/ROADMAP.md:152-166)

Goals (near-verbatim):

| Goal | Met by code @6a62f81? | Evidence |
|---|---|---|
| A post-commit bridge from the event store outbox to `ISagaDispatcher` for domain events | No | No reference to `ISagaDispatcher` or `Tarol.Sagas` outside the Sagas trees (grep) |
| Fix TSAGA006 for root events by preferring `AggregateRootId` (#29) | No | SagaModelBuilder.cs:410-416 unchanged (S1) |
| Fix compensation semantics: record `Compensated` on success; on failure record `CompensationFailed`, log at critical and dead-letter (#10, #30) | No | S2 and S3; no `CompensationFailed` exists |
| Add `ILogger` throughout | No | S10 |
| Prune the correlation index and support restart after finish (S6, S7) | No | S6 and S7 |
| Add saga snapshots | No | S8 |
| Port the saga store to Npgsql with `FOR UPDATE SKIP LOCKED` claims; delete Marten or keep it behind a package; fix the IVT name and restore `SagaStepTests` | No | Only the InMemory and Marten stores exist; Sagas.csproj:18; SagaStepTests.cs commented out |

Exit criteria (verbatim, ROADMAP.md:164-166): "A saga can start from a committed aggregate event with no user glue." **Not met.** "A failed compensation is visible: there is an event in the stream, a log entry, and a dead letter." **Not met**: the test asserts the opposite (LifecycleTests.cs:267). "Tests cover the Npgsql saga store." **Not met**: no such store exists.

---

## 9. Lessons for a rewrite

**Keep**
1. Decide/evolve split with a pure, data-only `SagaStep`; handlers never perform side effects (SagaStep.cs:5-12; SagaEngine.cs:169-277). This is the core reason the tests need no infrastructure.
2. One atomic commit of events, new correlation keys and outbox rows (Abstractions.cs:13-16, 42-48; InMemorySagaStore.cs:108-134; MartenSagaStore.cs:113-129). Tested for InMemory only (InMemorySagaStoreTests.cs:55).
3. The idempotency inbox in the stream (`SagaMessageHandled`), plus stable ids for timeouts (the outbox id) and change failures (`Derive`) (Saga.cs:97-99; SagaOutboxProcessor.cs:83, 96).
4. Deterministic UUIDv5 start ids, which merge concurrent starts (SagaInternals.cs:18-41; DeliveryTests.cs:164). Include an "incarnation" so that a restart is possible (S7).
5. A generated, reflection-free descriptor and registry with persisted aliases and an AOT-safe `ChangeFailed<T>` factory (SagaMessageType.cs:29-34; SagaEmitter.cs:75-116).
6. Compile-time correlation resolution with clear diagnostics TSAGA005-007 (SagaModelBuilder.cs:355-437). Correlation keys typed `(type, value)` with the invariant culture (Primitives.cs:148-171).
7. Outbox ordering by (DueAt, commit order, Position) so that reverse compensations keep their order (Abstractions.cs:73-77; LifecycleTests.cs:136-152).
8. The test-host style: real engine, real generated code and real serializer, with a controllable clock (src/TestUtility/Sagas.Testing/SagaTestHost.cs:39-48).

**Drop or change**
1. "Compensated/Faulted on enqueue" and the silently dropped compensation failures. Record success and failure events, add a `Compensating` state, and dead-letter and log failures (S2, S3; SagaEngine.cs:266-276; SagaOutboxProcessor.cs:93-104).
2. Registering a compensation before its change has run. Register after success, or skip the failed change's own compensation (S4; SagaEngine.cs:254-255).
3. A start handler that runs again on an existing saga (N1). Either reject or report a second start, or let `When` see `IsNewSaga`, and test it (SagaEngine.cs:154-178).
4. An index that only grows, and no restart (S6, S7). Remove keys on finish or on value change.
5. The name-based `DomainEvent` heuristic. Model Tarol events explicitly and prefer `AggregateRootId` (S1; SagaModelBuilder.cs:458-465).
6. `Func<DateTimeOffset>` and the stray `DateTimeOffset.UtcNow`. Use an injected `TimeProvider` everywhere, delays included (SagaOptions.cs:14; MartenSagaStore.cs:249; SagaEngine.cs:127).
7. Marten: matching conflicts by exception name, a per-document claim round trip, double-encoded payloads, and no tests. Replace it with an Npgsql store that uses `SKIP LOCKED` and `SagaJsonContext` (S13, S14; MartenSagaStore.cs:21-52, 273-294).
8. Lease ownership not checked on Complete/Retry. Carry a lease token (SagaOutboxProcessor.cs:43-57; ISagaOutbox at Abstractions.cs:94-111).
9. The generator: a broad syntax predicate, `KnownTypes` per node, locations inside models, a missing `ApplyState` default, silently skipped `When` overloads, incomplete AnalyzerReleases, and a separate generator package. Use `ForAttributeWithMetadataName` or a marker, keep locations out of cached models, add a throwing default, and ship the generator inside the runtime package as Tarol-Net requires (S9, S22, S23; SagaModelBuilder.cs:18-35, 270-271; Models.cs:39-43, 87; Sagas.Generators.csproj:8-10).
10. Packaging and DI: Engine→InMemory dependency, the empty Hosting project, the worker always registered, `TryAdd` swallowing a second `AddSagas`, and the wrong IVT (S17-S20).
11. No logging and serial fan-out without isolation. Use source-generated `LoggerMessage` and isolate each subscriber (S10, S11).
12. TSAGA012 only fires when a context exists. Make JSON context coverage mandatory, or diagnose it, for AOT (SagaGenerator.cs:85-86; SagaMessageSerializer.cs:102-106).
13. Close the test gaps listed in §6. The leftover `Tarol.Sagas.Testing.Tests.csproj` must not be migrated.
