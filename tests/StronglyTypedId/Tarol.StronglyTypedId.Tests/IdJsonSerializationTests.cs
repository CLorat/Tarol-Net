using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.StronglyTypedId.Tests;

public class IdJsonSerializationTests
{
    private static readonly Guid KnownGuid = Guid.Parse("018f2b3c-1234-7abc-9def-0123456789ab");

    [Fact]
    public void Serializes_As_Plain_Json_String_Not_Object_Wrapper()
    {
        var json = JsonSerializer.Serialize(Id<UserMarker>.From(KnownGuid));

        // The attribute-applied open generic converter means this needs ZERO configuration.
        json.ShouldBe($"\"{KnownGuid}\"");
        json.ShouldNotContain("Value");
    }

    [Fact]
    public void Deserializes_From_Plain_Json_String()
    {
        JsonSerializer.Deserialize<Id<UserMarker>>($"\"{KnownGuid}\"")
            .Value.ShouldBe(KnownGuid);
    }

    [Fact]
    public void Dto_With_Multiple_Id_Types_Round_Trips_Losslessly()
    {
        var dto = new OrderDto(
            Id<UserMarker>.New(),
            Id<OrderMarker>.New(),
            [Id<OrderMarker>.New(), Id<OrderMarker>.New()]);

        var roundTripped = JsonSerializer.Deserialize<OrderDto>(JsonSerializer.Serialize(dto));

        roundTripped.ShouldBeEquivalentTo(dto);
    }

    [Fact]
    public void Dictionary_Keys_Round_Trip()
    {
        // The scenario most strongly-typed-ID libraries break on:
        // ReadAsPropertyName / WriteAsPropertyName.
        var dict = new Dictionary<Id<UserMarker>, string>
        {
            [Id<UserMarker>.From(KnownGuid)] = "alice",
            [Id<UserMarker>.New()] = "bob"
        };

        var json = JsonSerializer.Serialize(dict);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<Id<UserMarker>, string>>(json);

        roundTripped.ShouldBeEquivalentTo(dict);
        json.ShouldContain(KnownGuid.ToString()); // key written as the raw guid string
    }

    [Fact]
    public void Nullable_Id_Round_Trips_Null()
    {
        JsonSerializer.Serialize((Id<UserMarker>?)null).ShouldBe("null");
        JsonSerializer.Deserialize<Id<UserMarker>?>("null").ShouldBeNull();
    }

    [Fact]
    public void Null_Into_NonNullable_Id_Throws()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Id<UserMarker>>("null"));
    }

    [Theory]
    [InlineData("\"not-a-guid\"")] // string token, invalid content
    [InlineData("123")] // wrong token type
    [InlineData("{}")] // wrong token type
    public void Invalid_Json_Throws_JsonException_Not_FormatException(string json)
    {
        // JsonException (not FormatException) matters: it's what serializer middleware
        // and ProblemDetails handlers catch to produce a 400 instead of a 500.
        var ex = Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Id<UserMarker>>(json));
        ex.Message.ShouldContain(nameof(UserMarker)); // error names the ID type for diagnosability
    }

    [Fact]
    public void Marker_Never_Registered_Anywhere_Still_Serializes()
    {
        // Id<FallbackMarker> is never spelled in source, so no generated code knows it. System.Text.Json closes the
        // open generic [JsonConverter(typeof(IdJsonConverter<>))] by itself.
        var idType = typeof(Id<>).MakeGenericType(typeof(FallbackMarker));

        var json = $"\"{KnownGuid}\"";
        var deserialized = JsonSerializer.Deserialize(json, idType);

        deserialized.ShouldNotBeNull();
        deserialized.GetType().ShouldBe(idType);
        JsonSerializer.Serialize(deserialized, idType).ShouldBe(json);
    }

    [Fact]
    public void Source_Generated_Context_Uses_The_Closed_Converter()
    {
        var dto = new OrderDto(Id<UserMarker>.From(KnownGuid), Id<OrderMarker>.New(), [Id<OrderMarker>.New()]);

        var json = JsonSerializer.Serialize(dto, TestJsonContext.Default.OrderDto);
        var back = JsonSerializer.Deserialize(json, TestJsonContext.Default.OrderDto);

        json.ShouldContain($"\"{KnownGuid}\"");
        back.ShouldNotBeNull();
        back.CustomerId.ShouldBe(dto.CustomerId);
        back.OrderId.ShouldBe(dto.OrderId);
        back.RelatedOrders.ShouldBe(dto.RelatedOrders);
        TestJsonContext.Default.GetTypeInfo(typeof(Id<UserMarker>))!.Converter
            .ShouldBeOfType<IdJsonConverter<UserMarker>>();
    }

    [Fact]
    public void Source_Generated_Context_Round_Trips_Dictionary_Keys()
    {
        var map = new Dictionary<Id<UserMarker>, int> { [Id<UserMarker>.From(KnownGuid)] = 7 };

        var json = JsonSerializer.Serialize(map, TestJsonContext.Default.DictionaryIdUserMarkerInt32);

        json.ShouldBe($"{{\"{KnownGuid}\":7}}");
        JsonSerializer.Deserialize(json, TestJsonContext.Default.DictionaryIdUserMarkerInt32)!
            [Id<UserMarker>.From(KnownGuid)].ShouldBe(7);
    }

    [Fact]
    public void Escaped_Value_And_Property_Name_Are_Unescaped_Before_Parsing()
    {
        // "\u0030" is '0': escaping is legal JSON and must not break the zero-allocation fast path.
        var escaped = "\\u0030" + KnownGuid.ToString()[1..];

        JsonSerializer.Deserialize<Id<UserMarker>>($"\"{escaped}\"").Value.ShouldBe(KnownGuid);
        JsonSerializer.Deserialize<Dictionary<Id<UserMarker>, int>>($"{{\"{escaped}\":1}}")!
            .Keys.Single().Value.ShouldBe(KnownGuid);
    }

    [Theory]
    [InlineData("{018f2b3c-1234-7abc-9def-0123456789ab}")]
    [InlineData("018f2b3c12347abc9def0123456789ab")]
    public void Values_And_Keys_Accept_Only_The_D_Format(string nonCanonical)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Id<UserMarker>>($"\"{nonCanonical}\""));
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Dictionary<Id<UserMarker>, int>>($"{{\"{nonCanonical}\":1}}"));
    }

    internal sealed record OrderDto(
        Id<UserMarker> CustomerId,
        Id<OrderMarker> OrderId,
        List<Id<OrderMarker>> RelatedOrders);
}
[JsonSerializable(typeof(IdJsonSerializationTests.OrderDto))]
[JsonSerializable(typeof(Dictionary<Id<UserMarker>, int>))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
