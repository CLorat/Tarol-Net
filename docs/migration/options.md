# Step 3: Options

Origin: `CLorat/Tarol@6a62f81`: `src/Options/Options` (one file, `OptionCombinedNullableExtensions.cs`),
`tests/Options/Options` (17 tests), the orphaned `tests/Options/Options.Persistence.Relational`, and the Primitives
`global using Tarol.Options;`. History: `e4722c4` removed `Option<T>`, `Options.Persistence.Relational` and
`Options.Serialization.Json`.
Old review: `docs/review/ids-results-options.md` (§1.4, O-1 … O-5, §7 "Design 5: Option stays as is"),
`docs/review/net11-event-generation.md` (§1 rows 1, 2, 5, 6).

**Status: implemented.** §0–§6 are the review, the experiments and the proposal; §7 the owner's
decisions; §8 onwards the implementation.

Evidence labels follow [conventions](../architecture/conventions.md): **[verified]** means reproduced here (probe and
command given), **[unverified]** means not tried here. The probes are in [`probes/options/`](probes/options/). Their
`*.csproj.txt` files are meant to be copied **outside** the repository, renamed to `.csproj` and run with
`-p:TarolNetRepo=<clone of Tarol-Net>`, so that the repository's props do not apply. The complete output is in
[`probes/options/output.txt`](probes/options/output.txt). Environment: SDK 11.0.100, .NET 11.0.0, linux-x64, Intel Xeon 2.1 GHz, 4 cores.

| Probe | What it shows | Command |
|---|---|---|
| `old/` | the real old extensions: O-2, O-3, O-4 | `dotnet run -p:TarolOldRepo=<clone of CLorat/Tarol>` |
| `probe/` | candidates B, C and D (`Shapes.cs`) and two JSON contracts for C (`Json.cs`): sizes, allocations, `default`/`null`, JSON with reflection and with source generation | `dotnet run -c Release`; AOT: `dotnet publish -c Release -r linux-x64 -p:NoReflJson=true` |
| `diag/` | "must be handled": compiler diagnostics for A–E, struct and class, nullable enabled and disabled | `dotnet build` (warnings not errors) |
| `side/` | the same code for `int`, `Guid`, `Id<User>`, `string`, `User` in A, C and E, compiled with warnings as errors; candidate B with one name per operation (`BSame.cs`) | `dotnet build` |
| `repo/` | raw Npgsql parameters and readers, event payloads in `jsonb` (old `T?` events read as `Option<T>` and back), EF Core 10 converters and LINQ; needs a PostgreSQL on port 55432 | `dotnet run` |
| `web/` | Minimal API binding (`int?` vs `Option<int>`), configuration binding (reflection and source-generated binder), JSON in a web app; JIT and Native AOT | `dotnet run`, `dotnet publish -c Release -r linux-x64`; switches `-p:OptParam=true`, `-p:OptCfg=true`, `-p:EnableConfigurationBindingGenerator=false` |
| `bench/` | BenchmarkDotNet 0.16.0-preview.2, `ShortRun` | `dotnet run -c Release -- --filter '*'` |

## 0. Summary

* **Recommended: C, a hand-written `[Union] readonly struct Option<T>` with the cases `T` and `None`. `default` is `None`.**
  It is the only candidate that meets all three requirements:
  * **Absence must be handled.** Using an `Option<T>` as a `T` is a compile **error** (CS0029, CS1503) in every
    nullable context. A `switch` without the `None` arm is CS8509, which is an error under warnings-as-errors.
  * **Same code for value and reference types.** `int`, `Guid`, `Id<User>`, `string` and `User` use identical
    code; only the type in the pattern changes. Generic code keeps the absence for every `T`.
  * **One call to and from `Tarol.Results`**: `option.ToResult(Error.NotFound(…))` and `result.ToOption()`.

  Cost: `Option<int>` is 8 B (the same as `int?`) and `Option<User>` is 16 B (`User?` is 8 B). It allocates nothing,
  and it runs as fast as `T?` (2.7 ns vs 2.6 ns for create + switch).
* **A (plain `T?`) does not meet the requirement.** Every check is a nullable *warning*: CS8602, CS8604 and CS8655 for
  classes, CS8629 for structs. They are errors under warnings-as-errors, but `!` silences them, they disappear in
  `#nullable disable` code, and **unconstrained generic code loses absence for structs**: `T?` with `T = int` is
  `int`, so `Find<int>()` returns `0` with no diagnostic. Struct and class code differ (`x.Value` vs `x!`, `HasValue` vs
  `is not null`).
* **B (the old extensions, fixed)** is A plus sugar. It adds no enforcement. One name per operation (`Map`) for every
  combination of class and struct needs **four static classes** (that is why the old code had `ClassExtensions2`), it
  still cannot be called from unconstrained generic code, and its `ToResult` is **ambiguous (CS0121)** with the one
  that `Tarol.Results` already ships. Rejected.
* **D (language `union Option<T>(T, None)`)** boxes **both** cases (24 B for `Some(int)` *and* for `None`; 3–6× slower).
  `default` and a `null` reference case crash a `switch` at run time (`SwitchExpressionException`) with no warning, and
  `Option<User>` cannot be deserialized (SYSLIB1227 / `JsonException`). Rejected.
* **E (`Result<T>` + `Error.NotFound`)** is right where absence is a **failure** (`GetById`), and it is the guidance for
  those cases. It is wrong for optional *data*: an optional property would serialize as `{"failure":{…}}`, every
  absence needs an error code, and absence cannot be told apart from other failures without matching on the code.
  It stays as guidance, not as the Option type.
* **Interop limits of C**, all verified. Bind `T?` at these edges and convert with `.ToOption()`:
  * A Minimal API `Option<int>` parameter is inferred as the body (400 on GET, under JIT and under the Request
    Delegate Generator).
  * The **configuration binder silently leaves `Option<int>` as `None`** (reflection binder), and the source-generated
    binder emits code that does not compile (CS1503).
* **JSON for C**: the converter on the type writes **`null` for `None` and the bare value for `Some`**, so the wire
  format is identical to `T?`. An absent property reads as `None`, and `JsonIgnoreCondition.WhenWritingDefault` omits
  `None`. It works with reflection, with the source generator and under Native AOT (0 IL warnings). Without the
  converter, the union contract fails at build time (SYSLIB1227).
* **Where it lives**: recommended **inside `Tarol.Results`** (namespace `Tarol.Results`). Both directions of the
  conversion are then instance members, users have one `using`, one analyzer and one skill. Its own package
  `Tarol.Options` (depending on `Tarol.Results`) also works. Nothing replaces the Primitives `global using`: libraries
  do not export global usings, and Primitives (step 6) imports `Tarol.Results` like any other file.
