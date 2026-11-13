# Time

Concept note for step 4 (see [kernel.md](../migration/kernel.md)). It covers the clock, event timestamps,
deterministic tests, and time in sagas (timeouts and the outbox).

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and command in
[`probes/kernel/`](../migration/probes/kernel/), output in [`output.txt`](../migration/probes/kernel/output.txt));
**[unverified]** means reasoned or read, not run here.

## 1. What a user of Tarol needs it for

* **"Now" in domain decisions**: an auction ends at `EndsAt`; a bid after that is rejected. Decisions compare the
  domain's data with the current time.
* **When an event was recorded**: audit, projections ("last changed"), support. In the old code this is
  `DomainEvent.Timestamp`.
* **Deferred work**: saga timeouts (`Schedule(msg, delay)`), outbox retries with backoff, leases, polling.
* **Deterministic tests**: a test sets the time, moves it forward, and asserts on what became due. Tests run in
  parallel (xUnit v3 runs test classes in parallel by default), so a test's clock must not leak into another test.

## 2. Where it touches the framework

| Place | What it needs | Old Tarol (6a62f81) |
|---|---|---|
| Aggregate behaviour | "now" for a decision (`EndsAt <= now`) | nothing; ArtFlow injects `TimeProvider` into a prerequisite **[verified by reading]** |
| Event creation | a recorded-at timestamp | field initializer `Timestamp = DomainClock.Provider.GetUtcNow()` (`DomainEvent.cs:48`): global mutable static, and it also runs on **deserialization** (finding 14) **[verified]** |
| Command handler | "now" to pass to the aggregate | none (no CQRS pipeline) |
| Unit of work / store (step 8) | one timestamp per commit, stored as a column | Marten stores its own commit time; the domain time is lost (persistence.md) |
| Sagas (step 9) | `now` per handling, `DueAt` for timeouts, outbox retry times, lease expiry | `SagaOptions.UtcNow` delegate (default `TimeProvider.System`); `MartenSagaStore` bypasses it with `DateTimeOffset.UtcNow` (S15) |
| Messaging (step 11) | reconnect window, backoff delays | static ambient `Clock.UtcNow` in `MessageBackplaneRunner`; `Task.Delay` with real time |
| Ids | UUIDv7 embeds a timestamp | `Id<T>.New(TimeProvider)` already exists in Tarol-Net (step 1); saga outbox uses `Guid.CreateVersion7(now)` |
| Web | nothing beyond the handler | — |

So the old code has **three clocks**: `DomainClock` (Primitives), the ambient `Tarol.Clock` (Messaging, recommended in
Sagas docs) and `SagaOptions.UtcNow`.

## 3. What .NET 11 already provides

* **`System.TimeProvider`** (BCL since .NET 8): `GetUtcNow()`, `GetTimestamp()`, `LocalTimeZone`, **`CreateTimer`**.
  `Task.Delay(TimeSpan, TimeProvider, ct)`, `Task.WaitAsync(TimeSpan, TimeProvider)`, `PeriodicTimer(TimeSpan,
  TimeProvider)` and `CancellationTokenSource(TimeSpan, TimeProvider)` all accept it **[unverified here; documented BCL
  API]**. A clock is therefore not only "now": it also drives delays and timers.
* **`FakeTimeProvider`** (`Microsoft.Extensions.TimeProvider.Testing` 11.0.0): settable, `Advance`, fires its timers
  synchronously on `Advance` **[verified]** (`flow/`). Test-only package.
* **Dependency injection**: hosting registers nothing by default; `services.AddSingleton(TimeProvider.System)` is the
  idiom **[unverified: whether the generic host registers `TimeProvider` itself; Tarol registers it in its own setup]**.
* **`AsyncLocal<T>`** for ambient values, flowing with the `ExecutionContext` (§5).

## 4. What Tarol must add on top

Very little, and nothing that replaces `TimeProvider`:

1. **One rule for where each kind of time comes from** (the main result of this note):
   * **Recorded time** (infrastructure): the store stamps *one* `RecordedAt` per commit from the injected
     `TimeProvider`, for all events of that commit, as a column (step 8). Raising or deserializing an event reads no
     clock.
   * **Business time** (domain data): the handler reads `TimeProvider` once and passes `DateTimeOffset now` into the
     behaviour (`auction.Bid(amount, now)`), and the behaviour copies it into the event payload where it matters
     (`BidPlaced(Amount, At)`). The aggregate stays a pure function of state, command and time.
   * **Scheduled time** (sagas, outbox): computed from the injected `TimeProvider` of the engine; delays and polling use
     the same provider (`Task.Delay(d, timeProvider)`), so `FakeTimeProvider.Advance` drives them.
