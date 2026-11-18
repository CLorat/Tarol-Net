# Data protection: evaluation (step 16 design)

This note evaluates [data-protection.md](../architecture/data-protection.md). It covers:
* the inventory of the old code and of where personal data lands in Tarol-Net today;
* the envelope, the codec candidates and their cost, JIT and Native AOT;
* where keys live, what a load and a commit cost with and without them;
* erasure: what an erased subject reads like, what projections, caches, lookups, the bus and the audit must forget;
* export of a subject's data, and retention per stream type;
* the owner's answers, and what PRs 16A–16D must do.

**Design only**: this PR changes no package code. Probe and output: [`probes/data-protection/`](probes/data-protection/),
[`output.txt`](probes/data-protection/output.txt), the inventory [`inventory.md`](probes/data-protection/inventory.md). Labels as in
the [conventions](../architecture/conventions.md). Source of the old code: `CLorat/Tarol@6a62f81`.

## 0. Summary

* **The old code has nothing to migrate** **[verified by reading]**. It has no encryption, no key store, no personal-data
  attribute, no erasure, no retention and no export, and its events carry no actor or tenant. Step 16 is new design.
* **Decided** (DP-1 … DP-9):
  * **Keys** (DP-1, DP-4): a port `IKeyStore` with two levels: a data key per (tenant, subject), wrapped by the tenant's key,
    wrapped by a master key behind `IKeyEncryptionKey`. Step 16 ships the store on tables in the store's database (schema step
    version 3). A KMS-backed version (e.g. Infisical) follows as its own package. Offboarding a tenant is one row.
  * **Marking** (DP-2): `[PersonalData]` on event, state, entity and projection-row members, subject = the stream id or
    `Subject = nameof(…)`. The generator emits JSON names and subject accessors. The codec seals after the serializer and opens
    before the upcasters, for any value type.
  * **Erasure** (DP-3, DP-5): a tombstone plus a `NOTIFY`. Sealed fields read as `null` (members must be nullable), aggregates
    still replay, and new personal data for the subject is refused. Projections re-fold the affected streams, page caches are
    evicted, personal lookups are deleted, and the act is audited. The bus is bounded by retention.
  * **Export** (DP-6): `IDataSubjects.Export` covers every event holding the subject's data, opened with that subject's key only,
    plus the state of its own streams. Admin endpoints `export` and `erase` sit under `MapTarolAdmin`'s policy.
  * **Retention** (DP-7): `[Retention(Days)]` per aggregate, overridden by configuration. The system job `tarol.retention`
    deletes whole streams in batches of 500.
  * **Actors** (DP-9): pseudonymous actor ids stay, and `operations_audit` gets an optional `AuditRetention`.
  * **Split** (DP-8): 16A attribute, generator, codec and port; 16B Npgsql; 16C the new `Tarol.DataProtection`; 16D guide and
    sample. The KMS version comes after step 17. There is no read audit.
* **Measured** (4 cores, PostgreSQL 17 in Docker on the same host, SDK 11.0.100, OpenSSL 3.0.13, JIT Release and Native AOT):
  * **Primitives:** sealing a field (AES-256-GCM, random nonce, base64url) takes 1.8–2.2 µs, opening it 1.0–1.3 µs. Unwrapping a
    key takes 1.0–1.2 µs, `new AesGcm` 1.3 µs, and a blind index (HMAC-SHA256) 1.5 µs.
  * **Codec** (one event, 2 personal fields of 3): today 0.5 / 0.47 µs. Sealed after the serializer: 4.6 / 2.9 µs, or 0.7 µs to
    decode once the key is erased. Through `JsonNode` 5.0 / 4.3 µs; a converter in the serializer 3.7 / 3.2 µs; the whole payload
    2.1 / 1.3 µs (but it loses the whole event). Native AOT is within 15 %, with 0 IL warnings.
  * **Loads** (2,000 customers × 10 events):
    * today's store 0.32 ms;
    * sealed state with a cached key 0.25 ms (plain 0.31); sealed replay with a cached key 0.40 ms (plain 0.39);
    * a cold key +0.6 ms (a second round trip and an unwrap);
    * keys always fetched in the same batch 0.50–1.27 ms.
  * **Commits** (4 writers): a new subject 3.2 vs 2.2 ms p50 (two more statements); a known subject 2.1–2.3 vs 2.0 ms. The GIN
    index on `subjects` adds nothing measurable.
  * **Erasure:**
    * the `NOTIFY` evicts another process's key in 7–17 ms;
    * finding a subject's events among 1,000,000 takes 0.56 ms with a GIN index (87 ms by sequential scan, 248 ms with `LIKE` on
      the payload);
    * re-folding the subject's streams takes 2.6 ms, while rebuilding a projection takes 1.7 s per 100,000 events.
  * **Retention:** 50,125 streams were deleted in 1.6 s in batches of 500 while 4 writers committed; the writers' p99 went from
    5.9 to 8.4 ms.
  * **Size:** an event grows from 67 to 221 bytes with two sealed fields; the events table grows 6.1 → 8.5 MB (+40 %, including
    the GIN index).
* **Found** (§5):
  * `AesGcm` is not safe for concurrent use;
  * batching the key query with the rows is slower than a cache;
  * the page cache keys on the checkpoint, which a re-fold does not move;
  * the read-model upsert refuses a rewrite at the same version, and `Apply` cannot delete;
  * lookups and the page cache's keys hold plaintext values;
  * NATS streams have no `MaxAge`;
  * existing plaintext events and a rolling deploy into sealing need a rule;
  * backups hold the wrapped keys.

## 1. Inventory **[verified by reading]**

Condensed; every file:line is in [`inventory.md`](probes/data-protection/inventory.md).

| Where | What | Verdict |
|---|---|---|
| `CLorat/Tarol` (whole tree) | SHA1/SHA256 for deterministic ids; an unused `IUserContext`; no-op `IRepositoryInterceptor` hooks; `Bidder.Name`/`Expert.Name` in ArtFlow, unmarked | nothing to migrate; ArtFlow (step 18) holds personal data |
| `events.payload`, `streams.state` | the event and the state as `jsonb` | no `UPDATE`/`DELETE` on `events` anywhere: personal data is permanent today |
| `lookups.key` | the app's key function's value (e.g. an email) | plaintext; released only when the key changes |
| `events.actor_id`, `on_behalf_of`, `idempotency_keys.scope` | the OIDC `sub` | pseudonyms; keys purged after 7 days |
| `operations_audit` | `actor` (principal name), `detail` (tenant, stream, event ids) | never deleted |
| `outbox_dead`, `jobs_dead`, `messages_dead` | `last_error`; `data`/`headers` of messages | `tarol.purge-dead` optional (O-5) |
| `bus.messages`; NATS; RabbitMQ | contract JSON, `ce-actor` | bus 7 days; NATS **no `MaxAge`**; RabbitMQ until acked |
| `rm_*` (`doc jsonb` + columns), `SqlProjection` | copies | `IProjection.Apply` cannot delete; the upsert guard `t.version < excluded.version` refuses a same-version rewrite |
| `Tarol.Caching` | pages; `PageKey.Query` holds filter values | `Clear()` only; valid until the checkpoint moves |
| logs 1001/1002, 7001, 9001/9002, 11001/11002, 11201, 11401/11402, 14001/14002, 15001–15004 | actor ids, free-text reasons, operator names | no field values in Tarol's logs; reasons are exception texts |
| `EventCodec` | synchronous `Encode<T>`/`Decode`, upcasters on `JsonObject` | the hook point (DP-2) |
| aggregate generator (`StateDiscovery.cs`) | `[Transient]`, `[StateName]`, `[JsonPropertyName]` per member | the pattern for `[PersonalData]` |
| cryptography in `src` | `SHA256`, `IncrementalHash`, `HMACSHA256` | **no encryption anywhere** |

Free names **[verified]**: decision prefix `DP-`; log ids 16001+ (and 8006+ in `Tarol.Persistence.Npgsql`); diagnostics TPRM030+
and TQRY005+.

## 2. Candidates

### 2.1 Keys (DP-1)

| | **port; tables in the store now, KMS later** (DP-1) | separate key database | external KMS per subject |
|---|---|---|---|
| key creation | in the commit's transaction (atomic) | before the commit (orphans harmless) | a KMS call per new subject |
| load, cached key **[verified]** | ≈ plain (0.25 vs 0.31 ms state) | the same | the same |
| load, cold key **[verified]** | +0.6 ms (round trip + unwrap) | +1 round trip to the other database | a KMS round trip |
| backups | hold wrapped keys until they expire | separable, shorter retention | none in the database |
| erasure reach | `NOTIFY` 7–17 ms **[verified]** | the key database's `NOTIFY` | the KMS's own cache rules |

