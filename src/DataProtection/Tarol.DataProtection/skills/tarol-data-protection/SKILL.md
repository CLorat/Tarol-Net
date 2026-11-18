---
name: tarol-data-protection
description: >-
  Use when storing personal data in Tarol aggregates so it can be erased (GDPR art. 17), exported (art. 15/20) or
  deleted after a retention period. Covers [PersonalData] (Subject), [Retention(Days)], AddDataProtection
  (DataProtectionOptions, Tarol:DataProtection:MasterKey, Tarol:Retention:<type>:Days), UseNpgsqlKeyStore /
  UseInMemoryKeyStore, IDataSubjects (EraseSubject, EraseTenant, Export), ErasureParticipant, the admin endpoints
  …/subjects/{id}/export|erase, tarol.retention, StoreLookups.DefinePersonal and SealHistory. Do not encrypt fields by
  hand, delete events or keys with SQL, rebuild projections to forget, or put personal data in contracts or logs.
---
# Tarol.DataProtection

## Rules

* Mark every personal member, on the event **and** on the state: `[property: PersonalData] string? Email` in a positional
  record, and `[PersonalData] public string? Email { get; private set; }` on the aggregate or entity. It must be nullable
  (TPRM030), because an erased value reads as `null`.
* The subject is the stream id. Another aggregate holding a person's data names the person:
  `[PersonalData(Subject = nameof(Placed.Customer))]` (a `Guid`/`Id<T>` member, TPRM031). Write `nameof(Record.Member)`:
  a bare parameter name does not resolve in its own attribute.
* Register the store with keys (`UseNpgsqlKeyStore()` or `UseInMemoryKeyStore()`), then `AddDataProtection()`. The master
  key comes from `Tarol:DataProtection:MasterKey` (base64, 32 bytes), kept in a secret store and never in the repository.
* Erase and export only through `IDataSubjects` or the admin endpoints. They audit, and they make projections, page
  caches and personal lookups forget.
* A unique lookup over a personal value: `StoreLookups.DefinePersonal<T>(name, key)`, which keeps a blind index.
* Contracts carry ids, not personal data; the receiver asks the owning service. Logs carry ids, never values.
* Plaintext written before a member was marked stays plaintext until `store.SealHistory<T>(500, ct)` has run once.
* Tables Tarol does not know (your own SQL) forget through your own `ErasureParticipant`, registered as a singleton.

## Example

```csharp
[Retention(Days = 3650)]
public partial class Customer : AggregateRoot<Customer>
{
    [PersonalData] public string? Email { get; private set; }
    public string Country { get; private set; } = "";
    public sealed record Registered([property: PersonalData] string? Email, string Country) : Event;
    private void On(Registered e) { Email = e.Email; Country = e.Country; }
}

services.AddTarol(json, tarol => tarol.Add<Customer>()
    .UseNpgsqlStore(cs, new NpgsqlStoreOptions { Lookups = new StoreLookups().DefinePersonal<Customer>("email", c => c.Email?.ToLowerInvariant()) })
    .UseNpgsqlKeyStore()
    .AddDataProtection(o => o.Retention["order"] = 365));

var erasure = await subjects.EraseSubject(tenant, customerId, actor: User.Identity!.Name!, ct);   // a tombstone, then forgetting
var export = await subjects.Export(tenant, customerId, actor: "dpo", ct);                           // export.Utf8Json
```

After an erasure the aggregate still loads: `Email` is `null` and `Country` is intact. A new event with an email for that
subject fails with `DataProtection.SubjectErased` (409); a returning person is a new subject.

## Common mistakes

| Mistake | Result |
|---|---|
| `[PersonalData] string Email` | TPRM030 |
| `Subject = nameof(Customer)` naming a `string`, or a bare `nameof(Customer)` that does not resolve | TPRM031 / CS0103 |
| marking only the event, not the state member | the state keeps the value in plain JSON |
| `[PersonalData]` inside a value object | TPRM032: mark the member holding it |
| `DELETE FROM events …` or `UPDATE subject_keys SET wrapped = NULL` | no tombstone/`NOTIFY`, projections and caches keep the data, no audit |
| rebuilding a projection to forget | minutes per projection; the erasure re-folds only the subject's streams |
| `AddDataProtection()` without a master key | `InvalidOperationException` naming `Tarol:DataProtection:MasterKey` |
| `AddDataProtection()` without a key store | the `IKeyStore` cannot be resolved: add `UseNpgsqlKeyStore()` |
| `Define` for an email lookup | the email in plain text in `lookups`; use `DefinePersonal` |
| expecting retention without a job host | nothing runs `tarol.retention`; add `AddJobs` |
| a participant that throws | `ErasureIncompleteException` (the key is erased already); fix and erase again |

## AOT

No reflection: the generator emits the marks, the codec seals with `Utf8JsonReader` slices and `AesGcm`, and the export
writes events through the codec's source-generated JSON. The `Tarol.DataProtection.AotSmoke` app publishes with 0 IL warnings.
