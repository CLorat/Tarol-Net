# Probes: Data protection (step 16 design)

Evidence for [docs/migration/data-protection.md](../../data-protection.md) and
[docs/architecture/data-protection.md](../../../architecture/data-protection.md). The probe is copied and adapted, never
referenced, and stays out of `Tarol.slnx`. `*.csproj.txt` becomes a project file in a work directory (`run.sh`); `TarolRoot`
points at this repository, so the probe builds against **today's** packages (`Tarol.Persistence.Npgsql`, `Tarol.Queries.Npgsql`,
the Primitives and Queries generators). Nothing in it is package code.

The shipped store cannot be hooked without changing package code, so the probe has its own **would-be protected store**
(`Vault`) over SQL in its own schema: today's `EventCodec` writes and reads the events, and the field transform runs between the
codec and SQL, where step 16's store change would put it. Loads and commits are compared with today's `NpgsqlAggregateStore`
on the same database.

| File | What it is |
|---|---|
| `DataProtection/Protect.cs` | the would-be primitives: `Envelope` (`"tdp1." + base64url(subject, key version, nonce, tag, ciphertext)`, AES-256-GCM, associated data = tenant + subject), `SubjectKey`, `KeyRing`, `KeyCache` (per process, time-bounded), `Kek` (the master key that wraps tenant keys), `BlindIndex` (HMAC-SHA256 for personal lookup keys), and the four codec candidates: **A** `FieldTransform` (after the serializer, one pass with `Utf8JsonReader`/`Utf8JsonWriter` over raw value slices, type-agnostic), **B** `NodeTransform` (the same through `JsonNode`), **C** `WholeTransform` (the whole payload in one envelope), **D** `PersonalString` (a converter inside the serializer with the key in a thread-static scope) |
| `DataProtection/Vault.cs` | the would-be store integration: `subject_keys` and `tenant_keys` (a data key per tenant and subject, wrapped by the tenant key, wrapped by the master key), `events.subjects` and `streams.subjects` (`uuid[]`, optional GIN index), append (keys created in the commit's transaction, refused for an erased subject), load from events or from the sealed state (keys batched with the rows, or cache-first), erasure (a tombstone and a `NOTIFY`), tenant erasure, export |
| `DataProtection/Domain.cs` | `Customer` (its own subject: `Name`, `Email`, `Street` personal; `Country`, `City`, `Tier` not), `Order` (`ShipTo` belongs to the customer named in the event), the projection `CustomerRow` (`customer-list`, copies name and email), and `Personal`: the field lists a generator would emit from `[PersonalData]` |
| `DataProtection/SelfTest.cs` | `selftest protect`: sealing on append, opening on load in another process, a relabelled envelope, an upcaster over a sealed field, erasure with the NOTIFY eviction, refusal of new data for an erased subject, export, blind index, tenant key, the projection re-fold, `AesGcm` shared between threads |
| `DataProtection/Bench.cs` | `bench crypto` (envelopes, unwrap, blind index), `codec` (candidates A–D), `load <streams>` (today's store vs plain vs protected; state and replay; keys batched, cached, cold; erased), `commit <per writer>` (4 writers; subjects column without and with GIN), `export <events>` (one subject among many: sequential scan, `LIKE`, GIN, a side table), `forget <streams>` (rebuild vs re-fold vs delete), `retention <streams>` (deleting whole streams in batches while 4 writers commit) |
| `inventory.md` | the old code (`CLorat/Tarol@6a62f81`) and where personal data lands in Tarol-Net today, file:line |
| `output.txt` | the last `all.sh` run (sections 0–9) |

```bash
dockerd > /tmp/dockerd.log 2>&1 &                     # postgres:17 (tarol-probe-dp, 55433); all.sh starts it
docs/migration/probes/data-protection/all.sh /tmp/dp    # resumable: sec-<n>.txt + .ok per section; ALL_REDO=<n> redoes one
docs/migration/probes/data-protection/run.sh /tmp/w DataProtection run -c Release -- selftest protect   # one run; PROBE_PG overrides the database
docs/migration/probes/data-protection/run.sh /tmp/w DataProtection run -c Release -- bench load 2000
```

Notes:
* The export bench seeds rows with SQL (`generate_series`): it measures the queries on a table of that size. The other benches
  write through the probe's store or today's `NpgsqlAggregateStore.Append`.
* Load rows are interleaved and run in 3 passes; the best pass per row is reported (the first runs in tier 0).
* Never run `all.sh` next to the full test suite: sections 2–9 measure latency.
