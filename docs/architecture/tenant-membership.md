# Tenant membership: accounts in several tenants, step-up, sessions, global data

Design note (Tatalu round 2, not built). With Zitadel the tenant is the user's own organisation, hashed one way; an account can belong to
one tenant only. Tatalu also needs step-up authentication, ending a person's sessions, master data shared by all tenants, and a tenant
directory from the store. Decision ids `TM-*` (proposed). Labels as in the [conventions](conventions.md). TN-1 stands: the tenant stays
out of `Id<T>` ([feedback/tatalu.md](../feedback/tatalu.md)); nothing here changes ids or stream keys.

## 1. The need (Tatalu)

| Finding | What hurts | Workaround (`origin/integration/welle-2`) |
|---|---|---|
| FB-F15.1-3 | Tenant = the person's organisation (`ZitadelTenants.Of`, a one-way hash): a parent with children at two day-care facilities (US-15.2.2) cannot have one account; to create an account in a tenant the app must find the organisation itself | organisation via the owner's account (`GET /v2/users/{owner}` → `resourceOwner`) in `backend/src/Tatalu.Einrichtung/Konto/ZitadelIdentityProvider.cs`; a second facility with the same e-mail is `Invitation.EmailTaken` |
| FB-F13.2-1 | No step-up: `login()` has no `acr_values`/`max_age`; "second factor required for finances" (F13.2-R1) has no server-side policy | `main.ts` reads `amr` for display only |
| FB-F13.2-2 | Sessions at the identity provider cannot be ended ("device lost", access withdrawn US-13.1.5) | app-level `Device` sessions, ended at the next connection (`Tatalu.Datenschutz/Geraete/Device.cs`) |
| FB-F17.7-2 | No cross-tenant master data (a catalogue every tenant reads) | catalogue in code, reconciled per tenant |
| FB-F03.4-3 | `PerTenant` jobs need an `ITenantDirectory`; Tarol ships none | `backend/src/Tatalu.Host/TenantDirectory.cs` reads the **whole feed** for `ChildDayCare.Opened` |

## 2. What Tarol has today

* `ZitadelClaimsTransformation`: organisation = `urn:zitadel:iam:user:resourceowner:id`; only roles granted **in that organisation**
  become role claims; the `tenant` claim is replaced by `ZitadelTenants.Of(organisation)`
  (`src/Security/Tarol.Security.Zitadel/ZitadelClaims.cs:41-75`); `Of` = SHA-256 → UUIDv8 (`:87-93`).
* The edge: tenant from the `TenantClaim` (mapped by `ChangeEndpointOptions.Tenant`), a `{tenant}` route value must match (R-11); an
  authenticated caller **without** a tenant claim runs with `Tenant = None` (`ChangeEndpoints.cs:114-130`, `ChangeEndpointOptions.cs:22-33`).
* The store keys every row by tenant; `None` is stored as `Guid.Empty` (`NpgsqlAggregateStore.cs:46`).
* `ITenantDirectory.Tenants(ct)` (`src/Jobs/Tarol.Jobs/Job.cs:161-167`); `[PerTenant]` aggregates have the tenant's id (TN-2,
  `TenantAggregates.cs`).

## 3. Options and recommendation

### 3.1 One account, several tenants (TM-1, TM-2)