* **Analyzer**: the compiler leaves four gaps. Three are worth rules: reading the `object Value` (as TRES003), a
  null or property pattern on an `Option` (`o is { } x` is **always true** and gives back the Option), and an ignored
  `TryGetValue` result (the value is silently `default` for structs). The fourth is `Option<T>?`: double absence, low
  value, optional.

## 1. Inventory

### 1.1 Old code

`src/Options/Options/OptionCombinedNullableExtensions.cs` (233 lines, no XML docs on most members, empty csproj):

| Class | Receiver | Members |
|---|---|---|
| `OptionExtensions` | unconstrained `T?` (= `T` for structs) | `IsSome`, `IsNone` (`[NotNullWhen]` on the receiver), `Value` (`where T : notnull`, throws), static `T.Some(v)`, `T.FromNullable(v)`, `Match(some, none)`, `Or(fallback)` |
| `ClassExtensions` | `T? where T : class` | `Filter`, `Map<TResult : class>`, `Bind<TResult : class>`, static `T.None` |
| `ClassExtensions2` | `T? where T : class` | `Map<TResult : struct>`, `Bind<TResult : struct>` |
| `StructExtensions2` | `T? where T : struct` | `Map<TResult : class>`, `Bind<TResult : class>` |
| `StructExtensions` | `T? where T : struct` | `Filter`, `Map<TResult : struct>`, `Bind<TResult : struct>`, static `T.None` |

Tests: `tests/Options/Options` has 17 (8 for classes, 9 for structs). `tests/Options/Options.Persistence.Relational`
has 8 active and 4 commented out; it is not in the solution and does not compile (O-1).

Removed in `e4722c4` (for the record): `Option<T>` was a `sealed record` **class** `where T : struct` (an allocation per
value, structs only), with `ValueOrThrow`, `Match`, `AsNullable`, `IComparable`. It also had an EF Core
`OptionTValueConverter<T>` and a reflection `OptionJsonConverterFactory`.

### 1.2 Consumers

Searched in `CLorat/Tarol@6a62f81`:
`grep -rln --include=*.cs -E "Tarol\.Options|\.IsSome\b|\.IsNone\b|\.FromNullable\(|\bOption<"` over the whole tree,
excluding `src/Options`, `tests/Options` and `docs`, plus a search for `T.None`/`T.Some` static calls **[verified]**:

| Where | Use | Consequence |
|---|---|---|
| `src/Primitives/Primitives/GlobalUsings.cs:4` | `global using Tarol.Options;`, with a project reference in `Primitives.csproj:11` | no member is called; it only makes the extensions appear on every type in Primitives (O-4) |
| `templates/TarolSolution` (`Program.cs`, `GlobalUsing.cs`, two test projects, two csproj, `Directory.Packages.props`) | `Tarol.Options.Serialization.Json` and `Tarol.Options.Persistence.Relational` 0.0.53/0.0.56 | these packages were removed in `e4722c4`; the template is not buildable and is replaced in step 14 (plan) |
| `examples/`, `ekm/`, all other `src/` | none | — |

`e4722c4` itself rewrote the last real consumers (`Jobs.CronSetting`, `Job`) to `DateTimeOffset?`. Those projects no
longer exist in 6a62f81.

## 2. Review: old findings re-verified, new findings

| # | Finding | Evidence |
|---|---|---|
| O-1 | `tests/Options/Options.Persistence.Relational` references `src/Options/Options.Persistence.Relational`, which does not exist, and uses the removed `Option<int>`/`OptionTValueConverter<int>`. | **[verified]** `ls` (output.txt, `old/`) |
| O-2 | For value types the unconstrained `T?` is `T`: `int? x = null; x.Or(5)` has the static type `Nullable<Int32>`, `int.Some(42)` is `Int32`, `y.Match(v => …)` hands `v` as `Nullable<Int32>`. | **[verified]** `old/` |
| O-3 | `int? x = null; x.Value` calls `Nullable<T>.Value` (BCL message "Cannot read the Value property of a Nullable object…"), not the extension ("Option value was null."). The struct "custom exception" tests test the BCL. | **[verified]** `old/` |
| O-4 | `42.IsSome == true`, `"a".Or("b")`, `DateTime.Now.IsNone`: the members appear on every type. | **[verified]** `old/` |
| O-5 | Five classes to split overloads by constraint; empty csproj (no `IsAotCompatible`, no docs). | by reading |
| N-1 | Nullable analysis is not a guarantee: `!` is silent, `#nullable disable` code gets no diagnostic, `default(struct).RefField` is CS8602 but `new string[3][0].Length` is silent, and unconstrained generic `T?` loses absence for structs (`Find<int>()` returns `0`). | **[verified]** `diag/` A5, A6, A9, N1–N3; `probe/` "A generic Find" |
| N-2 | `Tarol.Results` already has `T?.ToResult(Violation)` for classes and structs. A second `ToResult` on `T?` is CS0121. | **[verified]** `diag/` B6i, B6s |
| N-3 | Every C# union exposes a public `object Value` (CS9386, see results.md N-1). On `Option<T>`, `o.Value` boxes and is `object`. | **[verified]** `diag/` C1v |
| N-4 | On a union, `o is { } x`, `o is var x` and `o is not null` match the **union struct itself**: `x` is the `Option<T>`, and `None is { }` is **true**. Code written for `T?` (`if (x is { } v)`) still compiles after a change to `Option<T>`, but it is always true. | **[verified]** `probe/` |
| N-5 | `TryGetValue(out T)` with the bool ignored compiles silently; for structs the value is `default`. For classes the out value is CS8602 when dereferenced (`[MaybeNullWhen(false)]`). | **[verified]** `diag/` C1t, C1t2 |
| N-6 | The `Option<T>` constructor's null check `!typeof(T).IsValueType && value is null` (the Results pattern) misses `Nullable<T>`: `Option<int?>` holding `(int?)null` is `Some`, and `switch` gives `Some 0`. `Option<int?>` is CS8714 (an error under warnings-as-errors), but the run time must also reject it. | **[verified]** `probe/` "Option<int?> (int?)null" |

## 3. Candidates compared

| # | Candidate | Probe definition |
|---|---|---|
| A | plain `T?` + `is { } v`, `??`, `?.`, nullable analysis | — |
| B | old extensions fixed: `where T : class` and `where T : struct` overloads that unwrap (`IsSome`, `Or`, `Match`, `Map`), namespace import only | `probe/Shapes.cs` namespace `Probe.B`; `side/BSame.cs` |
| C | `[Union] readonly struct Option<T> : IUnion where T : notnull` with `T _value; bool _isSome`, cases `T` and `readonly struct None`; `HasValue => true`; `default` = `None`; `[JsonConverter]` on the type | `probe/Shapes.cs`, `probe/Json.cs` |
| D | `public union DOption<T>(T, None);` | `probe/Shapes.cs` |
| E | `Result<T>` from `Tarol.Results` with `Error.NotFound` | the real package |

