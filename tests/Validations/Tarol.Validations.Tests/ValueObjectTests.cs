using Tarol.Results;
using Tarol.Validations.Tests.Fixtures;
using Tarol.Validations.Tests.Shop;

namespace Tarol.Validations.Tests;

public class ValueObjectTests
{
    [Fact]
    public void Create_Returns_The_Value_When_Every_Rule_Holds()
    {
        Quantity.Create(40).GetValueOrThrow().Value.ShouldBe(40);
        Title.Create("Lamp").GetValueOrThrow().Value.ShouldBe("Lamp");
        Money.Create(10.5m, Currency.Eur).GetValueOrThrow().Amount.ShouldBe(10.5m);
    }

    [Fact]
    public void Create_Reports_The_Rule_Code_And_Limits_At_The_Root()
    {
        Quantity.Create(0).ShouldBe(Invalid.Field("", ValidationCodes.Range, "1..100"));
        Quantity.Create(101).ShouldBe(Invalid.Field("", ValidationCodes.Range, "1..100"));
        Title.Create("ab").ShouldBe(Invalid.Field("", ValidationCodes.Length, "3..10"));
        Email.Create("nope").ShouldBe(Invalid.Field("", ValidationCodes.Email));
        Zip.Create("1234").ShouldBe(Invalid.Field("", ValidationCodes.Pattern));
    }

    [Fact]
    public void A_Missing_String_Is_Required()
    {
        Title.Create(null!).ShouldBe(Invalid.Field("", ValidationCodes.Required));
        var errors = default(ValidationErrors);
        Title.Check(default, ref errors);                    // what a missing JSON property deserializes to
        errors.ToInvalid().ShouldBe(Invalid.Field("", ValidationCodes.Required));
    }

    [Fact]
    public void Decimal_And_One_Sided_Ranges()
    {
        Price.Create(0.01m).IsSuccess.ShouldBeTrue();
        Price.Create(0m).ShouldBe(Invalid.Field("", ValidationCodes.Range, "0.01..9999.99"));
        Price.Create(10_000m).IsFailure.ShouldBeTrue();
        Weight.Create(0d).IsSuccess.ShouldBeTrue();
        Weight.Create(-0.1d).ShouldBe(Invalid.Field("", ValidationCodes.Range, "0.."));
        Weight.Create(double.NaN).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_Value_Object_With_Several_Fields_Reports_The_Field_Name()
        => Money.Create(-1m, Currency.Usd).ShouldBe(Invalid.Field("Amount", ValidationCodes.Range, "0.."));

    [Fact]
    public void The_Custom_Rule_Runs_After_The_Attribute_Rules_And_Only_When_They_Pass()
    {
        Sku.Create("SKU-1").IsSuccess.ShouldBeTrue();
        Sku.Create("ABC-1").ShouldBe(Invalid.Field("", "Sku.Prefix"));
        Sku.Create("SK").ShouldBe(Invalid.Field("", ValidationCodes.Length, "4..12"));
    }

    [Fact]
    public void A_Record_Class_Value_Object_Treats_Null_As_Required()
    {
        Iban.Create("DE89370400440532013000").IsSuccess.ShouldBeTrue();
        var errors = default(ValidationErrors);
        Iban.Check(null!, ref errors);
        errors.ToInvalid().ShouldBe(Invalid.Field("", ValidationCodes.Required));
    }

    [Fact]
    public void Limits_Are_Constants()
    {
        Quantity.Min.ShouldBe(1);
        Quantity.Max.ShouldBe(100);
        Title.MinLength.ShouldBe(3);
        Title.MaxLength.ShouldBe(10);
        Price.Min.ShouldBe(0.01m);
        Money.AmountMin.ShouldBe(0m);
    }

    [Fact]
    public void Arithmetic_Works_On_The_Underlying_Value()
    {
        Quantity a = Quantity.Create(40).GetValueOrThrow(), b = Quantity.Create(70).GetValueOrThrow();
        Quantity[] all = [a, b];

        int sum = a + b;
        sum.ShouldBe(110);                                     // an int: 110 is not a Quantity
        (a * 2).ShouldBe(80);
        (a < b).ShouldBeTrue();
        (a < 50).ShouldBeTrue();
        Math.Max(a, b).ShouldBe(70);
        all.Sum(q => q).ShouldBe(110);
        all.Max().ShouldBe(b);
        all.OrderDescending().First().ShouldBe(b);
        Quantity.Create(a + b).IsFailure.ShouldBeTrue();      // back into the domain only through Create
    }

    [Fact]
    public void A_String_Value_Object_Reads_Like_A_String()
    {
        var title = Title.Create("Lamp").GetValueOrThrow();
        string text = title;

        text.Length.ShouldBe(4);
        title.ToString().ShouldBe("Lamp");
        default(Title).ToString().ShouldBe(string.Empty);
    }

    [Fact]
    public void ToString_Is_Invariant()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Price.Create(12.5m).GetValueOrThrow().ToString().ShouldBe("12.5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Same_Short_Name_In_Two_Namespaces_Keeps_Its_Own_Rules()
    {
        Bidders.Name.MaxLength.ShouldBe(60);
        Experts.Name.MaxLength.ShouldBe(120);

        var bidder = new Bidders.Bidder(Bidders.Name.Create("Ada Lovelace").GetValueOrThrow());
        bidder.Rename("Ada").IsSuccess.ShouldBeTrue();
        bidder.Rename("A").ShouldBe(Invalid.Field("", ValidationCodes.Length, "3..60"));
        bidder.Name.Value.ShouldBe("Ada");
    }
}
