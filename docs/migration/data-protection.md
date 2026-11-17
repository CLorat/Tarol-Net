# Data protection: evaluation (step 16 design)

**Draft: the owner's answers are recorded here as they are given; the rest of the note follows.**

## 9. Owner answers (2026-11-17)

Rounds:
* **Round 1**, after the inventory and the probes of the primitives, the codec candidates (JIT and Native AOT), loads and commits:
  DP-1 … DP-4.

The prefix DP- was free (taken: C-, CA-, D-, J-, M-, O-, P-, Q-, QR-, R-, S-, T-).

| # | Question (with the numbers it was asked with) | Answer |
|---|---|---|
| DP-1 | Where do the per-subject keys live? (a load with a cached key ≈ a plain load, 0.37 vs 0.40 ms; a cache miss +0.7 ms; a commit that creates a subject key +1.0 ms p50, 3.2 vs 2.2 ms; the erasure's NOTIFY evicts other processes' cached keys in 7–17 ms; backups hold wrapped keys until they expire) | **Configurable; key tables in the store now, a KMS version later** (owner's words: "make it configurable: key tables in the store now, and then create a version with a KMS like Infisical"). The key store is a port; step 16 ships the implementation on tables in the store's database (`subject_keys`, `tenant_keys`, schema step version 3); a KMS-backed implementation (e.g. Infisical) follows as its own version. |
| DP-2 | How are personal fields marked and sealed? (today's codec 0.5 / 0.47 µs per event; sealing a field ~2 µs, opening ~1 µs; candidates: transform after the serializer 4.6 / 2.9 µs, a converter in the serializer 3.7 / 3.2 µs, the whole payload 2.1 / 1.3 µs; ~0.2 % of a 2.2 ms commit; Native AOT within 15 %, 0 IL warnings) | **`[PersonalData]` + codec transform** (recommended): an attribute on event and state properties, the subject is the stream id or `Subject = nameof(…)`; the generator emits JSON names and subject accessors; the codec seals after the serializer and opens before upcasters (upcasters see plaintext), type-agnostic; a field reads as `null` when its key is gone. |
| DP-3 | What does an erased subject look like afterwards? (verified: the customer still replays, name, email and street read as null, country and city stay; an order keeps its total, the customer's ship-to reads as null) | **Null + tombstone** (recommended): sealed fields read as `null` (`[PersonalData]` members must be nullable, a generator diagnostic); the key row stays as a tombstone (no key, `erased_at`); new personal data for the subject is refused (`DataProtection.SubjectErased`); a returning person is a new subject id. |
| DP-4 | How do tenants relate to subjects? (verified: deleting a tenant key made every subject of that tenant unreadable, one row) | **Two-level keys** (recommended): a tenant key wraps its subjects' data keys; offboarding a tenant is one row; a subject is (tenant, Guid); the same person in two tenants is two subjects (P-5). |
