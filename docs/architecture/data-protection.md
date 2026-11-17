# Data protection

Concept note for step 16 (see [migration/data-protection.md](../migration/data-protection.md)): **personal data in an append-only
event store stays deletable. Every personal field is sealed under its subject's key, and deleting the key erases the subject
everywhere it was written, without rewriting a single event.**
* A property marked `[PersonalData]` leaves the process as an envelope sealed with its subject's data key (AES-256-GCM). Everything
  else stays plain, queryable JSON.
* Keys come in two levels: a data key per (tenant, subject), wrapped by the tenant's key, wrapped by a master key outside the
  database. The key store is a port. Step 16 ships it on tables in the store's database; a KMS-backed version follows.
* Erasure turns the subject's key into a tombstone. Its fields read as `null`, aggregates still replay, and new personal data for
  that subject is refused. Tarol then makes projections, the page cache and personal lookups forget, and audits the act.
* Export collects every event that holds a subject's data, opened with that subject's key only.
* Retention deletes whole streams of a type N days after their last event.

Labels as in the [conventions](conventions.md): **[verified]** means reproduced here (probe and output in
[`probes/data-protection/`](../migration/probes/data-protection/)); **[verified by reading]** means read in the named code, not run;
**[unverified]** means reasoned or read elsewhere, not run here.

**Status: designed** (owner decisions DP-1 … DP-9, [migration/data-protection.md](../migration/data-protection.md) §9). To be built
in PRs 16A–16D (§11 there).

## 1. Where personal data lands today, and what forgets it

| Place | What it holds | Forgets today | After step 16 |
|---|---|---|---|
| `events.payload` | the event's JSON | **never** (no `UPDATE`/`DELETE` on `events`) | `[PersonalData]` fields sealed; the key's tombstone makes them `null` (DP-2, DP-3) |
| `streams.state` | the aggregate's state | the next commit | sealed like events |
| `lookups.key` | e.g. an email, as the app's key function returns it | when the key changes | personal lookups store a blind index (HMAC under the tenant's lookup key); erasure deletes the subject's (DP-5) |
| `rm_*` rows, `SqlProjection` tables | copies of fields | rebuild only (`Apply` cannot delete) | erasure re-folds the subject's streams; retention deletes rows (DP-5, DP-7) |
| `Tarol.Caching` pages | pages with copies | the next checkpoint | erasure evicts the tenant's pages in every process (DP-5) |
| `bus.messages`, NATS, RabbitMQ | contract JSON | 7 days (bus); **never** (NATS: no `MaxAge`) | contracts carry ids, not personal data; NATS gets a `MaxAge` default (DP-5) |
| `outbox_dead`, `jobs_dead`, `messages_dead` | `last_error`, `data` (messages) | `tarol.purge-dead` (optional) | unchanged; the guide sets `DeadRetention` when contracts or errors may carry personal data |
| `events.actor_id`, `on_behalf_of`, `idempotency_keys.scope` | the OIDC `sub` (a pseudonym) | keys after 7 days | kept: the accountability record (DP-9) |
| `operations_audit.actor` | the operator's principal name | never | optional `AuditRetention` (`tarol.purge-audit`, DP-9) |
| logs, traces | free-text reasons, actor ids in 1001/1002 | the log store's retention | Tarol logs never carry field values; the guide says so for the app's logs |

## 2. Marking (DP-2)

```csharp
public sealed record Registered([property: PersonalData] string? Name, [property: PersonalData] string? Email, string Country) : Event;
public sealed record Placed(Guid Customer, [property: PersonalData(Subject = nameof(Customer))] string? ShipTo, decimal Total) : Event;

public partial class Customer : AggregateRoot<Customer>
{
    [PersonalData] public string? Name { get; private set; }   // the state is sealed too
}
```

