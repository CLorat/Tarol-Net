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

1. **Rolling deploy into sealing; existing plaintext** (16B; §5 item 7). Recommended: the version 3 steps keep `compatible_from` 2
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
   0 of 25 alone). **Fixed in its own PR** (owner's choice, 2026-11-17: "the minimum of several loops"): both allocation tests
   warm up 100 calls, then measure 5 loops of 1,000 and assert on the least allocating one. 25 of 25 runs alone and 10 of 10 runs
   of the whole project in Release passed **[verified]**.
