using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Options.AotSmoke;

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
Check("Option sizes: Option<int> 8 B, Option<Guid> 20 B, Option<string> 16 B",
    Unsafe.SizeOf<Option<int>>() == 8 && Unsafe.SizeOf<Option<Guid>>() == 20 && Unsafe.SizeOf<Option<string>>() == 16);
Check("Option switch over T | None, default is None", Name(Option.Some(new User("Ada"))) == "Ada" && Name(Option.None) == "guest" && Name(default) == "guest");
OptionLoop(); // warm-up: static lambdas cache their delegates on first use
var optionBefore = GC.GetAllocatedBytesForCurrentThread();
var optionSum = OptionLoop();
var optionAllocated = GC.GetAllocatedBytesForCurrentThread() - optionBefore;
Check("Option switch and Map/Filter allocate nothing", optionAllocated == 0 && optionSum == 499_500);
Check("Option to and from T?", ((int?)null).ToOption().IsNone && ((string?)"a").ToOption() == Option.Some("a")
    && Option.Some(4).ToNullable() == 4 && default(Option<string>).ToNullable() is null);
var profile = new Profile(1, "ada", default);
Check("Option JSON: value or null (wire = T?)", JsonSerializer.Serialize(profile, SmokeJson.Default.Profile) == """{"id":1,"nickname":"ada","age":null}""");
Check("Option JSON: missing property reads as None", JsonSerializer.Deserialize("""{"id":1}""", SmokeJson.Default.Profile) == new Profile(1, default, default));
Check("Option JSON: primitives without a context entry", JsonSerializer.Serialize(new Profile(2, default, 7), ProfileOnlyJson.Default.Profile) == """{"id":2,"nickname":null,"age":7}""");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

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

namespace Tarol.Options.AotSmoke
{
    public sealed record User(string Name);

    public sealed record Profile(int Id, Option<string> Nickname, Option<int> Age);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Profile))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int))]
    internal sealed partial class SmokeJson : JsonSerializerContext;

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Profile))]
    internal sealed partial class ProfileOnlyJson : JsonSerializerContext;
}
