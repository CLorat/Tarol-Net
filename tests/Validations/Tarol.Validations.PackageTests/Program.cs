using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;
using Tarol.Validations.PackageTests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

Check("generated Create from the package", Title.Create("ab") == Invalid.Field("", ValidationCodes.Length, "3..10"));
Check("generated Validate from the package", new Rename(Id<Item>.Empty, Title.Create("Lamp").GetValueOrThrow(), Option.None).Validated() == Invalid.Field("Item", "Required"));
var options = ValueObjectJson.Options(ConsumerJson.Default);
Check("JSON through the resolver", JsonSerializer.Serialize(new Renamed(Title.Create("Lamp").GetValueOrThrow()), options.GetTypeInfo<Renamed>()) == """{"title":"Lamp"}""");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Validations.PackageTests
{
    public sealed class Item;

    public readonly partial record struct Title([Length(3, 10)] string Value);

    public sealed partial record Rename(Id<Item> Item, Title Title, Option<Title> Note) : IValidatable;

    public sealed record Renamed(Title Title);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Renamed))]
    internal sealed partial class ConsumerJson : JsonSerializerContext;
}
