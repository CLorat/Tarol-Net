# Inventory of the old Messaging code (`CLorat/Tarol@6a62f81`)

Read file by file **[verified by reading]**; built and tested on SDK 11.0.100 where it says **[verified]**
([`output.txt`](output.txt) §10). Line numbers at `6a62f81`. Production code: **341 lines in 18 files, 12 projects**, plus a
test project under `src/` and an empty generator project (the review estimated about 450 lines).

## 1. Projects

| Project | Lines | Public types (file:line) | Depends on | Fate |
|---|---:|---|---|---|
| `src/Messaging/Messaging` | 6 | `MessageAttribute(string topic)` (MessageAttribute.cs:3-7), unused anywhere | — | not ported: `[Publishes<T>]`/`[Consumes]` replace it |
| `Messaging.Protocol` | 9 | `IMessageProtocol { Serialize<T>, Deserialize<T>, ContentType }` (IMessageProtocol.cs:3-10) | — | idea kept: the app's JSON context deserializes the contract record (`MessageScope.Payload<T>`) |
| `Messaging.Protocol.Json` | 43 | `JsonAotProtocol<TContext>` (JsonAOTMessageProtocol.cs:7-44; file name ≠ type name) | Protocol | not ported (F20) |
| `Messaging.Transport` | 17 | `ITransport { Start, Publish, Subscribe(topic, handler), Unsubscribe(topic) }` (ITransport.cs:6-18), client side | — | shape input for 11D (the UI client) |
| `Messaging.Transport.SignalR` | 107 | `SignalRTransport : ITransport` (SignalRTransport.cs:6-90), `ISignalRConnection` (ISignalRConnection.cs:5-19), no production adapter | Transport, Shared.SignalR, `SignalR.Client` | 11D: rebuilt (F7–F11, F17–F19) |
| `Messaging.Shared.SignalR` | 12 | `ISignalRMessageHub { Subscribe, Unsubscribe, Publish }` (ISignalRMessageHub.cs:3-8), `ISignalRMessageClient.OnMessage` (ISignalRMessageClient.cs:3-6) | — | 11D: the hub contract, plus a source-generated JSON context (F1) |
| `Messaging.Gateway` | 5 | `IMessageGateway.BroadcastAsync(topic, payload, ct)` (IMessageGateway.cs:3-6) | — | 11D: the UI push port |
| `Messaging.Gateway.SignalR` | 34 | `SignalRMessageHub : Hub<ISignalRMessageClient>` (SignalRMessageHub.cs:7-23), `SignalRMessageGateway` (SignalRMessageGateway.cs:6-13, does not implement `IMessageGateway`) | Backplane, Shared.SignalR, `SignalR.Core` 1.2.13, `Newtonsoft.Json` beta | 11D: rebuilt with an authenticated plain `Hub` |
| `Messaging.Backplane` | 82 | `IMessageBackplane { Publish(topic, bytes), Start(Func<topic, bytes, Task>) }` (IMessageBackplane.cs:3-8), `MessageBackplaneRunner : BackgroundService` (MessageBackplaneRunner.cs:6-56), health check + service | Clock, Gateway, `SignalR.Common` (unused), HealthChecks + Hosting abstractions | **candidate T-D** (§2 of the evaluation); the evolved shape is the transport port T-A |
| `Messaging.Backplane.InMemory` | 22 | `InMemoryMessageBackplane` (InMemoryMessageBackplane.cs:5-23), an unbounded `Channel` | Backplane | replaced by `InMemoryTransport` (fan-out per subscription, redelivery) |
| `Messaging.Security` | 5 | `ITopicAuthorizer.IsAuthorized(topic, ct)` (ITopicAuthorizer.cs:3-6; namespace without `Tarol.`), unused | — | 11D: `(principal, operation, topic)`; system-to-system: broker permissions per contract prefix |
| `Messaging.Client` | 5 | `public class IMessagingClient {}` (IMessagingClient.cs:3-6) | — | not migrated (plan.md) |
| `src/…/Messaging.Backplane.InMemory.UnitTests` | 130 | 4 tests (namespace `Messagings.…`, line 5) | InMemory | not ported |
| `gen/Messaging/Messaging.Generators` | 0 | csproj only (4 lines), referenced by nothing | — | not migrated (plan.md); the consumer and publication generator lives in the runtime package |

No project outside `src/Messaging` and `tests/Messaging` references a `Tarol.Messaging.*` type **[verified by reading]**
(grep over every `*.csproj`): Messaging is not connected to events, changes or sagas.