### 3.1 "Must be handled" **[verified]** (`diag/`, `dotnet build`; every warning below is an error under warnings-as-errors: `diag/` with `-p:TreatWarningsAsErrors=true` turns all 50 into errors)

| Misuse | A: `int?`/`Guid?`/`Id<User>?` | A: `string?`/`User?` | B | C `Option<T>` (any `T`) | D | E `Result<T>` |
|---|---|---|---|---|---|---|
| read the value without a check | `x.Value`: **CS8629** (warning) | `x.Length`: **CS8602** (warning) | as A | `int v = o;` **CS0029** / `o.Length` **CS1061** (errors) | CS0029 | CS0029 |
| `switch` without the absent arm | **CS8655** "null not covered" | **CS8655** | as A | **CS8509** "'None' is not covered" | CS8509 | CS8509 ("'Invalid' is not covered") |
| pass where a present value is required | **CS1503** (error) | **CS8604** (warning) | as A | **CS1503** (error) | CS1503 | CS1503 |
| assign to a non-nullable local | **CS0266** (error) | **CS8600** (warning) | as A | **CS0029** (error) | CS0029 | CS0029 |
| `!` / `default!` | silent | silent | silent | n/a: there is no `!` for an Option | n/a | n/a |
| `#nullable disable` file | `x.Value` silent; switch silent | silent | silent | **CS8509** and **CS0029** still reported | as C | as C |
| generic `T?` with `T = int` | absence lost, silent | — | cannot be called (constraint) | `Option<T>` keeps absence | as C | as C |
| `default` | `null` | `null` | `null` | `None`: correct by construction | `Value == null`: **`SwitchExpressionException`**, no warning | `Error.Uninitialized` |
| statement `switch` with a missing case | silent | silent | silent | silent | silent | silent |
| `TryGetValue` result ignored | — | — | — | silent (N-5) | silent | silent |
| `o.Value` (`object`) | — | — | — | silent (N-3) | silent | TRES003 |
| `o is { } x` | correct (unwraps) | correct | correct | **always true** (N-4) | same | same |

**Analyzer.** The compiler closes every case except the last four rows. Proposed rules, bundled with the package (D-10):

| Rule | Severity | Catches | Recommendation |
|---|---|---|---|
| read of `Option<T>.Value` | Warning | boxing and a value typed as `object` (N-3) | **yes** (in `Tarol.Results`: extend TRES003 to `Option<T>`) |
| `{ }`, `not null`, `null` or `var` pattern, `== null` / `!= null` on an `Option<T>` | Warning | a check that is always true or false (N-4); typical after a change from `T?` | **yes** |
| `TryGetValue(out T)` called as a statement (result discarded) | Warning | a struct value silently `default` (N-5) | **yes** |
| `Option<T>?` (`Nullable<Option<T>>`), `Option<Option<T>>` | Info/Warning | double absence; `Option<Option<T>>` collapses in the null JSON contract | optional; low value |
| statement `switch` without the `None` case | — | non-exhaustive statement switch | **no**: the same gap exists for `Result`. The docs say to use switch *expressions*; an exhaustiveness rule for statements would have to re-implement the compiler's subsumption |
| `!` on `T?` (for A) | — | — | **no**: noisy, and it fights the language for every nullable reference |

### 3.2 Same feel for struct and class **[verified]** (`side/Side.cs` compiles with warnings as errors; `side/BSame.cs`)

Each operation for `int` | `Guid` | `Id<User>` (struct) and `string` | `User` (class). ≠ marks where the code differs by
kind of type:

| Operation | A `T?` | C `Option<T>` |
|---|---|---|
| create | `int? x = 5; int? n = null;` · `User? u = user;` | `Option<int> x = 5; Option<int> n = Option.None;` · `Option<User> u = user;` |
| check | ≠ `x.HasValue` / `u is not null` (`x is not null` works for both) | `x.IsSome` |
| match | `x switch { { } v => …, null => … }` | `x switch { int v => …, None => … }` |
| map | `x is { } v ? v * 2 : null` · `u?.Name` (≠ lifted `x * 2` only for structs) | `x.Map(v => v * 2)` · `u.Map(v => v.Name)` |
| fall back | `x ?? 0` · `u ?? guest` | `x.GetValueOrDefault(0)` · `u.GetValueOrDefault(guest)` |
| unwrap | ≠ `x.Value` (CS8629 if unchecked) / `u!` (silent) | `x.GetValueOrThrow()` |
| to `Result<T>` | `x.ToResult(Error.NotFound("X.NotFound"))` (exists in Results) | `x.ToResult(Error.NotFound("X.NotFound"))` |
| from `Result<T>` | `r is int z ? z : null` (no one-call conversion) | `r.ToOption()` |
| generic `T` | ≠ `T?` is `T` for structs; needs a `class`/`struct` constraint per overload | `Option<T>` for every `T` |

B adds `IsSome`, `Or`, `Match` and `Map` for both kinds. One `Map` name for class→class, class→struct, struct→class
and struct→struct needs four static classes (`side/BSame.cs`; constraints are not part of the signature, CS0111
otherwise), and `Map` from a struct to `int?` is CS0452 (`bench/` first build). D has the same call site as C, but
`Map` and the rest would be extension members. E needs an error code to say "absent", and its `switch` arm is
`Violation`.

### 3.3 Size and allocations **[verified]** (`probe/`; `Unsafe.SizeOf`; `GC.GetAllocatedBytesForCurrentThread` over 1,000 iterations, half Some and half None)

| | `int` | `Guid` | `Id<User>` | `string` | `User` |
|---|---|---|---|---|---|
| A `T?` | 8 | 20 | 20 | 8 | 8 |
| C `Option<T>` (`T` + `bool`) | **8** | **20** | **20** | 16 | 16 |
| D language union | 8 (+24 B box per Some **and** per None) | 8 (+32 B box) | 8 (+32 B) | 8 (+24 B per None) | 8 (+24 B per None) |
| E `Result<T>` | 16 | 24 | 24 | 16 | 16 |

A generic struct has one layout for every `T`, so `Option<User>` cannot use `null` as `None` and stays at 16 B.

| Scenario (per operation) | A | B | C | D | E |
|---|---|---|---|---|---|
| create + check/switch, `int` | 0 | 0 (`Or`, `Match` with static lambdas) | 0 | **24 B** | 0 with a cached `Error`; **40 B** with `Error.NotFound(…)` per call |
| create + switch, `Id<User>` | — | — | 0 | **28 B** (avg. of 32 + 24) | — |
| `Map → Bind → Filter → GetValueOrDefault` (static lambdas) | — | — | 0 | — | — |
| `Match` with capturing lambdas | — | — | **136 B** JIT / 120 B AOT in the probe (closure + 2 delegates); 24 B in BenchmarkDotNet with one capture | — | — |
| `ToResult` / `ToOption` | 0 | — | 0 / 0 | — | — |

