using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Validations;

namespace Probe;

public sealed class Auction;
public readonly partial record struct Title([Length(3, 60)] string Value);

// the user's JSON context names the generated input, as an endpoint would need
[JsonSerializable(typeof(RenameAuction))]
internal sealed partial class Json : JsonSerializerContext;

public static class Program
{
    public static void Main()
    {
        var input = JsonSerializer.Deserialize("""{"auction":"01a196fb-1600-7c8e-bc04-029ed98b4d56","title":{"value":"x"}}""", Json.Default.RenameAuction)!;
        Console.WriteLine(input.Validated());
    }
}
