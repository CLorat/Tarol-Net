# Tarol.Options

A value that may be absent, as a type: `Option<T>` is either a `T` or `None`. Native-AOT ready. No dependencies.

## Install

```bash
dotnet add package Tarol.Options
```

## 30 seconds

`Option<T>` is a C# 15 union of `T` and `None`, written by hand. It cannot be used as a `T` (CS0029, CS1503), and a `switch` without
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
Option<string> nickname = FindByEmail(email).Bind(static u => u.Nickname);
```

Members: `IsSome`, `IsNone`, `TryGetValue`, `GetValueOrThrow()`, `GetValueOrDefault(fallback)`, `Map`, `Bind`,
`Filter`, `Match`; `Option.Some(v)`, `Option.None`; `T?.ToOption()`, `option.ToNullable()`.

## Rules

* Write `found ? value : Option.None`, **not** `found ? value : default`. The conditional has the type `T`, so
  `default` is `0` and becomes `Some(0)` (TOPT004).
* `Option.Some(null)` and `Option<string> o = (string?)null` throw `ArgumentNullException` (CS8604 warns first);
  `T?.ToOption()` maps `null` to `None`. `Option<string?>` and `Option<int?>` are CS8714.
* Test the case, not null: `o is User u`, `o is None`, `o.IsSome`. `o is { }` and `o != null` are always true (TOPT002).
* Use `Option<T>` for aggregate and entity properties, events, DTOs and query results. Tarol's own store maps it to a
  nullable column (the planned Npgsql store, step 8); events and DTOs use the JSON contract below.
* Three framework edges do not understand `Option<T>`. Bind `T?` there and convert with `ToOption()` / `ToNullable()`:
  * a Minimal API query or route parameter: an `Option<int>` parameter is inferred as the body (400 on GET);
  * an `IOptions<T>` configuration class: the binder leaves an `Option<int>` property `None`, and the source-generated
    binder does not compile;
  * raw Npgsql parameters and readers outside Tarol's store: Npgsql does not accept an `Option` as a parameter or a
    column type.
* OpenAPI: ASP.NET Core describes `Option<T>` as an empty, required schema. Register a schema transformer that
  describes it as `T` or `null` and not required (see `docs/migration/options.md` §3.11).

```csharp
app.MapGet("/users", (int? page) => List(page.ToOption()));
command.Parameters.Add(new() { Value = (object?)profile.Age.ToNullable() ?? DBNull.Value });
Option<int> age = reader.IsDBNull(2) ? Option.None : reader.GetFieldValue<int>(2);
```

## JSON

The value, or `null` for `None`. This is the same as `T?`, so an event field can change between `T?` and
`Option<T>` without breaking stored JSON. A missing property reads as `None`, and
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` omits `None`:

```text
record Profile(int Id, Option<string> Nickname, Option<int> Age);
{"id":1,"nickname":"ada","age":36}
{"id":1,"nickname":null,"age":null}
```

Native AOT: with a `JsonSerializerContext`, list the DTO and every `T` that is not a primitive. `string`, `int`, `long`,
`bool`, `decimal`, `double`, `Guid` and the date and time types resolve from this package's own metadata.

## Results

Absence that is a failure is a `Result<T>`. `Tarol.Results` depends on this package and adds
`option.ToResult(Error.NotFound("User.NotFound"))` and `result.ToOption()`.

## Analyzer (bundled)

| Id | Severity | Reports |
|---|---|---|
| TOPT001 | Warning | reading the untyped `option.Value` (the union protocol's `object`) |
| TOPT002 | Warning | `o is { }`, `o is not null`, `o is null`, `o == null` on an `Option`: always true or false |
| TOPT003 | Warning | `o.TryGetValue(out var v);` as a statement: the result is ignored, `v` may be `default` |
| TOPT004 | Warning | `found ? value : default` / `value ?? default` converted to `Option<T>`: becomes `Some(0)` |

## More

* Agent skill: `skills/tarol-options/SKILL.md` (in the package)
* Design, measurements and migration from Tarol: `docs/migration/options.md` in the repository

Licence: proprietary, see `LICENSE`.
