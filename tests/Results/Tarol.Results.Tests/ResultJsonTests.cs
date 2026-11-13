using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.Results.Tests;

// The JSON contract (Q-5): envelope for results, fixed names for violations. Reflection and source generation.
public partial class ResultJsonTests
{
    private static readonly JsonSerializerOptions Reflection = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Generated = new(JsonSerializerDefaults.Web) { TypeInfoResolver = TestJson.Default };

    public static Result<int> RoundTrip(Result<int> result)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(result, TestJson.Default.ResultInt32), TestJson.Default.ResultInt32);

    public static TheoryData<string> Modes => ["reflection", "generated"];

    private static JsonSerializerOptions Options(string mode) => mode == "reflection" ? Reflection : Generated;

    [Theory]
    [MemberData(nameof(Modes))]
    public void Success_Should_BeAValueEnvelope(string mode)
    {
        var json = JsonSerializer.Serialize(Result<User>.Success(new User("Ada")), Options(mode));

        json.ShouldBe("""{"value":{"name":"Ada"}}""");
        JsonSerializer.Deserialize<Result<User>>(json, Options(mode)).GetValueOrThrow().Name.ShouldBe("Ada");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Invalid_Should_UseTheFixedContract(string mode)
    {
        Result<User> result = new Invalid(new FieldError("Name", "Name.Required"), new FieldError("Email", "Email.Invalid", "no @"));

        var json = JsonSerializer.Serialize(result, Options(mode));

        json.ShouldBe("""{"failure":{"kind":"invalid","errors":[{"field":"Name","code":"Name.Required"},{"field":"Email","code":"Email.Invalid","detail":"no @"}]}}""");
        JsonSerializer.Deserialize<Result<User>>(json, Options(mode)).ShouldBe(result);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Error_Should_UseTheFixedContract(string mode)
    {
        Result<User> result = Error.NotFound("User.NotFound", "No user 7.");

        var json = JsonSerializer.Serialize(result, Options(mode));

        json.ShouldBe("""{"failure":{"kind":"error","type":"NotFound","code":"User.NotFound","detail":"No user 7."}}""");
        JsonSerializer.Deserialize<Result<User>>(json, Options(mode)).ShouldBe(result);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void NonGeneric_Should_RoundTrip(string mode)
    {
        JsonSerializer.Serialize(Result.Success(), Options(mode)).ShouldBe("""{"success":true}""");
        JsonSerializer.Deserialize<Result>("""{"success":true}""", Options(mode)).IsSuccess.ShouldBeTrue();
        Result failure = Error.Conflict("Order.Shipped");
        JsonSerializer.Deserialize<Result>(JsonSerializer.Serialize(failure, Options(mode)), Options(mode)).ShouldBe(failure);
    }

    [Fact]
    public void Default_Should_WriteTheUninitializedError()
        => JsonSerializer.Serialize(default(Result<int>), TestJson.Default.ResultInt32)
            .ShouldBe("""{"failure":{"kind":"error","type":"Internal","code":"Result.Uninitialized","detail":"default(Result) was used: the result was never assigned a success or a violation."}}""");

    [Fact]
    public void ValueTypes_And_NestedResults_Should_RoundTrip()
    {
        RoundTrip(5).ShouldBe(Result<int>.Success(5));
        var envelope = new Envelope(Result<int>.Success(1), Result.Failure(Error.Timeout("Upstream.Slow")));
        var json = JsonSerializer.Serialize(envelope, TestJson.Default.Envelope);

        json.ShouldBe("""{"count":{"value":1},"done":{"failure":{"kind":"error","type":"Timeout","code":"Upstream.Slow"}}}""");
        JsonSerializer.Deserialize(json, TestJson.Default.Envelope).ShouldBe(envelope);
    }

    [Fact] // a context that does not list Violation still works: the package's own metadata is used
    public void Violation_Should_NotNeedARegistration()
        => JsonSerializer.Serialize(Result<int>.Failure(Error.Gone("x")), OnlyInt.Default.ResultInt32)
            .ShouldBe("""{"failure":{"kind":"error","type":"Gone","code":"x"}}""");

    [Theory]
    [InlineData("""5""")]
    [InlineData("""{}""")]
    [InlineData("""{"value":1,"failure":{"kind":"error","type":"NotFound","code":"x"}}""")]
    [InlineData("""{"failure":{"kind":"invalid","errors":[]}}""")]
    [InlineData("""{"failure":{"kind":"error","type":"NotFound","code":""}}""")]
    [InlineData("""{"failure":{"kind":"other"}}""")]
    [InlineData("""{"failure":null}""")]
    public void Malformed_Should_Throw_JsonException(string json)
        => Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, TestJson.Default.ResultInt32));

    [Fact] // the converter hides T from the source generator: a context must list T itself
    public void Context_WithoutT_Should_Throw_NotSupported()
        => Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize(Result<Guid>.Success(Guid.Empty), MissingT.Default.ResultGuid));

    [Fact]
    public void Value_Null_Should_Throw_JsonException()
        => Should.Throw<JsonException>(() => JsonSerializer.Deserialize("""{"value":null}""", TestJson.Default.ResultString));

    [Fact]
    public void UnknownProperties_Should_BeIgnored()
        => JsonSerializer.Deserialize("""{"meta":{"a":1},"value":3}""", TestJson.Default.ResultInt32).ShouldBe(Result<int>.Success(3));

    public sealed record User(string Name);

    public sealed record Envelope(Result<int> Count, Result Done);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Result<int>))]
    [JsonSerializable(typeof(Result<string>))]
    [JsonSerializable(typeof(Result<User>))]
    [JsonSerializable(typeof(Result))]
    [JsonSerializable(typeof(Violation))]
    [JsonSerializable(typeof(User))]
    [JsonSerializable(typeof(Envelope))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(string))]
    internal sealed partial class TestJson : JsonSerializerContext;

    [JsonSerializable(typeof(Result<int>))]
    [JsonSerializable(typeof(int))]
    internal sealed partial class OnlyInt : JsonSerializerContext;

    [JsonSerializable(typeof(Result<Guid>))]
    internal sealed partial class MissingT : JsonSerializerContext;
}
