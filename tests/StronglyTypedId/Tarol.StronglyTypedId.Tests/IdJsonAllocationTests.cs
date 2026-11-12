using System.Text;
using System.Text.Json;

namespace Tarol.StronglyTypedId.Tests;

/// <summary>The converter's read and write paths allocate nothing (Tarol's property-name read allocated a string per key).</summary>
public class IdJsonAllocationTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("{\"018f2b3c-1234-7abc-9def-0123456789ab\":\"018f2b3c-1234-7abc-9def-0123456789ab\"}");

    [Fact]
    public void Reading_A_Value_And_A_Property_Name_Allocates_Nothing()
    {
        var converter = new IdJsonConverter<UserMarker>();
        var options = JsonSerializerOptions.Default;
        ReadBoth(converter, options); // warm up (JIT, static initialization)

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (key, value) = ReadBoth(converter, options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        key.ShouldBe(value);
        allocated.ShouldBe(0);
    }

    [Fact]
    public void Writing_A_Value_And_A_Property_Name_Allocates_Nothing_Beyond_The_Buffer()
    {
        var converter = new IdJsonConverter<UserMarker>();
        var id = Id<UserMarker>.New();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer);
        Write(converter, writer, id); // warm up

        writer.Reset();
        buffer.ResetWrittenCount();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Write(converter, writer, id);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        Encoding.UTF8.GetString(buffer.WrittenSpan).ShouldBe($"{{\"{id}\":\"{id}\"}}");
    }

    private static (Id<UserMarker> Key, Id<UserMarker> Value) ReadBoth(IdJsonConverter<UserMarker> converter, JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader(Json);
        reader.Read(); // {
        reader.Read(); // property name
        var key = converter.ReadAsPropertyName(ref reader, typeof(Id<UserMarker>), options);
        reader.Read(); // value
        var value = converter.Read(ref reader, typeof(Id<UserMarker>), options);
        return (key, value);
    }

    private static void Write(IdJsonConverter<UserMarker> converter, Utf8JsonWriter writer, Id<UserMarker> id)
    {
        writer.WriteStartObject();
        converter.WriteAsPropertyName(writer, id, JsonSerializerOptions.Default);
        converter.Write(writer, id, JsonSerializerOptions.Default);
        writer.WriteEndObject();
        writer.Flush();
    }
}