2. **No ambient clock in the framework.** Every place in the table above that needs time has a container (handler,
   store, saga engine, outbox worker, hosted service), or receives `now` as an argument (aggregate). See §5 for why an
   ambient clock does not fit the places that need it most.
3. **An analyzer** that reports `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow` (and
   `TimeProvider.System` outside composition) in user code: "inject `TimeProvider`" (decision 6 in kernel.md).
4. **Test helpers stay Microsoft's**: `FakeTimeProvider`. `Sagas.Testing` (step 9) wraps it instead of its own
   `SagaTestClock` (which could not virtualize timers).

## 5. Ambient vs injected: what was measured

**Finding 14 reproduced and fixed** **[verified]** (`isolation/`, xUnit v3 4.0.3, three runs): class A swaps the static
`DomainClock` to 2000-01-01 for 300 ms; class B, running in parallel, creates events. **100 % of B's events**
(214 217–279 083 per run) carry A's year 2000. With the `AsyncLocal` clock swapped the same way, **0** events leak. An
injected `FakeTimeProvider` cannot leak by construction.

**`AsyncLocal` flow** **[verified]** (`flow/`, JIT and Native AOT identical, 0 IL warnings):

| Flows into the ambient scope | Does **not** see it |
|---|---|
| code after `await`, `ConfigureAwait(false)` continuations, `Task.Run`, `StartNew(LongRunning)`, `Parallel.For`, a `Timer` created inside the scope, a `BackgroundService` **started** inside the scope | a timer or loop started **before** the scope, `UnsafeQueueUserWorkItem`, `ExecutionContext.SuppressFlow`, and the **caller** of an async helper that set it |

**Where a test can set an ambient value in xUnit v3** **[verified]**: the test method, the constructor, or a
*synchronously completing* `InitializeAsync` work; an `InitializeAsync` that awaits before setting it does **not** (the
value is lost when the async method returns). A value set and never disposed does **not** leak into the next test of the
class. **Cost** **[verified]** (`bench/`): an `AsyncLocal` read is 4.6 ns, ambient "now" +10 ns over
`DateTimeOffset.UtcNow` (31 ns); a set-and-reset scope allocates 72 B. Cost is no argument either way.

Consequences:

* An ambient clock **cannot drive the places that need a test clock most**: the outbox worker and messaging loops are
  started once (by the host) and run forever. They see the ambient value of *the moment the host started*, not the
  test's later scope; a host started inside one test's scope keeps that test's clock for its lifetime.
* An ambient "now" gives no **timers or delays**. Code would have to write `Task.Delay(d, Clock.Provider)` anyway, which
  is injection with extra steps.
* The one place without a container, the aggregate, should not read a clock at all (§4.1): reading it hides an input,
  and replay must not depend on it.

## 6. What must stay AOT-safe and testable

* `TimeProvider`, `FakeTimeProvider`, `AsyncLocal` and the hosted-service path publish with 0 IL warnings **[verified]**.
* Tests construct the clock: `var clock = new FakeTimeProvider(start);` and pass it (handler, saga host, store). No
  global state, so parallel test classes are independent.

## New insights

1. **There are three times, not one clock**: recorded (store, per commit), business (an argument of the behaviour, data
   in the payload) and scheduled (engine). Each has one source; only the first is framework metadata.
2. **The timestamp belongs to the commit, not to the `new` of an event.** Stamping in a field initializer read the clock
   for every *deserialized* event too, and gave the events of one commit different times. One `RecordedAt` per commit is
   cheaper, consistent, and needs no clock in Primitives at all.
3. **An ambient clock fails exactly where it was meant to help**: long-running workers capture the context at start, so a
   test scope opened later never reaches them; and it cannot provide `CreateTimer`/`Delay`.
4. **xUnit v3 lifecycle trap**: an `AsyncLocal` set in an awaiting `InitializeAsync` silently does not reach the test.
   Any ambient design needs this in its docs; an injected design does not.
5. **UUIDv7 ids are time too.** Event ids and outbox ids generated from the same injected `TimeProvider`
   (`Id<T>.New(timeProvider)`, `Guid.CreateVersion7(now)`) make ids deterministic in tests and sortable by the same clock.

## Open questions

* Should `RecordedAt` be the database's `now()` (single source across nodes) or the application's `TimeProvider`
  (testable)? Recommendation: the application's, so tests and the outbox use one clock; the database may add its own
  `inserted_at` for operations. Step 8 decides.
* Does an aggregate ever need the time *without* the handler passing it (e.g. inside a `Prerequisite` in step 5)?
  The prerequisite then takes `now` as a parameter too. Step 5 confirms.
* Clock skew between nodes for `DueAt` claims (outbox): lease length must exceed skew. Step 9.
