---
name: tarol-changes
description: >-
  Use when writing a Tarol change (the input record an actor sends to change an aggregate) or running one: IChange<T>,
  ICreation<T>, ILoads<T>, [Read], [Reads<T>], ChangeContext (Operation, Now, Read<T>, TryRead, Confirmed), ChangePipeline.Run,
  ChangeOptions, ChangeOutcome, IChangeAuthorization, Error.ConfirmationRequired, ChangeTest and ChangeTestOptions, and fixing
  TCHG001-TCHG005 and TCHG013. Reactions and scheduled changes are skill tarol-reactions. Do not decompile or web-search, do not
  hand-write a load/commit handler or IHasReads, do not call Raise from a change, do not retry inside Execute.
---
# Tarol.Changes

## Rules

1. **One change = one input record + one behaviour call.** `public sealed partial record X(Id<T> Target-ish, …) :
   IChange<T>, IValidatable { public Result Execute(T aggregate, ChangeContext context) => aggregate.Behaviour(…, context.Now); }`.
   Members are the input (value objects, ids), never the aggregate instance. `Execute` must not load, save or `Raise`.
2. **The pipeline loads the target.** The generator implements `Target` from the record's only `Id<T>` member. With two,
   implement `Id<T> IChange<T>.Target => Second;` (TCHG001). Keep the record `partial` (TCHG002).
   **One per tenant** (the facility that is the tenant, its settings): mark the root `[PerTenant]`; a change needs no
   `Id<T>` member (the pipeline loads the caller's tenant's one), the creation's factory uses
   `new Settings(TenantAggregates.IdOf<Settings>(context.Operation))`, an entry needs no `{id}` (`PUT /settings/name`,
   a view at `GET /settings`). Do not write an `ILoads<T>` for it.
3. **Creation** = `ICreation<T>` whose `Execute(context)` returns the static factory's `Result<T>`. The factory makes the
   id (`Id<T>.New(now)`). No load. `ChangeOutcome.StreamId` is the new id.
