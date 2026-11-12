# Step 1: StronglyTypedId

Origin: `CLorat/Tarol@6a62f81` — `src/StronglyTypedId/StronglyTypedId`, `gen/StronglyTypedId/StronglyTypedId.Generators`, `tests/StronglyTypedId/*`. Old review: `docs/review/ids-results-options.md`
(findings S-1 … S-14).

| Tarol-Net project | From |
|---|---|
| `src/StronglyTypedId/Tarol.StronglyTypedId` | `src/StronglyTypedId/StronglyTypedId` |
| `src/StronglyTypedId/Tarol.StronglyTypedId.Generators` | `gen/StronglyTypedId/StronglyTypedId.Generators` |
| `tests/StronglyTypedId/Tarol.StronglyTypedId.Tests` | `tests/StronglyTypedId/StronglyTypedId.UnitTests` (runtime part) |
| `tests/StronglyTypedId/Tarol.StronglyTypedId.Tests.ExternalAssembly` | `tests/StronglyTypedId/StronglyTypedId.Generators.Utility` |
| `tests/StronglyTypedId/Tarol.StronglyTypedId.Generators.Tests` | `tests/StronglyTypedId/StronglyTypedId.UnitTests` (generator part) |
| `tests/StronglyTypedId/Tarol.StronglyTypedId.AotSmoke` | new |
| `tests/StronglyTypedId/Tarol.StronglyTypedId.PackageTests` | new |
| `tests/Shared/Tarol.Testing.Generators` | `tests/Primitives/Primitives.Generators.UnitTests/Snapshot.cs`, generalised for every family |

## 1. Review

### What was already good (kept)

`Id<T>` as a `readonly record struct` over one `Guid`; internal constructor; explicit conversions only; UUIDv7 `New()`;
span and UTF-8 parsing and formatting; `ISpanParsable` (minimal API binding); dictionary-key JSON; `ThrowIfEmpty` with
`CallerArgumentExpression`; C# 14 `extension` blocks for `New/From/Parse/TryParse` (static members on the generic type
without CA1000 noise at the call site); an incremental generator with string-only models and a caching test.

### Problems found (old findings and new ones)

