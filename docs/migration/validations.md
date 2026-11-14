# Step 5: Validations

Origin: `CLorat/Tarol@6a62f81`: `src/Validations/{Validations,Validations.Value,Validations.Invariant}`,
`gen/Validations/{Validations.Value.Generators,Validations.Invariant.Generators,Validations.Generators}`,
`Validations.Prerequisite/` (repository root), `tests/Validations/*`. Old review: `docs/review/validations-and-changes.md`
(findings 1–32, §2.3, §4, §7) and `docs/review/ROADMAP.md` Phase 3.

**Status: implemented.** §0–§8 are the evaluation (inventory, review, experiments, proposal), §9 the owner's decisions,
§10 onwards the implementation. Where §6–§7 (the proposal) and §9–§10 differ, §9–§10 are what shipped: `[Pattern]`
emits a cached `Regex` with a timeout instead of `[GeneratedRegex]` (§10.3), the collector's member overload is
`AddAt`, and V-9 added `ValueObjectJson` and TVAL009.

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

### 3.7 How it reads: one bounded context (owner's question in round 2)

The concern: "I have to define value objects again and again." A value object is defined **once per concept per
bounded context**, in one line, and then reused by every command, event and aggregate. ArtFlow's auction context:

```csharp
// ArtFlow.Domain/Values.cs: every rule of the context, one line per concept
namespace ArtFlow;

public readonly partial record struct Title([Length(3, 80)] string Value);
public readonly partial record struct Description([Length(10, 1000)] string Value);
public readonly partial record struct PersonName([Length(3, 60)] string Value);
public readonly partial record struct Email([Email] string Value);
public readonly partial record struct Money([Range(Min = 0)] decimal Amount, Currency Currency);
```

```csharp
// commands: no rule repeated, the types say it
public sealed partial record CreateArtwork(Id<ArtWork> Id, Title Title, Description Description, Id<Expert> Expert) : IValidatable;
public sealed partial record RenameArtwork(Id<ArtWork> Id, Title Title) : IValidatable;
public sealed partial record RegisterBidder(Id<Bidder> Id, PersonName Name, Email Contact) : IValidatable;
public sealed partial record RegisterExpert(Id<Expert> Id, PersonName Name, Email Contact) : IValidatable;
public sealed partial record PlaceBid(Id<Auction> Auction, Id<Bidder> Bidder, Money Amount) : IValidatable;

// events: the same types; old data always loads (trusting converter)
public sealed record ArtworkCreated(Title Title, Description Description, Id<Expert> Expert);
public sealed record BidPlaced(Id<Bidder> Bidder, Money Amount, DateTimeOffset At);

// aggregate: state in value objects, so it cannot hold an unparsed string
public partial class ArtWork : AggregateRoot<ArtWork>
{
    public Title Title { get; private set; }
    public Result Rename(Title title) => title == Title ? Error.Conflict("Artwork.SameTitle") : Raise(new ArtworkRenamed(title));
}

// using it: strings appear only at the edge
var title = Title.Create(request.Title);                    // Result<Title>
if (title.Value.Length > 40) …                              // implicit to string: reads like a string
string shown = artwork.Title;                               // no .Value needed
```

Counted for this context: **5 rule declarations** serve 5 commands, 2 events and 3 aggregates. With rules on DTOs
(candidate a) the same rules appear **9 times** (`Title` twice, `PersonName` and `Email` twice each, …), and a change from
80 to 100 characters must be found in every copy. What remains per concept is one line; the generator writes `Create`,
the converter, the comparisons and the conversion.

**Incidental limits** that are not a domain concept (page size, a search text) would need a value object each. An escape
hatch is a rule on a primitive parameter of an `IValidatable` command (decision 1 asks):

```csharp
public sealed partial record SearchAuctions([Length(Max = 100)] string Text, [Range(1, 100)] int PageSize) : IValidatable;
```

### 3.8 Naming: the same kind of value with different rules (owner's question in round 2) **[verified]** (`naming/`)

"A name for bidders, experts, artworks …; page sizes and search texts that differ per query: how do we name them?"

