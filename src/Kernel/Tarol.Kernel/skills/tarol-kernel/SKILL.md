---
name: tarol-kernel
description: >-
  Use when code needs to know who performs an operation, for which tenant, or in which conversation (audit, event
  metadata, sagas, messages, multi-tenancy), or needs the current time in Tarol code. Covers Actor, ActorKind,
  OperationContext (Start, CausedBy, AsSystem), Id<ITenant>/Id<ICorrelation>/Id<ICausation>, KernelJsonContext, the
  rule "inject TimeProvider, no ambient clock" and TKRN001. Do not decompile the assembly or search the web: the old
  Tarol.Clock, DomainClock, IUserContext, IUser/ITrace and IAuthorizer no longer exist.
---
# Tarol.Kernel: operation context and time

## Rules

1. Who acts is an `Actor` (`using Tarol.Kernel;`): `Actor.User(sub)` (the identity provider's subject, a string, never
   `Id<User>`), `Actor.Service(clientId)`, `Actor.System(component, onBehalfOf)`, `Actor.Anonymous`. Never put names,
   e-mails or roles into an actor: it is stored in immutable events.
2. Build one `OperationContext` at the edge and pass it on (parameter or scoped service). There is no accessor and no
   `AsyncLocal`; never store it in a static.
   * start: `OperationContext.Start(actor, tenant, timeProvider)` (new UUIDv7 correlation from the clock);
   * a message being handled: `context.CausedBy(messageId)` (same correlation);
   * a saga, timeout or job continuing: `context.AsSystem("Sagas.X")` (keeps the user in `OnBehalfOf`).
3. Tenant: `Option<Id<ITenant>>`. `Option.None` means single-tenant or host-level. Take it from the authenticated
   context, never from a command payload.
4. Ids are `Id<ITenant>`, `Id<ICorrelation>`, `Id<ICausation>` (`Tarol.StronglyTypedId`); they cannot be mixed up.
5. Time: no Tarol clock. Inject `TimeProvider`; aggregates take `DateTimeOffset now` as a parameter; tests use
   `FakeTimeProvider`. `DateTime.Now/UtcNow/Today` and `DateTimeOffset.Now/UtcNow` are TKRN001.
6. JSON needs nothing: `Actor` and `OperationContext` carry converters with a fixed camelCase contract. Use
   `KernelJsonContext.Default.OperationContext` to serialize a context on its own.

## Minimal correct example

```csharp
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

public sealed record AuditEntry(string What, OperationContext Context);

public sealed class AuctionTimeoutHandler(TimeProvider clock)
{
    public AuditEntry Handle(OperationContext context, Id<ICausation> timeoutMessage)
    {
        var saga = context.CausedBy(timeoutMessage).AsSystem("Sagas.AuctionTimeout");
        var closedAt = clock.GetUtcNow();   // not DateTimeOffset.UtcNow (TKRN001)
        return new($"closed at {closedAt:O}", saga);
    }
}

// test
var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
var user = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), clock);
var entry = new AuctionTimeoutHandler(clock).Handle(user, Id<ICausation>.New(clock));
// entry.Context.Actor == Actor.System("Sagas.AuctionTimeout", "alice"); same CorrelationId as user
```

## Common mistakes

| Mistake | What happens |
|---|---|
| `DateTimeOffset.UtcNow` / `DateTime.Now` in handlers or aggregates | TKRN001 warning (an error with warnings as errors); tests cannot control time |
| a static or `AsyncLocal` "current context" or clock | leaks between parallel tests and into hosted workers; use parameters/DI |
| `new Actor(ActorKind.User, "alice", "bob")` | `ArgumentException`: only `System` acts on behalf of someone |
| `Actor.User("")` | `ArgumentException` |
| `Id<IUser>` or `Id<User>` for the caller | `IUser` does not exist; the caller is `Actor.User(subject)` |
| `tenant = isNull ? default : id` with an `Option<Id<ITenant>>` target | `Some(Empty)` (TOPT004); write `Option.None` |
| `option.TryGetValue(out var id)` on an `Option<Id<…>>` | CS0121 (ambiguous); write `out Id<ITenant> id` or `option is Id<ITenant> id` |
| `{"kind":"user",…}` | `JsonException`: the contract is case-sensitive |
| a permission check inside an aggregate | do it in the handler/pipeline (Tarol.Security, step 10); aggregates check invariants only |

## AOT

Native-AOT safe, 0 IL warnings; nothing to register. Put your own types in your `JsonSerializerContext`; the Kernel
types inside them need no entry.