| # | Problem | Resolution in Tarol-Net |
|---|---|---|
| S-1 | Generator reference pointed at a non-existent path (MSB9008); the generator never reached any consumer | Bundled through `TarolGenerator="true"` and `eng/Tarol.Packaging.targets`; verified in the `.nupkg` and by a package consumer |
| S-2 | Ids closed only through inheritance (`class Order : AggregateRoot<Order>` → `Id<Order>`) were never registered | Second generator pipeline walks generic base types and interfaces of every declared type and collects closed id member types (properties, fields, nullable, collections, arrays) |
| S-3 | JSON factory fell back to `Activator.CreateInstance(MakeGenericType(…))` with IL3050 suppressed; that was the only path actually used | **Removed.** `Id<T>` carries `[JsonConverter(typeof(IdJsonConverter<>))]`; System.Text.Json closes the open generic itself. **[verified]** on .NET 11: the STJ source generator emits `new IdJsonConverter<User>()` into the context; a Native-AOT binary with reflection-based JSON disabled round-trips ids, lists and dictionary keys; reflection mode works too. JSON needs no registry and no generator. |
| S-4/S-5/S-6 | EF Core helper (`Tarol.StronlgyTypedId.EntityFrameworkCore`): assembly scanning, missed interface/struct markers, typo in the package id | **Not migrated** (owner decision: EF Core is not Tarol's persistence provider). Id mapping for the Npgsql store comes with step 8. |
| S-7 | Module initializers run when the declaring assembly loads (JIT) | Unchanged by design; only `TypeDescriptor` depends on it now (JSON no longer does) |
| S-8 | Values accepted only "D", dictionary keys accepted every format | Both accept exactly "D" (what is written) |
| S-9 | `Empty` accepted on every inbound path | Kept and documented (README, skill) |
| S-10 | `PublishAot=true` on a library | Removed; `IsAotCompatible` from `src/Directory.Build.props` |
| S-11 | File/type/namespace mismatch (`StronglyTypedIdJsonGenerator.cs`, `…Generator` namespace) | `IdRegistrationGenerator.cs`, namespace `Tarol.StronglyTypedId.Generators` |
| S-12 | Private/file-local markers skipped silently (file-local ones produced uncompilable code) | File-local markers are skipped too; both report **TSID001** (Info), declared in `AnalyzerReleases.Unshipped.md` |
| S-13 | Unit tests did not run the generator on themselves | The test project references the generator as an analyzer, like a consumer |
| new | Property-name read allocated a string per key (`reader.GetString()`) | Parses from `ValueSpan`, or unescapes into a stack buffer: **[verified]** 0 bytes allocated on all four converter paths (`IdJsonAllocationTests`) |
| new | `TypeDescriptor.GetConverter(Type)` (IL2026) was the documented lookup | AOT apps use `TypeDescriptor.GetConverterFromRegisteredType` (.NET 9+); the smoke app enables `TypeDescriptor.RequireRegisteredTypes` to prove the generated registration |
| new | **Configuration binding under AOT.** The source-generated configuration binder treats `Id<T>` as an object and ignores `TypeConverter`s, so `Id<T>` options properties silently stay `Empty` **[verified]** (the reflection binder works) | Documented in README and skill with the AOT-safe pattern (bind `Guid`, convert); checked by the smoke app. Not fixable inside Tarol. |
| new | Registry suppressed IL2026/IL3050 | Neither warning occurs on .NET 11 with the closed generic calls: **both suppressions removed**. The package now has **no** suppression at all. |

### .NET 11 / C# 15 assessment

| Feature | Used? | Why |
|---|---|---|
| Open generic `JsonConverter` on a generic type (STJ, verified on .NET 11) | yes | Removes factory, registry and reflection from JSON |
| `IUtf8SpanParsable` without `#if NET9_0_OR_GREATER` | yes | net11.0 only; the conditional is gone |
| `IComparisonOperators<Id<T>, Id<T>, bool>` | yes | Ids work in generic-math constraints (`where TId : IComparisonOperators<…>`) |
| `TimeProvider` | yes | `Id<T>.New(TimeProvider)` for deterministic ids in tests |
| `TypeDescriptor.RegisterType` / `GetConverterFromRegisteredType` / `RequireRegisteredTypes` | yes | The AOT-friendly `TypeDescriptor` mode |
| C# 14 `extension` members | kept | Static factory members on the generic type, read as `Id<User>.New()` |
| C# 15 unions / closed hierarchies | no | Nothing in an id is a sum type; they matter for `Result` (step 2) and events (step 6) |
| `field` keyword, primary constructors | no | Nothing to simplify |
| `IStronglyTypedId<TSelf>` with `static abstract From(Guid)` | not added | Would let generic infrastructure build ids without reflection; no consumer needs it yet (revisit in step 8) |

### AOT, performance, developer experience

* **AOT:** `IsAotCompatible` with 0 analyzer warnings and 0 suppressions; the smoke app publishes with **0 IL warnings**
  and passes 11 checks as a native binary (`eng/aot-smoke.sh`).
* **Performance:** `Id<T>` is 16 bytes (checked natively); all members delegate to `Guid` with `AggressiveInlining`;
  JSON reads and writes allocate nothing (verified). No benchmark suite exists yet; add BenchmarkDotNet when a family
  has a hot path worth tracking (step 6).
* **Developer experience:** one package (generator inside); no configuration for JSON, minimal APIs (**[verified]**
  route + query binding and 400 on a malformed id, `MinimalApiBindingTests`), `TypeDescriptor` or the reflection
  configuration binder. MVC binding through `IParsable` is **[unverified]** here.
  `id.ToString("N")` triggers CA1305 in consumers that enable it, like any `IFormattable` with a one-argument overload;
  kept for parity with `Guid`.

## 2. Changes against Tarol (public API and behaviour)

| Change | Kind | Migration for users |
|---|---|---|
| `IdJsonConverterFactory` removed | API (breaking) | none: `[JsonConverter]` on `Id<T>` covers it; delete explicit references |
| `IdConverterRegistry` now only registers the `TypeConverter`; `TryGetConverter` removed (was internal) | behaviour | none |
| `Id<T>.New(TimeProvider)` | API (added) | — |
| `IComparisonOperators<Id<T>, Id<T>, bool>` implemented | API (added) | — |
| JSON dictionary keys accept only the "D" format | behaviour | write keys as produced by the converter |
| Generator registers ids inherited from generic bases; reports TSID001 | behaviour (added) | — |
| `Tarol.StronlgyTypedId.EntityFrameworkCore` not migrated | package (dropped) | map ids yourself if you use EF Core (`ValueConverter<Id<T>, Guid>`); Tarol persists through Npgsql (step 8) |
| `Id<T>.Empty` no longer has an explicit `= default` initializer | none (CA1805) | — |

## 3. Suppressions and analyzer configuration

* `Tarol.StronglyTypedId`, `.Generators`: **none**.
* Root `.editorconfig`: CA1000 off (static members on generic types are the API of `Id<T>`, `Result<T>`, …).
* `tests/.editorconfig`: CA1707, CA1515, CA2007, CS1591 off in tests (reasons in the file).

## 4. Verification (SDK 11.0.100, 2026-11-12)

| Command | Result |
|---|---|
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | 82 passed, 0 failed (runtime 62, generator 17, shared harness 3) |
| `dotnet format Tarol.slnx --verify-no-changes` | clean |
| `eng/aot-smoke.sh` | 0 IL warnings; 11/11 native checks |
| `eng/verify-packages.sh` | see below |

`eng/verify-packages.sh` output (contents of the packed `.nupkg`, then the consumer run):

```
Tarol.StronglyTypedId 0.0.0-alpha.0.5
    LICENSE
    README.md
    Tarol.StronglyTypedId.nuspec
    analyzers/dotnet/cs/Tarol.StronglyTypedId.Generators.dll
    lib/net11.0/Tarol.StronglyTypedId.dll
    lib/net11.0/Tarol.StronglyTypedId.xml
    skills/tarol-strongly-typed-id/SKILL.md
== tests/StronglyTypedId/Tarol.StronglyTypedId.PackageTests/Tarol.StronglyTypedId.PackageTests.csproj (Tarol packages 0.0.0-alpha.0.5)
ok   bundled generator registered the inherited Id<Order>
ok   JSON round-trip
ALL CHECKS PASSED
   skill files listed in artifacts/obj/Tarol.StronglyTypedId.PackageTests/project.assets.json:
     "skills/tarol-strongly-typed-id/SKILL.md"
verify-packages: OK
```

* The generator is **not** a package dependency and no `Tarol.StronglyTypedId.Generators` package exists **[verified]**.
* No `build/` or `buildTransitive/` folder in the package, so the analyzer loads once **[verified]**.
* `.snupkg` symbol packages are produced; the nuspec carries SourceLink repository metadata (url, branch, commit) **[verified]**.
* Package validation (`EnablePackageValidation`) runs during pack; there is no baseline version yet.
* The proprietary `LICENSE` is packed and referenced as the package licence file (plan D-1).

## 5. Skill evaluation checklist

Run each task with a frontier model in a fresh console project that references the packed package, once with the
skill copied to `.agents/skills/` and once without. Record correctness and token cost. Keep a skill only if it is
at least as correct and not more expensive.

`tarol-strongly-typed-id`:

1. "Add a strongly-typed id to `Customer` and expose `GET /customers/{id}`" → `Id<Customer>`, `Id<Customer>.New()`,
   route parameter typed `Id<Customer>`; no custom converter, no `[JsonConverter]`, no registration call.
2. "Serialize an order DTO with ids in a Native-AOT app" → a `JsonSerializerContext`; nothing id-specific.
3. "Bind an owner id from appsettings in a Native-AOT app" → binds `Guid` and converts (does not expect `Id<T>` to bind).
4. "Write a test that needs two ids in creation order" → `Id<T>.New(DateTimeOffset)` or `New(TimeProvider)`.
5. "Why does TSID001 appear?" → private/file-local marker; make it internal or call `IdConverterRegistry.Register<T>()`.
6. Must not: use `new Id<T>(…)`, implicit conversions, `MakeGenericType`, decompile the assembly or search the web.

Status: checklist written; **not yet run** (needs a model-evaluation harness outside this repository).

## 6. Open items

* Benchmarks (BenchmarkDotNet) once a hot path in a later family needs tracking.
* MVC (`IParsable`) binding not tested here.
* `IStronglyTypedId<TSelf>` static factory: decide in step 8 (persistence) whether generic infrastructure needs it.
* `dotnet-package-skills install` was not run (tool not installed here).
