# Step 5: Validations

Origin: `CLorat/Tarol@6a62f81`: `src/Validations/{Validations,Validations.Value,Validations.Invariant}`,
`gen/Validations/{Validations.Value.Generators,Validations.Invariant.Generators,Validations.Generators}`,
`Validations.Prerequisite/` (repository root), `tests/Validations/*`. Old review: `docs/review/validations-and-changes.md`
(findings 1–32, §2.3, §4, §7) and `docs/review/ROADMAP.md` Phase 3.

**Status: evaluation (stage 2) done; the design decisions (§9) wait for the owner.**

The concept review is in [validation.md](../architecture/validation.md) (kinds of rules, where they run, what .NET 11
provides, `Invalid` vs `Error`, paths, replay safety, new insights, the owner's scope answers S-1–S-4). This note does
not repeat it.

Evidence labels follow [conventions](../architecture/conventions.md). Probes are in [`probes/validations/`](probes/validations/)
(`*.csproj.txt`: copy a folder **outside** the repository, rename to `.csproj`, pass `-p:TarolNetRepo=<clone>`; `bench/`
compiles `../vo/*.cs`, so copy both). Environment: SDK 11.0.100, .NET 11.0.0, linux-x64, Intel Xeon, 4 cores.

| Probe | What it shows | Command |
|---|---|---|
| `mev/` | `Microsoft.Extensions.Validation` 11.0.0 without ASP.NET Core: paths, codes, `Option<T>`, `IValidatableObject`, ReDoS, allocations; JIT and Native AOT | `dotnet run -c Release`; `dotnet publish -c Release -r linux-x64` |
| `vo/` | struct value objects written exactly as the proposed generator would emit them: arithmetic, sizes, factories, `[GeneratedRegex]` timeout, JSON with a source-generated context (events, `Option<VO>`, old data under stricter rules), collect-all command validation with input paths, allocations; JIT and Native AOT | `dotnet run -c Release`; `dotnet publish -c Release -r linux-x64` |
| `bench/` | candidates (a)–(d) on the same command, valid and invalid (BenchmarkDotNet 0.16.0-preview.2, `ShortRun`) | `dotnet run -c Release -- --filter '*'` |

## 0. Summary

* **The owner's goal for the generator** (S-2): rules protect the domain and are **declared once**, never repeated per
  input. Declared on the **value object**, also for native types, as **struct value objects**, if serialization and
  arithmetic keep working.
* **Both conditions hold [verified]** (`vo/`, JIT and Native AOT, 0 IL warnings):
  * `Quantity` is 4 B, the same as `int`; `Title` is 8 B. Implicit conversion to the underlying type gives `a + b`,
    `a * 2`, `a < 50`, `Math.Max(a, b)`, `lines.Sum(l => l.Quantity)`, `Max`, `OrderBy`. Arithmetic returns `int`, not
    `Quantity` (40 + 70 = 110 is out of range); going back is `Quantity.Create(…)`.
  * JSON with a source-generated context writes the bare value (`"total":40`, `"name":"Lamp"`), `Option<Quantity>` as
    `70`/`null`, and needs no context entry for the value object (its converter is on the type).
  * An **old event that today's rules reject loads** (`total: 500`, `price: -1`, a 28-character title): the converter
    trusts, the factory validates.
* **Commands get a generated `Validate`** that calls each value object's rules with the input path. One command with
  ids, value objects, a list, an `Option` and a nested value reports all 7 errors with paths (`Lines[1].Quantity`,
  `Price.Amount`, `Nickname`) **[verified]**.
* **Cost [verified]** (`bench/`, compare ratios): the value-object path takes ≈ 95 ns and **0 B** for a valid command;
  `Microsoft.Extensions.Validation` is 29× slower and allocates 5.8 KB; DataAnnotations `Validator` is 20× slower and
  3.1 KB, and validates neither nested objects nor list elements.
* **Recommended**: one package `Tarol.Validations` (depends on Results, Options, StronglyTypedId) with rule attributes,
  a small runtime (`ValidationErrors`, rule helpers, codes), and **one bundled generator** for value objects and
  commands. Invariants and prerequisites are designed here and built in steps 6–7 (S-1).

## 1. Inventory

### 1.1 Old code **[verified]** (read at `6a62f81`)

| Project | Content | Tests |
|---|---|---|
| `src/Validations/Validations` | `GenerateValidationAttribute` (in `ValidationGroup.cs`), class targets only | `Validations.UnitTests`: 0 tests |
| `src/Validations/Validations.Value` | `ValueValidationAttribute` (abstract), `NotEmpty`, `Range` (int/long/double/decimal ctors, `Exclusive` ignored), `Length` (+ unusable `MinOnly`/`MaxOnly`), `Pattern`, `Email`; `ValueValidationHelpers` (unused, second e-mail definition, unbounded regex cache) | `Validations.Value.UnitTests`: no test framework, 0 tests |
| `src/Validations/Validations.Invariant` | `InvariantAttribute(code, description)` in namespace `Tarol.Validations` | — |
| `gen/…/Validations.Value.Generators` (545 lines) | `Tarol.Validation.Generators.ValidationGenerator`: per type a nested `Validation.<Prop>.Validate(value)` and `ValidateValues(this T)` | — |
| `gen/…/Validations.Invariant.Generators` (272 lines) | `GenerateValidationGenerator`: `ValidateInvariants(this T)` over `[Invariant]` static `bool` methods | 17 substring tests |
| `gen/…/Validations.Generators` | empty csproj | — |
| `Validations.Prerequisite/` (root) | `IPrerequisite<T>.Verify(T) : bool`, `IPrerequisiteAsync<T>`, `PrerequisiteAttribute(code, displayText, order)`; no runner | — |

### 1.2 Consumers **[verified]** (grep over `src/`, `examples/`, `templates/`)

| Consumer | Uses |
|---|---|
| `src/Primitives/Primitives/Value.cs` | `[GenerateValidation] abstract record Value`: every subtype must be `partial` (finding 7) |
| ArtFlow `Money` | `[Range(0, int.MaxValue)]` on `decimal Amount` **and** `[Invariant] Amount >= 0` (same rule twice) |
| ArtFlow `Auction`, `Bid`, `ArtWork`, `Bidder`, `Expert` | `[NotEmpty]`/`[Length]` on **aggregate properties**; `Bidder`, `Expert`, `Bid` lack `[GenerateValidation]` and are silently not validated (finding 6); `Description => string.Empty` with `[Length(10, 1000)]` always fails (18) |
| ArtFlow invariants | `WhenClosedEndtimeMustBeSet`, `WhenSettlesBidsMustBeEmpty` |
| ArtFlow prerequisites | 8 classes; 2 inject `TimeProvider`, 2 inject a domain service, 1 inverted (17), 2 ordered by number |
| ArtFlow `Test.TestArtWork` | the only call of `ValidateValues`/`ValidateInvariants` (finding 2) |
| `templates/TarolSolution` | `global using Tarol.Validations*` in 8 files; no rule used |
| Framework (`src/`) | nothing calls validation (finding 2) |

### 1.3 What the later steps need from step 5

| Step | Needs |
|---|---|
| 6 Primitives | value objects in events and aggregate state (trusting converters); the invariant hook and its call site; the replay test on the real `AggregateRoot`; whether `Value` stays as a base (§7.6) |
| 7 Changes | `IValidatable` to validate a command before load (S-4); guards in behaviours return `Result`; invariants after apply, instance discarded on failure |
| 10 CQRS / web package | pipeline order S-4; `Invalid` → problem details with JSON-cased paths; `Microsoft.Extensions.Validation` output for plain DTOs mapped to `Invalid` |

## 2. Review: old findings re-verified

| # | Finding (short) | Status in step 5 |
|---|---|---|
| 1 | no path from a change to `Raise` | step 7 (listed, not fixed here) |
| 2 | nothing calls validation | **design**: step 7 calls `IValidatable.Validate`; a value object cannot exist live without `Create` (§3.2) |
| 3 | attributes matched by `name.Contains` | **fix**: full metadata name; a DataAnnotations attribute on a value-object parameter is a diagnostic (TVAL007) |
| 4 | one `Validate` call per rule (duplicates) | **fix**: one check per member, rules emitted in order; test |
| 5 | abstract bases skipped | **moot**: rules live on value objects (structs/sealed), not on base classes; inheritance of rules not supported, diagnostic if a rule attribute is on a base member |
| 6 | silent skip without `[GenerateValidation]` | **fix**: a rule attribute on a non-partial / unsupported declaration is an error diagnostic; no opt-in marker to forget |
| 7 | internal / non-partial types | **fix**: generated code keeps the declared accessibility; missing `partial` is TVAL001 |
| 8 | nested and generic types | **fix**: nested types supported (containing partials emitted); generic value objects are TVAL002 |
| 9 | duplicate hint names | **fix**: hint name from the fully qualified metadata name; test with two equal names |
| 10 | invariants in a separate static class ignored | step 6 (invariant hook); §4 |
| 11 | rule shape not validated | **fix** for value objects (TVAL003–TVAL006); invariants step 6 |
| 12 | incrementality broken | **fix**: `ForAttributeWithMetadataName`, equatable models, no `Collect`, tracked-step tests |
| 13–15 | Changes / Primitives example issues | step 6/7 (listed) |
| 16 | `RegexOptions.Compiled`, no timeout | **fix**: `[GeneratedRegex]` with a timeout (default 100 ms); timeout = field error, **[verified]** 99–107 ms |
| 17 | inverted prerequisite | **moot**: guards are code in the behaviour (§5); recorded for step 13 (ArtFlow) |
| 18 | ArtFlow `Description` always fails | step 13 |
| 19–20 | dead `Range.Exclusive`, decimal ctor, `MinOnly`/`MaxOnly` | **fix**: `Range` takes `double`/`long` and strings for decimals (§7.2); named `Min`/`Max` properties instead of factories |
| 21 | `IsString`/`IsNumeric` by display string; `[NotEmpty]` on `Guid` does not compile | **fix**: `SpecialType` / interface checks; `NotEmpty` on unsupported types is TVAL004 |
| 22 | NRE on null nested values | **fix**: null member → `Required`; test |
| 23 | dead helpers, two e-mail definitions | **fix**: one `Email` rule (span check, no regex) |
| 24–25 | diagnostic ids/names inconsistent | **fix**: `TVAL` ids, `AnalyzerReleases.*.md`, one generator name |
| 26, 27, 30 | Changes naming / dead projects / references | step 7 |
| 28, 29 | empty generator project, prerequisite project at the root | **not migrated** (plan.md) |
| 31 | `Verify` returns `bool` | **moot**: guards return `Result` (§5) |
| 32 | invariant violations without a member path | **moot by design** (validation.md insight 2): invariants report `Error` with a code |

## 3. Experiments

### 3.1 `Microsoft.Extensions.Validation` (candidate b) **[verified]** (`mev/`)

See validation.md §4: works without ASP.NET Core and under Native AOT; right paths; **no codes**; `[StringLength]` on an
`Option<string>` throws `InvalidCastException`; a catastrophic `[RegularExpression]` throws `RegexMatchTimeoutException`
after ≈ 2.0 s; `IValidatableObject` only runs when every attribute passed; ≈ 5.1 KB per valid call. `MinLength` on a
collection is IL2026.

### 3.2 Struct value objects (candidate c, generated; the owner's conditions) **[verified]** (`vo/`)

User code (what the generator reads; the probe contains the code it would emit, written by hand):

```csharp
public readonly partial record struct Quantity([Range(1, 100)] int Value);
public readonly partial record struct Title([Length(3, 10)] string Value);           // string: Required implied
public readonly partial record struct Email([Email] string Value);
public readonly partial record struct Money([Range(Min = 0)] decimal Amount, Currency Currency);

public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;
public sealed partial record PlaceOrder(Id<Order> OrderId, Title Name, Email Contact,
    [Count(1, 50)] IReadOnlyList<Line> Lines, Option<Title> Nickname, Money Price) : IValidatable;
```

Emitted per value object: `Create(…) : Result<T>`, a `Check(…, ref ValidationErrors)` used by commands, constants
(`Quantity.Min`, `Max`), `implicit operator int`, `IComparable<T>` and comparison operators, `ToString`, and a nested
trusting `JsonConverter`. Emitted per `IValidatable` command: `Validate(ref ValidationErrors)` and
`Validated() : Result<T>`.

Arithmetic and size (owner's condition 2):

```text
a + b = 110 (int; 110 is out of range, so it is not a Quantity)
a + 1 = 41, a * 2 = 80, a > b = False, a < 50 = True, Math.Max(a, b) = 70
Sum(l => l.Quantity) = 110; Max = 70; OrderBy works: 40,70
Quantity.Create(a + b) = Invalid { : Range }
money.Amount * 3 = 31.5
sizeof Quantity = 4, Title = 8, Money = 24
```

Repository serialization (owner's condition 1; `[JsonSerializable(typeof(OrderPlaced))]` only, web defaults):

```text
{"orderId":"…","name":"Lamp","total":40,"bonus":70,"price":{"amount":10.5,"currency":0},"lines":[{"product":"…","quantity":40},…]}
round trip equal (scalars) = True
None -> "bonus":null
old event under stricter rules loads: name='A title that is far too long', total=500, price=-1
```

The event store of step 8 stores events as JSON (plan.md step 8), so this is the repository path. Ids keep their own
converter; a value object as a *database column* or a stream key is not planned (step 8 maps ids only) **[unverified]**.

Command validation (collect all, input paths):

```text
Invalid { OrderId: Required; Name: Length; Contact: Pattern; Lines[1].Product: Required; Lines[1].Quantity: Range; Nickname: Length; Price.Amount: Range }
missing name/contact -> Invalid { Name: Required; Contact: Required }
```

A missing string-based value object deserializes as `default` (`Value == null`) and reports `Required`. A missing
**`int`-based** one is `0` and reports its own rule (`Range`); "missing" and "zero" cannot be told apart without the
deserializer (open question 6 in validation.md; decision 4).

Allocations (1,000 iterations after warm-up, JIT and Native AOT identical):

| Call | Valid | Invalid |
|---|---|---|
| `Quantity.Create` | 0 B | 288 B (one error) |
| `Email.Create` | 0 B | — |
| `PlaceOrder.Validated()` | **0 B** | 1,704 B (7 errors) |

The path is a stack of segments in an inline array inside `ValidationErrors` and becomes a string only when an error is
added; the first version built path strings eagerly and allocated 192 B on the valid path.

### 3.3 Candidates side by side **[verified]** (`bench/`, `ShortRun`: compare ratios)

Same command (ids, 2 strings, list of 2 lines, an optional, a nested price). (a) is what a rewritten property-attribute
generator emits for a primitive DTO; (b) and (d) run on the same DTO with DataAnnotations attributes and map to `Invalid`.

| Candidate | Valid | Allocated | Invalid (7 errors) | Allocated | Codes | Nested / list paths | AOT |
|---|---:|---:|---:|---:|---|---|---|
| (c) value objects + generated `Validate` | 95 ns (1.00) | 32 B¹ | 515 ns | 1,736 B | yes | yes | 0 warnings |
| (a) generated over a primitive DTO | 53 ns (0.56) | 24 B¹ | 538 ns | 1,728 B | yes | yes | 0 warnings |
| (b) `Microsoft.Extensions.Validation` → `Invalid` | 2,773 ns (29×) | 5,784 B | 6,133 ns | 9,472 B | **no** (message as code) | yes | 1 warning (`MinLength`) |
| (d) DataAnnotations `Validator` → `Invalid` | 1,943 ns (20×) | 3,128 B | 3,234 ns | 4,680 B | **no** | **no** (flat) | `RequiresUnreferencedCode` |

¹ BenchmarkDotNet boxes the returned `Result` struct (the methods return `object`); the call itself allocates 0 B (§3.2).

(a) and (c) emit the same checks; (a) is faster on the valid path only because it skips the value-object `Check` calls.
The difference between them is **where the rule is written**: (a) repeats `[Length(3, 10)]` on every DTO that carries
a title, (c) writes it once on `Title` (S-2).

### 3.4 ReDoS **[verified]**

| Pattern `^(a+)+$`, input 30 × `a` + `!` | Time | Outcome |
|---|---|---|
| DataAnnotations `[RegularExpression]` (via b) | 1,996–1,999 ms | throws `RegexMatchTimeoutException` |
| `[GeneratedRegex(…, matchTimeoutMilliseconds: 100)]` + catch (c) | 99–107 ms | `Invalid { : Pattern }` |

Without a timeout the same input runs for minutes (exponential backtracking) **[unverified here; documented .NET
behaviour]**; the probe does not run it.

### 3.5 DataAnnotations attributes present (finding 3)

The generator matches Tarol attributes by fully qualified metadata name, so `[MaxLength]`/`[StringLength]` are never
read as a Tarol rule. Because the old bug turned them into a silent exact-length rule, a DataAnnotations validation
attribute on a value-object parameter becomes a diagnostic (TVAL007, Warning: "has no effect; use
`Tarol.Validations.Length`") **[unverified until the generator test]**. Both namespaces in one file give CS0104 for
`Range` (C# rule) **[unverified here]**: decision 6.

### 3.6 Replay **[verified for deserialization]** (`vo/`)

Old event data that today's rules reject deserializes and keeps its values. The aggregate part (`On`/apply does not
call rules; `FromSnapshot` likewise) is proved in the implementation with a minimal hand-written event-sourced aggregate
in the tests (S-3) and repeated on the real `AggregateRoot` in step 6.

## 4. Invariants (designed here, built in step 6)

| Shape | User code | Notes |
|---|---|---|
| I-1 old: `[Invariant]` static `bool` + generated aggregation | `[Invariant("Auction.SettledHasNoBids")] static bool SettledHasNoBids(Auction a) => …;` | the generator finds rules in partial files; separate static classes need a target (finding 10); `bool` hides the code until aggregation |
| I-2 one hand-written method the base calls | `protected override Result CheckInvariants() => Status != Settled \|\| Bids.Count == 0 ? Result.Success() : Error.Conflict("Auction.SettledHasNoBids");` | no generator; one place; a forgotten rule is visible in review |
| I-3 generated aggregation of `Result`-returning methods | `[Invariant] Result SettledHasNoBids() => …;` → generated `CheckInvariants()` | each rule its own file, codes inside the rule |

When: after the behaviour raised its events, live path only, before commit; on failure the pipeline discards the
instance (no rollback). Never in `On`/apply, replay, snapshots, projections. What a violation is: decision 3.

## 5. Prerequisites (designed here, built as guards in steps 6–7)

Decided (S-1): no `IPrerequisite` type. ArtFlow's prerequisites, rewritten:

```csharp
public Result<Bid> PlaceBid(Id<Bidder> bidder, Money amount, DateTimeOffset now)
{
    if (Status != AuctionStatus.Open || now < StartTime || now > EndTime) return Error.Conflict("Auction.NotOpen");
    if (amount.Currency != CurrentPrice.Currency) return Error.Conflict("Bid.CurrencyMismatch");   // before the comparison
    if (amount.Amount <= CurrentPrice.Amount) return Error.Conflict("Bid.TooLow");
    return Raise(new Bid.Placed(bidder, amount, now));
}

// a read of another aggregate is handler code (step 7/10), eventually consistent:
if (!await artworks.IsVerified(cmd.ArtworkId, ct)) return Error.UnprocessableEntity("Artwork.NotVerified");
var auction = Auction.Create(cmd.ArtworkId, cmd.EndTime, now);   // EndTime >= now + 1 day: Invalid.Field("EndTime", "TooSoon")
```

Ordering is code order; the first failing guard returns (short-circuit, validation.md insight 4). `now` is a parameter
(kernel.md §12); no `TimeProvider` reaches the aggregate.

## 6. Generator design

One incremental generator, `Tarol.Validations.Generators.ValidationGenerator`, bundled in `Tarol.Validations` (D-10):

* **Value objects**: `ForAttributeWithMetadataName` on each Tarol rule attribute, filtered to primary-constructor
  parameters of a `partial` `record struct` / `record`. No marker attribute: a rule on a parameter *is* the opt-in.
* **Commands / inputs**: types that declare `IValidatable` and are `partial` (syntax predicate on the base list, then a
  semantic check). Members are classified by type: value object (call its `Check`), `Id<T>` (`IsEmpty` → `Required`),
  `Option<T>` (rules on `Some`), `IReadOnlyList<T>`/arrays of an `IValidatable` or value object (per element, index
  path), nested `IValidatable` (recurse with the member name), anything else (only `null` → `Required` for reference
  types).
* **Models**: records of strings, ints and `EquatableArray<T>` (like StronglyTypedId's generator), no symbols,
  `Location` or `Diagnostic` in models; diagnostics carry a `LocationInfo`. Hint name `<metadata name>.Validation.g.cs`.
* **Tests**: snapshot per shape (value object kinds, nested type, internal, command with every member kind), one test per
  diagnostic and its negative, tracked-step incrementality (edit an unrelated file → `Unchanged`/`Cached`), runtime tests
  on compiled output.

Diagnostics (proposed; decision 5):

| Id | Severity | When |
|---|---|---|
| TVAL001 | Error | a rule attribute or `IValidatable` on a type that is not `partial` (also a containing type) |
| TVAL002 | Error | rule attribute on a generic value object |
| TVAL003 | Error | rule attribute outside a primary-constructor parameter of a record / record struct (property, field, method parameter, class) |
| TVAL004 | Error | rule not applicable to the parameter type (`Length` on `int`, `Range` on `string`, `Email` on `Guid`) |
| TVAL005 | Error | invalid rule arguments (`Min > Max`, negative length, pattern that does not parse) |
| TVAL006 | Warning | `new T(…)` of a validated value object outside its own declaration (bypasses `Create`); tests can suppress per file |
| TVAL007 | Warning | a `System.ComponentModel.DataAnnotations` validation attribute on a value-object parameter (no effect; finding 3) |
| TVAL008 | Warning | an `IValidatable` member type the generator cannot validate and that is not a known safe type (it is not validated) |

## 7. Proposed API

### 7.1 Runtime (`namespace Tarol.Validations`)

```csharp
public interface IValidatable { void Validate(ref ValidationErrors errors); }   // implemented by the generator

public struct ValidationErrors                     // collects; allocates only on the first error
{
    public void Enter(string member);  public void EnterIndex(int index);  public void Exit();
    public void Add(string code, string? detail = null);                  // at the current path
    public void Add(string member, string code, string? detail);          // at path.member
    public readonly bool IsValid { get; }
    public readonly Invalid? ToInvalid();
}

public static class ValidationCodes { Required, Range, Length, Pattern, Email, Count }   // const strings
public static class Rules                          // used by generated code and by hand-written factories
{
    public static bool InRange<T>(T value, T min, T max) where T : IComparisonOperators<T, T, bool>;
    public static bool Length(string? value, int min, int max);
    public static bool NotBlank(string? value);
    public static bool Email(ReadOnlySpan<char> value);
    public static bool Matches(Regex regex, string? value);   // a timeout is false, never an exception
}

public static class ValidatableExtensions
{
    extension<T>(T value) where T : IValidatable { public Result<T> Validated(); }
}
```

### 7.2 Attributes (`namespace Tarol.Validations`, `AttributeTargets.Parameter`)

`Range(long min, long max)`, `Range(double min, double max)`, `Range(string min, string max)` (decimal as invariant strings), and open-ended `Range { Min = 0 }` / `Range { Max = … }`;
`Length(int min, int max)` and `Length { Min, Max }`; `Pattern(string regex) { TimeoutMilliseconds = 100 }`;
`Email`; `NotEmpty` (strings: not blank; `Guid`/`Id<T>`: not empty; on a string-based value object `Required` is
implied); `Count(int min, int max)` (collections on `IValidatable` members).

### 7.3 Generated, per value object

```csharp
// user
public readonly partial record struct Quantity([Range(1, 100)] int Value);
// generated (shape)
[JsonConverter(typeof(Quantity.Json))]
readonly partial record struct Quantity : IComparable<Quantity>, IComparisonOperators<Quantity, Quantity, bool>
{
    public const int Min = 1, Max = 100;
    public static Result<Quantity> Create(int value);
    public static void Check(int value, ref ValidationErrors errors);
    public static implicit operator int(Quantity value);
    // CompareTo, <, >, <=, >=, ToString (invariant); nested sealed class Json : JsonConverter<Quantity> (trusting)
}
```

### 7.4 User code: the whole flow

```csharp
// domain: rules once
public readonly partial record struct Title([Length(3, 10)] string Value);
public readonly partial record struct Quantity([Range(1, 100)] int Value);

// input: no rules repeated
public sealed partial record AddLine(Id<Order> Order, Id<Product> Product, Quantity Quantity, Option<Title> Note) : IValidatable;

// handler (step 7 generates this part): parse before load (S-4)
if (command.Validated() is Invalid invalid) return invalid;                 // 400, all fields, input paths
var order = await store.Load(command.Order, context.Tenant, ct);          // 404
return order.AddLine(command.Product, command.Quantity);                  // guards → 409

// tests
Quantity.Create(0).ShouldBe(Invalid.Field("", ValidationCodes.Range));
new AddLine(Id<Order>.Empty, product, quantity, Option.None).Validated()
    .ShouldBe(Invalid.Field("Order", ValidationCodes.Required));
```

### 7.5 What a hand-written value object looks like (no generator)

```csharp
public readonly record struct Iban(string Value)
{
    public static Result<Iban> Create(string? value) =>
        value is not null && IbanChecksum.IsValid(value) ? new Iban(value) : Invalid.Field("", "Iban");
}
```

Hand-written value objects work in commands if they expose `static void Check(…, ref ValidationErrors)`
(decision 2 asks whether the generator also accepts a user `Check` / `Validate` method as a custom rule).

### 7.6 What replaces the Primitives `Value` base attribute

Nothing in step 5: value objects are found by their rule attributes, so `Value` (an abstract record) is not needed for
validation and its `[GenerateValidation]` goes. Step 6 decides whether `Value` stays as a marker.

## 8. Planned projects

| Project | Content |
|---|---|
| `src/Validations/Tarol.Validations` | runtime (§7.1–7.2), README, skill `skills/tarol-validations/SKILL.md`; references Results, Options, StronglyTypedId; bundles the generator and the Options/Results analyzers for its own code |
| `src/Validations/Tarol.Validations.Generators` | the generator and TVAL001–TVAL008, `AnalyzerReleases.*.md` |
| `tests/Validations/Tarol.Validations.Tests` | runtime, generated code, paths, `Option`/`Id`/collections, JSON round trip and old data, replay with a minimal aggregate, every fixed finding |
| `tests/Validations/Tarol.Validations.Generators.Tests` | snapshots, diagnostics, incrementality |
| `tests/Validations/Tarol.Validations.AotSmoke` | 0 IL warnings |
| `tests/Validations/Tarol.Validations.PackageTests` | consumer from the local feed, plus a temporary traps file per diagnostic |
| `tests/Validations/Tarol.Validations.Benchmarks` | from `probes/validations/bench`; not run in CI |

## 9. Decisions for the owner

Asked one per question with a recommendation; answers are recorded here and in validation.md.

1. Value validation mechanism and where it runs.
2. Value-object declaration shape and custom rules.
3. Invariants: shape, failure type.
4. `Option`/`Id`/collections: required vs optional, missing `int`, path casing.
5. Generator diagnostics and severities.
6. Package layout, attribute names vs DataAnnotations.
7. Replay enforcement: S-3 answered it (test now, analyzer decided in step 6); not asked again.
