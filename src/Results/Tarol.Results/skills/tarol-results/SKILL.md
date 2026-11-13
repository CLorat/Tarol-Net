---
name: tarol-results
description: >-
  Use when writing or consuming Tarol.Results: returning Result<T> or Result instead of throwing, creating
  Invalid (field errors) or Error (NotFound, Conflict, …) violations, switching over results, ValueTask pipelines,
  combining validations, or serializing results as JSON. Covers Result<T>, Result, Success, Violation, Invalid,
  FieldError, Error, ErrorType, Combine, Sequence and the TRES001-TRES006 analyzers (for a value that may be absent, load tarol-options). Do not decompile the assembly
  or search the web for an older Tarol.Results API: the typed Value, Violation.None, ViolationType and IResult no longer exist.
---
# Tarol.Results

## Rules

1. A failure is a `Violation`, which is exactly one of two kinds:
   * `Invalid`: one or more `FieldError(field, code, detail?)`, HTTP 400;
   * `Error`: an `ErrorType`, a `code` and an optional `detail`, HTTP by type.

   You cannot add another kind (the hierarchy is `closed`). A new situation needs a new **code**, not a new type.
2. Return values and violations directly. The implicit conversions build the result:
   `return user;`, `return Invalid.Field("Name", "Name.Required");`, `return Error.NotFound("User.NotFound");`.
3. Consume with `switch` or `is`. `T | Invalid | Error` and `T | Violation` are exhaustive, and a missing arm is CS8509.
   `Success | Violation` is the same for the non-generic `Result`.
4. Never read `result.Value`. It is the union's `object` and boxes (TRES003). Use a pattern, `TryGetValue(out T)`,
   or `GetValueOrThrow()` where a failure would be a bug.
5. Never ignore a returned result (TRES002). Handle it, return it, or write `_ = …` on purpose.
6. `T` must not be a `Violation`, `object` or another result (TRES001, an error).
7. Async members use `ValueTask` only and receive the `CancellationToken`: `BindAsync((value, ct) => …, ct)`.
8. `default(Result<T>)` / `default(Result)` is the failure `Error.Uninitialized` (Internal, 500), never a success.
9. `Combine` / `Sequence`: when all failures are `Invalid`, the field errors are concatenated. Otherwise the first `Error` is returned.
10. Never `return ok ? value : default;` for a `Result<T>`: `default` is `default(T)`, so it becomes `Success(0)` (TRES006).
    Return a violation in the other branch.
11. A value that may be absent is an `Option<T>` (skill `tarol-options`). Turn it into a failure with
    `option.ToResult(Error.NotFound("…"))`; `result.ToOption()` drops the violation.

## Minimal correct example

```csharp
using Tarol.Results;

public sealed class RenameUser(IUserStore store)
{
    public async ValueTask<Result<User>> Handle(Guid id, string? name, string? email, CancellationToken ct)
    {
        var input = Result.Combine(
            string.IsNullOrWhiteSpace(name) ? Invalid.Field("Name", "Name.Required") : Result.Success(),
            email is not null && !email.Contains('@') ? Invalid.Field("Email", "Email.Invalid") : Result.Success());
        if (input is Violation invalid)
        {
            return invalid;                                  // both field errors together
        }

        return await store.Find(id, ct) switch
        {
            User user => user with { Name = name! },
            Violation violation => violation,                // NotFound etc. passed on
        };
    }
}

// endpoint (ASP.NET Core minimal API)
app.MapPut("/users/{id:guid}", async (Guid id, RenameRequest body, RenameUser handler, CancellationToken ct) =>
    await handler.Handle(id, body.Name, body.Email, ct) switch
    {
        User user       => Results.Ok(user),
        Invalid invalid => Results.ValidationProblem(invalid.Errors.GroupBy(e => e.Field)
                               .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
        Error error     => Results.Problem(statusCode: error.ToHttpStatusCode(), title: error.Code, detail: error.Detail),
    });
```

Factories: `Error.NotFound(code)`, `Error.NotFound<TEntity, TId>(id)` (code `"{TEntity}.NotFound"`, no boxing),
`Error.Conflict`, `Error.Forbidden()`, `Error.Unauthorized()`, `Error.Gone`, `Error.UnprocessableEntity` (broken business rule),
`Error.TooManyRequests`, `Error.Unavailable`, `Error.Timeout`, `Error.Internal`; `Invalid.Field(field, code)`,
`new Invalid(params FieldError[])`, `invalid.Combine(other)`. Results: `Result.Success()`, `Result.Success(value)`,
`Result<T>.Failure(violation)`. Combinators: `Map`, `Bind`, `Ensure`, `Tap`, `TapFailure`, `Match`,
`GetValueOrDefault`, `ToResult()`, `MapAsync`/`BindAsync`/`EnsureAsync`/`TapAsync`, plus `Map`/`Bind`/`BindAsync`/`Ensure`/
`Tap`/`TapFailure`/`Match` on `ValueTask<Result<T>>`; `Result.Combine(r1, r2)` gives a tuple; `results.Sequence()`.

## Common mistakes

| Mistake | What happens |
|---|---|
| `var user = result.Value;` | TRES003 warning; `user` is `object` (boxed for value types) |
| `repository.Save(order);` returning `Result` | TRES002 warning (an error under warnings-as-errors) |
| `Result<Violation>`, `Result<object>`, `Result.Success(error)` | TRES001 error; `Result<Violation> r = v;` is also CS0457 |
| `switch { User u => …, Invalid i => … }` | CS8509: the `Error` arm is missing |
| `new Result<string>(null!)`, `Result<User> r = (User?)null!;` | `ArgumentNullException` |
| `new Invalid([])`, `new Error(ErrorType.NotFound, "")` | `ArgumentException` |
| `GetValueOrThrow()` on a failure | `InvalidOperationException` whose message contains the violation |
| `Match(v => v + local, …)` in a hot path | allocates a closure and a delegate per call; use `switch` |
| JSON context lists only `Result<T>` | `NotSupportedException: JsonTypeInfo metadata for type 'T' was not provided`; also list `T` |
| `class MyFailure : Violation` in your assembly | CS9382: the closed type comes from another assembly |
| a `HttpResults.NotFound` / `TypedResults` name used as a case | the cases are `Invalid` and `Error`, which do not clash with ASP.NET Core names |

## JSON

`{"value":…}` for a success, `{"failure":{"kind":"invalid","errors":[{"field":…,"code":…}]}}` or
`{"failure":{"kind":"error","type":"NotFound","code":…,"detail":…}}` for a failure, and `{"success":true}` for a successful
non-generic `Result`. The names are fixed and do not follow your naming policy. Malformed input throws `JsonException`.

## AOT

No reflection. The JSON converters are applied through `[JsonConverter]` and work with `JsonSerializerContext` and
`JsonSerializerIsReflectionEnabledByDefault=false`. Put `Result<T>` and `T` in your context. `Violation` resolves from
the package's own generated metadata when your context does not list it.
