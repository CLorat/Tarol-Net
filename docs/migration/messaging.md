# Messaging: evaluation (step 11 design)

Evaluation of [messaging.md](../architecture/messaging.md): inventory of the old Messaging code, candidates for the
transport, consumers, publications, inbox and outbox rows, measurements against PostgreSQL and the brokers, the owner's
answers, and what the step 11 PRs must do. **Design only**: this PR changes no package code. Probes and output:
[`probes/messaging/`](probes/messaging/). Labels as in the [conventions](../architecture/conventions.md). Source of the
old code: `CLorat/Tarol@6a62f81`.

**Work in progress.** Sections are filled as the session goes.

## Owner answers (2026-11-15)

| # | Question | Answer |
|---|---|---|
| M-1 | Scope: which transports does step 11 build? | **A transport port, an in-database transport over PostgreSQL, and one broker** chosen after probing (recommended). |
| M-2 | Which brokers to probe? | **NATS JetStream and RabbitMQ**, both; choose in a later round from the measurements (recommended). |
| M-3 | The old SignalR Gateway (the "user backend" push)? | **UI push as its own PR in step 11**, after the system-to-system PRs, rebuilt on top of them with an authenticated hub and tenant-scoped topics (recommended). Owner, with the session prompt: "Look at Tarol/Main there IS messaging for User Backend. We should utilize this as base as it is already a bit structured and flexible; this should be one option evaluated." The old layering (Protocol / Transport / Backplane / Gateway) is evaluated as a transport candidate (§2). |
| M-4 | Which broker gets its own package? (NATS: end to end p50/p99 7.7/22 ms, 702 consumed/s, +5.0 MB native, delayed retry and dedup native; RabbitMQ: 8.9/13 ms, 491/s, +1.3 MB, retry queue + TTL, `user_id` validated) | **Both, as two packages**: `Tarol.Messaging.Nats` and `Tarol.Messaging.RabbitMq` (against the recommendation, NATS only). |
| M-5 | The envelope on the wire? | **CloudEvents 1.0, binary content mode**: attributes as headers, the body is the contract JSON (recommended). |
| M-8 | Inbox: the key table only, or a table of received messages too? | **The idempotency-key table only** (C-8, P-11), key (tenant, `consumer:<name>`, message id) (recommended). |
| M-9 | Where do buried messages go? | **The consumer's own table** (`messages_dead`, DL-1), re-driven in process by id, the same on every transport (recommended). |
| M-6 | Which actor does a consumed message run as? (the Kernel allows `OnBehalfOf` only on a System actor) | **`Actor.System("Consumers.<name>", onBehalfOf: the envelope's actor id)`**, like a reaction; later hops keep it (against the recommendation `Service(source)`). Recorded, never used for authorization (R-6). |
| M-7 | How is a publication declared? | **`[Publishes<TEvent>(contract, version)]` on a static mapping method** (PB-A), the outward anti-corruption layer; generated table, names and serialization (recommended). |
| M-10 | Where are the publication rows? | **The same outbox**, rows named `publish:<contract>@<version>` (and `effect:<name>` for S-2 effects); the reaction worker's claim, head order, backoff, lease, dead letters and links are reused (O-A, recommended). |
| M-11 | Packages and PRs? | **The recommended split, five PRs**: 11A `Tarol.Messaging` (envelope, port, consumers, in-memory transport, dead letters, `MessageTest`, generator), 11B publications and effects, 11C the PostgreSQL bus, 11D NATS + RabbitMQ (M-4) with the Aspire sample, 11E the SignalR UI push (M-3). The option's label said "4 packages" but lists five; the place of the PostgreSQL bus is confirmed in M-14. |
| M-12 | Key scope of a consumer (the plan wrote `consumer:<contract>`; the probe shows the second consumer of a contract replaying the first one's outcome) | **`consumer:<consumer name>`** (the change's type name, pinned with `Name` before a rename, like S-14) (recommended). |
| M-13 | What may a consumer take from an envelope? | **The contract's prefix is the source**, enforced by the transport's permissions; the consumer buries a disagreeing `ce-source`, an unknown source, and a tenant the source is not allowed for (allow-list per source, default none, `AnyTenant` opt-in); correlation taken, causation = message id (recommended). |
| M-14 | Where does the PostgreSQL bus ship? | **`Tarol.Messaging.Npgsql`**, a package of its own; five new packages in step 11 (recommended). |
| M-15 | The runnable sample? | **Extend `samples/Sagas` in 11D** with a Billing project and NATS (RabbitMQ as an AppHost switch) (recommended). |