## 2. Tests **[verified]** (SDK 11.0.100, each test executable run)

| Project | Tests | Result | Note |
|---|---:|---|---|
| `Messaging.Backplane.InMemory.UnitTests` (under `src/`) | 4 | pass | |
| `Messaging.Backplane.UnitTests` | 3 | **1 fails in 5 of 6 runs** | `ExecuteAsync_Should_SetConnected_When_BackplaneStartsSuccessfully` (MessageBackplaneRunner.Tests.cs:50-56: `StartAsync` then `Task.Yield()`); the other two assert nothing (review §5) |
| `Messaging.Gateway.SignalR.UnitTests` | 1 | pass | group send only |
| `Messaging.SignalR.Hub.UnitTests` | 2 | pass | Subscribe, Publish |
| `Messaging.Transport.SignalR.UnitTests` | 9 | pass | mocked `ISignalRConnection` |
| `Messaging.Protocol.Json.UnitTests` | 7 | pass | |
| `Messaging.IntegrationTests` | 12 | **12 fail** | `InvalidOperationException: Reflection-based serialization has been disabled`, from `JsonHubProtocol.WriteArguments` ← `HubConnection.SendHubMessage` (F1) |
| `Messaging.UnitTests` | 0 | — | csproj only |

**With F1 fixed in a copy** (`AddJsonProtocol` with a source-generated context for `string` and `byte[]` on the client
and the server of `SignalRInMemoryTestHost.cs:44,76-82`): **12 of 12 pass** **[verified]**. F2 is therefore not a defect.

`plan-messagingIntegrationTests.prompt.md` (96 lines): eight scenarios over pluggable backplane, protocol and transport
factories. Present in `MessagingCycleTests.cs` (422 lines): 1–4, 6–8 (scenario 6 compares deserialized values, not
bytes); **missing**: 5 (reconnect and resubscribe) and the factories `IMessageBackplaneFactory`, `IMessageGatewayFactory`,
`ITransportFactory` (the host hard-codes InMemory + SignalR, SignalRInMemoryTestHost.cs:38-52). Its content moves to 11D.

## 3. Review findings (`docs/review/messaging.md:97-121`) re-verified

