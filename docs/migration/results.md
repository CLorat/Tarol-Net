# Step 2: Results

Origin: `CLorat/Tarol@6a62f81`: `src/Results/Results`, `tests/Results/Results.UnitTests` (80 tests, all pass on SDK 11.0.100).
Old review: `docs/review/ids-results-options.md` (findings R-1 … R-11, §7 `[Union]` sketch),
`docs/review/net11-event-generation.md` (§1 rows 1, 2, 3, 5, 6; §2.5; D-9), `docs/review/ROADMAP.md` (D-9).

**Status: evaluation stage.** This note gives the review, the union experiments with numbers, and the proposed
API. The design decisions in [§6](#6-decisions-for-the-owner) are open until the owner answers them. The
implementation stage then fills in §7 (changes against Tarol), §8 (suppressions), §9 (verification) and §10 (skill checklist).

Evidence labels follow [conventions](../architecture/conventions.md): **[verified]** = reproduced here (probe
and command given), **[unverified]** = not tried here. Every probe is in [`probes/results/`](probes/results/)
(`*.csproj.txt` files: copy a folder **outside** the repository, rename to `.csproj` and run, so the
repository's props do not apply). The complete console output is in
[`probes/results/output.txt`](probes/results/output.txt). Environment: SDK 11.0.100, .NET 11.0.0, linux-x64, Intel Xeon 2.7 GHz, 4 cores.

| Probe | What it shows | Command |
|---|---|---|
| `old/` | the real `Tarol.Results` assembly: R-1 … R-7, R-10, sizes | `dotnet run -c Release` (set `TarolOldRepo` to a clone of `CLorat/Tarol`) |
| `probe/` | shapes A, B, C (two layouts), D, E: sizes, allocations, `default`, JSON (reflection and source-generated) | `dotnet run -c Release`; AOT: `dotnet publish -c Release -r linux-x64 -p:NoReflJson=true` |
| `diag/` | compiler diagnostics: exhaustiveness, `null` arm, generic collisions, closed hierarchies | `dotnet build` (warnings not errors) |
| `xasm/` | `closed` across assemblies; `Result<object>` / `Result<Failure>` runtime semantics | `dotnet run` |
| `val/` | can a `[Union]` keep a typed `Value`? | `dotnet build` |
| `web/` | ASP.NET Core minimal API: `TypedResults`, problem details, JSON envelope, `IResult` clash, the four shapes side by side (`Compare.cs`) | `dotnet publish -c Release -r linux-x64`, then `curl` |
| `show/` | the same use case in A, M1, M2 and D: user code, HTTP output and the result as JSON (§3.9) | `dotnet publish -c Release -r linux-x64`, then `curl` |
| `m2/` | exhaustiveness and size of `T \| closed Failure { Invalid, Error }` | `dotnet run -c Release` |
| `bench/` | BenchmarkDotNet 0.16.0-preview.2, `ShortRun` (0.15.8 throws `GetRuntimeVersion not implemented` on net11) | `dotnet run -c Release -- --filter '*'` |

## 0. Summary

* **Recommended shape: C, a hand-written `[Union] readonly struct Result<T>`** holding the value and **one**
  reference (`null` = `default`, a private sentinel = success, otherwise the failure). It is 16 B for
  `Result<int>` and `Result<string>`, 24 B for `Result<Guid>`/`Result<Id<X>>` (today 48/56/64 B), allocates
  nothing, pattern-matches exhaustively (`r switch { User u => …, Violation v => … }`, a missing arm is CS8509),
  and makes `default` a well-defined failure (`Violation.Uninitialized`) that every `switch` already handles.
  The non-generic `Result` is 8 B. A `Map → Bind → Map → Match` chain is 9× faster than today (5.4 ns vs 48.6 ns).
* **Rejected: B (language `union`)** boxes every value-type success (24 B per `Result<int>`, 32 B per
  `Result<Id<X>>`) and `default` crashes a `switch` at run time with no compiler warning.
* **Not recommended: D (`closed` failure hierarchy)**: exhaustive over failure kinds, but users cannot add a
  kind in their own assembly (CS9382), the case names `NotFound`/`Conflict` collide with
  `Microsoft.AspNetCore.Http.HttpResults` (CS0104), every handler must list every kind, and JSON needs a
  `$type` discriminator. A `ViolationType` enum on a sealed `Violation` class gives the HTTP mapping without those costs.
* **The price of any `[Union]` shape**: the union protocol requires a public `object? Value` (CS9386), so
  today's typed `result.Value` cannot stay. Value access moves to patterns, `TryGetValue(out T)` and
  `GetValueOrThrow()`.
* **JSON**: the built-in union contract (raw active case) cannot work for `Result<SomeObject>` (SYSLIB1227 at
  build time with the source generator, `JsonException` at run time with reflection). An explicit envelope
  `{"value": …}` / `{"failure": {…}}` through an open-generic `[JsonConverter]` works with the source
  generator and under Native AOT, and silences SYSLIB1227.

## 1. Inventory

### 1.1 Old code (`src/Results/Results`, 5 files, ≈ 78 KB with XML docs)

| Type | Shape | Members |
|---|---|---|
| `Result` | `readonly record struct` (`bool`, `Violation`) | `IsSuccess`, `IsFailure`, `Violation`; `Success()`, `Failure(Violation)`, implicit from `Violation`; `Combine(params Result[])`, `Combine(IEnumerable<Result>)` (both aggregate); `Map`, `MapAsync` (Task), `Bind`, `BindAsync` (Task), `Match`, `Switch`, `Tap`, `TapError` |
| `Result<TValue>` | `readonly record struct` (`TValue?`, `Violation`, `bool`) | the above plus `Value` (throws on failure), `[MemberNotNullWhen(true, nameof(Value))] IsSuccess`, implicit from `TValue` and `Violation`, `ToResult()`, `TryGetValue`, `TryGetError`, `MapAsync`/`BindAsync`/`MatchAsync`/`EnsureAsync` (ValueTask), `GetValueOrDefault(TValue)`, `GetValueOrDefault(Func<Violation,TValue>)`, `Ensure` |
| `Violation` | `readonly record struct`, 40 B | `required string Code`, `required ViolationType Type`, `Detail`, `PropertyName`, `IReadOnlyList<Violation>? InnerErrors`; `None`; factories `Validation`, `NotFound(code)`, `NotFound<TEntity>(object id)`, `Conflict`, `Unauthorized`, `Forbidden`, `Internal`, `Aggregate`; `ToHttpStatusCode()` |
| `ViolationType : byte` | enum | `Validation = 0` … `Timeout = 10`, `Custom = 255` |
| `IResult`, `IResult<out TValue>` | interfaces | `IsSuccess`, `IsFailure`, `Violation`; `Value` |
| `ResultExtensions` | static | `Combine<T1,T2>`, `Combine<T1,T2,T3>` (first failure), `Sequence` (aggregates), `ToResult` for `T?` (class and struct), `ValueTask<Result<T>>` extensions `Map`, `Bind`, `BindAsync`, `Match`, `Tap`, `TapError`, `Ensure` |

`Results.csproj` references `StronglyTypedId` only for a `<see cref>` (R-11). No generator.

### 1.2 Consumers in Tarol and what the later steps need

Searched with `grep -rn -E 'Result<|Result\.|Violation|IResult|IsSuccess' src gen examples` in `CLorat/Tarol`.

| Consumer (step) | Uses | Needs from Tarol.Results |
|---|---|---|
| Persistence (8): `IAggregateRootRepository.GetById`, `IUnitOfWork`, `IUnitOfWorkScope.ExecuteAsync`, `IRepositoryInterceptor`, `PersistenceErrors` | `ValueTask<Result<T>>`, `Task<Result<T>>`, `ValueTask.FromResult(Result.Success())`, `result.IsFailure`, `result.Violation.Code`, `Violation.NotFound/Internal` | cheap `ValueTask<Result<T>>`; a failure's code without unwrapping; a cached success |
| Sagas (9): `SagaFaulted(Violation, …)` event, `ChangeFailed<TChange>(TChange, Violation)`, `SagaStep.Fail(Violation)`, `Saga.Fault`, `IChangeDispatcher.DispatchAsync → Task<Result>` | `Violation` **inside persisted events**; `Violation?` fields | a stable, AOT-safe **JSON contract for `Violation`**; a nullable `Violation` (a reference type makes `Violation?` cheap) |
| DomainEvent / Changes (7): `IDomainEventDispatcher.Dispatch → ValueTask<Result>` | non-generic `Result` | cheap non-generic `Result` |
| Validations (5) | project reference only (no code uses it yet) | aggregation of many failures with property names; the aggregate's type |
| CQRS (10): `CqrsDispatcherGenerator` emits `global::Tarol.Results.IResult` as fallback response type | a type name in a string | nothing that boxes (R-9); a non-generic `Result` as "no payload" response |
| TestUtility `Sagas.Testing.FakeChangeDispatcher` | `Result.Success()`, `Result.Failure(Violation.Internal(…))` | factories |

No consumer uses `IResult` at run time, `Switch`, `MatchAsync` or `GetValueOrDefault(Func)` (grep).

## 2. Review: old findings re-verified, new findings

All R-findings were reproduced against the **real** old assembly (`probes/results/old`, `dotnet run -c Release`) **[verified]**:

| # | Result of the probe |
|---|---|
| R-1 | `default(Result)`: `IsFailure=True`, `Violation.Code is null`, `Type=Validation`; same for `default(Result<int>)` |
| R-2 | `Violation.None.ToHttpStatusCode()` = 400; `Result r = Violation.None` is a failure |
| R-3 | `Result<string> r = (string?)null!` → `IsSuccess=True`, `Value is null` |
| R-4 | serializing a failed `Result<int>` throws `InvalidOperationException`; a serialized success deserializes as a **failure** (`IsSuccess=False`) |
| R-5 | `Result<Violation> r = violation` is CS0457; `Result<object> r = (object)violation` is a **success** |
| R-6 | `Combine(NotFound, Forbidden)` → `Type=Validation`, HTTP 400; two equal aggregates are not `Equals` |
| R-7 | `Combine<T1,T2>(NotFound a, Conflict b)` → code `a`, no inner errors (first failure), while `Result.Combine` aggregates |
| R-8 | by reading: `Result.MapAsync/BindAsync` take `Func<Task<…>>`, `Result<T>` takes `ValueTask`; no `CancellationToken` anywhere |
| R-9 | `using Tarol.Results;` at file level in a Web SDK project (global `using Microsoft.AspNetCore.Http;`) → **CS0104** "'IResult' is an ambiguous reference" (`probes/results/web`, temporary `Clash.cs`) |
| R-10 | `Violation.NotFound<string>(Guid.NewGuid())` allocates 304 B (box + two strings) |
| R-11 | by reading: `Results.csproj` → `StronglyTypedId` for one `cref` |
| sizes | `Violation` 40 B; `Result` 48 B; `Result<int>` 48 B; `Result<string>` 56 B; `Result<Guid>` 64 B (the old review's "≈64 B" holds) |

New findings from this evaluation:

| # | Finding | Evidence |
|---|---|---|
| N-1 | A `[Union]` type must have a **public instance `object? Value`**; an explicit `IUnion.Value` plus a typed `public T Value` is **CS9386**. Any union shape therefore drops today's typed `result.Value` (and `IResult<T>.Value`). Old code `var u = r.Value;` still compiles and silently becomes `object`. | **[verified]** `val/` |
| N-2 | `Match` with a **capturing** lambda allocates a closure and a delegate per call: 80 B (JIT) / 72 B (AOT) per call, 7.4 ns vs 1.4 ns for the equivalent `switch`. Static lambdas allocate nothing. | **[verified]** `probe/`, `bench/` |
| N-3 | Sagas persist `Violation` inside events (`SagaFaulted`, `ChangeFailed<T>`). Today `Violation` has no JSON contract; with `InnerErrors` as `IReadOnlyList<Violation>?` and the enum as a number it round-trips by accident in reflection mode only. Step 9 needs a deliberate, AOT-safe contract. | by reading |
| N-4 | Names of failure cases in a closed hierarchy (`NotFound`, `Conflict`) collide with `Microsoft.AspNetCore.Http.HttpResults.NotFound`/`Conflict` in endpoint files: CS0104, then CS8121. | **[verified]** `web/Compare.cs` |
| N-5 | No compiler or analyzer warns about a **discarded** `Result` (`repo.Save(x);`) or a **statement** `switch` that misses a case. | **[verified]** `diag/` L11, L14 |
| N-6 | `Result.Combine(params Result[])` allocates the array on every call; C# 13 `params ReadOnlySpan<Result>` avoids it. | by reading |

## 3. Shapes compared

| # | Shape | Definition used in the probes (`probe/Shapes.cs`) |
|---|---|---|
| A | today's record struct | copy of the 6a62f81 layout (`T?`, `Violation` struct, `bool`), plus the real assembly in `old/` |
| B | language union | `public union BResult<T>(T, Failure);` with `Failure` a sealed record class |
| C | hand-written `[Union]` struct | **C1** (review sketch): `T`, `Failure?`, `bool`. **C2** (recommended layout): `T` plus one `Failure?` state reference: `null` = `default`, a private static sentinel = success, otherwise the failure. Both: `HasValue => true`, `TryGetValue(out T)`, `TryGetValue(out Failure)`, `Value` maps `default` to `Failure.Uninitialized`, constructors reject `null` |
| D | C2 + closed failures | `public closed record Problem(string Code, …)` with sealed `NotFound`, `Conflict`, `Invalid`, `Uninitialized` |
| E | C2 + explicit JSON envelope | C2 with `[JsonConverter(typeof(EResultConverter<>))]` (`probe/Conv.cs`) |

### 3.1 Size **[verified]** (`Unsafe.SizeOf`, `probe/`)

| | `Result<int>` | `Result<Guid>` | `Result<Id<User>>` | `Result<string>` | `Result` |
|---|---|---|---|---|---|
| A (today) | 48 | 64 | 64 | 56 | 48 |
| B language union | 8 (+24 B box per success) | 8 (+32 B box) | 8 (+32 B box) | 8 | 8 |
| C1 `T` + ref + `bool` | 16 | 32 | 32 | 24 | — |
| **C2 `T` + one ref** | **16** | **24** | **24** | **16** | **8** |
| D | 16 | 24 | 24 | 16 | — |

### 3.2 Allocations **[verified]** (`GC.GetAllocatedBytesForCurrentThread`, 1,000 iterations after warm-up; JIT and Native AOT give the same numbers except where noted)

| Scenario | A | B | C1 | C2 | D |
|---|---|---|---|---|---|
| create success + `switch` (`int`) | 0 (`Match`, static lambdas) | **24,000** | 0 | 0 | 0 |
| create success + `switch` (`Id<User>`) | — | **32,000** | — | 0 | — |
| create failure + `switch` | — | 0 | — | 0 | — |
| `Map → Bind → Map → Match` (static lambdas) | 0 | — | — | 0 | — |
| `Match` with a capturing lambda | — | — | — | 80,000 (AOT 72,000) | — |

### 3.3 BenchmarkDotNet **[verified]** (`bench/`, `ShortRun`, so the error columns are wide; compare ratios, not nanoseconds)

| Method | Mean | Allocated | Note |
|---|---:|---:|---|
| A_Create_Switch (baseline) | 16.05 ns | – | create `Result<Id<User>>`, test it |
| B_Create_Switch | 7.68 ns | 32 B | boxing |
| C_Create_Switch | **1.71 ns** | – | |
| A_Fail_Match | 3.84 ns | – | |
| C_Fail_Switch | **1.01 ns** | – | |
| C_Fail_Match (static lambdas) | 1.69 ns | – | |
| A_Chain (`Map → Bind → Map → Match`) | 48.56 ns | – | 64 B copies per step |
| C_Chain | **5.42 ns** | – | |
| C_Match_Capturing | 7.43 ns | 24 B | closure + delegate (N-2) |
| C_Switch_Capturing | **1.39 ns** | – | |
| A_AsyncPipeline (2 × `BindAsync`, completed `ValueTask`) | 131.0 ns | – | |
| C_AsyncPipeline | **74.6 ns** | – | |
| A_AsyncYield (1 × `BindAsync` that yields) | 1,741 ns | 718 B | async state machine boxes hold the result |
| C_AsyncYield | 1,412 ns | **452 B** | smaller state machines |

The benchmark project moves into the repository as `tests/Results/Tarol.Results.Benchmarks` in the
implementation stage (not run in CI).

### 3.4 Exhaustiveness and developer experience **[verified]** (`diag/`, `xasm/`)

| Question | B | C (C1/C2) | D |
|---|---|---|---|
| `r switch { T v => …, Failure f => … }` compiles without a `null` arm? | yes | yes | yes (listing the leaves) |
| missing case | CS8509 (error under TWAE) | **CS8509** ("'Failure' is not covered") | **CS8509** ("'Uninitialized' is not covered") |
| `null` arm | accepted, never needed by the compiler | accepted; never matches (`HasValue => true`) | same |
| `default(Result<int>)` in a `switch` without `null` arm | **`SwitchExpressionException` at run time**, no warning (CS8655 appears only after the code tested `Value is null`) | **matches the `Failure` arm** with `Failure.Uninitialized` | matches the `Uninitialized` arm |
| statement `switch` with a missing case | no diagnostic (all shapes) | no diagnostic | no diagnostic |
| `Result<Failure>`: implicit conversion | — | **CS0457** (ambiguous) | — |
| `Result<Failure>`: `new(failure)` | — | silently picks the `Failure` constructor → a **failure** | — |
| `Result<object> r = (object)failure` | `switch` takes the `Failure` arm (runtime type) | a **success** whose value is a `Failure`; `r is Failure` is **false** | — |
| user-defined failure kind in another assembly | n/a | any `Violation` code | **CS9382** "cannot use a closed type from another assembly as a base type" |
| ASP.NET Core name collisions | none | none (`Violation`) | **CS0104** for `NotFound`, `Conflict` (N-4) |
| typed `result.Value` | impossible (CS9386 applies to every union) | impossible (N-1) | impossible |
| discarded result `M();` | no diagnostic | no diagnostic | no diagnostic |

Side by side, the same validation → load → endpoint flow (all four compile in `web/Compare.cs`, abridged):

```csharp
// A (today): flag checks, delegate-based Match, typed Value
var name = ValidName(cmd.Name);
if (!name.IsSuccess) return name.Violation;
var user = await repo.Get(cmd.Id);
return user.Map(u => new User { Name = name.Value });                       // capturing lambda allocates
… (await Handle(repo, cmd)).Match<Results<Ok<User>, ProblemHttpResult>>(u => TypedResults.Ok(u), v => …);

// C (recommended): patterns, no delegates, exhaustive
if (ValidName(cmd.Name) is Violation invalid) return invalid;
return await repo.Get(cmd.Id) switch { User => new User { Name = cmd.Name }, Violation v => v };
… await Handle(repo, cmd) switch { User u => TypedResults.Ok(u), Violation v => v.ToProblem() };

// D (closed): every handler lists every kind; ASP.NET names must be qualified
… switch { User u => TypedResults.Ok(u), Probe.NotFound nf => …, Probe.Conflict c => …, Invalid i => …, Uninitialized u => … };

// B (language union): like C, but each success of a value type boxes, and default needs a null arm by hand
```

### 3.5 `default(Result)` (R-1)

| Option | How | Compiler support | Verdict |
|---|---|---|---|
| impossible | no way in C#: `default(T)` exists for every struct, arrays and `ValueTask<Result>` defaults included | — | — |
| detectable / throws | every member throws on `default` | none; a `switch` would throw from `TryGetValue` | rejected: turns a bug into a crash far from its cause |
| `null` case (B, C with `HasValue = false`) | `Value == null`, pattern `null` | **none**: no warning for a missing `null` arm; run-time `SwitchExpressionException` | rejected |
| **well-defined failure** | `default` reads as `Violation.Uninitialized` (`Type = Internal`, `Code = "Result.Uninitialized"`, detail explains it) | **full**: every exhaustive `switch` already handles it in its failure arm; `IsSuccess` is `false`; HTTP 500; JSON writes the failure | **recommended** **[verified]** `probe/` (`C2 default: … fail Result.Uninitialized`) |

For the non-generic `Result` the same rule applies: `default(Result)` is the `Uninitialized` failure, not a success.

### 3.6 JSON **[verified]** (`probe/`, `web/`; JIT with reflection, JIT and Native AOT with source generation)

| Case | A (today) | B / C2 (built-in union contract) | E (C2 + envelope converter) |
|---|---|---|---|
| success, reflection | `{"IsSuccess":true,"Value":1,"Violation":{…}}` | `5` (active case, no wrapper) | `{"value":5}` |
| failure, reflection | **throws** | `{"Kind":4,"Code":…}` | `{"failure":{…}}` |
| round-trip, reflection | success comes back as **failure** | `Result<int>`, `Result<string>`: ok (token kind decides) | ok |
| `Result<User>` (object value) | — | **reflection: `JsonException` "JSON value type 'Object' is ambiguous"**; **source generator: SYSLIB1227 at build time** | ok; the `[JsonConverter]` attribute also stops SYSLIB1227 |
| source-generated, Native AOT | — | `Result<int>` ok | ok, 0 IL warnings; the context must list `T` (and `Violation` unless it has its own converter) |
| `default` | — | writes the `Uninitialized` failure | `{"failure":{"Kind":0,"Code":"Result.Uninitialized",…}}` |
| D (closed failures) | — | `[JsonPolymorphic(InferClosedTypePolymorphism = true)]` round-trips under AOT with `"$type":"NotFound"` (the CLR name) | — |

`JsonUnion(TypeClassifier = …)` / `JsonUnionTypeStructuralClassifier` exist in .NET 11 STJ. A classifier would have
to guess "value or failure" from the shape of an object, which is fragile for `Result<SomethingWithACode>`; it was
not pursued. The envelope makes the case explicit and costs one property name.

Problem details: an endpoint that **switches** into `Results<Ok<User>, ProblemHttpResult>` works under
Native AOT (0 IL warnings): `GET /users/{known}` → `200 {"name":"Ada"}`, `GET /users/{unknown}` →
`404 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"User.NotFound","status":404,"code":"User.NotFound",…}`.
An endpoint that **returns the union directly** (`Result<User>`) has no metadata (SYSLIB1227 keeps it out of the
context) and, under AOT, the first request fails with `NotSupportedException` for **every** endpoint of the app.
The envelope endpoint returns `{"value":{"name":"Ada"}}` / `{"failure":{"kind":4,…}}` (the enum still serializes
as a number: the contract must set a string enum converter).

### 3.7 AOT **[verified]**

`probe/` published with `PublishAot=true`, `TrimmerSingleWarn=false`, `JsonSerializerIsReflectionEnabledByDefault=false`:
0 IL warnings; its output equals the JIT output with reflection JSON off, line for line, except closure sizes
(80 → 72 B). `web/` publishes with 0 IL warnings. No shape uses reflection; the open-generic converter pattern
(`[JsonConverter(typeof(Conv<>))]` on a generic struct) is the same one `Tarol.StronglyTypedId` already relies on.

### 3.8 Interop

* **Minimal APIs**: `switch` into `TypedResults` works (above). Mapping `Violation` to a status code stays a pure
  function (`ToHttpStatusCode()`); building `ProblemHttpResult` needs `Microsoft.AspNetCore.Http` and therefore
  belongs in a web package (decision 5).
* **`ValueTask` / runtime async**: `ValueTask<Result<T>>` of C2 is 24–32 B instead of 56–72 B; state machines
  shrink (452 B vs 718 B per yielding `BindAsync`). Runtime async (`<Features>runtime-async=on</Features>`) is
  opt-in for the app and needs nothing from the library **[unverified here]**.
* **Validations (step 5)** need `Combine`/`Sequence` that aggregate with property names, and a sensible aggregate type (R-6).
* **Changes / Persistence (7, 8)** need a cached success (`Result.Success()` is a static field read, 8 B) and
  `ValueTask<Result>`; `result is Violation v ? v.Code : …` replaces `result.Violation.Code`.
* **CQRS (10)** needs a non-boxing "no payload" response: the non-generic `Result`; no interface (R-9).
* **Sagas (9)** need the `Violation` JSON contract (N-3).

### 3.9 User code and JSON per shape and failure model **[verified]** (`show/`, `m2/`)

Asked by the owner before deciding Q-1. The owner's model for Q-2: *a failure is either validation errors,
or one light, non-fatal error* (we use results so we do not throw). Three failure models over the C layout
were therefore compared with today's A:

| Model | Failure type(s) |
|---|---|
| A | today: `Violation` struct; validation = `Aggregate` with `InnerErrors` |
| **M1** | `Violation` sealed class with today's fields (`Type`, `Code`, `Detail`, `PropertyName`, `InnerErrors`) |
| **M2** | `closed record Failure` with exactly two leaves: `Invalid(IReadOnlyList<FieldError> Errors)` and `Error(ErrorType Type, string Code, string? Detail)`; union cases are `T` and `Failure` |
| D | `closed record Failure` with one leaf per kind (`InvalidField`, `NotFound`, `Conflict`, `Forbidden`, `Unexpected`) |

The same use case in each: validate `name` and `email`, load the user, rename; then map to HTTP.
All compile with warnings as errors and run as one Native-AOT app (0 IL warnings).

```csharp
// ---- M2: validation errors | one light error ----------------------------------------------------------------
public Result<User> Rename(Guid id, string? name, string? email)
{
    List<FieldError> errors = [];
    if (string.IsNullOrWhiteSpace(name)) errors.Add(new("Name", "Name.Required"));
    if (email is not null && !email.Contains('@')) errors.Add(new("Email", "Email.Invalid"));
    if (errors.Count > 0) return new Invalid(errors);

    return store.TryGetValue(id, out var user)
        ? user with { Name = name! }
        : Error.NotFound("User.NotFound", $"No user {id}.");
}

// endpoint: three arms, exhaustive (a missing Error arm is CS8509)
Rename(id, name, email) switch
{
    User user       => TypedResults.Ok(user),
    Invalid invalid => TypedResults.ValidationProblem(invalid.Errors.GroupBy(e => e.Field)
                                                       .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
    Error error     => TypedResults.Problem(statusCode: error.Type.ToHttpStatusCode(), title: error.Code, detail: error.Detail),
};

// a handler that only passes failures on: one arm
static Result<Order> Next(Result<User> r) => r switch { User u => Order.For(u), Failure f => f };

// ---- M1: one Violation type ---------------------------------------------------------------------------------
    if (errors.Count > 0) return Violation.Aggregate(errors);                      // errors: List<Violation>
    …
Rename(id, name, email) switch
{
    User user => TypedResults.Ok(user),
    Violation { Type: ViolationType.Validation } v => TypedResults.ValidationProblem(
        (v.InnerErrors.Count > 0 ? v.InnerErrors : [v]).GroupBy(e => e.PropertyName ?? "")
                                                      .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
    Violation v => TypedResults.Problem(statusCode: v.ToHttpStatusCode(), title: v.Code, detail: v.Detail),
};

// ---- A: today ------------------------------------------------------------------------------------------------
var result = Rename(id, name, email);
if (result.IsSuccess) return TypedResults.Ok(result.Value);
var v = result.Violation;
if (v.Type == ViolationType.Validation) return TypedResults.ValidationProblem(/* as M1 */);
return TypedResults.Problem(statusCode: v.ToHttpStatusCode(), title: v.Code, detail: v.Detail);

// ---- D: one record per kind: the endpoint lists every kind; a validation failure carries one field ------------
    User user => TypedResults.Ok(user),
    InvalidField i => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [i.Field] = [i.Code] }),
    Show.D.NotFound nf => TypedResults.Problem(statusCode: 404, …),   // must be qualified: HttpResults.NotFound (CS0104)
    Show.D.Conflict c => …, Forbidden f => …, Unexpected u => …,
```

**HTTP** (the API contract a client sees) is the same for A, M1 and M2. D lists only the first invalid field:

```text
200 {"id":"00000000-0000-0000-0000-000000000000","name":"Grace"}
400 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,
     "errors":{"Name":["Name.Required"],"Email":["Email.Invalid"]},"traceId":"…"}
404 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"User.NotFound","status":404,
     "detail":"No user 11111111-1111-1111-1111-111111111111.","traceId":"…"}
```

**The result itself as JSON** (a message, a saga event, a client SDK; envelope converter, web defaults):

| | success | validation failure | light error |
|---|---|---|---|
| A (today) | `{"IsSuccess":true,"Value":{…},"Violation":{…}}` | **throws** (R-4) | **throws** |
| M1 | `{"value":{"id":"…","name":"Grace"}}` | `{"failure":{"type":"Validation","code":"Validation.Multiple","detail":null,"propertyName":null,"innerErrors":[{"type":"Validation","code":"Name.Required","detail":null,"propertyName":"Name","innerErrors":[]},{…"Email.Invalid"…}]}}` | `{"failure":{"type":"NotFound","code":"User.NotFound","detail":"No user …","propertyName":null,"innerErrors":[]}}` |
| **M2** | `{"value":{"id":"…","name":"Grace"}}` | `{"failure":{"kind":"invalid","errors":[{"field":"Name","code":"Name.Required","detail":null},{"field":"Email","code":"Email.Invalid","detail":null}]}}` | `{"failure":{"kind":"error","type":"NotFound","code":"User.NotFound","detail":"No user …"}}` |
| D | `{"value":{…}}` | `{"failure":{"kind":"invalidField","field":"Name","code":"Name.Required"}}` (one field) | `{"failure":{"kind":"notFound","detail":"No user …","code":"User.NotFound"}}` |

M2 in numbers: `Result<int>` 16 B, `Result<Guid>` 24 B, 0 B per success + `switch` (`m2/`). `default` reads as
`Error.Uninitialized` and lands in the `Error` arm. The `kind` discriminator is a fixed, Tarol-owned name
(`[JsonDerivedType(typeof(Invalid), "invalid")]`), not a CLR name, so it is safe to persist.

Assessment: M2 encodes the owner's rule in the type. Validation is a **list of field errors** (no aggregate
`Violation` that pretends to be one, so R-6 and R-7 disappear: combining validations concatenates lists, and an
`Error` stops the pipeline). A "light exception" is one `Error` with a type for the HTTP mapping and a free code.
The handler has exactly two failure arms, or one `Failure f` arm to pass failures on. Users cannot add a third
kind (CS9382), and they do not need one: new situations are new codes. M1 keeps today's names, but every
consumer must remember that `Type == Validation` means "look at `InnerErrors`".

### 3.10 Analyzer / generator needs (item 7)

No generator is needed: the shape is hand-written and the JSON converter is open generic. An **analyzer** would
catch what the compiler does not:

| Candidate | Why | Proposed |
|---|---|---|
| `Result<T>` with `T` = `Violation` (or derived), `object`, `Result`/`Result<…>` | CS0457 only for implicit conversions; `new(…)` silently picks the failure, `object` silently succeeds (§3.4) | Error |
| discarded `Result`/`Result<T>`/`ValueTask<Result…>` | the most common bug with result types; no built-in rule covers it (N-5; CA2012 only covers `ValueTask` misuse) | Warning |
| `result.Value` (the `object?` union member) on `Result<T>` | boxes and loses the type after N-1 | Warning, with a code fix to a pattern |

Recommendation: build it, **bundled in `Tarol.Results`** (D-10), as its **own PR after this one** (one feature per
PR); this PR documents the three pitfalls in the README and the skill instead.

## 4. Proposed API (pending §6)

```csharp
namespace Tarol.Results;

public enum ViolationType : byte { Validation, Unauthorized, Forbidden, NotFound, Conflict, Gone,
                                   UnprocessableEntity, TooManyRequests, Internal, Unavailable, Timeout, Custom = 255 }

[JsonConverter(typeof(ViolationJsonConverter))]           // hand-written: AOT-safe, no context entry needed
public sealed class Violation : IEquatable<Violation>    // a reference: Result<T> = T + 8 B
{
    public Violation(ViolationType type, string code, string? detail = null, string? propertyName = null,
                     IReadOnlyList<Violation>? innerErrors = null);
    public ViolationType Type { get; }  public string Code { get; }  public string? Detail { get; }
    public string? PropertyName { get; }  public IReadOnlyList<Violation> InnerErrors { get; }   // never null
    public static Violation Uninitialized { get; }        // what default(Result) reads as (Internal, 500)
    public static Violation Validation(string code, string? propertyName = null);  // NotFound, Conflict, Unauthorized,
    public static Violation NotFound<TEntity, TId>(TId id) where TId : ISpanFormattable;  // Forbidden, Gone, …
    public static Violation Aggregate(string code, IReadOnlyList<Violation> innerErrors); // type rule: R-6
    public int ToHttpStatusCode();
    // value equality, InnerErrors compared element-wise (R-6)
}

[Union, JsonConverter(typeof(ResultJsonConverter<>))]     // {"value":…} | {"failure":{…}}
public readonly struct Result<T> : IUnion, IEquatable<Result<T>>
{
    public Result(T value);                // ArgumentNullException for null (R-3)
    public Result(Violation violation);
    public bool IsSuccess { get; }  public bool IsFailure { get; }
    [EditorBrowsable(Never)] public object? Value { get; }   // union protocol; boxes value types
    [EditorBrowsable(Never)] public bool HasValue { get; }   // always true
    public bool TryGetValue([MaybeNullWhen(false)] out T value);
    public bool TryGetValue([NotNullWhen(true)] out Violation? violation);
    public T GetValueOrThrow();                               // replaces the typed Value
    public T GetValueOrDefault(T defaultValue);  public T GetValueOrDefault(Func<Violation, T> factory);
    public static implicit operator Result<T>(T value);  public static implicit operator Result<T>(Violation violation);
    public Result<TNew> Map<TNew>(Func<T, TNew> map);  public Result<TNew> Bind<TNew>(Func<T, Result<TNew>> bind);
    public Result<T> Ensure(Func<T, bool> predicate, Violation violation);
    public Result<T> Tap(Action<T> action);  public Result<T> TapFailure(Action<Violation> action);
    public TOut Match<TOut>(Func<T, TOut> success, Func<Violation, TOut> failure);
    public Result ToResult();
    public ValueTask<Result<TNew>> MapAsync<TNew>(Func<T, CancellationToken, ValueTask<TNew>> map, CancellationToken ct = default);
    // BindAsync, EnsureAsync, TapAsync likewise: ValueTask only, CancellationToken passed through (R-8)
}

public readonly struct Success;                            // the success case of the non-generic Result

[Union, JsonConverter(typeof(ResultJsonConverter))]
public readonly struct Result : IUnion, IEquatable<Result> // 8 B
{
    public Result(Success success);  public Result(Violation violation);
    public static Result Success();                        // cached
    public static Result Combine(params ReadOnlySpan<Result> results);   // aggregates (R-7, N-6)
    public static Result<(T1, T2)> Combine<T1, T2>(Result<T1> first, Result<T2> second);  // aggregates too (R-7)
    // IsSuccess, IsFailure, TryGetValue(out Violation), implicit from Violation, Map, Bind, Tap, TapFailure,
    // Match, *Async as above
}

public static class ResultExtensions
{
    // Sequence, ToResult for T? (class / struct), ValueTask<Result<T>> pipeline: Map, Bind, BindAsync, Ensure, Tap, TapFailure, Match
}
```

Usage:

```csharp
public async ValueTask<Result<User>> Rename(Id<User> id, string name, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(name)) return Violation.Validation("User.NameRequired", nameof(name));
    return await repo.GetById(id, ct) switch
    {
        User user => user.Rename(name),
        Violation violation => violation,
    };
}
```

Removed: `IResult`, `IResult<T>` (R-9), `Violation.None` (R-2), `Switch` (statement `switch` / `Match`),
`TryGetError` (now the `TryGetValue(out Violation)` overload), the typed `Value` (N-1), `Task`-based overloads (R-8),
the StronglyTypedId reference (R-11). Aggregate type rule (R-6): all inner violations of one type → that type;
mixed → the most severe by a fixed order (5xx before 401, 403, 404, 409, 410, 422, 429, 400).

## 5. Planned projects

| Tarol-Net project | From |
|---|---|
| `src/Results/Tarol.Results` | `src/Results/Results` |
| `tests/Results/Tarol.Results.Tests` | `tests/Results/Results.UnitTests` (80 tests) + tests for every finding and every union behaviour relied on |
| `tests/Results/Tarol.Results.AotSmoke` | new |
| `tests/Results/Tarol.Results.PackageTests` | new |
| `tests/Results/Tarol.Results.Benchmarks` | new (from `probes/results/bench`; not in CI) |

## 6. Decisions for the owner

Asked in the session (2026-11-12/13), one question per decision with a recommendation. Round 1: the owner asked
for user code and JSON per shape (added as §3.9), described failures as "validation errors, or one light,
non-fatal error", and deferred `default` to the end. Round 2 asked whether a union of the outcomes themselves
(success | validation error | …) would make more sense than a `Result`: that is what the agreed shape is at the
call site (`switch { T, Invalid, Error }`), while per-operation `union` declarations were measured
(`probes/results/outcome`: boxing of value-type successes, `default` throws, SYSLIB1227, no generic framework
contract) and not chosen.

| # | Decision | Owner's answer |
|---|---|---|
| Q-1 | `Result<T>` shape | **C**: hand-written `[Union]` struct, `T` + one state reference |
| Q-2 | failure model | **`closed record Violation`** with exactly two kinds: **`Invalid`** (list of **`FieldError`**) and **`Error`** (**`ErrorType`** + code + detail). Union cases: `T` and `Violation` |
| Q-3 | `default(Result)` | **`Error.Uninitialized`** (type `Internal`, code `Result.Uninitialized`) for both `Result<T>` and `Result` |
| Q-4 | combinators | no preference → the proposal: Map, Bind, Ensure, Tap, TapFailure, Match, GetValueOrDefault, GetValueOrThrow, ToResult, Combine, Sequence; `switch` documented first; no `Switch`, no `Task` overloads; `*Async` are `ValueTask` only with `CancellationToken`. Combining: `Invalid` + `Invalid` concatenates field errors, the first `Error` wins |
| Q-5 | JSON / problem details | as proposed: AOT-safe envelope `{"value":…}` / `{"failure":{"kind":…}}` and `ErrorType` as string in `Tarol.Results`; pure `ToHttpStatusCode()`; problem-details mapping in a later web package (no ASP.NET Core dependency) |
| Q-6 | `IResult` | **removed** |
| Q-7 | analyzer | **yes, in this PR**, bundled in `Tarol.Results` |

## 7. Changes against Tarol

*Filled in during the implementation stage.*

## 8. Suppressions and analyzer configuration

*Filled in during the implementation stage.*

## 9. Verification

*Filled in during the implementation stage.*

## 10. Skill evaluation checklist

*Filled in during the implementation stage.*
