using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations.Tests.Shop;

namespace Tarol.Validations.Tests;

/// <summary>The owner's condition for struct value objects (S-2): repository serialization keeps working.</summary>
public class JsonTests
{
    private static readonly JsonSerializerOptions Options = ValueObjectJson.Options(ShopJson.Default);

    private static JsonTypeInfo<T> Info<T>() => Options.GetTypeInfo<T>();

    [Fact]
    public void An_Event_Round_Trips_With_Bare_Values_Through_A_Source_Generated_Context()
    {
        var lines = InputTests.ValidLines;
        var placed = new OrderPlaced(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), Quantity.Create(40).GetValueOrThrow(),
            Quantity.Create(70).GetValueOrThrow(), Money.Create(10.5m, Currency.Eur).GetValueOrThrow(), lines);

        var json = JsonSerializer.Serialize(placed, Info<OrderPlaced>());
        var back = JsonSerializer.Deserialize(json, Info<OrderPlaced>())!;

        json.ShouldContain("\"name\":\"Lamp\"");
        json.ShouldContain("\"total\":40");
        json.ShouldContain("\"bonus\":70");
        json.ShouldContain("\"price\":{\"amount\":10.5,\"currency\":0}");
        json.ShouldContain("\"quantity\":3");
        (back.Name, back.Total, back.Bonus, back.Price).ShouldBe((placed.Name, placed.Total, placed.Bonus, placed.Price));
        back.Lines.ShouldBe(lines);
    }

    [Fact]
    public void None_Is_Null_And_Null_Reads_As_None()
    {
        var placed = new OrderPlaced(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), Quantity.Create(1).GetValueOrThrow(),
            Option.None, Money.Create(1m, Currency.Eur).GetValueOrThrow(), []);

        var json = JsonSerializer.Serialize(placed, Info<OrderPlaced>());

        json.ShouldContain("\"bonus\":null");
        JsonSerializer.Deserialize(json, Info<OrderPlaced>())!.Bonus.IsNone.ShouldBeTrue();
    }

    [Fact]
    public void Old_Data_That_Todays_Rules_Reject_Still_Loads()
    {
        const string old = """
            {"orderId":"0195f3a4-0000-7000-8000-000000000001","name":"A title that is far too long","total":500,
             "bonus":null,"price":{"amount":-1,"currency":0},"lines":[{"product":"00000000-0000-0000-0000-000000000000","quantity":0}]}
            """;

        var loaded = JsonSerializer.Deserialize(old, Info<OrderPlaced>())!;

        loaded.Name.Value.ShouldBe("A title that is far too long");
        loaded.Total.Value.ShouldBe(500);
        loaded.Price.Amount.ShouldBe(-1m);
        loaded.Lines[0].Quantity.Value.ShouldBe(0);
    }

    [Fact]
    public void An_Input_From_Json_Is_Validated_After_Reading()
    {
        const string request = """
            {"orderId":"0195f3a4-0000-7000-8000-000000000001","lines":[{"product":"0195f3a4-0000-7000-8000-000000000002","quantity":3}],
             "total":{"amount":1,"currency":0},"shipping":{"street":"Main","zip":"12345"},"nickname":null}
            """;

        var order = JsonSerializer.Deserialize(request, Info<PlaceOrder>())!;

        // Missing value objects deserialize as default: a string-based one reports Required.
        order.Validated().ShouldBe(new Invalid(new FieldError("Name", "Required"), new FieldError("Contact", "Required")));
    }

    [Fact]
    public void A_Null_Token_Reads_As_Default_For_A_Struct_Value_Object()
        => JsonSerializer.Deserialize("""{"orderId":"0195f3a4-0000-7000-8000-000000000001","name":null,"total":null,"bonus":null,"price":{"amount":0,"currency":0},"lines":[]}""",
            Info<OrderPlaced>())!.Name.ShouldBe(default);

    [Fact]
    public void Every_Generated_Converter_Is_Registered()
    {
        ValueObjectJson.IsRegistered(typeof(Quantity)).ShouldBeTrue();
        ValueObjectJson.IsRegistered(typeof(Title)).ShouldBeTrue();
        ValueObjectJson.IsRegistered(typeof(Money)).ShouldBeFalse();   // two fields: the record's own object shape
    }

    [Fact]
    public void V9_A_Same_Assembly_Context_Used_Directly_Writes_The_Object_Shape()
    {
        var placed = new OrderPlaced(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), Quantity.Create(40).GetValueOrThrow(),
            Option.None, Money.Create(1m, Currency.Eur).GetValueOrThrow(), []);

#pragma warning disable TVAL009 // the hazard TVAL009 reports, measured
        var json = JsonSerializer.Serialize(placed, ShopJson.Default.OrderPlaced);
#pragma warning restore TVAL009

        json.ShouldContain("\"total\":{\"value\":40}");
        JsonSerializer.Serialize(placed, Info<OrderPlaced>()).ShouldContain("\"total\":40");
    }

    [Fact]
    public void AddValueObjects_Puts_The_Resolver_First_Once()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = ShopJson.Default };
        options.AddValueObjects().AddValueObjects();

        options.TypeInfoResolverChain[0].ShouldBeSameAs(ValueObjectJson.Resolver);
        options.TypeInfoResolverChain.Count(r => ReferenceEquals(r, ValueObjectJson.Resolver)).ShouldBe(1);
        JsonSerializer.Serialize(Quantity.Create(3).GetValueOrThrow(), options).ShouldBe("3");
    }
}
