using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.Results.Tests;

// The JSON contract (Q-5): the value or null, the same as T?. Reflection and source generation.
public partial class OptionJsonTests
{
    private static readonly JsonSerializerOptions Reflection = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Generated = new(JsonSerializerDefaults.Web) { TypeInfoResolver = OptionTestJson.Default };

    public static TheoryData<string> Modes => ["reflection", "generated"];

    private static JsonSerializerOptions Options(string mode) => mode == "reflection" ? Reflection : Generated;

    [Theory]
    [MemberData(nameof(Modes))]
    public void Some_Should_BeTheBareValue(string mode)
    {
        JsonSerializer.Serialize(new Profile(1, "ada", 36, new User("Ada")), Options(mode))
            .ShouldBe("""{"id":1,"nickname":"ada","age":36,"manager":{"name":"Ada"}}""");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void None_Should_BeNull_LikeTheNullableWireFormat(string mode)
    {
        var json = JsonSerializer.Serialize(new Profile(1, default, default, default), Options(mode));

        json.ShouldBe("""{"id":1,"nickname":null,"age":null,"manager":null}""");
        json.ShouldBe(JsonSerializer.Serialize(new NullableProfile(1, null, null, null), Options(mode)));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void MissingOrNullProperties_Should_ReadAsNone(string mode)
    {
        var missing = JsonSerializer.Deserialize<Profile>("""{"id":1}""", Options(mode))!;
        var nulls = JsonSerializer.Deserialize<Profile>("""{"id":1,"nickname":null,"age":null,"manager":null}""", Options(mode))!;

        missing.ShouldBe(new Profile(1, default, default, default));
        nulls.ShouldBe(new Profile(1, default, default, default));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void NullableAndOption_Should_ReadEachOther(string mode) // event evolution in both directions (§3.9)
    {
        var old = JsonSerializer.Serialize(new NullableProfile(1, "ada", null, new User("Ada")), Options(mode));
        JsonSerializer.Deserialize<Profile>(old, Options(mode)).ShouldBe(new Profile(1, "ada", default, new User("Ada")));

        var current = JsonSerializer.Serialize(new Profile(2, default, 5, default), Options(mode));
        JsonSerializer.Deserialize<NullableProfile>(current, Options(mode)).ShouldBe(new NullableProfile(2, null, 5, null));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void WhenWritingDefault_Should_OmitNone(string mode)
    {
        JsonSerializer.Serialize(new Sparse { Id = 1 }, Options(mode)).ShouldBe("""{"id":1}""");
        JsonSerializer.Serialize(new Sparse { Id = 1, Nickname = "a", Age = 0 }, Options(mode)).ShouldBe("""{"id":1,"nickname":"a","age":0}""");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void RootOption_Should_RoundTrip(string mode)
    {
        JsonSerializer.Serialize(Option.Some(new User("Ada")), Options(mode)).ShouldBe("""{"name":"Ada"}""");
        JsonSerializer.Serialize(default(Option<User>), Options(mode)).ShouldBe("null");
        JsonSerializer.Deserialize<Option<User>>("null", Options(mode)).IsNone.ShouldBeTrue();
        JsonSerializer.Deserialize<Option<User>>("""{"name":"Ada"}""", Options(mode)).ShouldBe(Option.Some(new User("Ada")));
    }

    [Fact] // the package's own metadata for primitives: a context that lists only the DTO still works
    public void Primitives_Should_ResolveWithoutAContextEntry()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = DtoOnlyJson.Default };

        var json = JsonSerializer.Serialize(new Primitives("a", 1, Guid.Empty, default), options);

        json.ShouldBe("""{"text":"a","number":1,"id":"00000000-0000-0000-0000-000000000000","at":null}""");
        JsonSerializer.Deserialize<Primitives>(json, options).ShouldBe(new Primitives("a", 1, Guid.Empty, default));
    }

    [Fact]
    public void UnknownValueType_Should_ExplainTheMissingContextEntry()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = DtoOnlyJson.Default };

        Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize(new WithUser(Option.Some(new User("Ada"))), options))
            .Message.ShouldContain("[JsonSerializable(typeof(User))]");
    }

    [Fact] // documented: an inner None is written as null and reads back as the outer None
    public void NestedOption_Should_CollapseInnerNone()
    {
        var json = JsonSerializer.Serialize(Option.Some(default(Option<int>)), Reflection);

        json.ShouldBe("null");
        JsonSerializer.Deserialize<Option<Option<int>>>(json, Reflection).IsNone.ShouldBeTrue();
    }

    public sealed record User(string Name);

    public sealed record Profile(int Id, Option<string> Nickname, Option<int> Age, Option<User> Manager);

    public sealed record NullableProfile(int Id, string? Nickname, int? Age, User? Manager);

    public sealed class Sparse
    {
        public int Id { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Option<string> Nickname { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Option<int> Age { get; init; }
    }

    public sealed record Primitives(Option<string> Text, Option<int> Number, Option<Guid> Id, Option<DateTimeOffset> At);

    public sealed record WithUser(Option<User> Manager);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Profile))]
    [JsonSerializable(typeof(NullableProfile))]
    [JsonSerializable(typeof(Sparse))]
    [JsonSerializable(typeof(Option<User>))]
    [JsonSerializable(typeof(User))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int))]
    internal sealed partial class OptionTestJson : JsonSerializerContext;

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Primitives))]
    [JsonSerializable(typeof(WithUser))]
    internal sealed partial class DtoOnlyJson : JsonSerializerContext;
}
