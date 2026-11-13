# Tracing and correlation

Concept note for step 4 (see [kernel.md](../migration/kernel.md)): `System.Diagnostics.Activity` / W3C trace context
against Tarol's own ids; correlation and causation ids on events and messages.
Labels and probes as in [time.md](time.md).

## 1. What a user of Tarol needs it for

* **Debugging a request end to end**: one trace in the APM (Jaeger, Aspire dashboard, Application Insights) from the
  HTTP request through the command, the commit and the outbox.
* **Following a business conversation**: "everything that happened because of order 42's checkout", across a saga that
  runs for days, retries, and messages to other services.
* **Idempotency and causality**: "which message caused this event", "was this message already handled".

## 2. Where it touches the framework

| Place | Id | Old Tarol (6a62f81) |
|---|---|---|
| Incoming HTTP request | trace (W3C `traceparent`) | ASP.NET Core's activity; nothing in Tarol |
| Command / handler (step 10) | causation = the command's id; correlation from the context | none |
| Event metadata (steps 6, 8) | event id, correlation, causation, trace parent | none: `EventColumns` has type, version, ids, timestamp only **[verified by reading]** |
| Saga engine (step 9) | message id (idempotency), correlation (defaults to saga id), causation (= handled message id) | `SagaEnvelope(MessageId, Message, CorrelationId?, CausationId?)`; `ActivitySource "Tarol.Sagas"` with tags `saga.type`, `saga.id`, `messaging.message.id` |
| Outbox (step 9) | carries correlation and causation to the delivered message | `SagaOutboxMessage.CorrelationId/CausationId` |
| Messaging (step 11) | an envelope with id, correlation, causation, tenant | topic + bytes only; ROADMAP Phase 5 plans an envelope |
| `IUserContext.TraceId` | `Id<ITrace>` (a UUIDv7 `Guid`) | no implementation |

## 3. What .NET 11 already provides

* **`Activity` / `ActivitySource`**: W3C trace context (`traceparent`: `00-<trace-id 32 hex>-<span-id 16 hex>-<flags>`),
  `Activity.Current` flowing with the `ExecutionContext`, `ActivityLink` for causal links between traces, `Baggage`.
  OpenTelemetry .NET exports it. In .NET 11 the trace flags are `03` (sampled + **random trace id**, W3C level 2) when
  sampled **[verified]** (`authz/`).
* **ASP.NET Core creates the request activity without OpenTelemetry** while logging is enabled (`Information` and
  `Warning` both), and honours an incoming `traceparent` (the trace id is the caller's) **[verified]** (`web/`).
* **But no listener, no activity**: with logging `None` and no OpenTelemetry, `Activity.Current` is `null` in the endpoint,
  and the incoming `traceparent` is dropped **[verified]** (`web/`). `ActivitySource.StartActivity` returns `null` when
  nothing listens **[verified]** (`authz/`).
* **`HttpContext.TraceIdentifier`** (`<connection>:<n>`) exists always but is Kestrel-local and not W3C **[verified]**.

## 4. What Tarol must add on top

1. **Tarol does not invent a trace id.** `Id<ITrace>` is dropped: a trace id is `ActivityTraceId`, owned by
   `System.Diagnostics`, and a UUIDv7 `Guid` cannot carry it without a conversion that tools do not understand.
2. **Tarol owns the *business* ids, because tracing does not provide them reliably**:
   * `CorrelationId` (a `Guid`, UUIDv7): the conversation. Created at the edge when an operation starts (a request, a
     job, a saga start) and copied unchanged to every event, message and saga step that follows. Sagas default it to
     the saga id, as today.
   * `CausationId` (`Guid`): the id of the command or message that directly caused this event/message.
   * `EventId` / `MessageId` (`Guid`, UUIDv7 from the injected `TimeProvider`): idempotency key.
3. **The trace is *linked*, not continued, across the outbox.** The event/outbox row stores the `traceparent` of the
   activity that committed it (`Option<string>`, absent without an activity). The outbox worker starts its own activity
   with an `ActivityLink` to it **[unverified design]**. Continuing the same trace across a saga that waits for days
   would make one trace span days (APMs handle that badly); a link keeps the path without that.
4. **An `ActivitySource` per package** (`Tarol.Persistence`, `Tarol.Sagas`, …) with OpenTelemetry semantic-convention tag
   names, in the step that adds the work (8, 9, 11). No Tarol tracing abstraction.

## 5. AOT and testability

* `Activity`, `ActivityListener` and the W3C id formatting publish with 0 IL warnings **[verified]** (`authz/`).
* Tests assert on ids they pass in (`CorrelationId` in the operation context), not on `Activity.Current`.
  A test that wants spans adds an `ActivityListener` for the source (as the `authz/` probe does).

## New insights

1. **`Activity.Current` is optional infrastructure, not data.** It disappears when logging is off and nothing listens
   **[verified]**, so nothing that must be *stored* (correlation, causation) may depend on it; only the optional
   `traceparent` link does.
2. **An incoming `traceparent` is attacker-controlled** **[verified that it is honoured]**: fine for tracing, never an
   input to authorization or idempotency. Tarol creates its correlation id server-side at the edge; accepting a
   client-provided correlation id is an explicit opt-in of the web package.
3. **Correlation ≠ trace**: a saga conversation outlives traces (timeouts days later run in new traces). The stable
   business correlation id plus a trace *link* covers both needs; the old `ITrace` marker mixed them up.
4. **The saga engine already has the right model** (message id, correlation defaulting to the saga id, causation = the
   handled message id). Step 4 lifts it into the shared context so events and commands use the same three ids.

## Open questions

* Store `traceparent` as text (55 chars) or as the 16-byte trace id + 8-byte span id? Step 8 (text is simpler and
  readable; size is irrelevant against the payload).
* Should the outbox worker continue the trace for short delays (seconds) and link only for long ones? Default: always
  link; revisit with real traces in step 9.
* Baggage for tenant propagation across services: no (tenant is security data, §security.md; baggage is untrusted).