### 2.2 Marking and the codec (DP-2), one event with 2 personal fields of 3 **[verified]** (JIT; Native AOT in §4.2)

| | **A: `[PersonalData]` + transform after the serializer** (DP-2) | B: through `JsonNode` | C: whole payload | D: converter in the serializer |
|---|---|---|---|---|
| encode / decode | 4.6 / 2.9 µs | 5.0 / 4.3 µs | 2.1 / 1.3 µs | 3.7 / 3.2 µs |
| decode with the key erased | 0.7 µs, fields `null` | 1.7 µs | the **whole event** is gone (country too) | 0.4 µs |
| value types | any (raw JSON slices) | any | any | one converter per type, generated |
| key hand-over | an argument of the codec call | the same | the same | a thread-static scope (ambient state) |
| upcasters | see plaintext **[verified]** | see plaintext | must open first | see envelopes |
| subjects per event | many | many | one | many |
| queryable payload | yes, except sealed fields | yes | no | yes, except sealed fields |

### 2.3 What an erased subject reads like (DP-3)

| | **null + tombstone** (DP-3) | null, no tombstone | null + delete own streams |
|---|---|---|---|
| sealed fields | `null` (members nullable) | `null` | `null` elsewhere; own streams gone |
| new data for the subject | refused (`DataProtection.SubjectErased`) **[verified]** | silently gets a new key | refused |
| append-only | kept | kept | broken for the subject's streams |

### 2.4 Tenants and subjects (DP-4)

| | **two-level keys** (DP-4) | one level | global subjects |
|---|---|---|---|
| tenant offboarding | one row **[verified]** | N subjects, N notifications | per person |
| isolation | tenant first (P-5) | tenant first | crosses tenants |

### 2.5 Forgetting beyond the store (DP-5) **[verified]** (§4.5, §4.6)

| | **erasure orchestrates** (DP-5) | read models keep envelopes | key only + guidance |
|---|---|---|---|
| projections | re-fold the subject's streams: 2.6 ms | nothing to do; ~1 µs per field per row read (0.1 ms per 50-row page); no sort or filter on personal columns | the app rebuilds: 1.7 s per 100,000 events |
| page cache | evicted per tenant, in every process | still holds opened pages: evict | the app's |
| lookups | personal ones are blind indexes, deleted | the same | plaintext unless the app hashes |
| bus, dead letters | bounded by retention; NATS `MaxAge` default | the same | the app's |

### 2.6 Export (DP-6)

| | **library + admin endpoint** (DP-6) | library only | also read-model rows |
|---|---|---|---|
| events | every stream type via `events.subjects`, the subject's key only **[verified]** | the same | the same |
| HTTP | `GET {prefix}/subjects/{id}/export`, policy required, audited | each app | as recommended |
| projections | not included (derived data) | – | needs a subject column per row |

### 2.7 Retention (DP-7) **[verified]** (§4.7)

| | **delete whole streams** (DP-7) | crypto-shred per stream | none in Tarol |
|---|---|---|---|
| storage | reclaimed | stays | the app's |
| non-personal data | deleted | stays | – |
| writers meanwhile | p99 5.9 → 8.4 ms (batches of 500) | unaffected | – |
| projections | rows of deleted streams deleted | fields `null` | the app's |

### 2.8 Scope and split (DP-8)

| | **16A–16D, KMS after** (DP-8) | KMS as 16E | also a read audit |
|---|---|---|---|
| step 16 | attribute, codec, port; Npgsql; `Tarol.DataProtection`; guide and sample | + a KMS-backed store with an Infisical container in the tests | + one row per load and export (high volume, itself personal data) |

### 2.9 Actor ids (DP-9)

| | **keep pseudonyms** (DP-9) | seal actor ids | replace on erasure |
|---|---|---|---|
| accountability | kept | lost on erasure | lost |
| cost | none | a key lookup per distinct actor on every feed read | an `UPDATE` scan of `events` by actor (no index) |

## 3. The scenarios as probed **[verified]** ([`output.txt`](probes/data-protection/output.txt) §1, §4: JIT and Native AOT, 17 of 17 on both)

The probe's `Vault` is the would-be store: today's `EventCodec` writes and reads the events, and the transform runs between the
codec and SQL.
1. **Append** seals `name`, `email` and `street`. `country` and `city` stay plain JSON, and every row records its `subjects`.
2. **Load in another process** opens every field; an order's `shipTo`, sealed for the customer, opens with the customer's key.
3. **An envelope relabelled** to another subject fails the AES-GCM tag.
4. **Upcasting**: an old event (`customer.signed-up`, field `fullName`) opens first, then the upcaster renames the field as plaintext.
5. **Erasure**: the `NOTIFY` evicts the key from the other process's cache (17.1 ms JIT, 7.0 ms native). Then:
   * the customer replays with `name`, `email` and `street` as `null`, while `country` and `city` stay;
   * the order keeps its total, and its `shipTo` reads as `null`;
   * new personal data for the subject is refused;
   * other subjects are unaffected, and the tombstone holds no key.
6. **Export** returns the subject's events in its customer stream and in an order, opened.
7. **Blind index**: equal for equal normalized emails, different between tenants.
8. **Tenant key**: blanking it makes every subject of the tenant unreadable.
9. **Projection re-fold**: re-folding the affected stream nulls the row's personal columns and its `doc`.
10. **`AesGcm` shared by 4 threads** without a lock fails 133,850–136,874 of 200,000 round trips.

## 4. Measurements **[verified]** ([`output.txt`](probes/data-protection/output.txt); 4 cores, 15 GB, SDK 11.0.100, PostgreSQL 17.12 with default settings in Docker on the same host, Npgsql 10.0.3, OpenSSL 3.0.13; JIT runs in Release)

### 4.1 Primitives (§2 JIT, §4 Native AOT)

| operation | JIT | Native AOT |
|---|---|---|
| seal a 32-byte value (envelope of 112 chars) | 1976 ns | 1818 ns |
| open it | 1108 ns | 988 ns |
| seal / open a 256-byte value (411 chars) | 1843 / 1027 ns | 2215 / 1269 ns |
| `new AesGcm(key)` | 1272 ns | 1372 ns |
| unwrap a data key | 1180 ns | 978 ns |
| blind index (HMAC-SHA256) | 1516 ns | 1572 ns |
| `RandomNumberGenerator.GetBytes(32)` | 799 ns | 817 ns |

The random nonce is 40 % of a seal. Keep the `AesGcm` per cached key (1.3 µs to build) under a lock (§5 item 1).

### 4.2 Codec (§3 JIT, §4 Native AOT)

| candidate | encode JIT / AOT | decode JIT / AOT | decode, key erased | stored |
|---|---|---|---|---|
| today | 508 / 306 ns | 471 / 511 ns | – | 67 B |
| **A** after the serializer | 4639 / 4692 ns | 2877 / 3034 ns | 710 / 839 ns | 221 B |
| B `JsonNode` | 4950 / 5883 ns | 4278 / 4796 ns | 1698 / 1753 ns | 221 B |
| C whole payload | 2075 / 2193 ns | 1309 / 1595 ns | event lost | 170 B |
| D converter | 3688 / 3982 ns | 3187 / 2577 ns | 431 / 572 ns | 221 B |

A's overhead is two seals plus about 0.6 µs of transform; the transform without crypto is cheap, and the envelope dominates. Finding
the subjects of a payload by scanning for envelopes takes 167–209 ns per event (the fallback when a row has no `subjects`).

### 4.3 Loads (§5; 2,000 customers × 10 events, rows interleaved, best of 3 passes)

| load of one customer | median | p99 |
|---|---|---|
| today's `NpgsqlAggregateStore.Load` (state) | 0.32 ms | 0.57 ms |
| plain state, 1 statement | 0.31 ms | 0.54 ms |
| **sealed state, cached key** | **0.25 ms** | 0.59 ms |
| sealed state, cold key (2 round trips + unwrap) | 0.92 ms | 2.15 ms |
| sealed state, keys in the same batch (cached / cold) | 0.50 / 0.59 ms | 1.26 / 1.58 ms |
| plain replay of 10 events | 0.39 ms | 0.79 ms |
| **sealed replay, cached key** | **0.40 ms** | 1.74 ms |
| sealed replay, cold key | 1.01 ms | 1.91 ms |
| sealed replay, keys in the same batch | 1.27 ms | 4.03 ms |
| erased subject, tombstone cached (replay / state) | 0.54 / 0.42 ms | 1.01 / 0.95 ms |

### 4.4 Commits (§6; 4 writers, 2,000 commits each per kind)

