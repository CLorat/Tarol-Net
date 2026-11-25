using System.Buffers;
using System.Text;
using System.Text.Json;
using Tarol.Results;
using Tarol.Results.Json;

namespace Tarol.Persistence.Npgsql;

// TN-46: the refusal stored with an idempotency key, in the JSON form of Tarol.Results (no serializer context, AOT-safe)
internal static class Refusals
{
    private static readonly ViolationJsonConverter Converter = new();
    private static readonly JsonSerializerOptions Unused = new();                       // the converter reads no option

    public static string Write(Violation refusal)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Converter.Write(writer, refusal, Unused);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static Violation Read(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return Converter.Read(ref reader, typeof(Violation), Unused);
    }
}
