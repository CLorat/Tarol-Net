using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tarol.Kernel.Json;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Kernel.Tests;

public partial class KernelJsonTests
{
    private const string SagaJson = """{"actor":{"kind":"System","id":"Sagas.AuctionTimeout","onBehalfOf":"alice"},"tenant":"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5b","correlationId":"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5c","causationId":"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5d"}""";

    private static readonly Id<ITenant> Tenant = Id<ITenant>.Parse("0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5b", null);
    private static readonly Id<ICorrelation> Correlation = Id<ICorrelation>.Parse("0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5c", null);
    private static readonly Id<ICausation> Causation = Id<ICausation>.Parse("0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5d", null);

    private static readonly OperationContext Saga = new(Actor.System("Sagas.AuctionTimeout", "alice"), Tenant, Correlation, Causation);
    private static readonly OperationContext Anonymous = new(Actor.Anonymous, Option.None, Correlation, Option.None);

    public static TheoryData<string> Modes => ["kernel", "app", "reflection", "snake_case"];

    private static JsonTypeInfo<OperationContext> Info(string mode) => mode switch
    {
        "kernel" => KernelJsonContext.Default.OperationContext,
        "app" => AppJson.Default.OperationContext,
        "snake_case" => new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo<OperationContext>(),
        _ => new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo<OperationContext>(),
    };

    [Theory] // a fixed contract: event metadata must not change with the caller's naming policy
    [MemberData(nameof(Modes))]
    public void Context_Should_HaveAFixedContract(string mode)
    {
        JsonSerializer.Serialize(Saga, Info(mode)).ShouldBe(SagaJson);
        JsonSerializer.Serialize(Anonymous, Info(mode)).ShouldBe("""{"actor":{"kind":"Anonymous","id":"anonymous","onBehalfOf":null},"tenant":null,"correlationId":"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5c","causationId":null}""");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Context_Should_RoundTrip(string mode)
    {
        JsonSerializer.Deserialize(JsonSerializer.Serialize(Saga, Info(mode)), Info(mode)).ShouldBe(Saga);
        JsonSerializer.Deserialize(JsonSerializer.Serialize(Anonymous, Info(mode)), Info(mode)).ShouldBe(Anonymous);
    }

    [Fact] // the case that broke with Option<Id<…>> metadata: the context nested in an app type, app context only
    public void Context_InsideAnAppType_Should_NeedNoExtraMetadata()
    {
        var audit = new Audit("bid", Saga);
        var json = JsonSerializer.Serialize(audit, AppJson.Default.Audit);
        json.ShouldBe($$"""{"what":"bid","context":{{SagaJson}}}""");
        JsonSerializer.Deserialize(json, AppJson.Default.Audit).ShouldBe(audit);
    }

    [Fact]
    public void Read_Should_AcceptMissingOptionalParts_AndSkipUnknownProperties()
        => JsonSerializer.Deserialize("""{"correlationId":"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5c","extra":{"a":[1]},"actor":{"id":"anonymous","kind":"Anonymous","x":1}}""",
            KernelJsonContext.Default.OperationContext).ShouldBe(Anonymous);

    [Theory]
    [InlineData("""{"kind":"User","id":"alice","onBehalfOf":"bob"}""")] // onBehalfOf only for System
    [InlineData("""{"kind":"User","id":""}""")]
    [InlineData("""{"kind":"Robot","id":"r2"}""")]
    [InlineData("""{"kind":"user","id":"alice"}""")] // the contract is case-sensitive
    [InlineData("""{"id":"alice"}""")]
    [InlineData("""{"kind":"User"}""")]
    [InlineData("""["User","alice"]""")]
    public void Invalid_Actor_Should_BeAJsonException(string json)
        => Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, KernelJsonContext.Default.Actor));

    [Theory]
    [InlineData("""{"actor":{"kind":"User","id":"a"}}""")]
    [InlineData("""{"correlationId":"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5c"}""")]
    [InlineData("""{"actor":{"kind":"User","id":"a"},"correlationId":"not-a-guid"}""")]
    public void Invalid_Context_Should_Throw(string json)
        => Should.Throw<Exception>(() => JsonSerializer.Deserialize(json, KernelJsonContext.Default.OperationContext)).ShouldBeAssignableTo<Exception>();

    [Fact]
    public void Ids_Should_BeGuidStrings()
        => JsonSerializer.Serialize(Tenant, KernelJsonContext.Default.IdITenant).ShouldBe("\"0193a6f2-7b4c-7d11-9a2e-0c1d2e3f4a5b\"");

    public sealed record Audit(string What, OperationContext Context);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(OperationContext))]
    [JsonSerializable(typeof(Audit))]
    internal sealed partial class AppJson : JsonSerializerContext;
}
