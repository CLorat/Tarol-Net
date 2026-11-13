# Tarol.Results

Results without exceptions. `Result<T>` is either the value or a `Violation`, and `Result` is either `Success` or a
`Violation`. A violation is exactly one of two kinds:

* `Invalid`: the input is invalid. It holds one or more `FieldError(Field, Code, Detail?)` and maps to HTTP 400.
* `Error`: one light, non-fatal error. It holds an `ErrorType` (NotFound, Conflict, Forbidden, …), a `Code` and an
  optional `Detail`, and maps to the type's HTTP status.

Both result types are C# 15 unions written by hand. They hold the value plus one reference (`Result<int>` is 16 B,
`Result<Guid>` 24 B, `Result` 8 B). Pattern matching does not box, and a `switch` is exhaustive: a missing arm is CS8509.
`Option<T>` is either a `T` or `None`, with the same code for value and reference types (see below).
Native-AOT ready. The package has no dependencies.

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

## Option&lt;T&gt;: a value that may be absent

`Option<T>` is a C# 15 union of `T` and `None`. It cannot be used as a `T` (CS0029, CS1503), and a `switch` without
the `None` arm is CS8509, so the caller must handle absence. This holds in every nullable context, and the code is
the same for `int`, `Guid`, `Id<User>`, `string` and `User`. `default` is `None`. `Option<int>` is 8 B and
`Option<User>` 16 B; pattern matching does not box.

```csharp
public Option<User> FindByEmail(string email) => byEmail.TryGetValue(email, out var user) ? user : Option.None;

string name = FindByEmail(email) switch
{
    User user => user.Name,
    None      => "guest",
};

Option<int> age = user.Age;                                    // the same API for a value type
int years = age.GetValueOrDefault(0);
Option<string> name = FindByEmail(email).Map(static u => u.Name);

Result<User> found = FindByEmail(email).ToResult(Error.NotFound("User.NotFound"));   // absence as a failure
Option<User> back = found.ToOption();                                                // and back
```

Members: `IsSome`, `IsNone`, `TryGetValue`, `GetValueOrThrow()`, `GetValueOrDefault(fallback)`, `Map`, `Bind`,
`Filter`, `Match`, `ToResult(violation)`; `Option.Some(v)`, `Option.None`; `T?.ToOption()`, `option.ToNullable()`.

Rules:

* Write `found ? value : Option.None`, **not** `found ? value : default`. The conditional has the type `T`, so
  `default` is `0` and becomes `Some(0)` (TRES006; the same applies to `Result<T>`, where it becomes `Success(0)`).
* `Option.Some(null)` and `Option<string> o = (string?)null` throw `ArgumentNullException` (CS8604 warns first);
  `T?.ToOption()` maps `null` to `None`. `Option<string?>` and `Option<int?>` are CS8714.
* Test the case, not null: `o is User u`, `o is None`, `o.IsSome`. `o is { }` and `o != null` are always true (TRES004).
* **Keep `T?` at the edges that bind by reflection or by column type**, and convert there with `ToOption()` /
  `ToNullable()`. A Minimal API `Option<int>` parameter is inferred as the body (400 on GET). The configuration binder
  leaves an `Option<int>` property `None`, and the source-generated binder does not compile. Npgsql does not accept an
  `Option` as a parameter or a column type, and EF Core cannot map a struct `Option` to a `NULL` column.

```csharp
app.MapGet("/users", (int? page) => List(page.ToOption()));
command.Parameters.Add(new() { Value = (object?)profile.Age.ToNullable() ?? DBNull.Value });
Option<int> age = reader.IsDBNull(2) ? Option.None : reader.GetFieldValue<int>(2);
```

JSON: the value, or `null` for `None`. This is the same as `T?`, so an event field can change between `T?` and
`Option<T>` without breaking stored JSON. A missing property reads as `None`, and
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` omits `None`:

```text
record Profile(int Id, Option<string> Nickname, Option<int> Age);
{"id":1,"nickname":"ada","age":36}
{"id":1,"nickname":null,"age":null}
```

## JSON

The contract is fixed and works with the source generator and Native AOT:

```text
{"value":{…}}
{"failure":{"kind":"invalid","errors":[{"field":"Name","code":"Name.Required"}]}}
{"failure":{"kind":"error","type":"NotFound","code":"User.NotFound","detail":"…"}}
{"success":true}                                   // non-generic Result
```

With a `JsonSerializerContext`, list `Result<T>` **and** `T`. `Violation` needs no entry, and neither do the common
primitives inside an `Option<T>` (`string`, `int`, `long`, `bool`, `decimal`, `double`, `Guid`, date and time types):

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
| TRES003 | Warning | reading the untyped `result.Value` / `option.Value` |
| TRES004 | Warning | `o is { }`, `o is not null`, `o is null`, `o == null` on an `Option`: always true or false |
| TRES005 | Warning | `x.TryGetValue(out var v);` as a statement: the result is ignored, `v` may be `default` |
| TRES006 | Warning | `found ? value : default` / `value ?? default` converted to `Option<T>` or `Result<T>`: becomes `Some(0)` / `Success(0)` |

## More

* Agent skills: `skills/tarol-results/SKILL.md` and `skills/tarol-options/SKILL.md` (in the package)
* Design, measurements and migration from Tarol: `docs/migration/results.md` and `docs/migration/options.md` in the repository

Licence: proprietary, see `LICENSE`.