| commit | new customer p50 / p99 | known customer p50 / p99 |
|---|---|---|
| plain | 2.21 / 5.50 ms | 2.04 / 4.18 ms |
| sealed, `subjects` without index | 3.28 / 6.43 ms | 2.25 / 5.24 ms |
| sealed, `subjects` + GIN | 3.18 / 5.99 ms | 2.07 / 4.29 ms |
| today's `NpgsqlAggregateStore.Append` (new customer) | 2.38 / 4.83 ms | – |

A new subject costs two statements more (insert the key, read it back); 16B folds them into one `INSERT … ON CONFLICT DO NOTHING
RETURNING` in the commit's batch.

### 4.5 Finding a subject's events (§7; 1,000,000 events, 100,000 subjects, 316 MB)

| query | median | index |
|---|---|---|
| `subjects @> ARRAY[s]`, sequential scan | 86.9 ms | – |
| `payload::text LIKE '%s%'` (no `subjects` column) | 248 ms | – |
| **`subjects @> ARRAY[s]`, GIN** | **0.56 ms** | 13 MB, built in 1.6 s |
| a `subject_streams` side table, then the events | 0.80 ms | 148 MB |

### 4.6 Forgetting in a projection (§8; 20,000 customers × 5 events)

| | time |
|---|---|
| rebuild from the feed (100,000 events, `ProjectionHost`, batch 1,000) | 1727 ms |
| **re-fold the subject's streams** (find, load with the key gone, replace the row) | **2.63 ms** |
| delete the subject's row | 0.31 ms |

### 4.7 Retention (§9; 100,000 customers × 5 events, 186 MB, half expired, 4 writers committing)

| run | one batch, median / max | writers p50 / p99 / max |
|---|---|---|
| no deletion | – | 2.61 / 5.92 / 49.6 ms |
| **50,125 streams in batches of 500** (101 batches, 1.6 s) | 14.1 / 58.3 ms | 2.79 / 8.43 / 23.9 ms |
| in batches of 5,000 (11 batches, 1.0 s) | 92.6 / 123 ms | 2.71 / 7.57 / 14.4 ms |

### 4.8 Native AOT (§4)

The probe (Tarol.Persistence.Npgsql, Tarol.Queries.Npgsql, `System.Security.Cryptography`) publishes with **0 IL warnings**, 7.9 MB.
`AesGcm`, `HMACSHA256`, `Base64Url` and `Utf8JsonReader`/`Writer` need no reflection.

## 5. Found while probing

1. **`AesGcm` is not safe for concurrent use** **[verified]**. The probe's first commit bench failed with
   `AuthenticationTagMismatchException` because 4 writers shared the master key's instance; a direct test fails 133,850 of 200,000
   round trips. A cached key holds its instance under a lock (uncontended in practice: one subject per commit).
2. **Batching the keys with the rows is slower than caching them** **[verified]**: the key subquery adds 0.2–0.9 ms to every
   load. Cache first; fetch only the missing keys.
3. **The page cache keys on the checkpoint** **[verified by reading]** (`CachedReadModels`, `IPageCache.TryGet(key, position)`). A
   re-fold outside the feed does not move the checkpoint, so cached pages keep the erased data until the next commit to that
   projection. Erasure must evict, in every process (the `NOTIFY`).
4. **The read-model upsert refuses a rewrite at the same version** (`WHERE t.version < excluded.version`,
   `NpgsqlReadModelStore.cs:72`), and `IProjection.Apply` cannot delete **[verified by reading]**. A re-fold needs a forced
   replace, and retention needs a row delete.
5. **Lookups hold plaintext** (`lookups.key`), and so do the page cache's keys (`PageKey.Query` holds filter values)
   **[verified by reading]**. Personal lookups need a blind index, and erasure deletes them. A page key lives only in memory and
   is evicted with its page.
6. **NATS streams keep messages forever** (no `MaxAge`, `NatsTransport.cs:112-116`) **[verified by reading]**.
7. **Existing plaintext** **[unverified]**. Events written before an app marks a field stay plaintext; erasure cannot reach them.
   Old code in a rolling deploy reads an envelope as a string value. Both need a rule in 16B (§14).
8. **Backups hold wrapped keys** **[unverified]**: an erasure is complete only when older backups are gone (the guide says so).
9. **A new subject costs two statements** **[verified]** (+1.0 ms p50): fold them into the commit's batch.
10. **Export opens with the subject's key only** **[verified]**: another subject's field in the same event reads as `null`, which
    is the right answer for an export.
11. **The GIN index on `subjects` did not slow commits** **[verified]** (3.18 / 5.99 vs 3.28 / 6.43 ms without it; `fastupdate`).
    It costs 13 MB per 1,000,000 events and turns 87 ms into 0.56 ms.

## 6. Decided design (sketch for 16A–16D; names final in the PRs)

### 6.1 Marking (`Tarol.Primitives`, its generator; 16A)

* `PersonalDataAttribute` (property, field, parameter) with `string? Subject`; `RetentionAttribute` (class) with `int Days`.
* The aggregate generator emits, per event (on `EventCatalogEntry`) and per state, the sealed members' paths (top level,
  entity collections in the state) and a subject accessor (`Func<DomainEvent, Guid, …>` over typed members, no reflection). The
  codec maps member names to JSON names once at registration (`[JsonPropertyName]` or the options' naming policy).
* Diagnostics: **TPRM030** a `[PersonalData]` member is not nullable; **TPRM031** `Subject` names no `Guid`/`Id<T>` member of the
  same type; **TPRM032** `[PersonalData]` on a type that is neither an event, an aggregate or entity, nor a projection row (and a
  nested member of a value object: mark the whole member); **TPRM033** `[Retention]` on a non-aggregate or `Days < 1`.

### 6.2 Contracts and the codec (`Tarol.Persistence`; 16A)

* `Envelope`, `SubjectKey` (its `AesGcm` used under a lock), `KeyRing`, `PersonalField(Path, Subject)`.
* `IKeyStore`: `Keys(tenant, subjects, create)`, `Erase(tenant, subject)`, `EraseTenant(tenant)`, `Erased` (the eviction
  signal).
* `IKeyEncryptionKey`: `Wrap`/`Unwrap` a tenant key. The default `ConfiguredKeyEncryptionKey` reads a 256-bit master key and its
  id from configuration (`Tarol:DataProtection:MasterKey`). `InMemoryKeyStore` for tests.
* `EventCodec`:
  * `SubjectsOf<T>(event, stream)`;
  * `Encode<T>(event, payload, KeyRing)` seals after the serializer;
  * `Decode(streamType, stored, payload, KeyRing)` opens before the upcasters;
  * state: `SealState<T>`/`OpenState`;
  * the existing overloads stay for types without `[PersonalData]`.
* `InMemoryAggregateStore` seals and opens through the same codec (tests see `null` after erasure).
* `Error.Conflict("DataProtection.SubjectErased")`.
* Contracts for 16C: `IDataSubjects` (`Erase`, `Export`), `ErasureParticipant` (name, `Forget(tenant, subject, streams)`),
  `RetentionPolicy`.

### 6.3 Npgsql (`Tarol.Persistence.Npgsql`; 16B)

* Schema version 3 steps (additive, O-4):
  * `tenant_keys` and `subject_keys`;
  * `events.subjects uuid[] NULL` and `streams.subjects uuid[] NULL`;
  * `CREATE INDEX CONCURRENTLY events_subjects … USING gin (subjects)`.
* `NpgsqlKeyStore`: keys created in the commit's batch (`INSERT … ON CONFLICT DO NOTHING RETURNING`), cache-first loads, an
  idle-bounded cache evicted by `LISTEN tarol_erased`, the tombstone and the `NOTIFY` in one statement.
* The store:
  * seals events and state on `Append`;
  * opens on `Load`, `ReadAll` (the feed: one key query per batch for missing keys) and `RebuildStates`;
  * `ReadStream(tenant, type, id)` for re-folds;
  * `DeleteStreams(type, olderThan, batch)` for retention, skipping streams with outbox rows;
  * the subject queries for export.
* Personal lookups: `StoreLookups.DefinePersonal<T>(name, key)` stores HMAC-SHA256 under the tenant's lookup key (derived from the
  tenant key), and `Find` hashes the same way.
* Logs 8006–8008 (key created / erased / cache evicted, Debug).

### 6.4 `Tarol.DataProtection` (new; 16C)

* `AddDataProtection(o => …)`: the master key, the cache bounds, retention overrides (`Tarol:Retention:<streamType>:Days`).
* `DataSubjects : IDataSubjects`: `Erase` runs the steps of the concept note §5 (participants in registration order, an audit row,
  a log), `Export` builds the document of §6.