* The **subject** of a field is the stream id by default (the customer is its own subject). `Subject = nameof(…)` names a `Guid`
  or `Id<T>` member of the same record (an order holds its customer's address).
* The Primitives generator emits, per event and per state, the members' JSON names and a subject accessor. It emits no reflection
  and no `[JsonConverter]`. Diagnostics: a `[PersonalData]` member must be nullable, because an erased value reads as `null`; a
  `Subject` must name a `Guid`/`Id<T>` member; the attribute belongs on events, aggregate state, entities and projection rows only.
* Any value type can be marked (string, value object, record, list): the whole JSON value of the member is sealed.

## 3. The envelope and the codec

A sealed field is a JSON string `"tdp1." + base64url(subject 16 | key version 4 | nonce 12 | tag 16 | ciphertext)`. The plaintext
is the member's JSON value, and the associated data is (tenant, subject), so an envelope relabelled to another subject does not
open **[verified]**. A field grows from its JSON to about 77 bytes more: 67 → 221 bytes for an event with two short fields, and the
events table grows by 40 % in the probe (6.1 → 8.5 MB including the GIN index) **[verified]**.

The `EventCodec` seals **after** the serializer and opens **before** the upcasters and the serializer. It makes one pass with
`Utf8JsonReader`/`Utf8JsonWriter` over raw value slices, the same code for every type **[verified]**:

| per event, 2 personal fields of 3 (JIT; Native AOT within 15 %) | encode | decode | decode, key erased |
|---|---|---|---|
| today's codec | 0.5 µs | 0.47 µs | – |
| **sealed (DP-2)** | 4.6 µs | 2.9 µs | 0.7 µs |

Sealing costs ~2 µs per field (AES-256-GCM 1 µs plus a random nonce 0.8 µs), opening ~1 µs: about 0.2 % of a 2.2 ms commit. An
upcaster sees plaintext: an old event whose field was renamed opens and upcasts like any other **[verified]**. `AesGcm` instances
are **not safe for concurrent use**: one instance shared by 4 threads failed 133,850 of 200,000 round trips **[verified]**, so a
cached key is used under a lock or one instance per call.

## 4. Keys (DP-1, DP-4)

```
master key (IKeyEncryptionKey: configuration now, a KMS such as Infisical later)
  └─ tenant key        tenant_keys(tenant, wrapped, created_at, erased_at)
       └─ data key     subject_keys(tenant, subject, version, wrapped, created_at, erased_at)
```

* **Port** `IKeyStore` (in `Tarol.Persistence`). Step 16 implements it on tables in the store's database (schema step version 3,
  additive, O-4). A data key is created in the commit's own transaction the first time a subject's field is sealed. A KMS-backed
  store is its own package after step 17 (DP-1, DP-8).
* **Cache first.** Unwrapped keys are cached per process (bounded, with an idle time). A load reads the row, then the keys it
  lacks: a cached key costs nothing measurable (state 0.25 vs 0.31 ms, replay 0.40 vs 0.39 ms plain), a miss adds a round trip and
  an unwrap (+0.6 ms). Always fetching the keys in the same batch costs +0.2–0.9 ms **[verified]**.
* **Erasure** writes a tombstone (no key, `erased_at`) and a `NOTIFY` in the same statement; every process evicts the key, in
  7–17 ms in the probe **[verified]**. Offboarding a tenant blanks its tenant key: one row erases every subject of the tenant
  **[verified]**.
* **Backups** hold the wrapped keys until they expire. An erasure is complete when the backups older than it are gone, and the
  guide says so. A key store in its own database with shorter backups is possible behind the port.
* A commit that creates a subject costs +1.0 ms p50 in the probe (two more statements); 16B folds the key insert into the commit's
  batch. A commit of a known subject costs +0.03–0.2 ms **[verified]**.

## 5. Erasure (DP-3, DP-5, DP-9)

`IDataSubjects.Erase(tenant, subject, actor)` runs, in order:
1. the tombstone and the `NOTIFY` (key caches and page caches in every process listen);
2. the affected streams, found through `events.subjects` (a GIN index: 0.56 ms among 1,000,000 events, 87 ms without; `LIKE` on
   the payload 248 ms) **[verified]**;
3. every projection with `[PersonalData]` columns **re-folds** those streams and replaces their rows: 2.6 ms, against a rebuild of
   1.7 s per 100,000 events, per projection and per erasure **[verified]**;
4. the blind-indexed personal lookups of the subject's own streams are deleted;
5. an `operations_audit` row (`erase`, `subject:<id>`) and a log line.

Afterwards the aggregate still replays, its sealed fields read as `null`, and its other fields are intact. Another aggregate keeps
its own data while the subject's field in it reads as `null`. New personal data for the subject is refused with
`DataProtection.SubjectErased` (all **[verified]**). Actor ids are pseudonyms and stay (DP-9).

## 6. Export (DP-6)

`IDataSubjects.Export(tenant, subject)` returns one JSON document: every event that holds the subject's data, in every stream type,
opened with **that subject's key only** (other subjects' fields read as `null`), plus the current state of the subject's own
streams **[verified]** for events. `MapTarolAdmin` gains `GET {prefix}/subjects/{id}/export` and `POST {prefix}/subjects/{id}/erase`
under its required policy, audited.

## 7. Retention (DP-7)

`[Retention(Days = 365)]` on an aggregate, overridden by `Tarol:Retention:<streamType>:Days`. The system job `tarol.retention`
(hourly, a `MaintenanceTask`) deletes whole streams (events, state, lookups) whose last event is older, 500 streams per statement,
skipping streams with pending reactions; projections delete the rows of deleted streams; each batch is audited. With 4 writers
committing, deleting 50,125 streams took 1.6 s, and the writers' p99 rose from 5.9 to 8.4 ms **[verified]**.

## 8. Out of scope

* **Read audit** (who read what): not built (DP-8).
* **Encrypting contracts on the bus**: receivers in other services have no keys; contracts carry ids, and personal data is
  fetched from the owning service.
* **Nested personal members inside value objects** are sealed as a whole value; paths into entity collections in the state are
  16A's work and were not probed **[unverified]**.
