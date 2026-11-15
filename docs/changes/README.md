# Changes

Package guide: [Tarol.Changes](../../src/Changes/Tarol.Changes/README.md).
Agent skill: [tarol-changes](../../src/Changes/Tarol.Changes/skills/tarol-changes/SKILL.md).
Concept: [docs/architecture/changes.md](../architecture/changes.md). Design, measurements, decisions C-1 … C-16 and the
implementation notes: [docs/migration/changes.md](../migration/changes.md). The store it runs on:
[docs/persistence](../persistence/README.md).

## Design in six points

1. **A change is the entry of an actor into the domain.** You write the input once, as a record that names its target
   and calls one behaviour method. Who calls it (a job, a saga, a test; later REST, reactions, webhooks, consumers) only
   changes how the input and the context are built and how the outcome is reported.
2. **One pipeline, in library code.** Validate before any read, load, check the permission, decide with the time read
   once, run the invariants, append with the expected version and the idempotency key. It is generic over the aggregate,
   needs no reflection and costs about 145 ns and 56 B over hand-written code.
3. **The pipeline loads; special changes say how.** The default loads the target named by the record's `Id<T>`.
   `ILoads<T>` covers another key or load-or-create, and `[Read]` brings other aggregates read-only. A change that must
   write two aggregates is a saga.
4. **Failure discards, conflicts are answers.** A failed guard, invariant or lifecycle step, or an exception, drops the
   loaded instance. A version conflict comes back as 409; the pipeline never retries, the caller decides.
5. **Generated where a list of your types is needed:** the target, the read-only loads and a typed `Dispatch(object)`.
   Never the pipeline itself, never input records (other generators could not see them).
6. **Tests run the real thing.** `ChangeTest` runs your change through the pipeline on the in-memory store, with a
   clock that stands still.
7. **A reaction to an own event is a change with a factory.** `[OnEvent<Bid.Won>] public static MarkPayoutDue From(Bid.Won e,
   RecordedEvent recorded)` maps the committed event to the input; the generated `GeneratedReactions.Set` gives the store
   its outbox table and the worker its runner, which runs the change as the system on behalf of the user, caused by the
   event, keyed (reaction, event id). Sagas are aggregates whose steps are such reactions
   ([sagas.md](../architecture/sagas.md)).
8. **An HTTP entry is an attribute on the change** (step 10, [rest.md](../architecture/rest.md);
   package guide [Tarol.Changes.AspNetCore](../../src/Changes/Tarol.Changes.AspNetCore/README.md), skill
   [tarol-endpoints](../../src/Changes/Tarol.Changes.AspNetCore/skills/tarol-endpoints/SKILL.md)):
   - `[Endpoint("POST /auctions/{auction}/bids", Policy = …)]` on the record, and `app.MapChanges()`.
   - The generated delegate binds the route and the body (every field at once) and builds the context from the
     principal; `Idempotency-Key` and `If-Match` map onto the pipeline.
   - The pipeline authorizes the change (skill `tarol-security`); its outcome maps to 201/204 with `ETag`, or to problem
     details.
   - GET by id is a view (`IView<Auction, TSelf>`); webhooks are `[Webhook]` factories.

## HTTP in one picture

```text
request → authentication (the app's) → endpoint policy → route + body → context (actor, tenant, key, If-Match)
        → ChangePipeline: validate → key seen → [Read] → load (404) → permission (401/403) → If-Match (409) → Execute → append
        → 201 / 204 + ETag, or 400 / 403 / 404 / 409 / 422 problem details
```

Registration, in one place for every package (`AddTarol` → `TarolBuilder`):

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>())
    .WithReactions(GeneratedReactions.Set)
    .UseNpgsqlStore(builder.Configuration.GetConnectionString("tarol")!)
    .AddSecurity(roles => roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>())
    .AddEndpoints()
    .AddZitadel(z => (z.Authority, z.ProjectId) = (authority, project));   // optional: Tarol.Security.Zitadel
app.MapChanges();
```