* Participants:
  * `Tarol.Queries` registers one per projection with `[PersonalData]` columns: re-fold through `ReadStream`, then
    `IReadModelStore.Replace` (new); retention through `IReadModelStore.Remove` (new);
  * `Tarol.Caching` evicts the tenant's pages (`IPageCache.Clear(tenant)`, new) on the erasure signal in every process;
  * the store deletes the personal lookups.
* System jobs: `tarol.retention` (hourly, batches of 500, audited, a `MaintenanceTask`). `tarol.purge-audit` comes with
  `OperationsOptions.AuditRetention` in `Tarol.Operations`.
* Meter `Tarol.DataProtection`: `tarol.subjects.erased`, `tarol.subjects.exported`, `tarol.retention.streams`, no tenant tag.
  Logs 16001–16008.
* `MapTarolAdmin` maps `GET {prefix}/subjects/{id}/export` and `POST {prefix}/subjects/{id}/erase` when `IDataSubjects` is
  registered.
* `Tarol.Messaging.Nats`: `MaxAge` defaults to the bus retention (7 days), configurable (a behaviour change).

### 6.5 Guide and sample (16D)

`docs/data-protection/README.md`:
* what to mark, and the subject rules;
* keys, the master key and its rotation, backups;
* erasure and export (and what they cannot reach: existing plaintext, logs, other services);
* retention;
* what contracts and logs must not carry.

`samples/Sagas` marks the bidder's name and email, shows erasure and export through the admin entry, and sets a retention on one
stream type.

## 7. Earlier decisions that constrain data protection (not re-asked)

| Decision | Text (as recorded) | Consequence here |
|---|---|---|
| P-4, O-4 | versioned additive schema steps under a lock, `CONCURRENTLY`, `lock_timeout` | the key tables, `subjects` columns and GIN index are version 3 steps |
| P-5 | tenant first in every key | keys, queries and erasure are per (tenant, subject) (DP-4) |
| P-12 | a state that does not parse is replayed | a state with unopenable fields still parses (fields `null`); no replay needed |
| C-8, S-5 | outbox rows per event; dead letters kept for a human | retention skips streams with pending reactions; dead letters keep ids only, except `last_error` |
| QR-3 | projection versions are dropped explicitly | erasure re-folds; it never drops or rebuilds a version |
| CA-2, CA-5 | the page cache is valid while the checkpoint is current; tenant first in the key | erasure evicts per tenant (§5 item 3) |
| O-1 | no tenant tag on metrics | the `Tarol.DataProtection` meter has no tenant or subject tag |
| O-3, O-5 | admin acts audited in the same statement; the policy is required; dead letters kept | erase and export endpoints follow; `tarol.purge-dead` stays the dead letters' retention |
| J-9 | Tarol's maintenance as system jobs | `tarol.retention`, `tarol.purge-audit` |
| M-9, messaging.md | contracts are the public language of a service | contracts carry ids, not sealed personal data |
| conventions | no reflection on AOT paths; no upward references; contracts in the lowest package | generated metadata; `IKeyStore`, `IDataSubjects`, `ErasureParticipant` in `Tarol.Persistence` |
| time.md | no ambient clock | `erased_at`, retention cut-offs and cache idle times come from the injected `TimeProvider` |

## 8. Plan impact

* **Row 16:** "designed", split 16A–16D (DP-8).
* **After step 17:** the KMS-backed key store (e.g. Infisical) as its own package (DP-1, DP-8).
* **Row 17 (release):**
  * the public API baseline includes the codec overloads and the new ports;
  * the release notes name schema version 3 and the rolling-deploy rule for sealing (§14);
  * the guide's backup note.
* **Row 18 (samples):** ArtFlow's `Bidder.Name` and `Expert.Name` are marked `[PersonalData]`.

## 9. Owner answers (2026-11-17)

Rounds:
* **Round 1**, after the inventory and the probes of the primitives, the codec candidates (JIT and Native AOT), loads and commits:
  DP-1 … DP-4.
* **Round 2**, after the export, forgetting and retention probes: DP-5 … DP-8.
* **Round 3**, the one gap the answers left (actor ids): DP-9.

