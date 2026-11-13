using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
namespace Probe;

// C, contract "null-or-value": Some(v) = v, None = null; absent property = None (default)
public sealed class OptionNullConverter<T> : JsonConverter<Option<T>> where T : notnull
{
    public override bool HandleNull => true;
    public override Option<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        var info = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var v = JsonSerializer.Deserialize(ref reader, info);
        return v is null ? default : new Option<T>(v);
    }
    public override void Write(Utf8JsonWriter writer, Option<T> value, JsonSerializerOptions options)
    {
        if (value.TryGetValue(out T? v)) JsonSerializer.Serialize(writer, v, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
        else writer.WriteNullValue();
    }
}

// C, contract "envelope": Some(v) = {"value":v}, None = {}
public sealed class OptionEnvelopeConverter<T> : JsonConverter<Option<T>> where T : notnull
{
    public override Option<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("object expected");
        Option<T> result = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString(); reader.Read();
            if (name == "value") result = new Option<T>(JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))!);
            else reader.Skip();
        }
        return result;
    }
    public override void Write(Utf8JsonWriter writer, Option<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.TryGetValue(out T? v)) { writer.WritePropertyName("value"); JsonSerializer.Serialize(writer, v, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T))); }
        writer.WriteEndObject();
    }
}

public sealed class OptionNullConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Option<>);
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "probe")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trim", "IL2055", Justification = "probe")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trim", "IL2070", Justification = "probe")]
    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) => (JsonConverter)Activator.CreateInstance(typeof(OptionNullConverter<>).MakeGenericType(t.GetGenericArguments()))!;
}

public sealed record ADto(int Id, string? Nick, int? Age, Guid? Ref);
public sealed record CDto(int Id, Option<string> Nick, Option<int> Age, Option<Guid> Ref);
public sealed class CDtoIgnore
{
    public int Id { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public Option<string> Nick { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public Option<int> Age { get; init; }
}
public sealed record DDto(int Id, DOption<string> Nick, DOption<int> Age);

[JsonSerializable(typeof(ADto))]
[JsonSerializable(typeof(CDto))]
[JsonSerializable(typeof(CDtoIgnore))]
[JsonSerializable(typeof(Option<int>))]
[JsonSerializable(typeof(Option<User>))]
[JsonSerializable(typeof(Option<Id<User>>))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Tarol.Results.Result<User>))]
[JsonSerializable(typeof(DOption<int>))]
[JsonSerializable(typeof(DDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ProbeContext : JsonSerializerContext;
