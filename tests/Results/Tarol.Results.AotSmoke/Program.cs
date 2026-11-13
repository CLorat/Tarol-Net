using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Results;
using Tarol.Results.AotSmoke;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

Check("running as native code", !RuntimeFeature.IsDynamicCodeSupported);
Check("sizes: Result 8 B, Result<int> 16 B, Result<Guid> 24 B",
    Unsafe.SizeOf<Result>() == 8 && Unsafe.SizeOf<Result<int>>() == 16 && Unsafe.SizeOf<Result<Guid>>() == 24);

Check("switch over T | Invalid | Error", Describe(1) == "value 1" && Describe(Invalid.Field("Name", "Name.Required")) == "invalid Name" && Describe(Error.NotFound("User.NotFound")) == "error 404");
Check("default reads as Error.Uninitialized", Describe(default) == "error 500" && default(Result).TryGetValue(out Violation? uninitialized) && uninitialized == Error.Uninitialized);

Describe(0);
var before = GC.GetAllocatedBytesForCurrentThread();
var sum = 0;
for (var i = 0; i < 1000; i++)
{
    Result<int> r = i;
    sum += r switch { int v => v, Violation => 0 };
}

Check("switch over a value-type success allocates nothing", GC.GetAllocatedBytesForCurrentThread() - before == 0 && sum == 499_500);

var chained = Result.Success(2).Map(static v => v * 10).Bind(static v => v > 10 ? Result.Success(v.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Result<string>.Failure(Error.Conflict("x")));
Check("Map and Bind chain", chained is "20");
var combined = Result.Combine(Invalid.Field("A", "A.X"), Result.Success(), Invalid.Field("B", "B.X"));
Check("Combine concatenates field errors", combined is Invalid { Errors.Count: 2 });
var asyncResult = await Result.Success(1).BindAsync(static async (v, ct) =>
{
    await Task.Yield();
    return Result.Success(v + 1);
}, CancellationToken.None);
Check("ValueTask pipeline", asyncResult is 2);
Check("NotFound<TEntity, TId> formats the id", Error.NotFound<User, Guid>(Guid.Empty).Detail == "User '00000000-0000-0000-0000-000000000000' was not found.");

var user = Result.Success(new User("Ada"));
Result<User> invalid = new Invalid(new FieldError("Name", "Name.Required"));
Result<User> error = Error.NotFound("User.NotFound", "No user 7.");
Check("JSON success envelope", JsonSerializer.Serialize(user, SmokeJson.Default.ResultUser) == """{"value":{"name":"Ada"}}""");
Check("JSON invalid contract", JsonSerializer.Serialize(invalid, SmokeJson.Default.ResultUser) == """{"failure":{"kind":"invalid","errors":[{"field":"Name","code":"Name.Required"}]}}""");
Check("JSON error contract", JsonSerializer.Serialize(error, SmokeJson.Default.ResultUser) == """{"failure":{"kind":"error","type":"NotFound","code":"User.NotFound","detail":"No user 7."}}""");
Check("JSON round-trips all three", RoundTrip(user) == user && RoundTrip(invalid) == invalid && RoundTrip(error) == error);
Check("JSON non-generic Result", JsonSerializer.Serialize(Result.Success(), SmokeJson.Default.Result) == """{"success":true}"""
    && JsonSerializer.Deserialize(JsonSerializer.Serialize((Result)Error.Gone("g"), SmokeJson.Default.Result), SmokeJson.Default.Result) == Result.Failure(Error.Gone("g")));
Check("JSON without Violation in the context (package metadata)", JsonSerializer.Serialize(Result<int>.Failure(Error.Timeout("t")), IntOnlyJson.Default.ResultInt32) == """{"failure":{"kind":"error","type":"Timeout","code":"t"}}""");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static string Describe(Result<int> result) => result switch
{
    int v => $"value {v}",
    Invalid invalid => $"invalid {invalid.Errors[0].Field}",
    Error error => $"error {error.ToHttpStatusCode()}",
};

static Result<User> RoundTrip(Result<User> result)
    => JsonSerializer.Deserialize(JsonSerializer.Serialize(result, SmokeJson.Default.ResultUser), SmokeJson.Default.ResultUser);

namespace Tarol.Results.AotSmoke
{
    public sealed record User(string Name);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Result<User>))]
    [JsonSerializable(typeof(User))]
    [JsonSerializable(typeof(Result))]
    [JsonSerializable(typeof(Violation))]
    internal sealed partial class SmokeJson : JsonSerializerContext;

    [JsonSerializable(typeof(Result<int>))]
    [JsonSerializable(typeof(int))]
    internal sealed partial class IntOnlyJson : JsonSerializerContext;
}
