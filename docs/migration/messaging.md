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
