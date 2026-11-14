using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Results;
using Tarol.Validations.Tests.Shop;

namespace Tarol.Validations.Tests.MultiField;

/// <summary>A cross-field rule: the custom rule takes the whole value object.</summary>
public readonly partial record struct Period(DateOnly Start, DateOnly End)
{
    private static void Validate(Period value, ref ValidationErrors errors)
    {
        if (value.End < value.Start)
            errors.AddAt("End", "Period.EndBeforeStart");
    }
}

/// <summary>A value object with a nested value object and a plain field.</summary>
public readonly partial record struct Shipment([Range(Min = 0)] decimal Weight, Quantity Parcels, Title Label);

public sealed partial record Book(Period Stay, Shipment Shipment) : IValidatable;

public sealed record Booked(Period Stay, Shipment Shipment);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Booked))]
public partial class MultiJson : JsonSerializerContext;

public class MultiFieldTests
{
    private static readonly DateOnly May1 = new(2026, 5, 1), May9 = new(2026, 5, 9);

    [Fact]
    public void Create_Takes_Every_Field_And_Reports_Field_Paths()
    {
        Period.Create(May1, May9).GetValueOrThrow().End.ShouldBe(May9);
        Period.Create(May9, May1).ShouldBe(Invalid.Field("End", "Period.EndBeforeStart"));
        Shipment.Create(-1m, Quantity.Create(2).GetValueOrThrow(), Title.Create("Box").GetValueOrThrow())
            .ShouldBe(Invalid.Field("Weight", ValidationCodes.Range, "0.."));
    }

    [Fact]
    public void Nested_Value_Objects_Are_Checked_With_Their_Own_Rules()
    {
#pragma warning disable TVAL006 // invalid nested values on purpose
        var bad = new Shipment(-1m, new Quantity(0), new Title("x"));
#pragma warning restore TVAL006
        var errors = default(ValidationErrors);
        Shipment.Check(bad, ref errors);

        errors.ToInvalid().ShouldBe(new Invalid(
            new FieldError("Weight", "Range", "0.."),
            new FieldError("Parcels", "Range", "1..100"),
            new FieldError("Label", "Length", "3..10")));
    }

    [Fact]
    public void Inside_An_Input_The_Paths_Are_Prefixed()
    {
#pragma warning disable TVAL006
        var book = new Book(new Period(May9, May1), new Shipment(1m, new Quantity(500), new Title("Box")));
#pragma warning restore TVAL006

        book.Validated().ShouldBe(new Invalid(
            new FieldError("Stay.End", "Period.EndBeforeStart"),
            new FieldError("Shipment.Parcels", "Range", "1..100")));
    }

    [Fact]
    public void Json_Is_An_Object_With_Bare_Nested_Values_And_Old_Data_Loads()
    {
        var options = ValueObjectJson.Options(MultiJson.Default);
        var booked = new Booked(Period.Create(May1, May9).GetValueOrThrow(),
            Shipment.Create(2.5m, Quantity.Create(2).GetValueOrThrow(), Title.Create("Box").GetValueOrThrow()).GetValueOrThrow());

        var json = JsonSerializer.Serialize(booked, options.GetTypeInfo<Booked>());

        json.ShouldBe("""{"stay":{"start":"2026-05-01","end":"2026-05-09"},"shipment":{"weight":2.5,"parcels":2,"label":"Box"}}""");
        JsonSerializer.Deserialize(json, options.GetTypeInfo<Booked>()).ShouldBe(booked);

        var old = JsonSerializer.Deserialize("""{"stay":{"start":"2026-05-09","end":"2026-05-01"},"shipment":{"weight":-1,"parcels":0,"label":"x"}}""",
            options.GetTypeInfo<Booked>())!;
        old.Stay.End.ShouldBe(May1);                // end before start: loads, today's rules are not applied
        old.Shipment.Parcels.Value.ShouldBe(0);
    }

    [Fact]
    public void Multi_Field_Value_Objects_Get_No_Single_Field_Members()
    {
        ValueObjectJson.IsRegistered(typeof(Period)).ShouldBeFalse();     // the record's own object shape
        Shipment.WeightMin.ShouldBe(0m);                                   // constants are prefixed with the field
    }
}