| | Option | For | Against |
|---|---|---|---|
| A | Tenant chosen at sign-in: the client asks for `urn:zitadel:iam:org:id:{id}`, one token per tenant | Zitadel's own model (branding, policies per organisation) | switching tenant = a new login; how Zitadel narrows the roles claim under that scope is **[unverified]** |
| B | **One token, tenant per request**: header `Tarol-Tenant`, accepted only when the token's project roles claim grants a role in that tenant's organisation; the roles of that organisation become the role claims | switching without login; the roles claim already lists roles per organisation (`{"role":{"<org>":"<domain>"}}`) | a middleware must run before authorization; the token grows with many grants |
| C | Memberships only in the app (IdP authenticates, the app's own register authorizes) | full control | duplicates the IdP's grants; two sources of truth |

**Recommend B**, with A's scope still usable (it narrows the claim, B then has one choice):

```csharp
builder.Services.AddTarol(…).AddEndpoints().AddZitadel(o => { o.Authority = …; o.ProjectId = …; o.Tenants = ZitadelTenantMode.PerRequest; });
app.UseAuthentication(); app.UseZitadelTenant(); app.UseAuthorization();     // before authorization: roles depend on the tenant
```

* No header and one granted organisation → that one; several → 401 `Auth.TenantChoice` on tenant-scoped entries; `/me`
  (`MeEndpoint.cs`) lists `tenants: [{ id, roles }]` so the client can offer the choice. An organisation without a grant → 404
  `Tenant.NotFound` (R-11: never confirm another tenant).
* **TM-2: a reversible register.** `IZitadelTenants.OrganisationOf(tenant)` / `TenantOf(organisation)`. Options: keep the hash and add a
  register (a table filled at provisioning and at first sight), or a reversible encoding (Zitadel ids are decimal strings of 64-bit numbers
  **[unverified]**: embed the number in a UUIDv8 with a fixed prefix). Recommend the **register** for compatibility with stored data
  (Tatalu's tenants are hashes); the reversible mapping as an option for new applications.
* Provisioning (TN-17 a) stays optional: `IZitadelProvisioning.Organisation(name, owner)` and `GrantRole(subject, tenant, role)` behind a
  service-account credential from configuration.

### 3.2 Step-up (TM-3)

`RequireSecondFactor(maxAge?)` on an `AuthorizationPolicyBuilder`: succeeds when `amr` contains a second factor (the set of Zitadel `amr`
values is configurable, defaults **[unverified]**) and `auth_time` is within `maxAge`. A failure answers 401 with
`WWW-Authenticate: Bearer error="insufficient_user_authentication", acr_values="…", max_age=…` (RFC 9470), so the client logs in again
with those values (tarol-oidc `login({ acr, maxAge })`, a Tarol-Svelte item). Usable as an endpoint `Policy` or in a row rule.

### 3.3 Ending sessions (TM-4)

`IIdentitySessions.EndAll(string subject, CancellationToken)` in `Tarol.Security` (a port), implemented by `ZitadelSessions` over the
session API v2 (list by user, delete each) and the revocation of the user's refresh tokens **[unverified]: exact endpoints to be probed**.
The app calls it from an effect (access withdrawn, device lost). Issued JWT access tokens stay valid until they expire: the guide recommends
a short lifetime (5–10 minutes); token introspection is not part.

### 3.4 Global master data (TM-5)

`[Global]` on an aggregate: it lives under `Tenant = None` (`Guid.Empty`) whatever the caller's tenant. Every tenant **reads** it
(`IAggregateReader.Load` resolves the scope from `T.Global`, a generated static like `T.PerTenant`; `[Reads<T>]` in changes,
[change-reads.md](change-reads.md) CR-1; views; projections with tenant key `Guid.Empty` included in every tenant's list). **Writes** only
by operations without a tenant **and** a platform grant (`roles.Platform("tatalu-admin").May<PublishCatalog>()`); a tenant-scoped caller
writing a global aggregate is 403 `Tenant.Global`. Telling tenants about a change: a reaction on the global event with
`ForEachTenant` (fan-out through `ITenantDirectory`, one occurrence per tenant). Because an authenticated caller without a tenant claim
runs with `None` today, TM-5 also closes that: tenant-less HTTP calls are refused unless the entry is marked global.

### 3.5 Tenants from the store (TM-6)

`UseStoreTenants<T>()` where `T` is a `[PerTenant]` aggregate: the tenants are the ids of its streams (`SELECT id FROM streams WHERE
type = @type`, its id is the tenant's). Exact, one indexed query instead of a feed scan; a tenant erased by `EraseTenant` is excluded
(its tenant key is a tombstone). `UseStoreTenants()` without a type (`DISTINCT tenant`) only if the owner wants it: it needs an index or a
`tenants` table.

## 4. Not part of this

* Tenant in `Id<T>` or in stream ids (TN-1); `GlobalRef<T>` (TN-3, separate).
* Other identity providers than Zitadel (the port in 3.3 allows them later).
* Moving data between tenants; a user switching tenants in one request.
* The browser parts (tenant picker, `login({ acr, maxAge })`, `oidc.revoke()`): Tarol-Svelte.

## 5. Open questions for the owner

1. TM-1: per-request tenant (B, recommended) or one token per tenant (A)?
2. TM-2: register (keeps today's tenant ids) or reversible encoding (changes them)?
3. TM-3: which `amr` values count as a second factor by default; is `RequireSecondFactor` in `Tarol.Security` (generic, configurable) or
   `Tarol.Security.Zitadel`?
4. TM-5: is "tenant-less HTTP calls are refused" acceptable as a behaviour change (single-tenant apps would set a fixed tenant)?
5. TM-6: tenants by a `[PerTenant]` marker type only, or also a `tenants` table maintained at every first append?

## 6. Steps (one PR each)

1. **TM-A** `UseStoreTenants<T>()` (Jobs + Npgsql), integration test against Tatalu's pattern.
2. **TM-B** `ZitadelTenantMode.PerRequest`, `UseZitadelTenant()`, `/me` tenants, register; tests with tokens granting two organisations.
3. **TM-C** `RequireSecondFactor` with RFC 9470 challenge.
4. **TM-D** `IIdentitySessions` and `ZitadelSessions` (probed against the Testcontainers Zitadel first).
5. **TM-E** `[Global]` aggregates, platform grants, `ForEachTenant`, refusal of tenant-less calls; migration note lists the behaviour change.