The prefix DP- was free (taken: C-, CA-, D-, J-, M-, O-, P-, Q-, QR-, R-, S-, T-). Not asked because already decided: §7.
DP-1 was asked with the load numbers of a 300-customer dry run (cached 0.37 vs plain 0.40 ms); the recorded run (§4.3, 2,000
customers) gives 0.25 vs 0.31 ms and a cold key +0.6 ms. The answer does not depend on the difference.

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| DP-1 | Where do the per-subject keys live? (a load with a cached key ≈ a plain load, 0.37 vs 0.40 ms; a cache miss +0.7 ms; a commit that creates a subject key +1.0 ms p50, 3.2 vs 2.2 ms; the erasure's NOTIFY evicts other processes' cached keys in 7–17 ms; backups hold wrapped keys until they expire) | **Configurable; key tables in the store now, a KMS version later** (owner's words: "make it configurable: key tables in the store now, and then create a version with a KMS like Infisical"). The key store is a port; step 16 ships the implementation on tables in the store's database (`subject_keys`, `tenant_keys`, schema step version 3); a KMS-backed implementation (e.g. Infisical) follows as its own version. |
| DP-2 | How are personal fields marked and sealed? (today's codec 0.5 / 0.47 µs per event; sealing a field ~2 µs, opening ~1 µs; candidates: transform after the serializer 4.6 / 2.9 µs, a converter in the serializer 3.7 / 3.2 µs, the whole payload 2.1 / 1.3 µs; ~0.2 % of a 2.2 ms commit; Native AOT within 15 %, 0 IL warnings) | **`[PersonalData]` + codec transform** (recommended): an attribute on event and state properties, the subject is the stream id or `Subject = nameof(…)`; the generator emits JSON names and subject accessors; the codec seals after the serializer and opens before upcasters (upcasters see plaintext), type-agnostic; a field reads as `null` when its key is gone. |
| DP-3 | What does an erased subject look like afterwards? (verified: the customer still replays, name, email and street read as null, country and city stay; an order keeps its total, the customer's ship-to reads as null) | **Null + tombstone** (recommended): sealed fields read as `null` (`[PersonalData]` members must be nullable, a generator diagnostic); the key row stays as a tombstone (no key, `erased_at`); new personal data for the subject is refused (`DataProtection.SubjectErased`); a returning person is a new subject id. |
| DP-4 | How do tenants relate to subjects? (verified: deleting a tenant key made every subject of that tenant unreadable, one row) | **Two-level keys** (recommended): a tenant key wraps its subjects' data keys; offboarding a tenant is one row; a subject is (tenant, Guid); the same person in two tenants is two subjects (P-5). |
| DP-5 | What must "forget" do beyond the event store? (rebuilding a projection: 1.7 s per 100,000 events, per projection and erasure; re-folding the subject's streams 2.6 ms; finding them among 1,000,000 events 0.56 ms with a GIN index on `events.subjects`, 87 ms without; deleting a row 0.3 ms) | **Erasure orchestrates all** (recommended): tombstone + NOTIFY; every projection with `[PersonalData]` columns re-folds the affected streams; the tenant's page cache is cleared; the blind-indexed personal lookups of the subject's own streams are deleted; an `operations_audit` row. The bus is bounded by retention: contracts carry ids, NATS gets a `MaxAge` default, dead letters go through `tarol.purge-dead`. |
| DP-6 | How is a subject's data exported? (verified: found in every stream type through `events.subjects`, opened with the subject's key only) | **Library + admin endpoint** (recommended): `Export(tenant, subject)` returns one JSON document (every event holding the subject's data, opened with that subject's key only, other subjects' fields `null`; the current state of the subject's own streams); `GET {prefix}/subjects/{id}/export` under `MapTarolAdmin`'s required policy, audited. |
| DP-7 | Retention per stream type? (100,000 customers × 5 events, 4 writers: 50,125 expired streams deleted in batches of 500 in 1.6 s, a batch 14 ms median / 58 ms max, the writers' p99 5.9 → 8.4 ms; batches of 5,000: 1.0 s, 93 ms per batch, p99 7.6 ms) | **Delete whole streams** (recommended): `[Retention(days)]` per aggregate, overridden by `Tarol:Retention:<streamType>`; a stream is deleted N days after its last event (events, state, lookups) by the system job `tarol.retention` (hourly, batches of 500, audited); streams with pending reactions are skipped; projections delete the rows of deleted streams. |
| DP-8 | Scope and split; when does the KMS version come? | **16A–16D now, KMS after** (recommended): 16A attribute + generator + codec hook + in-memory key store; 16B Npgsql key tables, subjects columns, store integration (schema version 3); 16C `Tarol.DataProtection`: erasure, export, retention, admin endpoints; 16D guide + sample. The KMS-backed key store (e.g. Infisical) is its own package after step 17's release. **No read audit.** |
| DP-9 | What happens to actor ids when a person is erased? (`events.actor_id`/`on_behalf_of` hold the OIDC `sub`, a pseudonym, never a name or an email in Tarol's code; `idempotency_keys.scope` holds it too, purged after 7 days; `operations_audit.actor` holds the operator's principal name, never deleted) | **Keep pseudonyms** (recommended): actor ids stay (the accountability record; the link to a person lives in the identity provider, deleting the account unlinks them); `operations_audit` gets an optional `AuditRetention` (`tarol.purge-audit`); the guide forbids names or emails as actor ids. |

## 10. Changes against steps 7–15 (announced here, made in 16A–16D)

| Where | Change | Why |
|---|---|---|
| `Tarol.Primitives` | `[PersonalData]`, `[Retention]`; the generator emits sealed-member metadata on `EventCatalogEntry` and the state; TPRM030–TPRM033 | DP-2, DP-7 (16A) |
| `Tarol.Persistence` | `Envelope`, `SubjectKey`, `KeyRing`, `IKeyStore`, `IKeyEncryptionKey`, `InMemoryKeyStore`; `EventCodec` overloads with a `KeyRing` and `SubjectsOf`; `IDataSubjects`, `ErasureParticipant`, `RetentionPolicy`; the in-memory store seals | DP-1 … DP-4 (16A) |
| `Tarol.Persistence.Npgsql` | `NpgsqlSchema.Version = 3` (key tables, `subjects` columns, GIN); `NpgsqlKeyStore`; sealing on `Append`, opening on `Load`/`ReadAll`/`RebuildStates`; `ReadStream`, `DeleteStreams`; `StoreLookups.DefinePersonal`; logs 8006–8008 | DP-1, DP-5, DP-7 (16B) |
| `Tarol.Queries`, `.Npgsql`, `.Generators` | `IReadModelStore.Replace` and `Remove` (new members, in-memory parity); a projection with `[PersonalData]` columns registers an erasure participant; TQRY005 (a `[PersonalData]` column that is `[Sort]`ed: allowed, but it sorts `null` after erasure: Info) | DP-5, DP-7 (16C) |
| `Tarol.Caching` | `IPageCache.Clear(Guid tenant)`; eviction on the erasure signal | DP-5 (16C) |
| `Tarol.Messaging.Nats` | JetStream `MaxAge` defaults to 7 days (**behaviour change**: streams kept forever today) | DP-5 (16C) |
| `Tarol.Operations` | `MapTarolAdmin` maps subject export and erase; `OperationsOptions.AuditRetention` → `tarol.purge-audit` | DP-6, DP-9 (16C) |
| `Tarol.DataProtection` (new) | `AddDataProtection`, `DataSubjects`, `tarol.retention`, meter, logs 16001–16008, skill | DP-5 … DP-7 (16C) |
| `samples/Sagas`, `docs/data-protection/` | the guide; the sample marks and erases | 16D |
| `docs/operations/README.md` | the log-event table gains 8006–8008 and 16001–16008 | 16B, 16C |

No change to `IAggregateStore.Append`/`Load` signatures, the outbox claim, the job claim or the feed's ordering. Aggregates without
`[PersonalData]` are stored exactly as today.

## 11. What each PR must do (in the decided order)

The PRs stack 16A → 16B → 16C → 16D: **retarget each PR to `main` before its base is merged**, and put a fix found later into the
lowest branch it belongs to.

### PR 16A `primitives, persistence: [PersonalData], sealed fields in the codec, the key-store port`
* §6.1 and §6.2; `AnalyzerReleases.Unshipped.md` for TPRM030–TPRM033.
* Generator snapshot, incrementality and diagnostics tests:
  * a nullable and a non-nullable member;
  * `Subject` on a `Guid` and an `Id<T>`;
  * entity members in the state;
  * `[JsonPropertyName]` and a naming policy.
* Codec tests:
  * round trips of strings with escapes, numbers, value objects, records, lists and `null`;
  * a relabelled envelope;
  * a missing key gives `null`;
  * an upcaster over a sealed field;
  * 4 threads on one `SubjectKey` (the lock), 10–25 runs in Release;
  * events without `[PersonalData]` are byte-identical to today's.
* In-memory store: erasure, refusal (`DataProtection.SubjectErased`), tenant erasure.
* `Tarol.Persistence.AotSmoke` and `Tarol.Primitives.AotSmoke` extended (0 IL warnings).
* Skills `tarol-primitives` and `tarol-persistence` updated.

### PR 16B `persistence.npgsql: key tables, subjects columns, a sealed store (schema version 3)`
* §6.3. The version 3 steps follow O-4: `CREATE TABLE IF NOT EXISTS`, nullable columns, the GIN index `CONCURRENTLY` with
  invalid-index cleanup.
* Integration tests (Testcontainers):
  * the probe's 17 scenarios against the real store;
  * a version 2 database with events upgraded while writers commit;
  * the `NOTIFY` eviction across two store instances, bounded by the notification, not a wall clock;
  * `ReadAll` over a batch with many subjects (one key query per batch);
  * `DeleteStreams` skipping streams with outbox rows;
  * `DefinePersonal` lookups: `Find`, uniqueness, deletion;
  * the rolling-deploy rule of §14 item 1.
* The contract suites run with and without sealed aggregates.
* `Tarol.Persistence.Npgsql.AotSmoke` with a sealed aggregate when `TAROL_PG` is set.
* Skill `tarol-persistence-npgsql` updated; the log table in `docs/operations/README.md`.

### PR 16C `data-protection: Tarol.DataProtection — erasure, export, retention`
* `src/DataProtection/Tarol.DataProtection/` per the conventions §8 (§6.4):
  * README, XML docs, the skill `skills/tarol-data-protection/SKILL.md`;
  * `Tarol.DataProtection.Tests`, `.IntegrationTests`, `.AotSmoke` (the 24th app), `.PackageTests`.
* Package changes:
  * the Queries ports (`Replace`, `Remove`) with in-memory parity and the shared contract suite;
  * the projection participant and TQRY005;
  * `IPageCache.Clear(tenant)`;
  * the NATS `MaxAge` default;
  * the admin endpoints and `AuditRetention` in `Tarol.Operations`.
* Tests:
  * erasure end to end: store, two projections, the page cache in two processes, lookups, the audit row;
  * export (the document's shape, other subjects `null`, the policy: 401/403);
  * retention on a fake clock (`FakeTimeProvider`), configuration overrides, skipped streams, the rows removed;
  * the NATS stream's `MaxAge`.
* Skills `tarol-queries`, `tarol-caching`, `tarol-operations` and `tarol-messaging-nats` updated.

### PR 16D `docs: the data-protection guide; samples/Sagas with personal data`
* `docs/data-protection/README.md` (§6.5); CHANGELOG, root README package table; `docs/architecture/data-protection.md` status;
  plan row 16 "done".
* `samples/Sagas`: the bidder's name and email marked; erase and export through the admin entry; a retention on one stream type;
  the AppHost run (`apphost.sh`) passes.

## 12. Skill evaluation checklist (`tarol-data-protection`, 16C; updates in 16A–16C)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| store a customer's email so it can be erased | `[property: PersonalData] string? Email` on the event and `[PersonalData]` on the state member | encrypting by hand in the aggregate; a non-nullable member (TPRM030); marking only the event and not the state |
| an order holds the customer's address | `[PersonalData(Subject = nameof(CustomerId))]` | the order's own id as the subject (erasing the customer misses it) |
| erase a customer (GDPR art. 17) | `IDataSubjects.Erase(tenant, subject, actor)` or `POST {prefix}/subjects/{id}/erase` | deleting events or keys by SQL (no tombstone, projections and caches keep the data, no audit); rebuilding projections |
| answer an access request (art. 15/20) | `IDataSubjects.Export` or `GET {prefix}/subjects/{id}/export` | scanning payloads with `LIKE`; exporting other subjects' fields |
| find a customer by email | `StoreLookups.DefinePersonal<Customer>("email", c => c.Email)` + `Find` | a plain lookup on a personal value (plaintext in `lookups`) |
| keep orders for 10 years, then delete | `[Retention(Days = 3650)]` or `Tarol:Retention:order:Days` + `AddJobs` | a `DELETE` cron job that leaves state, lookups and read-model rows behind |
| offboard a tenant | `IKeyStore.EraseTenant(tenant)` through `IDataSubjects` | erasing subjects one by one |
| publish a customer change to another service | a contract with the customer id; the receiver asks the owner | personal data in the contract (it stays in the bus and in other services) |
| log a failed change | the change and the stream id | the payload or a personal value in the log message |
| configure the master key | `Tarol:DataProtection:MasterKey` from a secret store | the key in `appsettings.json` in the repository; a per-environment key copied into backups |

## 13. Verification of this PR (SDK 11.0.100) **[verified]**

Docs and probes only; the package code is `main` at `eabacb5` (step 15 done).
* **At the start on `main`** (2026-11-17, worktree at `eabacb5`):
  * CI run 192 of `main` (`build`, `aot-smoke`, `integration`): green;
  * `dotnet build Tarol.slnx`: 0 warnings;
  * `dotnet format --verify-no-changes`: clean;
  * `dotnet test --solution Tarol.slnx` with Docker: **1702 of 1702** (as 15C/15D recorded in operations.md §17.7, §18.6);
  * `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **23 apps**, 0 IL warnings, all checks passed;
  * `dotnet pack -c Release` + `eng/verify-packages.sh`: OK.
* **On this branch** (2026-11-17, at `f038158`; only docs and probes changed), in a detached worktree:
  * `dotnet build Tarol.slnx`: 0 warnings, 0 errors;
  * `dotnet format --verify-no-changes`: clean;
  * `dotnet test --solution Tarol.slnx` with Docker:
    * first run 1701 of 1702: `Tarol.Validations.Tests.AllocationTests.Validating_A_Valid_Input_Allocates_Nothing` measured
      3,136 bytes. This branch changes no code. The class passed 25 of 25 runs alone; most likely a tier-up landing inside the
      measured loop under the parallel suite's load. It is a pre-existing flake, recorded in §14 item 6, not fixed here (one
      feature per PR);
    * second run: **1702 of 1702**;
  * `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: 23 apps, 0 IL warnings, all checks passed;
  * `dotnet pack -c Release` + `eng/verify-packages.sh`: OK.
* **The probe**: `all.sh` sections 0–9 ([`output.txt`](probes/data-protection/output.txt)), JIT and Native AOT (0 IL warnings,
  17 of 17 checks on both). Fixes made before the recorded run:
  * the first commit bench failed with `AuthenticationTagMismatchException`: 4 writers shared one `AesGcm` (§5 item 1); the probe
    now locks per key and has a check that shows the race;
  * the load bench's cold-cache rows cleared the cache the "cached key" rows used (44 % key queries); they got their own key cache,
    and section 5 was rerun;
  * the plain state row ran an empty key statement and now runs one statement.

## 14. Open items

1. **Rolling deploy into sealing; existing plaintext**: **decided, DP-10 (§16.3)**. As designed (16B; §5 item 7): the version 3 steps keep `compatible_from` 2
   (old code runs on the new schema). A store that seals raises `compatible_from` to 3 on its first `EnsureSchema` with sealing
   on, so pre-16B pods refuse instead of showing envelopes as values. Existing plaintext fields are sealed by an explicit
   `SealHistory<T>()` (batched, like `RebuildStates`), the one physical rewrite of `events` Tarol does; it is never automatic.
   **Ask the owner in 16B.**
2. **Master-key rotation**: a new key id re-wraps the tenant keys only (no event is touched). `Rotate` belongs on
   `IKeyEncryptionKey`; 16A defines it, the guide describes it.
3. **The KMS-backed key store** (e.g. Infisical) after step 17 (DP-1, DP-8): it implements `IKeyEncryptionKey` (wrap and unwrap
   tenant keys; data keys stay in the tables, so one KMS call per cold tenant) or `IKeyStore` as a whole; an Infisical
   container for its integration tests.
4. **Entity collections in the state** (§6.1) were not probed **[unverified]**: 16A's generator tests and a probe-style benchmark
   of a state with 100 entities.
5. **`SqlProjection` tables** (the app's own SQL) are outside the participants: the guide says the app registers its own
   `ErasureParticipant`.
6. **`AllocationTests.Validating_A_Valid_Input_Allocates_Nothing` can fail under the parallel suite** (§13: 3,136 bytes once,
   0 of 25 alone). A fix (a longer warm-up, or measuring the minimum of several loops) goes in its own PR.

## 15. Implementation 16A: `[PersonalData]`, sealed fields in the codec, the key-store port

PR 16A builds §6.1 and §6.2. The flaky allocation test (§14 item 6) was fixed first in its own PR (#59).

### 15.1 Public API

`Tarol.Primitives`:
* `PersonalDataAttribute` (property, field, parameter) with `string? Subject { get; init; }`.
* `RetentionAttribute` (class) with `int Days { get; init; }`.
* `PersonalShape(PersonalMember[] members, params PersonalChild[] children)`, `PersonalMember(Name, JsonName?, Subject?,
  SubjectJsonName?)` and `PersonalChild(JsonName, Shape)`: what the generator emits.
* `EventCatalogEntry.Personal` (init). `EventCatalog.State`, `EventCatalog.RetentionDays` (init) and
  `EventCatalog.HasPersonalData`.
* The generator emits these on `T.EventTypes` only for marked aggregates. An unmarked aggregate's generated code is
  unchanged (the four existing snapshots did not move).
* Diagnostics TPRM030–TPRM033 come from a new `PersonalDataAnalyzer` (`AnalyzerReleases.Unshipped.md`).

`Tarol.Persistence`:
* `Envelope`: `Prefix`, `HeadSize`, `MaxLength`, `IsEnvelope`, `TryReadSubject`, `Seal`, `TryOpen`.
* `SubjectKey`: `Create`, `Erased`, `IsErased`, `Version`; its `AesGcm` is used under a lock. `SubjectErasedException`.
* `KeyRing`: `Add`, `Contains`, `TryGet`, `Count`, `Subjects`.
* `KeyWrap` and `KeyBinding`: the 60-byte AES-GCM key wrap and the associated data it binds to.
* `IKeyEncryptionKey` (`KeyId`, `Wrap`, `Unwrap(keyId, …)`) and `ConfiguredKeyEncryptionKey`
  (`FromBase64`, earlier keys for rotation).
* `IKeyStore`: `Keys(tenant, subjects, create)`, `EraseSubject`, `EraseTenant`, `BlindIndex`, `Rewrap`, and the `Erased`
  event (`KeyErasedEventArgs`).
* `StoreLookups.DefinePersonal<T>`, `IsPersonal`, `HasPersonal`; `KeyBinding.BlindIndex` (HMAC-SHA256 under a lookup key
  derived from the tenant key with HKDF, base64url); `InMemoryAggregateStore.DeletePersonalLookups`.
* `InMemoryKeyStore`: `Create`, `ErasedAt`.
* `EventCodec`, new members:
  * `HasPersonalData<T>()` and `HasPersonalData(streamType)`;
  * `SubjectsOf<T>(type, payload, stream, subjects)` and `SubjectsOf<T>(event, stream)`;
  * `Seal<T>`, `Encode<T>(…, stream, keys)`, `Decode(…, keys)`;
  * `StateSubjectsOf<T>`, `SealState<T>`;
  * static `OpenState`, `SealedSubjects`, `IsSealed`.
* `InMemoryStoreOptions.Keys`.
* `DataProtectionErrors.SubjectErased` / `SubjectErasedCode` (`DataProtection.SubjectErased`, 409).
* Contracts for 16C:
  * `IDataSubjects` (`EraseSubject`, `EraseTenant`, `Export`) with `SubjectErasure` and `SubjectExport`;
  * `ErasureParticipant` (`Name`, `Forget`, `ForgetTenant`, `Deleted`) and `StreamRef(Tenant, StreamType, Id)`;
  * `RetentionPolicy` (`Add<T>`, `Set`, `TryGetDays`, `Days`).

### 15.2 Deliberate differences from §6.1, §6.2 and §11

1. **The subject is found in the serializer's output, not through a generated `Func`** (§6.1 sketched a subject accessor
   over typed members). The generator emits the subject member's name, and the transform reads its value (a `Guid` or
   `Id<T>`, both JSON strings) from the same JSON object. One transform then serves events, the state and entity
   children (the state is not an event, and a `Func<DomainEvent, …>` cannot reach an entity in a set). It also needs no
   generated delegates. Cost: a second scan of an object, and only when one of its members has an explicit `Subject`.
2. **The transform replaces value slices instead of rewriting through `Utf8JsonWriter`**, unlike the probe. Every byte
   outside a sealed value is the serializer's own, so the escaping and number formats stay as written. Opening is the
   same replacement in reverse, for envelopes anywhere in the payload, because an upcast payload's old shape is unknown.
3. **Sealing is three calls** for store authors (`Encode` → `SubjectsOf` → `Keys` → `Seal`), plus a one-call
   `Encode(…, stream, keys)`. Keys must be fetched (or created in the commit) between the plain payload and the seal, so
   one call cannot do both.
4. **A ring that lacks a subject throws** rather than reading the field as `null`. A cache bug would otherwise read live
   data as erased, and the next commit would store the `null`. A store fills tombstones for subjects without a row.
   **A tag mismatch throws `CryptographicException`** (relabelled or tampered); only an erased key reads as `null`.
5. **Names**:
   * `IKeyStore.Erase` and `IDataSubjects.Erase` are `EraseSubject`: CA1716, because `Erase` is a Visual Basic keyword;
   * the participants' stream reference is `StreamRef`: CA1711 forbids a type name ending in `Stream`.
6. **`IDataSubjects.Export` takes the actor** (§6.4 audits exports, so the call must say who exported).
   `IDataSubjects.EraseTenant` is added for offboarding through the orchestrator (§12 row "offboard a tenant").
7. **Personal lookups move from 16B to 16A**, because the blind index needs the tenant key and therefore the port.
   §6.3 put `DefinePersonal` with the Npgsql store. `IKeyStore.BlindIndex` and `StoreLookups.DefinePersonal` are in 16A,
   with the in-memory store using them, so 16B only stores and finds the index. The lookup key is derived from the
   tenant key, so a tenant erasure makes the lookups unfindable too.
8. **Rotation**: §14 item 2 put `Rotate` on `IKeyEncryptionKey`. Here the master key unwraps under any configured id and
   wraps under its current one, and `IKeyStore.Rewrap` re-wraps the tenant rows. The key does not know the rows, so the
   store has to do the re-wrap. No event and no data key is touched (a test).
9. **Marking a member does not change `StateSchema`**: a sealed value is still the same member, and the state opener
   passes plaintext through. Marking an existing aggregate therefore does not force a replay of every stream.
10. **`Subject = nameof(Member)` in a positional record does not compile** unless a type of that name happens to be in
   scope: C# does not resolve a primary-constructor parameter in its own attribute. The design's example
   `nameof(Customer)` worked only because `Customer` is also a type. Write `nameof(Placed.Customer)` (the skills say so).
11. **Generated `PersonalChild` arguments are written with their type**: the target-typed form compiled under the preview
    language version the generator tests use, but not in a consumer's project (found by the persistence tests).

### 15.3 Changes against steps 7–15

* The changes below are additive: existing overloads keep their behaviour, except the plain `EventCodec.Decode` for a
  payload that holds envelopes, which now reads them as `null` instead of failing or showing them as strings.
* `InMemoryAggregateStore.Load`/`Append` became `async`; their behaviour is unchanged for unmarked aggregates and for
  stores without `Keys`.
* With `Keys`, the store keeps the sealed bytes of marked aggregates and opens them on every `Load`, `History`, `ReadAll`
  and `Recorded`, so erasure is visible.

### 15.4 Tests

* `Tarol.Primitives.Generators.Tests` (+31):
  * the `Personal` snapshot: event shapes with stream-id, `Guid` and `Id<T>` subjects and a pinned JSON name; a state
    shape with root members, an `EntitySet` and an `EntityRef` child, a `Subject` member; the retention;
  * `PersonalDataDiagnosticTests`: valid marks (property, parameter, field, entity, `Subject = nameof(Id)`), TPRM030
    (four shapes), TPRM031 (not a `Guid`, nullable, missing, itself), TPRM032 (a value object, another type, `[Transient]`,
    static, computed, `[LoadByReplay]`) and TPRM033 (`Days = 0`, none, on an entity);
  * incrementality: a new mark regenerates the aggregate, text moved above it does not.
* `Tarol.Persistence.Tests` (+35):
  * `DataProtectionCodecTests` (24): strings with quotes, backslashes, control characters, unicode, empty and `null`;
    numbers, lists, records; the envelope's documented size; a `Subject` member; a relabelled envelope and another
    tenant (`CryptographicException`); an erased subject reads `null` with the plain fields intact; a ring without the
    subject throws; sealing for an erased subject throws; an upcaster renames a sealed field after it is opened;
    unmarked events byte-identical; the plain bytes around a sealed value unchanged; the state with entity children
    sealed, opened byte-identically and read as `null` after erasure; **4 threads on one `SubjectKey`, 20,000 round
    trips: 0 failures, 25 of 25 runs in Release**; tenant erasure; master-key rotation;
  * `InMemoryDataProtectionTests` (11): state and replay; erasure across the customer, an order holding its address, the
    feed and `Recorded`, another subject untouched; refusal on the subject's own stream and on an order, a non-personal
    commit still accepted; tenant erasure; unmarked aggregates stored as before (history and state bytes); personal lookups: `Find` by the blind
    index (no plaintext, equal within a tenant, different across tenants and lookups), uniqueness, deletion frees the
    index, a tenant erasure makes it unfindable, a key store is required.
* AOT smoke: `Tarol.Primitives.AotSmoke` checks the generated metadata. `Tarol.Persistence.AotSmoke` checks sealing,
  opening, erasure and refusal natively.

**Entity collections in the state (§14 item 4)** **[verified]**. A root with 100 entities, one sealed member each, plus one
on the root: JIT Release, the minimum of 5 loops, the second of two rounds (the first ran in tier 0).

| | per state |
|---|---|
| write the state, plain | 13.5 µs |
| find the subjects | 14.2 µs |
| seal 101 fields | 193 µs (1.9 µs per field) |
| open 101 fields | 108 µs (1.1 µs per field) |
| open and read | 150 µs (plain read 36 µs) |
| size | 9,594 → 17,567 bytes |

The per-field cost is the probe's (§4.1). A state with many entities pays it per entity on every commit, so mark only
what is personal.

### 15.5 Skill checklist (`tarol-primitives`, `tarol-persistence`, updated)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| store a customer's email so it can be erased | `[property: PersonalData] string? Email` on the event and `[PersonalData]` on the state member | non-nullable (TPRM030); marking only the event |
| an order holds the customer's address | `[PersonalData(Subject = nameof(Placed.Customer))]` | `nameof(Customer)` that only compiles by accident; the order's own id |
| test that an erased customer still loads | `InMemoryStoreOptions { Codec, Keys = InMemoryKeyStore.Create(time) }`, `EraseSubject`, `Load` | encrypting by hand; deleting events |
| implement sealing in a store | `Encode` → `SubjectsOf` → `IKeyStore.Keys(create: true)` → `Seal`; `SealedSubjects` → `Keys(create: false)` → `Decode(…, keys)` | a ring without every subject; sharing an `AesGcm` without a lock |
| keep orders 10 years | `[Retention(Days = 3650)]` | on an entity, `Days = 0` (TPRM033) |
| find a customer by email | `StoreLookups.DefinePersonal<Customer>("email", c => c.Email?.ToLowerInvariant())` + `Find` | `Define` (plaintext in `lookups`); normalizing differently in `Find` |

### 15.6 Suppressions

None. CA1716 and CA1711 were met by renaming (§15.2 item 5).

### 15.7 Verification (SDK 11.0.100, 2026-11-17) **[verified]**

In a detached worktree at `a8bd77e`, with Docker (PostgreSQL 17, NATS 2.11, RabbitMQ 4 from `mirror.gcr.io`: Docker Hub
answered 429):
* `dotnet build Tarol.slnx`: 0 warnings, 0 errors;
* `dotnet format Tarol.slnx --verify-no-changes`: clean;
* `dotnet test --solution Tarol.slnx`: **1768 of 1768** (1702 on `main` + 31 generator + 35 persistence);
* `eng/aot-smoke.sh` with `TAROL_PG`, `TAROL_NATS`, `TAROL_RABBITMQ`: **23 apps, 0 IL warnings**, all checks passed
  (the Primitives and Persistence apps with the new checks);
* `dotnet pack -c Release` + `eng/verify-packages.sh`: OK (the updated skills are in the packages);
* `DataProtectionCodecTests.One_Subject_Key_Serves_Four_Threads` alone in Release: 25 of 25.

## 16. Implementation 16B: key tables, `subjects` columns, a sealed store (schema version 3)

PR 16B builds §6.3 on top of 16A.

### 16.1 Public API

`Tarol.Persistence.Npgsql`:
* `NpgsqlSchema.Version = 3`, `NpgsqlSchema.SealingVersion = 3`. The version 3 steps are `tenant_keys`, `subject_keys`,
  `events.subjects` and `streams.subjects` (`uuid[] NULL`), and the GIN index `events_subjects`, built `CONCURRENTLY`
  with the upgrader's invalid-index cleanup and `lock_timeout` (O-4).
* `NpgsqlKeyStore : IKeyStore, IAsyncDisposable` and `NpgsqlKeyStoreOptions` (`Schema`, `MaxCachedKeys` = 100,000,
  `IdleTime` = 1 h). It also has `StartListening`, `Channel` (`<schema>_erased`), `ClearCache`, `CachedKeys` and
  `KeyQueries`. Logs 8006–8008 (Debug).
* `NpgsqlStoreOptions.Keys`. With it the store:
  * seals on `Append` and opens on `Load`, `ReadAll`, the outbox claim, `RebuildStates` and `ReadStream`;
  * stores blind indexes for personal lookups;
  * raises `compatible_from` to 3 on `EnsureSchema`.
* `NpgsqlAggregateStore` gains:
  * `ReadStream(StreamRef)`, `StreamsOf(tenant, subject)`;
  * `EventsOf(tenant, subject)` and `StateOf(stream, subject)`, opened with that subject's key only;
  * `DeletePersonalLookups(tenant, streams)`, `DeleteStreams(type, olderThan, batch)`;
  * `SealHistory<T>(batch)`.
* `NpgsqlTarolBuilderExtensions.UseNpgsqlKeyStore`. `UseNpgsqlStore` takes the container's `IKeyStore` when the options
  set none.

`Tarol.Persistence` (16A's package, needed here):
* `EventCodec.SealStored<T>`: a history rewrite, in which an erased subject's field becomes `null`;
* `EventCodec.PersonalEventTypes<T>`;
* `StoreLookups.PersonalOf(streamType)`.

### 16.2 Deliberate differences from §6.3 and §11

1. **New subject keys are created just before the commit, not in its batch.** `IKeyStore.Keys(create: true)` inserts them
   with `ON CONFLICT DO NOTHING` and reads them back. A key needs the tenant key to be wrapped and the ring needs it to
   seal, so it must exist before the payload is sealed. In the batch, a concurrent creation of the same subject would
   seal under a key that lost the race. A failed commit leaves an unused key (harmless, §2.1). A cached subject costs
   nothing; a new one costs one round trip more.
2. **The subjects of a sealed payload are read from its envelopes** (`EventCodec.SealedSubjects`, about 0.2 µs per event,
   §4.2) rather than from the `subjects` column. The column is what the subject queries and the GIN index use.
3. **Only a store with keys writes the `subjects` columns.** A store without keys runs the SQL of version 2. It keeps
   working on a schema that is not upgraded yet (the existing test "a populated version 1 database is upgraded while
   writers commit" commits before `EnsureSchema`), and it stores rows exactly as before.
4. **The version 3 steps are compatible from 1, not 2** (§14 item 1 said "keep `compatible_from` 2"). The steps are
   additive, and code of version 1 and 2 never touches the new tables or columns, so it runs on a version 3 schema that
   does not seal. A sealing store raises it to 3. The owner confirmed this (DP-10, §16.3).
5. **`ReadStream` returns `PositionedEvent`s** (with the `RecordedEvent`'s context and position) instead of
   `StreamEvent`s, so a projection re-fold (16C) can call its `When(event, recorded)` methods.
6. **`DeleteStreams` uses `streams.updated_at`** (the last commit's time) for "older than", and locks the expired stream
   rows `FOR UPDATE SKIP LOCKED`: a racing commit waits, then finds no stream row and reports a version conflict.

### 16.3 Owner's answers (2026-11-18)

Asked with the measurement §14 item 1 required **[verified]** ([`SealHistoryBench.cs.txt`](probes/data-protection/SealHistoryBench.cs.txt),
[`sealhistory-output.txt`](probes/data-protection/sealhistory-output.txt); 4 cores, PostgreSQL 17 in Docker on the same host,
JIT Release). The setup: 250,000 customers with 4 plaintext events each (1,000,000 events, 250,000 states), then
`SealHistory<T>(500)` while 4 writers committed new sealed customers.

| | value |
|---|---|
| `SealHistory`: 1,000,004 events + 250,001 states | **264 s** (3,787 events/s), 0 left unsealed |
| writers alone (20 s, 16,795 commits) | p50 4.43 ms, p99 10.3 ms, max 67 ms |
| writers during `SealHistory` (190,047 commits) | p50 4.92 ms, p99 13.3 ms, **max 353 ms** |
| events table afterwards (1,206,846 rows) | 789 MB |

The first run of the benchmark found two faults, both fixed before the recorded run:
* the key cache's bound crashed under concurrent writers (§16.4);
* `SealHistory` paged and updated by `position`, which has no index, so every page scanned the table: about 770
  events/s at 780,000 events, and slower as the writers grew the table. It now pages by the primary key and writes one
  update per page.

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| DP-10 | §14 item 1: the rolling deploy into sealing, and existing plaintext (the table above; the version 3 steps are additive, so 16B makes them compatible from 1 where the design said 2) | **Raise on seal + explicit** (recommended): the version 3 steps are compatible from 1, so code of version 1 and 2 keeps running on a schema that does not seal; a store with keys raises `compatible_from` to 3 on its first `EnsureSchema`, so pods from before 16B refuse instead of showing envelopes as values; existing plaintext is sealed only by an explicit, batched `SealHistory<T>()`, never automatically. |

### 16.4 Changes against steps 7–15 and 16A

* Schema version 3. Every integration test that pinned version 2 now reads `NpgsqlSchema.Version`.
* The outbox claim decodes the claimed page with keys: reactions see the events opened, as a load does.
* `RecordedEvents` reads raw rows and decodes a page with one key call per tenant.
* The shared contract model `Badge.Name` is `[PersonalData] string?`. `Member` is therefore a sealed aggregate wherever a
  store has keys, and an unmarked one elsewhere.
* `SealHistory` pages and updates by the primary key (§16.3: the first version, by `position`, scanned the table on every page).
* A bug that measurement found: the key cache's bound evicted by ordering the live `ConcurrentDictionary` while other
  threads added keys, which throws. It now orders a snapshot. A regression test with a bound of 50 and four writers
  passes; the race is timing-dependent, and the test has not been shown to fail on the old code.

### 16.5 Tests

`Tarol.Persistence.Npgsql.IntegrationTests` (+19 data-protection tests, plus the contract runs below):
* the probe's scenarios on the real store:
  * no plaintext in `payload` or `state`, plain fields queryable, `subjects` recorded;
  * another process opens every field, and an order opens with the customer's key;
  * a cached key costs no key query;
  * a relabelled envelope throws;
  * opening runs before upcasting;
  * the `NOTIFY` evicts the key in another process, bounded by the notification;
  * after erasure the customer loads with personal fields `null`, the order keeps its total, other subjects are intact,
    and the tombstone holds no key;
  * new personal data is refused on the subject's own stream and on an order, while a non-personal commit is accepted;
  * the subject's streams and events are found and opened with its key only;
  * the blind index of a personal lookup: `Find`, uniqueness, deletion, other tenants;
  * a tenant erasure;
* one key query for a feed page of 50 subjects;
* `DeleteStreams` removes expired streams with their events and lookups, and skips a stream with a pending reaction;
* an unmarked aggregate is stored byte-identically by a sealing and a plain store (`payload` and `subjects` NULL);
* the rolling-deploy rule: version 2 code runs on a plain version 3 schema and refuses one a sealing store raised;
* a version 2 database upgraded to 3 while four "version 2 writers" insert: no error, the GIN index valid;
* `SealHistory` seals events and states written by a plain store, a rerun finds nothing, and erasure then reaches them;
* four writers seal concurrently;
* the key cache stays bounded under four writers.

Contract runs with sealed aggregates: `SealedStoreContractTests` and `SealedOutboxContractTests` (PostgreSQL) and
`InMemorySealedStoreContractTests`. The unsealed runs are unchanged.

`Tarol.Persistence.Npgsql.AotSmoke`: a sealing store raises `compatible_from`, stores no plaintext, opens, erases, and
reads the erased field as `null`, natively (0 IL warnings).

### 16.6 Skill checklist (`tarol-persistence-npgsql`, updated)

| Task given to an agent | Expected API | Mistakes to avoid |
|---|---|---|
| seal personal data in PostgreSQL | `NpgsqlKeyStore` + `NpgsqlStoreOptions.Keys` (or `UseNpgsqlKeyStore()` with an `IKeyEncryptionKey`) | a key store on another schema; encrypting in the app |
| marked a member of an existing aggregate | `SealHistory<T>(500, ct)` once | expecting old events to be sealed by themselves |
| erase by hand in SQL | none: `IDataSubjects.EraseSubject` | `UPDATE subject_keys SET wrapped = NULL` (no `NOTIFY`, no forgetting, no audit) |
| a rolling deploy into sealing | deploy, then let a sealing pod run `EnsureSchema` (old pods then refuse) | turning keys on while pods from before 16B still serve |

### 16.7 Suppressions

None.

### 16.8 Verification