1. **A different rule is a different concept; the namespace names it.** Put each value object next to its aggregate and
   give it the plain name. `ArtFlow.Bidders.Name` (max 60) and `ArtFlow.Experts.Name` (max 120) coexist; inside
   `Bidder`, a property `Name Name` and `Name.Create(…)` both resolve (C# "Color Color" rule), with warnings as errors.
   Code that uses both writes `Experts.Name` through a namespace alias. No `BidderName`/`ExpertName` prefixes.
2. **The same rule is one shared type**, in the context's root namespace: `Email`, `Money`, `PersonName` when bidders
   and experts really share the limit.
3. **A limit that belongs to one request, not to the domain** (page size per query, a search text per endpoint) is not a
   value object: it is a rule attribute on the primitive parameter of that `IValidatable` input. Queries are the read
   side (step 10); their limits change with the screen, not with the model.

```csharp
namespace ArtFlow.Bidders;
public readonly partial record struct Name([Length(3, 60)] string Value);
public sealed partial record RegisterBidder(Id<Bidder> Id, Name Name, Email Contact) : IValidatable;

namespace ArtFlow.Experts;
public readonly partial record struct Name([Length(3, 120)] string Value);

namespace ArtFlow.Auctions.Queries;
public sealed partial record SearchAuctions([Length(Max = 100)] string Text, [Range(1, 50)] int PageSize) : IValidatable;
public sealed partial record ListBids(Id<Auction> Auction, [Range(1, 500)] int PageSize) : IValidatable;   // other limit, no new type
```

### 3.9 JSON in the same assembly (found in stage 3) **[verified]** (`stj/`, JIT and Native AOT)

The generated `[JsonConverter]` is invisible to the System.Text.Json source generator (generators do not see each other's
output). A source-generated context **in the same assembly** as the value object therefore writes the record's object
shape, silently: `{"Total":{"Value":40}}`. From **another assembly** it is right (`{"Total":40}`, old data loads), because
the attribute is then in metadata. Tried and failed: `[JsonSourceGenerationOptions(Converters = [typeof(Quantity.ValueJsonConverter)])]`
(compiles, ignored) and a generated static constructor that inserts a resolver into `Default.Options`
(`InvalidOperationException`: options are sealed by their context). What works: a resolver first in the options' chain,
including nested properties and `Option<Quantity>` (`{"Total":40,"Bonus":null}`, 0 IL warnings). Decision V-9.

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

Asked in the session (2026-11-13), one question per decision with a recommendation. Round 1 (scope) is in
[validation.md](../architecture/validation.md#scope-decisions-owner-round-1-2026-11-13). Round 2:

| # | Decision | Owner's answer |
|---|---|---|
| V-1 | value validation mechanism | **(c)**: rules once on (struct) value objects, generated `Validate` on `IValidatable` inputs, run before load. Asked again after "I have to define value objects again and again": §3.7 example accepted; plus the **escape hatch** of rules on primitive parameters of an `IValidatable` input for per-request limits (§3.8) |
| V-2 | value-object shape | **`readonly partial record struct X([Rule] T Value)`**, public constructor (JSON, replay, tests), **TVAL006** warns on `new X(…)` outside the type; custom rules in a user `static void Validate(T value, ref ValidationErrors errors)` that the generator calls after the attribute rules |
| V-3 | invariants (built in step 6) | **I-3**: `[Invariant]` methods returning `Result`, aggregated into a generated `CheckInvariants()`; a violation is the rule's `Error` (usually `Error.Conflict`); the pipeline discards the instance |
| V-4 | `Option`/`Id`/collections/paths | **the type decides**: `Option<T>` optional (rules on `Some`); every non-`Option` member required (`Id.Empty`, `null`/`default` string VO, `null` list or element → `Required`); a missing `int` VO reports its own rule; indexed paths; **C# member names**, the web package converts per segment with the JSON naming policy |
| V-5 | diagnostics | **as proposed**: TVAL001–TVAL005 Error, TVAL006–TVAL008 Warning |
| V-6 | package and names | **one `Tarol.Validations`** (bundled generator; depends on Results, Options, StronglyTypedId); attribute names **`Range`, `Length`, `Pattern`, `Email`, `NotEmpty`, `Count`** |
| V-7 | naming convention | **value objects next to their aggregate with plain names** (`Bidders.Name`, `Experts.Name`), shared ones in the context root, per-request limits on primitive input parameters (§3.8) |
| V-9 | value-object JSON in a same-assembly context (found in stage 3, §3.9) | **resolver + TVAL009**: generated code registers every converter in `ValueObjectJson.Resolver`; Tarol-owned options (event store, web package) insert it; user-owned contexts use `ValueObjectJson.Options(MyJson.Default)`; TVAL009 (Warning) on `MyJson.Default` used directly when the context reaches a same-assembly value object |
| V-8 | replay enforcement | S-3: test with a minimal aggregate now; step 6 repeats it and decides the analyzer |

Consequence of V-1/V-7 for TVAL003: a rule attribute is allowed on a primary-constructor parameter of a value object
**or** of an `IValidatable` record; anywhere else it is TVAL003.

## 10. Changes against Tarol

### 10.1 Projects

| Tarol-Net project | From |
|---|---|
| `src/Validations/Tarol.Validations` | `src/Validations/Validations` + `Validations.Value` + `Validations.Invariant` (rewritten on the agreed design; old files used as the specification) |
| `src/Validations/Tarol.Validations.Generators` | `gen/Validations/Validations.Value.Generators` (rewritten: `ValidationGenerator`, `Emitter`) + new `ValidationAnalyzer`; bundled into `Tarol.Validations` (D-10) |
| `tests/Validations/Tarol.Validations.Tests` | new (the old `Validations.Value.UnitTests` had 0 tests; its models became fixtures) |
| `tests/Validations/Tarol.Validations.Generators.Tests` | replaces `Validations.Invariant.UnitTests` (17 substring tests of a generator that is not migrated): snapshots, diagnostics, incrementality |
| `tests/Validations/Tarol.Validations.AotSmoke`, `.PackageTests`, `.Benchmarks` | new |
| not migrated | `gen/…/Validations.Invariant.Generators` and `InvariantAttribute` (invariants move to step 6, V-3); `Validations.Prerequisite/` (prerequisites are guards, S-1); `gen/…/Validations.Generators` and `tests/…/Validations.UnitTests` (empty); `ValueValidationHelpers` (dead, finding 23) |

### 10.2 Public API and behaviour changes (every one deliberate)

| Tarol | Tarol-Net | Why |
|---|---|---|
| `[GenerateValidation]` on a class (and on Primitives' `Value`, inherited) | no marker: a Tarol rule on a record's primary-constructor parameter makes a value object; `IValidatable` makes an input | S-2, findings 6, 7 |
| rules on any property of any class (`ArtWork.Title [Length]`) | rules on value objects and on primitive parameters of `IValidatable` inputs only; elsewhere TVAL003 | validation.md insight 1, V-1, V-7 |
| `Tarol.Validations.Value.{NotEmpty,Range,Length,Pattern,Email}Attribute` | `Tarol.Validations.{NotEmpty,Range,Length,Pattern,Email,Count}Attribute`, `AttributeTargets.Parameter` | V-6 |
| `Range(int/long/double/decimal)`, `Exclusive` (ignored) | `Range(long, long)`, `Range(double, double)`, `Range(string, string)` (decimal), named `Min`/`Max`; bounds checked against the type at compile time (TVAL005); `Exclusive` removed | findings 19, 21 |
| `Length(int exact)`, `Length(min, max)`, `MinOnly`/`MaxOnly` (unusable) | `Length(min, max)`, named `Min`/`Max` | findings 3, 20 |
| `Pattern(string)`: `new Regex(…, Compiled)`, no timeout | `Pattern(string) { TimeoutMilliseconds = 100, IgnoreCase }`: cached `Regex`, `CultureInvariant`, timeout; a timeout is a `Pattern` field error | finding 16, insight 9 |
| `Email`: a regex (and a second, unused span check) | `Email`: one span check (`Rules.Email`) | finding 23 |
| `NotEmpty` on `Guid` did not compile | `NotEmpty` on strings (blank), `Guid`, `Id<T>` | finding 21 |
| — | `Count(min, max)` on arrays and `IReadOnlyList<T>` | collections (§6) |
| generated `T.Validation.<Prop>.Validate(value) : Result` + `ValidateValues(this T)` | value object: `Create(…) : Result<T>`, `Check(T, ref ValidationErrors)`, constants, conversion, comparisons, `ToString`, `ValueJsonConverter`; input: `Validate(ref ValidationErrors)` + `Validated() : Result<T>` | S-2, V-1 |
| one `Validate` call per **rule** (duplicate violations) | one check chain per value; the first failing rule reports | finding 4 |
| `Violation.Validation("Money:Amount:Range", "Amount")` | `FieldError("Amount", "Range", "0..")`: the code names the rule, the field the **input path**, limits in the detail | insight 2 |
| `Result.Combine` of all rule results | `ValidationErrors` collects, allocates only on the first error; valid path 0 B | §3.2, validation.md §8 |
| nested delegation by `GetSymbolsWithName` (source only), no null check | value objects found also from metadata (`Check`), inputs by `IValidatable`; `null` → `Required` | finding 22 |
| `ValidateInvariants(this T)` over `[Invariant] static bool` | removed here; step 6 builds `[Invariant] Result` rules (V-3) | S-1 |
| `IPrerequisite<T>.Verify : bool`, `IPrerequisiteAsync<T>`, `[Prerequisite(code, text, order)]` | removed: guard clauses in the behaviour returning `Result`, `now` as a parameter; reads in the handler | S-1, insights 3–5, finding 31 |
| VAL001 (Error), VAL999 (exception text) | TVAL001–TVAL005 (Error), TVAL006–TVAL009 (Warning), no exception diagnostics | V-5, findings 11, 24 |
| JSON of value objects: the record's object shape | bare value through the generated converter; reads without validation; `ValueObjectJson.Resolver`/`Options`/`AddValueObjects` for same-assembly contexts | S-2, V-9 |

### 10.3 Implementation notes

* **No `[GeneratedRegex]`.** A source generator cannot hand a `[GeneratedRegex]` partial method to the regex generator,
  for the same reason the JSON generator cannot see the generated `[JsonConverter]` (§3.9, **[verified]** for JSON;
  for regex **[unverified]**, same Roslyn rule). The generated field is `new Regex(pattern, CultureInvariant, timeout)`
  without `RegexOptions.Compiled` (ignored under Native AOT, finding 16); the timeout bounds the cost (§3.4; test
  `Finding_16_…`: 30 × `a` + `!` with 50 ms → `Pattern` error).
* **Paths without allocation.** `ValidationErrors` keeps up to 8 path segments in an `[InlineArray]` and builds the
  string only in `Add` (`AllocationTests`: 0 B for 1,000 valid validations).
* **Module initializers** register each value object's converter (`ValueObjectJson.Register`); private nested value
  objects are not registered (a module initializer must be reachable from the module).
* **Hand-written code wins.** A user `Create`, `Check`, `ToString`, `CompareTo`, conversion, `[JsonConverter]` or
  `IValidatable.Validate` suppresses the generated member.

## 11. Suppressions and analyzer configuration

None in `src/`. No `NoWarn`, no `.editorconfig` change. In tests and samples, by `#pragma` at the narrowest scope with a
comment: `TVAL006` where a fixture builds an invalid value object on purpose (`InputTests`, `AotSmoke`, `Benchmarks`),
`TVAL009` in the one test that measures the hazard it reports (`JsonTests.V9_…`).

## 12. Verification (SDK 11.0.100, 2026-11-14)

| Command | Result |
|---|---|
| `dotnet format Tarol.slnx --verify-no-changes` | clean (exit 0) |
| `dotnet build Tarol.slnx` | 0 warnings, 0 errors |
| `dotnet test --solution Tarol.slnx` | **417 passed**, 0 failed (334 before; Validations 65 + generator 18) |
| `eng/aot-smoke.sh` | `Tarol.Validations.AotSmoke`: 0 IL warnings, 10/10 native checks; Kernel, Options, Results, StronglyTypedId unchanged |
| `eng/verify-packages.sh` | OK; `Tarol.Validations` contains `analyzers/dotnet/cs/Tarol.Validations.Generators.dll` and `skills/tarol-validations/SKILL.md`; the consumer's `project.assets.json` lists `tarol-validations`, `tarol-results`, `tarol-options`, `tarol-strongly-typed-id` |
| diagnostics reach a package consumer | a temporary traps file in `Tarol.Validations.PackageTests` → `error TVAL001` … `error TVAL009` (one trap each; warnings are errors there), then removed |
| skill sample | the skill's example compiled with warnings as errors and the bundled generator (`SkillSampleTests`) |

```
Tarol.Validations 0.0.0-alpha.0.9
    LICENSE
    README.md
    Tarol.Validations.nuspec
    analyzers/dotnet/cs/Tarol.Validations.Generators.dll
    lib/net11.0/Tarol.Validations.dll
    lib/net11.0/Tarol.Validations.xml
    skills/tarol-validations/SKILL.md
== tests/Validations/Tarol.Validations.PackageTests/Tarol.Validations.PackageTests.csproj
ok   generated Create from the package
ok   generated Validate from the package
ok   JSON through the resolver
ALL CHECKS PASSED
```

## 13. Skill evaluation checklist

Run each task with a frontier model in a fresh console project that references the packed package, once with the
skills copied to `.agents/skills/` and once without. Record correctness and token cost.

`tarol-validations`:

1. "A bid amount is a positive decimal with a currency; use it in a command and an event" → one
   `readonly partial record struct Money([Range(Min = 0)] decimal Amount, Currency Currency)`; the command and the event
   use `Money`; no rule repeated on a DTO.
2. "Bidder names 3–60, expert names 3–120" → `Bidders.Name` and `Experts.Name` in their namespaces; no `BidderName`
   prefix, no shared type with the wrong limit.
3. "Validate the PlaceOrder command before loading the order" → `: IValidatable` on a `partial record`, `Validated()`,
   return the `Invalid`; paths like `Lines[1].Quantity`.
4. "Search endpoint with a page size of 1–50" → `[Range(1, 50)] int PageSize` on the `IValidatable` query; no value object.
5. "An IBAN needs a checksum" → `[Length(15, 34)]` plus `static void Validate(string value, ref ValidationErrors errors)`.
6. "Store events with value objects as JSON in the same project" → `ValueObjectJson.Options(MyJson.Default)` (TVAL009).
7. "The auction end time must be at least one day in the future" → a guard in the behaviour with a `now` parameter, not
   a rule attribute.
8. Must not: use `[GenerateValidation]`, `ValidateValues()`, `IPrerequisite`, DataAnnotations or FluentValidation on Tarol
   inputs; validate in a constructor, `On(event)` or a JSON converter; `new X(…)` in live code; `[Required]` on members;
   decompile the assembly or search the web.

Status: checklist written; **not yet run** (needs a model-evaluation harness outside this repository).

## 14. What later steps must do (also in plan.md)

| Step | Must |
|---|---|
| 6 Primitives | `[Invariant]` methods returning `Result`, aggregated into a generated `CheckInvariants()` run after apply on the live path (V-3); drop `[GenerateValidation]` from `Value` and decide whether `Value` stays; repeat the replay test (`ReplayTests`) on the real `AggregateRoot`, with snapshots, and decide the replay analyzer (S-3); events that embed value objects use the trusting converters |
| 7 Changes | validate the command (`IValidatable.Validated()`) **before** loading (S-4); behaviour guards return `Result` (`Error.Conflict` for state, `Invalid.Field` for an input field), `now` as a parameter; reads of other aggregates in the handler; discard the instance when an invariant fails |
| 8 Persistence | insert `ValueObjectJson.Resolver` first into the event store's `JsonSerializerOptions` (`AddValueObjects()`, V-9) |
| 10 CQRS / web package | pipeline order S-4; map `Invalid` to problem details and convert paths per segment with the JSON naming policy (V-4); add `ValueObjectJson.Resolver` to ASP.NET Core's JSON options; map `Microsoft.Extensions.Validation` errors of plain DTOs to `Invalid` where both meet |
| 13 Samples | rebuild ArtFlow's rules on value objects and guards (findings 17, 18 disappear with it) |

## 15. Open items

* Same-assembly JSON contexts need `ValueObjectJson.Options` (V-9). If a later Roslyn lets generators see each other's
  output, the resolver and TVAL009 can go.
* "Missing" vs `0` for an `int`-based value object from JSON (validation.md open question 6): left to the JSON options
  at the edge (`RespectRequiredConstructorParameters`), decided with the web package.
* Invariant failure type: V-3 says the rule's `Error`; whether `Error.Internal` is ever right is decided with step 6.

