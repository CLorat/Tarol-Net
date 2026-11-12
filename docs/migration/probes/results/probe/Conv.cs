using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
namespace Probe;

// E: C2 layout + explicit JSON shape { "value": … } | { "failure": {…} } via an open generic converter (AOT pattern from Id<T>)
[Union]
[JsonConverter(typeof(EResultConverter<>))]
public readonly struct EResult<T> : IUnion
{
    private readonly T _value; private readonly Failure? _state;
    public EResult(T value) { ArgumentNullException.ThrowIfNull(value); _value = value; _state = C2Result<Ok>.OkSentinel; }
    public EResult(Failure failure) { ArgumentNullException.ThrowIfNull(failure); _value = default!; _state = failure; }
    public bool IsSuccess => ReferenceEquals(_state, C2Result<Ok>.OkSentinel);
    public object? Value => IsSuccess ? _value : _state ?? Failure.Uninitialized;
    public bool HasValue => true;
    public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _value; return IsSuccess; }
    public bool TryGetValue([NotNullWhen(true)] out Failure? value) { value = IsSuccess ? null : _state ?? Failure.Uninitialized; return value is not null; }
    public static implicit operator EResult<T>(T v) => new(v);
    public static implicit operator EResult<T>(Failure f) => new(f);
}

public sealed class EResultConverter<T> : JsonConverter<EResult<T>>
{
    public override EResult<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("object expected");
        EResult<T>? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString(); reader.Read();
            if (name == "value") result = new EResult<T>(((JsonTypeInfo<T>)options.GetTypeInfo(typeof(T))).Deserialize(ref reader)!);
            else if (name == "failure") result = new EResult<T>(((JsonTypeInfo<Failure>)options.GetTypeInfo(typeof(Failure))).Deserialize(ref reader)!);
            else reader.Skip();
        }
        return result ?? throw new JsonException("neither value nor failure");
    }
    public override void Write(Utf8JsonWriter writer, EResult<T> r, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (r)
        {
            case T v: writer.WritePropertyName("value"); ((JsonTypeInfo<T>)options.GetTypeInfo(typeof(T))).Serialize(writer, v); break;
            case Failure f: writer.WritePropertyName("failure"); ((JsonTypeInfo<Failure>)options.GetTypeInfo(typeof(Failure))).Serialize(writer, f); break;
        }
        writer.WriteEndObject();
    }
}
file static class Ext { public static void Serialize<T>(this JsonTypeInfo<T> ti, Utf8JsonWriter w, T v) => JsonSerializer.Serialize(w, v, ti); public static T? Deserialize<T>(this JsonTypeInfo<T> ti, ref Utf8JsonReader r) => JsonSerializer.Deserialize(ref r, ti); }
