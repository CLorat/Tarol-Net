# Tarol.Results

Results without exceptions. `Result<T>` is either the value or a `Violation`, and `Result` is either `Success` or a
`Violation`. A violation is exactly one of two kinds:

* `Invalid`: the input is invalid. It holds one or more `FieldError(Field, Code, Detail?)` and maps to HTTP 400.
* `Error`: one light, non-fatal error. It holds an `ErrorType` (NotFound, Conflict, Forbidden, …), a `Code` and an
  optional `Detail`, and maps to the type's HTTP status.

Both result types are C# 15 unions written by hand. They hold the value plus one reference (`Result<int>` is 16 B,
`Result<Guid>` 24 B, `Result` 8 B). Pattern matching does not box, and a `switch` is exhaustive: a missing arm is CS8509.
Native-AOT ready. The only dependency is `Tarol.Options` (`Option<T>`).

## Install

```bash
dotnet add package Tarol.Results
```

## 30 seconds

```csharp
using Tarol.Results;

public Result<User> Rename(Guid id, string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return Invalid.Field("Name", "Name.Required");
    return users.TryGetValue(id, out var user) ? user with { Name = name } : Error.NotFound("User.NotFound");
}

// consume with a switch: T | Invalid | Error is exhaustive
var response = Rename(id, name) switch
{
    User user       => TypedResults.Ok(user),
    Invalid invalid => TypedResults.ValidationProblem(invalid.Errors.GroupBy(e => e.Field)
                           .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
    Error error     => TypedResults.Problem(statusCode: error.ToHttpStatusCode(), title: error.Code, detail: error.Detail),
};

// pass failures on with one arm
Result<Order> next = Rename(id, name) switch { User u => Order.For(u), Violation v => v };
```

Async pipelines use `ValueTask` and pass a `CancellationToken`:

```csharp
Result<Receipt> receipt = await repository.GetById(id, ct)
    .Ensure(static o => o.IsOpen, Error.Conflict("Order.Closed"))
    .BindAsync((o, ct) => payments.Charge(o, ct), ct);
```

## Rules

* `default(Result<T>)` and `default(Result)` are failures: `Error.Uninitialized` (Internal, 500).
* A success value is never `null` (`ArgumentNullException`).
* `Combine` and `Sequence`: when the failures are all `Invalid`, their field errors are concatenated. Otherwise the first `Error` is returned.
* `result.Value` is the union's untyped `object` (C# requires it). Use a pattern, `TryGetValue`, or
  `GetValueOrThrow()` (TRES003).
* Prefer a `switch` to `Match`: a capturing lambda allocates on every call.

## Option&lt;T&gt;

`Option<T>` (a value that may be absent) is in its own package, `Tarol.Options`, which this package depends on.
`Tarol.Results` adds the two conversions:

```csharp
using Tarol.Options;
using Tarol.Results;

Result<User> found = FindByEmail(email).ToResult(Error.NotFound("User.NotFound"));   // absence as a failure
Option<User> back = found.ToOption();                                                // and back (drops the violation)
```

## JSON

The contract is fixed and works with the source generator and Native AOT:

```text
{"value":{…}}
{"failure":{"kind":"invalid","errors":[{"field":"Name","code":"Name.Required"}]}}
{"failure":{"kind":"error","type":"NotFound","code":"User.NotFound","detail":"…"}}
{"success":true}                                   // non-generic Result
```

With a `JsonSerializerContext`, list `Result<T>` **and** `T`. `Violation` needs no entry:

```csharp
[JsonSerializable(typeof(Result<User>))]
[JsonSerializable(typeof(User))]
partial class AppJson : JsonSerializerContext;
```

## Analyzers (bundled)

| Id | Severity | Reports |
|---|---|---|
| TRES001 | Error | `Result<Violation>`, `Result<Error>`, `Result<object>`, `Result<Result<…>>`: value and failure cannot be told apart |
| TRES002 | Warning | a discarded `Result` / `Task<Result…>` / `ValueTask<Result…>` (`repository.Save(x);`); use `_ =` to discard on purpose |
| TRES003 | Warning | reading the untyped `result.Value` |
| TRES005 | Warning | `result.TryGetValue(out var v);` as a statement: the result is ignored, `v` may be `default` |
| TRES006 | Warning | `found ? value : default` / `value ?? default` converted to `Result<T>`: becomes `Success(0)` |

The Option rules (TOPT001-TOPT004) come with `Tarol.Options`. There is no TRES004 (it became TOPT002).

## More

* Agent skill: `skills/tarol-results/SKILL.md` (in the package); `tarol-options` ships with `Tarol.Options`
* Design, measurements and migration from Tarol: `docs/migration/results.md` and `docs/migration/options.md` in the repository

Licence: proprietary, see `LICENSE`.
