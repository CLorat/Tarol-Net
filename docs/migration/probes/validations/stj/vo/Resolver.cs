using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
namespace Lib;
// What Tarol.Validations would ship: generated code registers each value object's converter (module initializer).
public sealed class ValueObjectResolver : IJsonTypeInfoResolver
{
    public static readonly ValueObjectResolver Instance = new();
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        => type == typeof(Quantity) ? JsonMetadataServices.CreateValueInfo<Quantity>(options, new Quantity.ValueJsonConverter()) : null;
}