4. **Special changes**: another key or load-or-create → `ILoads<T>` with `Load(IAggregateReader reader, ChangeContext
   context, CancellationToken ct)`; data for the decision (the actor's role, facts) → `ILoads<T, TData>`: `Load` returns
   `new Loaded<T, TData>(aggregate, data)`, `Execute(T aggregate, TData data, ChangeContext context)` (never a field).
   Load or start → the aggregate implements `IStartable<T>` (`static T Start(Id<T> id, ChangeContext c)`), the change
   `IOrStart<T>`: a missing target is started, the answer is 201 `{id, version}` (`ChangeOutcome.Created`), and its view before
   the first event shows the started state (ETag `"0"`). Ids from business keys: `Id<T>.Derive("scope", parts…)`. Another aggregate, read-only, for the decision → a `[Read] Id<X>` member and
   `context.Read<X>()` (missing → 422 `X.NotFound`; `[Read(Optional = true)]` + `context.TryRead(out X? x)`). Without an
   input member: `[Reads<X>]` on the change for a `[PerTenant]` X, `[Reads<X>(SameIdAs = nameof(Member))]` for the X under
   the same Guid (`Optional = true` allowed). Never implement `IHasReads` yourself (TCHG013). A `[Read]` of the target's
   type is fine next to the target member (a sibling). A rule that must *change* two aggregates is a saga, not a change.
5. **Time and actor come from the context**: `context.Now` (read once from the pipeline's `TimeProvider`),
   `context.Operation.Actor`; the tenant's day: `context.Today(zone)` / `LocalNow(zone)` (zone from `TimeZones.Find`). Never `DateTimeOffset.UtcNow` (TKRN001).
6. **Inputs are `IValidatable`**, so value rules run before any read and return every field as `Invalid` (TCHG005 warns).
7. **Run** with an explicit context: `await pipeline.Run(change, OperationContext.Start(actor, tenant, time), options, ct)`.
   `ChangeOptions(Key: new IdempotencyKey(tenant, scope, key))` for redeliverable work. `ExpectedVersion:` for If-Match.
8. **A version conflict without an expected version is retried**: a change without `ExpectedVersion` (no `If-Match`) that loses a
   race is loaded and decided again, up to `ChangePipeline.ConflictAttempts` (3) times. With `ExpectedVersion` the caller gets
   `Stream.VersionConflict` (`StoreErrors.IsVersionConflict`) at once: it saw a state that is gone.
   The permission (`IChangeAuthorization`, skill `tarol-security`) runs only for runs with `ChangeOptions.Principal`,
   after the load and before the expected version; jobs and reactions pass no principal.
9. **Reactions and scheduled changes** (`[OnEvent<T>]`, `[Scheduled]` factories, `GeneratedReactions.Set`,
   `GeneratedJobs.Set`, TCHG006–TCHG012): skill `tarol-reactions`.

## Example

```csharp
public sealed partial record CloseAuction(Id<Auction> Auction) : IChange<Auction>, IValidatable
{
    public Result Execute(Auction auction, ChangeContext context) => auction.Close(context.Now);
}

public sealed partial record CreateAuction(string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt) : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.Create(context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

public sealed partial record CreateAuctionOfArtwork([Read] Id<Artwork> Artwork, string Lot, Title Title, Amount Reserve, DateTimeOffset EndsAt)
    : ICreation<Auction>, IValidatable
{
    public Result<Auction> Execute(ChangeContext context)
        => Auction.CreateFor(context.Read<Artwork>(), context.Operation.Actor.Id, Lot, Title, Reserve, EndsAt, context.Now);
}

// test: the real pipeline on an in-memory store with a clock that stands still
var test = ChangeTest.At(start).Given(auction);
var then = await test.When(new CloseAuction(auction.Id), Actor.User("seller"));
then.Result.IsSuccess.ShouldBeTrue();
then.Events.ShouldBe([new Auction.Closed(start)]);
then.Recorded[0].Context.Actor.ShouldBe(Actor.User("seller"));
var twice = await test.When(new CloseAuction(auction.Id));
twice.Result.TryGetValue(out Violation? conflict).ShouldBeTrue();      // Error.Conflict("AuctionStatus.NotAllowed", …)
// like the app's store: sealed personal data (codec), the app's lookups, the reactions a change causes
var app = ChangeTest.At(start, new ChangeTestOptions { Codec = codec, Lookups = lookups, Reactions = GeneratedReactions.Set, RunReactions = true });
(await app.When(change)).Reactions;   app.History(id);   app.StoredStateMatchesReplay(id, json.Options);

// an internal run by hand (a scheduled job uses [Scheduled] or JobRun.Change of skill tarol-jobs, which key it)
var result = await pipeline.Run(new SettleAuction(id), OperationContext.Start(Actor.System("Jobs.AuctionSettlement"), tenant, time), cancellationToken: ct);
```

## Common mistakes

| Mistake | Result |
|---|---|
| `ChangeTest.At(now)` for an aggregate with `[PersonalData]` | nothing is sealed: a sealing error shows up in the host only; pass `ChangeTestOptions.Codec` |
| `new ChangeOptions(Principal: p)` in a `ChangeTest` without `Authorization` | `InvalidOperationException` (the pipeline would answer 403 `Auth.NotConfigured`) |
| `Store.Recorded.Where(r => r.StreamId == id)` for a `[PerTenant]` aggregate | events of every singleton of the tenant; use `test.History(id)` |
| `record Close(Auction Auction)` (the instance as input) | not serializable, not reloadable; take `Id<Auction>` |
| `[Read] string Customer`, or `[Read]` on the own target id | TCHG004 |
| `[Reads<X>]` of an X that is not `[PerTenant]`, or a hand-written `IHasReads` next to reads | TCHG013 (was CS8646 without a hint) |
| value-object members but no `IValidatable` | TCHG005: rules do not run before the load |
| loading or saving inside `Execute` | not possible: `Execute` gets no store; use `ILoads<T>` or `[Read]` |
| retrying `Stream.VersionConflict` around `Run` without an expected version | the pipeline did that already (3 attempts); with `If-Match` show the conflict instead |
| an id member in a creation input | the factory chooses ids; send an `IdempotencyKey` to make retries safe |
| a warning with an input flag (`bool ConfirmQuota`) and the data in `detail` | `if (!context.Confirmed("Absence.QuotaExceeded")) return Error.ConfirmationRequired("Absence.QuotaExceeded", args);` — the client resends with `Tarol-Confirm: Absence.QuotaExceeded` |

## Operations

Each run: span `change <Name>` (source `Tarol.Changes`), counter `tarol.changes.runs` and histogram `tarol.changes.duration`
tagged `tarol.change` and `tarol.outcome` (`committed`, `conflict`, `invalid`, `denied`, `not_found`, `error`); logs 7001–7003,
7006 (retry). Never the tenant as a tag; your own numbers go to your own meter.

## AOT

No reflection: `Run<T>` is generic, dispatch, reads and reaction/job tables are generated `switch`es. List the aggregates' state
member types in your `JsonSerializerContext`.