### 3.4 BenchmarkDotNet **[verified]** (`bench/`, `ShortRun`: compare ratios, not nanoseconds)

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| A_Int_Pattern (`int?`, `is { } v`) | 2.64 ns | 1.00 | – |
| B_Int_Or | 2.97 ns | 1.12 | – |
| **C_Int_Switch** | **2.74 ns** | 1.04 | – |
| D_Int_Switch | 8.24 ns | 3.12 | 24 B |
| E_Int_Switch (cached `Error`) | 3.03 ns | 1.15 | – |
| **C_Id_Switch** | **3.00 ns** | 1.14 | – |
| D_Id_Switch | 16.30 ns | 6.17 | 32 B |
| A_User_Pattern | 3.13 ns | 1.18 | – |
| **C_User_Switch** | **3.26 ns** | 1.23 | – |
| A_Chain (patterns) | 2.79 ns | 1.06 | – |
| B_Chain (`Map`, `Match`) | 4.02 ns | 1.52 | – |
| C_Chain (`Map → Bind → Filter → GetValueOrDefault`) | 4.05 ns | 1.53 | – |
| C_Match_Capturing | 9.46 ns | 3.58 | 24 B |
| C_Switch_SameLogic | 3.41 ns | 1.29 | – |
| C_ToResult | 3.06 ns | 1.16 | – |
| E_ToOption | 3.38 ns | 1.28 | – |
| A_ToResult (Results' `T?.ToResult`) | 3.14 ns | 1.19 | – |

The benchmark project moves into the repository as `tests/Options/Tarol.Options.Benchmarks` (or into the Results
benchmarks, if Option goes into `Tarol.Results`). It is not run in CI.

### 3.5 `default`, `null` and nullable flow **[verified]** (`probe/`, `diag/`)

| Question | C | D |
|---|---|---|
| `default(Option<int>)`, `default(Option<string>)` | `None`; `switch` takes the `None` arm; `IsSome == false` | `Value == null`; `switch { int v => …, None => … }` throws **`SwitchExpressionException`**, no warning |
| `Some(null)`: `new Option<string>(null!)`, `Option.Some<string>(null!)`, `Option<string> o = (string)null!` | `ArgumentNullException` in all three. Without `!`: **CS8604** at compile time (`diag/` C4b, C4i) | `Value == null`; `switch` throws |
| `Option.FromNullable((string?)null)`, `FromNullable((int?)null)` | `None` | — |
| `Option<string?>`, `Option<int?>` | **CS8714** (error under warnings-as-errors). At run time `Option<int?>` holding `null` is `Some` (N-6, fixed in the implementation) | — |
| `null` arm in a `switch` | accepted, never matches | needed by hand |
| flow state | `o is User u` / `case User u`: `u` is not null. `TryGetValue(out T? v)` has `[MaybeNullWhen(false)]`: `v` is not null in the `true` branch, CS8602 otherwise (C1t2). No `[MemberNotNullWhen]`: there is no typed `Value` (N-3) | same |

A (for comparison): `[NotNullWhen(true)]` on an extension receiver of `Nullable<T>` works: `x.IsSome ? x.Value : 0` has
no CS8629 (`diag/` B2i). So B's flow attributes are correct, but they are only warnings.

### 3.6 JSON **[verified]** (`probe/`: reflection with web defaults, source generator with camelCase, Native AOT; `web/`)

Two contracts were implemented for C as `JsonConverter<Option<T>>` (`probe/Json.cs`). The value is written through
`options.GetTypeInfo(typeof(T))`, so it works with the source generator.

| Case | A `T?` | **C, null contract** (`[JsonConverter]` on the type) | C, envelope | C without a converter (union contract) | D (union contract) |
|---|---|---|---|---|---|
| DTO with values | `{"id":1,"nick":"ada","age":36,"ref":"0000…"}` | **identical** | `{"id":1,"nick":{"value":"ada"},"age":{"value":36},…}` | — | `{"id":1,"nick":"ada","age":36}` |
| DTO, all absent | `{"id":1,"nick":null,"age":null,"ref":null}` | **identical** | `{"id":1,"nick":{},"age":{},"ref":{}}` | — | `None` → `{}`; `default` → `null` |
| read `{"id":1}` (property absent) | `null`s | **`None`** (constructor default) | `None` | — | `Value == null` (crashes a `switch`) |
| read `"nick":null` | `null` | `None` | — | — | — |
| `WhenWritingDefault` on the property | omits `null` | **omits `None`**: `{"id":1}` | omits | — | — |
| `DefaultIgnoreCondition = WhenWritingNull` | omits | does **not** omit (`None` is a struct value, not `null`) | — | — | — |
| `Option<User>` / `Option<Id<User>>` | — | `{"name":"Ada"}` / `null` | ok | **SYSLIB1227 at build time** (case types `User` and `None` are both JSON objects) | reflection: **`JsonException`** "JSON value type 'Object' is ambiguous" |
| `Option<Option<int>>` | — | `Some(None)` writes `null` and reads back as `None` (collapses) | distinct | — | — |
| source generator | ok | ok; the context must list `T` (`string`, `int`, `Id<User>` …), otherwise `NotSupportedException` "metadata for type 'System.String' was not provided" (`web/` GET /json, first run) | ok | build error | ok for `int`/`string` cases |
| Native AOT, reflection off | ok | **ok, 0 IL warnings, output identical to JIT** | ok (JIT only in the probe) | — | ok |

**As a DTO property and as a record field:** a positional record (`CDto(int Id, Option<string> Nick, …)`) and a class
with `init` properties both round-trip, and a missing property is `None`. Changing a field from `T?` to `Option<T>`
does not change its JSON (null contract). That matters for persisted events (step 7–9), where old events must still
read.

To reduce the "context must list `T`" failure, the implementation can fall back to package-owned source-generated
metadata for the common primitives (`string`, `int`, `long`, `Guid`, `DateTimeOffset`, `bool`, `decimal`, `double`),
the way `Tarol.Results` falls back for `Violation`.

### 3.7 AOT **[verified]**

`probe/` is published with `PublishAot=true`, `TrimmerSingleWarn=false` and `JsonSerializerIsReflectionEnabledByDefault=false`:
**0 IL warnings**. Its output equals the JIT output line for line, except the fixed 24 B that the JIT measurement adds
to every scenario (0 under AOT) and the capturing-`Match` size (136 → 120 B). `web/` (Minimal API with the Request
Delegate Generator, source-generated configuration binder, JSON context) publishes with **0 IL warnings**. No
candidate uses reflection. The probe's `JsonConverterFactory` (`MakeGenericType`) was **not** used: the open-generic
`[JsonConverter(typeof(OptionNullConverter<>))]` attribute is enough, as it is for `Result<T>` and `Id<T>`.

### 3.8 Interop **[verified]** unless marked (`web/`)

| Area | A `T?` | C `Option<T>` | What it would take for C |
|---|---|---|---|
| Minimal API query/route parameter that may be missing | `(int? page)`: missing → `null`, `?page=3` → `3` (JIT and AOT) | `(Option<int> page)`: **inferred as the body → 400** on GET, under JIT and under the Request Delegate Generator | bind `int?` and call `.ToOption()` (works, AOT). A `static bool TryParse(string?, IFormatProvider?, out Option<T>)` on `Option<T>` cannot parse an unconstrained `T` without type tests **[unverified]**; belongs in a later web package, like problem details (results.md Q-5) |
| JSON body | ok | ok (null contract; §3.6) | list `T` in the context |
| Configuration binding (`IOptions<T>`) | `int? Size` binds `20`; a missing key stays `null` (reflection and source-generated binder, AOT) | reflection binder: **`Option<int> Size` stays `None` although `Paging:Size=20` is set** (silent). Source-generated binder: treats `Option<int>` as a POCO (`Value`, `IsSome`, `IsNone`, `HasValue` keys) and emits **code that does not compile** (`BindingExtensions.g.cs: CS1503 cannot convert from 'object' to 'int'`) | options classes keep `T?`, documented in the README and skill. (The analyzer could also flag `Option<T>` properties on types passed to `Configure<T>`/`Bind`; not proposed.) |
| Npgsql store (step 8) | native: `null` ↔ `DBNull` | not native | writing: `o.TryGetValue(out var v) ? v : DBNull.Value`; reading: `reader.IsDBNull(i) ? Option.None : reader.GetFieldValue<T>(i)`. Event payloads in `jsonb` use the null contract, so no type handler is needed **[unverified]**; a small helper in the Npgsql package |
| `Tarol.Results` | `x.ToResult(violation)` exists; reverse: `r is T v ? v : null` | `o.ToResult(violation)`, `r.ToOption()`: one call each, 0 B (§3.3) | — |
| ASP.NET Core and other names | — | `None` and `Option<T>` compile next to the Web SDK implicit usings and `Microsoft.Extensions.Options` (`web/`). `System.CommandLine` has its own `Option<T>`: CS0104 in a file that imports both **[unverified]** | alias in such files |

### 3.9 Repository and serialization (the owner's question after round 1)

The owner liked C, but noted that the old `Option<T>` failed most often at the repository and in serialization. This
section covers why it failed then, and what C does at each edge, measured against a real PostgreSQL 16 (`repo/`).

**Why the old `Option<T>` failed** (by reading `e4722c4^`):

* It was a `sealed record` **class**. An absent JSON property or a non-initialized field was a `null` *Option*, which
  crashes at `opt.IsSome`: the absence moved one level up.
* It was `where T : struct`, so `Option<string>`/`Option<User>` had no JSON or EF support at all (the EF converter for
  classes is commented out).
* JSON was an envelope `{"Value":…}`. The reader expected exactly that shape and order and threw
  `JsonException("Invalid JSON for Option<T>")` for `null` or any other shape. It had to be registered as a
  **reflection** factory (`MakeGenericType` + `Activator.CreateInstance`), which is not AOT-safe and is easy to forget.
* EF: `OptionTValueConverter<T>` (structs only) per property; its test project was orphaned (O-1).

**C at each edge** (**[verified]** `repo/`, `probe/`, `web/` unless marked):

| Edge | Result | Consequence for the design |
|---|---|---|
| JSON, every serializer setup | the converter sits on the type (`[JsonConverter(typeof(…<>))]`): nothing to register, no reflection; reflection, source generator and AOT give the same JSON (§3.6) | keep the converter on the type |
| JSON wire format | `null` / bare value, **identical to `T?`**; absent property → `None`; a struct is never `null`, so the "null Option" crash cannot happen | null contract |
| event payloads in `jsonb` (step 8 shape: source-generated context, `NpgsqlDbType.Jsonb`) | **an old event written with `T?`** (`{"nick":"ada","age":null}`), a new event written with `Option<T>`, and an even older event **without the properties** (`{"id":…}`) all read as `Option<T>` (`Some(ada)`, `None`, `None`); the reverse (new event read by a `T?` record) also works | switching an event field between `T?` and `Option<T>` is wire-compatible in both directions: no upcaster needed |
| JSON with the source generator | the context must list `T` (`string`, `int`, `Id<User>` …): `NotSupportedException` otherwise (`web/`) | package-owned fallback metadata for primitives, plus a clear README rule |
| Npgsql parameter `Value = option` / `NpgsqlParameter<Option<int>>` | `InvalidCastException: Writing values of 'Option`1' is not supported` | the store never passes an Option to Npgsql: `Value = option.TryGetValue(out var v) ? v : DBNull.Value` works (or `(object?)option.ToNullable() ?? DBNull.Value`) |
| Npgsql `reader.GetFieldValue<Option<int>>(i)` | `InvalidCastException: Reading as 'Option`1' is not supported` | `reader.IsDBNull(i) ? Option.None : reader.GetFieldValue<T>(i)` works; a `GetOption<T>(i)` helper belongs in the Npgsql package (step 8) |
| EF Core 10 `ValueConverter<Option<T>, T?>` (the old approach) | `None` → **`23502 null value in column … violates not-null constraint`**: a struct property is required by convention; `IsRequired(false)` → **`InvalidOperationException: … cannot be marked as nullable/optional because the type … is not a nullable type`** | a struct Option cannot become a `NULL` column through a converter. EF is not in the plan (Npgsql thin store); for an EF read model, map a private `T?` field and expose `Option<T>` (verified: Some/None and `NULL` columns round-trip) |
| EF LINQ over an Option property | `p.Age == Option.Some(36)`, `p.Age.IsSome`, `p.Age.GetValueOrDefault(0) > 30`: all untranslatable; `EF.Property<int?>(p, "_age") > 30` works | queries use the `T?` field |
| Minimal API parameters, configuration binding | see §3.8: 400 / silently `None` / non-compiling generated binder | `T?` at those edges |
| AOT of the Npgsql parts | — | **[unverified]** here; Npgsql itself supports AOT with the slim builder; nothing Option-specific is involved because only `T`/`DBNull` reach Npgsql |

**Rule that follows:** *`Option<T>` in the domain, in handlers and in messages/events (JSON); `T?` at the storage and
transport edges that bind by reflection or by column type (SQL parameters and columns, ORM entities, query strings,
configuration).* The conversion at the edge is one call (`ToOption()` / `ToNullable()`), and the README and skill
state this rule. The edges in Tarol's plan are the Npgsql store and JSON, and both work.

**One more trap found here** (`probe/Trap.cs`, **[verified]**): `Option<int> F(bool b, int v) => b ? v : default;`
returns **`Some(0)`** for `b == false`. The conditional has the natural type `int`, so `default` is `0`, and only then is
it converted to `Option<int>`. `v ?? default` does the same. **`Result<int>` has the same trap** (`Success(0)`). For
reference types it is CS8604 plus an `ArgumentNullException`; for value types there is no diagnostic.
`b ? v : Option.None` is target-typed and correct. An analyzer rule can flag a `default` literal (or `default(T)`)
that reaches an implicit `T → Option<T>` / `T → Result<T>` conversion.

### 3.10 Fit with the plan

| Home | For | Against |
|---|---|---|
| **inside `Tarol.Results`** (namespace `Tarol.Results`) | `ToOption()` on `Result<T>` and `ToResult()` on `Option<T>` are instance members; one `using`, one package, one analyzer (TRES rules extended), one skill section; every later step references Results anyway | the package id `Tarol.Options` disappears; the Results package grows by ≈ 1 file + converter |
| own package `Tarol.Options` → `Tarol.Results` | the plan's layout; a separate package id and readme | it still depends on Results (for `ToResult`); `Result<T>.ToOption()` becomes an extension; a second analyzer project and skill; one more package to keep in step |
| `Tarol.Kernel` (step 4, D-7) | — | Kernel does not exist yet; Option is needed before it (Persistence, Changes) |
| dropped (A/E as guidance) | no new type | fails the requirement (§3.1, §3.2) |

**The Primitives `global using`** is not replaced. A library does not export global usings: `global using` in
Primitives only affected Primitives. When Primitives is migrated (step 6), it imports `Tarol.Results` where it uses
`Result` or `Option`.

## 4. Proposed API (candidate C)

```csharp
namespace Tarol.Results;                                   // or Tarol.Options (decision 6)

/// The "absent" case of Option<T>; carries nothing.
public readonly struct None : IEquatable<None>;            // default(None) is the only value

public static class Option
{
    public static None None { get; }                       // return Option.None;  (converts to any Option<T>)
    public static Option<T> Some<T>(T value) where T : notnull;              // ArgumentNullException for null
    // T? → Option<T>, same name for classes and structs (two overloads, narrow receivers, no global import):
    extension<T>(T? value) where T : class  { public Option<T> ToOption(); }
    extension<T>(T? value) where T : struct { public Option<T> ToOption(); }
    // Option<T> → T? for the edges (EF, config, Npgsql parameters):
    extension<T>(Option<T> option) where T : class  { public T? ToNullable(); }
    extension<T>(Option<T> option) where T : struct { public T? ToNullable(); }
}

[Union, JsonConverter(typeof(OptionJsonConverter<>))]   // Some(v) = v, None = null; absent property = None
public readonly struct Option<T> : IUnion, IEquatable<Option<T>> where T : notnull
{
    public Option(T value);                // ArgumentNullException for null, also for Nullable<T> null (N-6)
    public Option(None none);
    public bool IsSome { get; }  public bool IsNone { get; }
    [EditorBrowsable(Never)] public object Value { get; }   // union protocol (CS9386); boxes; analyzer warns
    [EditorBrowsable(Never)] public bool HasValue { get; }  // always true: None is a case, not null
    public bool TryGetValue([MaybeNullWhen(false)] out T value);
    public bool TryGetValue(out None none);
    public T GetValueOrThrow();                              // InvalidOperationException on None
    public T GetValueOrDefault(T fallback);
    public T GetValueOrDefault(Func<T> fallback);
    public static implicit operator Option<T>(T value);      // = Some
    public static implicit operator Option<T>(None none);    // = None
    public Option<TNew> Map<TNew>(Func<T, TNew> map) where TNew : notnull;
    public Option<TNew> Bind<TNew>(Func<T, Option<TNew>> bind) where TNew : notnull;
    public Option<T> Filter(Func<T, bool> predicate);
    public TOut Match<TOut>(Func<T, TOut> some, Func<TOut> none);   // prefer switch (capturing lambdas allocate)
    public Result<T> ToResult(Violation whenNone);           // one call to Results
    public Result<T> ToResult(Func<Violation> whenNone);
    // Equals: None == None == default; Some(a) == Some(b) when a equals b. ToString: "Some(42)" / "None".
}

// in Result<T> (same package) or as an extension (own package):
public Option<T> ToOption();                                 // the violation is dropped
```

User code:

```csharp
public Option<User> FindByEmail(string email) => _byEmail.TryGetValue(email, out var user) ? user : Option.None;

// the caller must handle None: a missing arm is CS8509 (an error under warnings-as-errors)
string greeting = FindByEmail(email) switch
{
    User user => $"Hello {user.Name}",
    None      => "Hello guest",
};

// absence as a failure: one call
public Result<User> GetByEmail(string email) => FindByEmail(email).ToResult(Error.NotFound("User.NotFound"));

// the same code for a value type
Option<int> age = user.Age;                                  // Option<int> property
int years = age.GetValueOrDefault(0);
Option<Id<User>> manager = user.ManagerId.Map(static id => id);

// edges stay T?: Minimal API and configuration bind T?, then convert
app.MapGet("/users", (int? page) => List(page.ToOption()));
```

JSON (null contract, web defaults):

```text
record Profile(Id<User> Id, Option<string> Nickname, Option<int> Age);
Profile(…, "ada", 36)        → {"id":"…","nickname":"ada","age":36}
Profile(…, None, None)       → {"id":"…","nickname":null,"age":null}
[JsonIgnore(Condition = WhenWritingDefault)] on Nickname → {"id":"…","age":null}
{"id":"…"}                   → Nickname = None, Age = None
```

Alternatives for the API surface (decision 4): with or without `Match`; with or without async (`MapAsync`/`BindAsync`
with `ValueTask` and `CancellationToken` like `Result<T>`, or "await, then switch"); `GetValueOrDefault` (Results'
name) vs `Or`; `Option.None` vs `default` vs `new None()` as the documented way to say "absent".

## 5. Planned projects

If Option goes into `Tarol.Results` (recommended):

| Project | Change |
|---|---|
| `src/Results/Tarol.Results` | + `Option.cs`, `OptionOfT.cs`, `None.cs`, `Json/OptionJsonConverter.cs`; `Result<T>.ToOption()`; README section; skill `skills/tarol-options/SKILL.md` (or a section in `tarol-results`) |
| `src/Results/Tarol.Results.Generators` | the agreed rules as TRES004+ (and TRES003 extended to `Option<T>`), `AnalyzerReleases.Unshipped.md` |
| `tests/Results/Tarol.Results.Tests` | + `Option*Tests` (the 17 old tests ported where they still make sense, findings, union, JSON, compiler behaviour) |
| `tests/Results/Tarol.Results.Generators.Tests`, `.AotSmoke`, `.PackageTests`, `.Benchmarks` | extended |

If it is its own package: `src/Options/Tarol.Options` (+ `Tarol.Options.Generators` if there is an analyzer) and
`tests/Options/Tarol.Options.{Tests,Generators.Tests,AotSmoke,PackageTests,Benchmarks}`.

Old tests: the 8 class tests and the 9 struct tests translate to `Option<T>` (create, `None`, `Map` across kinds, `Bind`,
`Match`, fallback, `Filter`). Dropped: the three "custom exception" struct tests that test the BCL (O-3; replaced by a
`GetValueOrThrow` test), and the whole `Options.Persistence.Relational` project (O-1; EF Core is not in the plan).

## 6. Not decided here

* Problem-details/web binding for `Option<T>` parameters: a later web package, together with Results' problem details.
* The Npgsql helpers: step 8.

## 7. Decisions for the owner

Asked in the session (2026-11-13), one question per decision with a recommendation.

Round 1: Q-2, Q-3 and Q-6 were answered as recommended. On Q-1 the owner liked C, but asked what happens at the
repository and in serialization, where the old `Option<T>` failed most. The answer is §3.9 (new probe `repo/` against
PostgreSQL 16). It also found the `? : default` trap, which affects `Result<T>` as well.

| # | Decision | Recommendation | Owner's answer |
|---|---|---|---|
| Q-1 | candidate | **C** | round 1: asked about repository and serialization (→ §3.9); round 2: **C with the rule "`T?` at the edges"** (SQL parameters/columns, ORM entities, query strings, configuration); Npgsql helpers come with step 8 |
| Q-2 | how "must be handled" is enforced | compiler (CS0029/CS1503/CS8509) **plus** three analyzer rules as Warnings: `Value` read, null/property pattern on an Option, discarded `TryGetValue` | **compiler + 3 analyzer rules** |
| Q-2b | the `? : default` trap (§3.9) | a fourth rule, for `Option<T>` and `Result<T>` | **yes, for both** |
| Q-3 | `default` / `null` | `default` = `None`; `Some(null)` throws `ArgumentNullException` (CS8604 at compile time); `ToOption()` maps `null` to `None`; `Option<T?>` stays CS8714 and throws at run time | **as recommended** |
| Q-4 | API surface | `switch` first; `IsSome`/`IsNone`, `TryGetValue`, `GetValueOrThrow`, `GetValueOrDefault`, `Map`, `Bind`, `Filter`, `Match`, `ToResult`, `ToOption`, `ToNullable`; **no async** in the first version | **as recommended** |
| Q-5 | JSON contract | null contract (wire = `T?`), absent property = `None`, `WhenWritingDefault` omits; fallback metadata for primitives | **as recommended** |
| Q-6 | where it lives | **inside `Tarol.Results`** | **inside `Tarol.Results`** |

Round 2 also recorded the rule that follows from Q-1: `Option<T>` in the domain, handlers, DTOs and events; `T?` at
SQL parameters and columns, ORM entities, query strings and configuration, converted with `ToOption()` / `ToNullable()`.

## 8. Changes against Tarol

Projects (Q-6: no `Tarol.Options` package; nothing under `src/Options` or `tests/Options`):

| Tarol-Net | Change | From |
|---|---|---|
| `src/Results/Tarol.Results` | + `None.cs`, `Option.cs` (`Option`, `OptionExtensions`, `OptionValueTypeExtensions`), `OptionOfT.cs`, `Json/OptionJsonConverter.cs`; `Result<T>.ToOption()`; README section; skill `skills/tarol-options/SKILL.md`; `tarol-results` skill rules 10–11 | `src/Options/Options` (rewritten on the agreed shape) |
| `src/Results/Tarol.Results.Generators` | TRES003 extended to `Option<T>`; new TRES004, TRES005, TRES006 | new |
| `tests/Results/Tarol.Results.Tests` | + `OptionTests` (the old tests ported, old name in a comment), `OptionUnionTests`, `OptionJsonTests`: 50 tests | `tests/Options/Options` (17) |
| `tests/Results/Tarol.Results.Generators.Tests` | + `OptionAnalyzerTests` (6), `OptionCompilerTests` (4: the compiler behaviour the design relies on) | new |
| `tests/Results/Tarol.Results.AotSmoke`, `.PackageTests` | + 7 and 3 Option checks | new |
| `tests/Results/Tarol.Results.Benchmarks` | + `OptionBenchmarks` (not run in CI) | `probes/options/bench` |

Old tests: 15 of the 17 are ported. The two struct tests `ValueOrThrow_Should_Throw_When_None` and
`Value_Property_Should_Throw_Custom_Exception_On_None` tested the BCL's `Nullable<T>.Value` (O-3). They are dropped and
replaced by `GetValueOrThrow_Should_Throw_OnNone`, which covers both kinds of type. `tests/Options/Options.Persistence.Relational`
(8 active tests, 4 commented out) is not ported: it does not compile (O-1), and EF Core is not in the plan (§3.9).

Public API and behaviour (every change is deliberate):

| Tarol (6a62f81) | Tarol-Net | Why |
|---|---|---|
| no option type; extension members on unconstrained `T?` in five static classes | `[Union] readonly struct Option<T> : IUnion, IEquatable<Option<T>> where T : notnull` with the cases `T` and `None` | Q-1; must-handle (§3.1), the same API for both kinds (§3.2) |
| `IsSome`, `IsNone` on every type, `[NotNullWhen]` on the receiver | `Option<T>.IsSome`, `IsNone` | O-4 |
| `Value` (extension; shadowed by `Nullable<T>.Value`) | `GetValueOrThrow()`; `Value` is the union's `object` (TRES003) | O-3, N-3 (CS9386) |
| `T.Some(v)` (returns `T` for structs), `T.FromNullable(v)`, `T.None` | `Option.Some(v)`, `Option.None`, implicit from `T` and `None`, `T?.ToOption()` | O-2 |
| `Or(fallback)` (does not unwrap structs) | `GetValueOrDefault(T)`, `GetValueOrDefault(Func<T>)` | O-2; Results' names |
| `Match(some, none)` (struct `v` is `T?`) | `Match` (documented after `switch`) | O-2 |
| `Map`, `Bind` in four constraint-split classes; `Filter` | `Map`, `Bind`, `Filter` on `Option<T>` for every `T` | O-5 |
| — | `TryGetValue(out T)` / `TryGetValue(out None)`, `ToResult(Violation)`, `ToResult(Func<Violation>)`, `Result<T>.ToOption()`, `ToNullable()`, `==`, `ToString()` = `Some(x)` / `None` | Q-4 |
| `null` = absent | `default` = `None`; `null` as a value throws `ArgumentNullException`, also for `Nullable<T>` (N-6) | Q-3 |
| no JSON | `[JsonConverter(typeof(OptionJsonConverter<>))]` on the type: the value or `null`; package metadata for primitives | Q-5 |
| `global using Tarol.Options;` in Primitives | nothing: libraries do not export global usings | §3.10 |
| — | TRES004 (null test on an Option), TRES005 (ignored `TryGetValue`, also `Result`), TRES006 (`default` becomes `Some`/`Success`, also `Result`), TRES003 for `Option<T>.Value` | Q-2, Q-2b |
| no `IsAotCompatible`, no docs | part of `Tarol.Results` (AOT, XML docs, skill) | O-5 |

`TRES005` and `TRES006` also report `Result<T>` code. That is a new warning for code that compiled warning-free with
Step 2: `r.TryGetValue(out int v);` as a statement, and `ok ? value : default` assigned to a `Result<T>`.

## 9. Suppressions and analyzer configuration

| Where | Rule | Reason |
|---|---|---|
| `Option` (static class), `Option<T>` | `[SuppressMessage]` CA1716 "Identifiers should not match keywords" | the owner chose the name (Q-1); `Option` is a VB keyword only, and Tarol targets C# (as for `Error`, results.md §8) |
| `Option<T>.HasValue` | `[SuppressMessage]` CA1822 "Mark members as static" | the C# union protocol needs an instance `HasValue` |
| `OptionUnionTests` | `#pragma warning disable TRES003`, `TRES004` | the tests check the very behaviour these rules warn about |
| `OptionTests.NullableValueType_Should_Throw_WhenNull` | `#pragma warning disable CS8714` | the test checks the run-time guard behind the compiler warning (N-6) |
| `Tarol.Results.Generators.Tests` | `LanguageVersion.Preview` | unchanged from Step 2 (Roslyn 5.9 calls unions preview) |

No `NoWarn` and no `.editorconfig` change. No new package versions.

`OptionValueTypeExtensions` is a second static class only because C# cannot overload `ToNullable(this Option<T>)` on the
`class`/`struct` constraint alone (CS0111); the call site is the same for both kinds of type.

## 10. Verification (SDK 11.0.100, 2026-11-13)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | clean (exit 0) |
| `dotnet build Tarol.slnx -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | **283 passed**, 0 failed: Results 184 (+50 Option), Results analyzers 17 (+10), StronglyTypedId 62 + 17, shared harness 3 |
| `eng/aot-smoke.sh` | `Tarol.Results.AotSmoke`: 0 IL warnings, 22/22 native checks (7 new: sizes, switch, `default`, no allocation, Result conversions, JSON value-or-null, missing property, primitives without a context entry) |
| `eng/verify-packages.sh` | OK; the package contains `skills/tarol-options/SKILL.md` next to `skills/tarol-results/SKILL.md`, and the consumer lists both in `project.assets.json` |
| analyzers reach a package consumer | a temporary file in `Tarol.Results.PackageTests` with the four traps → `error TRES003`, `TRES004`, `TRES005`, `TRES006` (warnings as errors), then removed |
| README and skill samples | compiled in a Web SDK project with warnings as errors and the bundled analyzer: 0 warnings |

```
Tarol.Results 0.0.0-alpha.0.8
    LICENSE
    README.md
    Tarol.Results.nuspec
    analyzers/dotnet/cs/Tarol.Results.Generators.dll
    lib/net11.0/Tarol.Results.dll
    lib/net11.0/Tarol.Results.xml
    skills/tarol-options/SKILL.md
    skills/tarol-results/SKILL.md
== tests/Results/Tarol.Results.PackageTests/Tarol.Results.PackageTests.csproj
ok   switch over the package's union
…
ok   Option switch over T | None
ok   Option to Result and back
ok   Option JSON is the value or null
ALL CHECKS PASSED
```

BenchmarkDotNet on the shipped package (`dotnet run -c Release --project tests/Results/Tarol.Results.Benchmarks -- --filter '*OptionBenchmarks*'`,
`ShortRun`, same machine as §3.4; compare ratios):

| Method | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Nullable_Int_Pattern (`int?`, baseline) | 4.02 ns | 1.00 | – |
| Option_Int_Switch | 3.89 ns | 0.98 | – |
| Option_Guid_Switch | 3.83 ns | 0.96 | – |
| Nullable_String_Pattern | 3.18 ns | 0.80 | – |
| Option_String_Switch | 3.21 ns | 0.81 | – |
| Option_Chain (`Map → Bind → Filter → GetValueOrDefault`) | 4.50 ns | 1.13 | – |
| Option_Match_Capturing | 9.30 ns | 2.34 | 24 B |
| Option_Switch_SameLogic | 4.11 ns | 1.03 | – |
| Option_ToResult | 3.56 ns | 0.90 | – |
| Result_ToOption | 3.57 ns | 0.90 | – |

Found during implementation: the AOT smoke loop first reported 136 B. These were the one-time delegate allocations of
its static lambdas (they are cached on first use), not per-call costs; the loop now runs once before it is measured,
like the unit test.

## 11. Skill evaluation checklist

Run each task with a frontier model in a fresh console project that references the packed package, once with the
skills copied to `.agents/skills/` and once without. Record correctness and token cost. Keep the skill only if it is at
least as correct and not more expensive.

`tarol-options`:

1. "Add `FindByEmail` that may not find a user, and a greeting that handles the missing case" → returns `Option<User>`,
   `? user : Option.None`, a `switch` with `User` / `None` arms; no `User?`, no `null` checks.
2. "Make the user's age optional" (a value type) → `Option<int> Age`; the same API as for a reference type;
   `GetValueOrDefault(0)` or `switch`.
3. "Return 404 when it is missing" → `ToResult(Error.NotFound("User.NotFound"))`, then the Results `switch`.
4. "Add an optional `page` query parameter to the endpoint" → binds `int? page` and calls `page.ToOption()`; does not
   declare `Option<int> page`.
5. "Save and load the optional age with Npgsql" → `ToNullable()` / `DBNull.Value` and `IsDBNull`; no `Option` handed to Npgsql.
6. "Serialize the profile in a Native-AOT app" → a context listing the DTO; expects `"age":null`; adds non-primitive `T`s.
7. "Why TRES006?" → `ok ? v : default` becomes `Some(0)`; write `Option.None`.
8. Must not: use `is { }`/`!= null` on an Option, read `.Value`, ignore `TryGetValue`, write `Option<string?>`, reference a
   `Tarol.Options` package or the old `T?` extensions (`int.Some`, `User.None`, `Or`), put `Option<T>` in an `IOptions<T>`
   class or an EF entity column, decompile the assembly or search the web.

Status: checklist written; **not yet run** (needs a model-evaluation harness outside this repository).

## 12. Open items

* **Npgsql helpers** (`GetOption<T>(ordinal)`, a parameter helper) belong in the Npgsql package (step 8), with
  `Option<T>` fields in events covered by its integration tests.
* **Minimal API binding** of `Option<T>` parameters (a `TryParse`/`BindAsync` story) and problem details: a later web package.
* **Statement `switch`** without the `None` case stays silent (also for `Result`); documented, no rule (§3.1).
* **`Option<T>?` / `Option<Option<T>>`**: no rule (Q-2 chose three rules plus Q-2b); the JSON collapse is documented.
* **Skill evaluation** (§11) is not run yet.
