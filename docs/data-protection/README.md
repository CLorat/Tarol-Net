# Data protection

Personal data in an append-only event store stays deletable. A member marked `[PersonalData]` is stored sealed under its
subject's key. Erasing the subject deletes the key, and Tarol makes everything it wrote forget the data: events, stored
state, projections, page caches and lookups. It does so without rewriting events. This guide covers what to mark, how keys
and the master key work, erasure, export and retention, and what Tarol cannot reach. Concept and decisions:
[architecture/data-protection.md](../architecture/data-protection.md) and [migration/data-protection.md](../migration/data-protection.md).

## Wire it

```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Customer>().Add<Order>())
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions
    {
        Lookups = new StoreLookups().DefinePersonal<Customer>("email", c => c.Email?.ToLowerInvariant()),
    })
    .UseNpgsqlKeyStore()                       // tarol.tenant_keys and tarol.subject_keys (schema version 3)
    .UseNpgsqlReadModels().AddProjections(p => p.Add<CustomerRow>())
    .AddCaching()
    .AddDataProtection(o => o.Retention["order"] = 3650)
    .AddJobs(j => { })                         // runs tarol.retention (hourly, once per cluster)
    .AddOperations();

app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");   // + GET …/subjects/{id}/export, POST …/subjects/{id}/erase
```

The master key comes from configuration: `Tarol:DataProtection:MasterKey`, the base64 of 32 random bytes, for example from
`openssl rand -base64 32`. Keep it in a secret store (Kubernetes secret, Vault, Azure Key Vault, …), never in
`appsettings.json` in the repository, and use a separate key for each environment.

## What to mark

* **Every field that identifies or describes a person**: name, email, phone, address, birth date, free text a person
  wrote, an IP address. Mark it on the event **and** on the state member (`[property: PersonalData]` on a positional
  record). An unmarked state member stores the value in plain JSON.
* **Members are nullable** (TPRM030). After an erasure they read as `null`, and the aggregate still loads and replays.
* **The subject** is whose data it is:
  * by default the stream id (a customer is its own subject);
  * another aggregate holding a person's data names the person: `[PersonalData(Subject = nameof(Placed.Customer))]`,
    a `Guid` or `Id<T>` member of the same record (TPRM031). Write `nameof(Record.Member)`: a bare parameter name does not
    resolve inside its own attribute.
  * Never use the order's own id as the subject of the customer's address: erasing the customer would miss it.
* **A value object is sealed as a whole**: mark the member that holds it, not the value object's members (TPRM032).
* **Do not mark what you need to query**: a sealed field cannot be filtered, sorted or joined in SQL. Projections can
  hold a copy (it is re-folded on erasure), but a personal column cannot be a `[Sort]` column.
* **A lookup over a personal value** (find a customer by email) uses `DefinePersonal`. The table keeps an HMAC of the
  value, never the value, and `Find` hashes what you pass, so normalize it the same way on both sides.
* **Ids of people are not personal data in Tarol's sense.** Actor ids are the identity provider's pseudonymous `sub`:
  they stay as the accountability record. Never put a name or an email into an actor id.

Cost: about 2 µs to seal and 1 µs to open per field, roughly 0.2 % of a commit. A field grows by about 77 bytes. A state
with 100 sealed entities costs about 0.2 ms to seal per commit, so mark only what is personal.

## Keys

```
master key   IKeyEncryptionKey: Tarol:DataProtection:MasterKey (a KMS such as Infisical later)
  └─ tenant key    tarol.tenant_keys   (one row per tenant; blanking it erases the whole tenant)
       └─ data key   tarol.subject_keys (one row per tenant and subject; a tombstone after erasure)
```

* Unwrapped keys are cached per process (`NpgsqlKeyStoreOptions.MaxCachedKeys`, `IdleTime`). A load with a cached key costs
  nothing measurable, and a cold key costs one round trip.
* **Rotation** of the master key:
  1. set the new key as `MasterKey` with a new `MasterKeyId`, and the old one under `Tarol:DataProtection:PreviousKeys:<id>`;
  2. deploy, then call `IKeyStore.Rewrap()`, which re-wraps the tenant keys (no event and no data key is touched);
  3. remove the previous key.