| # | Finding | file:line at `6a62f81` | Status now |
|---|---|---|---|
| F1 | integration tests fail: no source-generated SignalR JSON context, `PublishAot` disables reflection | Messaging.IntegrationTests.csproj:5-6; SignalRInMemoryTestHost.cs:44,76-82 | **confirmed** [verified]: 12/12 fail on SDK 11; the proposed fix makes 12/12 pass [verified] |
| F2 | `CancellationToken` on non-streaming hub methods fails binding | ISignalRMessageHub.cs:5-7; SignalRMessageHub.cs:9,14,19 | **refuted** [verified]: with F1 fixed every hub call binds and the 12 tests pass (JIT; under Native AOT not run) |
| F3 | no authentication or topic authorization | SignalRMessageHub.cs:7-23; ITopicAuthorizer.cs:3-6 | **confirmed** [verified by reading]: no `[Authorize]`, `ITopicAuthorizer` unreferenced, no principal or operation |
| F4 | `SignalR.Common` vulnerable (NU1903) and unused in Backplane | Messaging.Backplane.csproj:10; Directory.Packages.props:27 | **partly**: the pin is 11.0.0 now and restore reports no NU1903 [verified]; the reference is still unused [verified by reading]; new: NU1510 for `Hosting.Abstractions` (Backplane.csproj:12, .NET 11 base framework) [verified] |
| F5 | `SignalR.Core` 1.2.13 (ASP.NET Core 2.x), `Newtonsoft.Json` beta unused, `SignalR` 1.2.0 pinned unused | Gateway.SignalR.csproj:6-7; Directory.Packages.props:24,28,55 | **confirmed** [verified by reading]; on .NET 11 the server API is the `Microsoft.AspNetCore.App` framework reference |
| F6 | `SignalRMessageGateway` does not implement `IMessageGateway` | SignalRMessageGateway.cs:6; test adapter SignalRInMemoryTestHost.cs:98-104 | **confirmed** [verified by reading] |
| F7 | no production `ISignalRConnection`; test adapter discards the `Reconnected` task | ISignalRConnection.cs:5-19; SignalRInMemoryTestHost.cs:109-120 | **confirmed** [verified by reading] |
| F8 | Subscribe before Start never reaches the server | SignalRTransport.cs:31,40-58 | **confirmed** [verified by reading] |
| F9 | a failed server Subscribe leaves the topic dead | SignalRTransport.cs:43-53 | **confirmed** [verified by reading] |
| F10 | `_subs` read without the lock | SignalRTransport.cs:16-27,82 | **confirmed** [verified by reading] |
| F11 | `Unsubscribe(topic)` removes every handler | SignalRTransport.cs:61-78 | **confirmed** [verified by reading] |
| F12 | a backplane whose `Start` returns makes the runner spin | MessageBackplaneRunner.cs:30-36 | **confirmed** [verified]: 8.6 million restarts in 1 s (probe `legacy`, the loop copied) |
| F13 | one failing broadcast loses the message and stops delivery for the back-off | MessageBackplaneRunner.cs:30-33,43-52 | **confirmed** [verified]: the message is gone, the next arrives after 1.6 s |
| F14 | unbounded channel | InMemoryMessageBackplane.cs:9 | **confirmed** [verified by reading] |
| F15 | no envelope (id, type, tenant, correlation, …) | whole family | **confirmed** [verified by reading]; the wire is `(string topic, byte[] payload)` |
| F16 | `IsConnected = true` before connecting; no `volatile` | MessageBackplaneRunner.cs:28; MessagingBackplaneHealthService.cs:5-6 | **confirmed** [verified by reading] |
| F17 | `On(...)` handle discarded, semaphore not disposed, ownership of the connection | SignalRTransport.cs:10,16,89 | **confirmed** [verified by reading] |
| F18 | handlers serial on the receive loop; one throw skips the rest | SignalRTransport.cs:20-25 | **confirmed** [verified by reading] |
| F19 | `payload.ToArray()` copies | SignalRTransport.cs:35; SignalRMessageGateway.cs:11 | **confirmed** [verified by reading] |
| F20 | cast `(JsonTypeInfo<T?>?)` fails for value types | JsonAOTMessageProtocol.cs:19,35 | **confirmed** [verified by reading] |
| F21 | names and typos (`Messagings.`, `IMessagingClient` a class, `Protocoll`, `// NEW:`) | InMemoryMessageBackplane.Tests.cs:5; IMessagingClient.cs:3; ITransport.cs:16 | **confirmed** [verified by reading]; disappear by construction (plan.md) |
| F22 | a test project under `src/` | src/Messaging/Messaging.Backplane.InMemory.UnitTests | **confirmed** |
| F23 | empty generator project | gen/Messaging/Messaging.Generators/Messaging.Generators.csproj:1-4 | **confirmed** |
| new N1 | two runners on one in-memory backplane compete for messages (no fan-out across nodes) | InMemoryMessageBackplane.cs:9,18-21 | [verified]: 50 / 50 of 100 messages (probe `legacy`) |
| new N2 | at most once by construction: `Start(Func<topic, bytes, Task>)` has no answer, so no ack, retry or dead letter can exist behind this port | IMessageBackplane.cs:5-7 | [verified by reading]; why T-D needs an answer per message |

## 4. ROADMAP Phase 5 (`docs/review/ROADMAP.md:170-189`) and D-6 (`:212`)

| Phase 5 goal | Where it goes in step 11 |
|---|---|
| an envelope (id, type alias, tenant, correlation, version) | `Message`: CloudEvents attributes + extensions (evaluation §2.3) |
| a shipped source-generated SignalR JSON resolver | 11D (verified as the F1 fix) |
| a plain `Hub` without `CancellationToken` on non-streaming methods | 11D: plain `Hub` for AOT (typed hubs use Reflection.Emit, review §4); the token is fine (F2 refuted) |
| authentication + per-topic authorization with tenant isolation | system-to-system: broker/database permissions per contract prefix + the consumer's source/tenant rules (verified); UI: 11D |
| a production `ISignalRConnection`, resubscribe on reconnect, bounded channel, logging | 11D |
| a messaging generator from `[Message]` | the consumer switch and publication set, generated inside the runtime package |
| exit: integration suite passes incl. reconnect; every hub operation authorized | 11D's tests |

D-6 recommended "(b) for now: the outbox plus `pg_notify` as the internal bus, SignalR as a client gateway with real
authorization; generalize later". M-1 widens it to a port, an in-database transport and one broker; M-3 keeps the gateway as
the UI push (11D).
