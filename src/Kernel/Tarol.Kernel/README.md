# Tarol.Kernel

The origin of every operation and every event: **who** acts (`Actor`), for **which tenant**, in **which
conversation** (correlation) and **because of what** (causation), as one immutable `OperationContext`. Plus an
analyzer that keeps the system clock out of your code: Tarol has no ambient clock, you inject `TimeProvider`.
Native-AOT ready. Depends on `Tarol.Options` and `Tarol.StronglyTypedId`.

## Install

```bash
dotnet add package Tarol.Kernel
```

## 30 seconds

```csharp
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

// at the edge (request, message, job): build once, then pass it on
var context = OperationContext.Start(Actor.User(subject), tenant: tenantId, clock);   // clock: TimeProvider

// a message handled later: same conversation, caused by that message
var handling = context.CausedBy(messageId);                    // messageId: Id<ICausation>

// a saga or job continues on its own, on behalf of the user
var saga = handling.AsSystem("Sagas.AuctionTimeout");          // Actor.System("Sagas.AuctionTimeout", onBehalfOf: subject)

// tests: construct it, nothing global
var test = new OperationContext(Actor.User("alice"), Option.None, Id<ICorrelation>.New(), Option.None);
```

| Type | What |
|---|---|
| `Actor` | `Kind` (`Anonymous`, `User`, `Service`, `System`), `Id` (the identity provider's subject, a client id or a component name), `OnBehalfOf` (only for `System`). No personal data: events are immutable. |
| `OperationContext` | `Actor`, `Tenant` (`Option<Id<ITenant>>`, `None` = single-tenant or host-level), `CorrelationId` (`Id<ICorrelation>`), `CausationId` (`Option<Id<ICausation>>`); `Start`, `CausedBy`, `AsSystem` |
| `ITenant`, `ICorrelation`, `ICausation` | phantom types for `Id<T>` (interfaces, so they never clash with your own `Tenant` aggregate) |
| `KernelJsonContext` | source-generated metadata to serialize the Kernel types on their own |

## Time: inject `TimeProvider`

There is no Tarol clock. Code with a container takes a `TimeProvider`; an aggregate behaviour takes `now` from its
handler; tests use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`). The store stamps one recorded-at time
per commit. An ambient clock does not reach hosted workers and gives no timers (see `docs/architecture/time.md`).

```csharp
public Result Bid(Money amount, DateTimeOffset now) => now >= EndsAt ? Error.Conflict("Auction.Ended") : Raise(new BidPlaced(amount, now));
auction.Bid(cmd.Amount, timeProvider.GetUtcNow());
```

## JSON

A fixed contract (independent of your naming policy, because it is stored), applied by converters on the types: you
list only your own type in your `JsonSerializerContext`.

```text
{"actor":{"kind":"System","id":"Sagas.AuctionTimeout","onBehalfOf":"alice"},"tenant":"0193a6f2-…","correlationId":"0193a6f2-…","causationId":null}
```

Invalid input (unknown kind, empty id, `onBehalfOf` on a user) is a `JsonException`.

## Analyzer (bundled)

| Id | Severity | Reports |
|---|---|---|
| TKRN001 | Warning | `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`: inject `TimeProvider` or take `now` as a parameter. `TimeProvider.System` is allowed. |

## More

* Agent skill: `skills/tarol-kernel/SKILL.md` (in the package)
* Concepts: `docs/architecture/time.md`, `identity-context.md`, `tracing.md`, `security.md`; design and measurements:
  `docs/migration/kernel.md` in the repository

Licence: proprietary, see `LICENSE`.