* **Backups** hold the wrapped keys. An erasure is complete once the backups older than it have expired. Say so in your
  records of processing, and keep the backup retention short, or keep the key tables in a database of their own with
  shorter backups.

## Erasure (GDPR art. 17)

`IDataSubjects.EraseSubject(tenant, subject, actor)`, or `POST /tarol/admin/subjects/{id}/erase?tenant=<id>`, does:
1. writes the tombstone and a `NOTIFY`; every process evicts the key, and new personal data for the subject is refused
   (`DataProtection.SubjectErased`, 409) — a returning person is a new subject;
2. finds the subject's streams through `events.subjects`;
3. runs the participants:
   * every projection re-folds those streams;
   * every process drops the tenant's cached pages;
   * personal lookups are deleted;
   * your own participants run;
4. writes an `operations_audit` row (`erase`, `subject:<id>`) and log 16001.

Erasing a whole tenant (offboarding) is `EraseTenant`, a single row. If a participant fails, the erasure throws
`ErasureIncompleteException` (an `IIncompleteErasure`); the key stays erased, so run the erasure again. The admin entry
answers it as 409 `DataProtection.ErasureIncomplete` with the failed participants in `failed`.

**Your own tables** (an `ISqlProjection`, a report table, a search index) need your own `ErasureParticipant`, registered as
a singleton. Its `Forget` gets the tenant, the subject and its streams. `Evict` runs in every process when any process
erases, for in-memory copies.

## Export (GDPR art. 15 and 20)

`IDataSubjects.Export(tenant, subject, actor)`, or `GET /tarol/admin/subjects/{id}/export?tenant=<id>`, returns one JSON
document. It holds every event with the subject's data (in any stream type, the stream, version and time) and the current
state of the subject's own streams. Everything is opened with **that subject's key only**, so another person's fields in
the same events read as `null`. The export is audited.

## Retention

* `[Retention(Days = 3650)]` on an aggregate, or `Tarol:Retention:<streamType>:Days` (configuration wins; `0` turns it off).
* The system job `tarol.retention` (hourly, with a job host) deletes whole streams that many days after their last event:
  events, state and lookups, 500 streams per statement. Streams with pending reactions are skipped until they are done.
  Projections drop the rows of deleted streams, and each batch is audited.
* The bus: the PostgreSQL bus purges after 7 days, NATS streams keep messages 7 days (`NatsTransportOptions.MaxAge`), and
  dead letters go when `OperationsOptions.DeadRetention` is set. Set `DeadRetention` when error texts or messages may hold
  personal data.
* The audit: `OperationsOptions.AuditRetention` lets `tarol.purge-audit` delete old `operations_audit` rows.

## What Tarol cannot reach

* **Plaintext written before a member was marked.** Run `store.SealHistory<T>(500, ct)` once per aggregate, after every
  pod runs the new version. It is the one rewrite of `events` Tarol does: about 4.4 minutes per million events, with the
  writers' p99 rising from 10 to 13 ms in the measurement.
* **A rolling deploy into sealing**: a store with keys raises the schema's `compatible_from` to 3, so pods from before
  step 16 refuse to start instead of reading envelopes as values. Roll out the new version everywhere, then turn keys on.
* **Backups** older than the erasure, until they expire.
* **Logs.** Tarol's logs carry ids and counts, never field values. Yours must do the same: log the change and the stream
  id, never the payload or a personal value.
* **Other services.** Contracts are the public language of a service and stay in the bus and in the receivers' stores.
  Publish ids, not personal data; a receiver that needs a name asks the owning service.
* **Files, emails and caches outside Tarol**: your participants, or your own processes.

## Logs and metrics

* Logs:
  * 8006–8008 (`NpgsqlKeyStore`, Debug): keys created, a key erased, keys evicted;
  * 16001–16008 (`Tarol.DataProtection`): erased, tenant erased, exported, retention deleted, a participant failed
    (Error), evicted, erased again, retention skipped.
* Meter `Tarol.DataProtection`: `tarol.subjects.erased`, `tarol.subjects.exported`, `tarol.retention.streams`. There is no
  tenant or subject tag.
