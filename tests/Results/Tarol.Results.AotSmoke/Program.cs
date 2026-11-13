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

// ---- Option<T> (docs/migration/options.md)
Check("Option sizes: Option<int> 8 B, Option<Guid> 20 B, Option<string> 16 B",
    Unsafe.SizeOf<Option<int>>() == 8 && Unsafe.SizeOf<Option<Guid>>() == 20 && Unsafe.SizeOf<Option<string>>() == 16);
Check("Option switch over T | None, default is None", Name(Option.Some(new User("Ada"))) == "Ada" && Name(Option.None) == "guest" && Name(default) == "guest");
OptionLoop(); // warm-up: static lambdas cache their delegates on first use
var optionBefore = GC.GetAllocatedBytesForCurrentThread();
var optionSum = OptionLoop();
var optionAllocated = GC.GetAllocatedBytesForCurrentThread() - optionBefore;
Check("Option switch and Map/Filter allocate nothing", optionAllocated == 0 && optionSum == 499_500);
Check("Option to Result and back", default(Option<User>).ToResult(Error.NotFound("User.NotFound")) is Error { Code: "User.NotFound" }
    && Result.Success(3).ToOption() == Option.Some(3) && ((int?)null).ToOption().IsNone && Option.Some(4).ToNullable() == 4);
var profile = new Profile(1, "ada", default);
Check("Option JSON: value or null (wire = T?)", JsonSerializer.Serialize(profile, SmokeJson.Default.Profile) == """{"id":1,"nickname":"ada","age":null}""");
Check("Option JSON: missing property reads as None", JsonSerializer.Deserialize("""{"id":1}""", SmokeJson.Default.Profile) == new Profile(1, default, default));
Check("Option JSON: primitives without a context entry", JsonSerializer.Serialize(new Profile(2, default, 7), ProfileOnlyJson.Default.Profile) == """{"id":2,"nickname":null,"age":7}""");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static string Describe(Result<int> result) => result switch
{
    int v => $"value {v}",
    Invalid invalid => $"invalid {invalid.Errors[0].Field}",
    Error error => $"error {error.ToHttpStatusCode()}",
};

static int OptionLoop()
{
    var sum = 0;
    for (var i = 0; i < 1000; i++)
    {
        Option<int> o = i % 2 == 0 ? i : Option.None;
        sum += o.Map(static v => v + 1).Filter(static v => v > 0).GetValueOrDefault(0) + o switch { int v => v, None => 0 };
    }

    return sum;
}

static string Name(Option<User> user) => user switch
{
    User u => u.Name,
    None => "guest",
};

static Result<User> RoundTrip(Result<User> result)
    => JsonSerializer.Deserialize(JsonSerializer.Serialize(result, SmokeJson.Default.ResultUser), SmokeJson.Default.ResultUser);

namespace Tarol.Results.AotSmoke
{
    public sealed record User(string Name);

    public sealed record Profile(int Id, Option<string> Nickname, Option<int> Age);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Result<User>))]
    [JsonSerializable(typeof(User))]
    [JsonSerializable(typeof(Result))]
    [JsonSerializable(typeof(Violation))]
    [JsonSerializable(typeof(Profile))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int))]
    internal sealed partial class SmokeJson : JsonSerializerContext;

    [JsonSerializable(typeof(Result<int>))]
    [JsonSerializable(typeof(int))]
    internal sealed partial class IntOnlyJson : JsonSerializerContext;

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Profile))]
    internal sealed partial class ProfileOnlyJson : JsonSerializerContext;
}
