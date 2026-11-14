using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations.Tests.Fixtures;
using Tarol.Validations.Tests.Shop;

namespace Tarol.Validations.Tests;

public class InputTests
{
    internal static readonly Line[] ValidLines =
    [
        new(Id<Product>.New(), Quantity.Create(3).GetValueOrThrow()),
        new(Id<Product>.New(), Quantity.Create(7).GetValueOrThrow())
    ];

    internal static PlaceOrder ValidOrder() => new(
        Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), Email.Create("a@b.cd").GetValueOrThrow(), ValidLines,
        Option.None, Money.Create(10m, Currency.Eur).GetValueOrThrow(),
        new Address("Main 1", Zip.Create("12345").GetValueOrThrow()), null);

#pragma warning disable TVAL006 // invalid fixtures on purpose: the constructor trusts, Validate must catch it
    internal static PlaceOrder InvalidOrder() => new(
        Id<Order>.Empty, new Title("x"), new Email("nope"),
        [ValidLines[0], new Line(Id<Product>.Empty, new Quantity(0))],
        new Title("n"), new Money(-2m, Currency.Eur), new Address(" ", new Zip("1")), new string('c', 201));
#pragma warning restore TVAL006

    [Fact]
    public void A_Valid_Input_Is_Returned_Unchanged()
    {
        var order = ValidOrder();
        order.Validated().GetValueOrThrow().ShouldBeSameAs(order);
    }

    [Fact]
    public void An_Invalid_Input_Reports_Every_Field_With_Its_Input_Path()
    {
        InvalidOrder().Validated().ShouldBe(new Invalid(
            new FieldError("OrderId", "Required"),
            new FieldError("Name", "Length", "3..10"),
            new FieldError("Contact", "Email"),
            new FieldError("Lines[1].Product", "Required"),
            new FieldError("Lines[1].Quantity", "Range", "1..100"),
            new FieldError("Nickname", "Length", "3..10"),
            new FieldError("Total.Amount", "Range", "0.."),
            new FieldError("Shipping.Street", "Required"),
            new FieldError("Shipping.Zip", "Pattern"),
            new FieldError("Comment", "Length", "..200")));
    }

    [Fact]
    public void Option_Is_Optional_And_Its_Some_Value_Is_Validated()
    {
        (ValidOrder() with { Nickname = Option.None }).Validated().IsSuccess.ShouldBeTrue();
        (ValidOrder() with { Nickname = Title.Create("Nick").GetValueOrThrow() }).Validated().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Null_Members_Are_Required_Unless_Annotated_Nullable()
    {
        (ValidOrder() with { Lines = null!, Shipping = null! }).Validated().ShouldBe(new Invalid(
            new FieldError("Lines", "Required"),
            new FieldError("Shipping", "Required")));
        (ValidOrder() with { Comment = null }).Validated().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Collections_Check_Count_And_Each_Element()
    {
        (ValidOrder() with { Lines = [] }).Validated().ShouldBe(Invalid.Field("Lines", "Count", "1..50"));
        (ValidOrder() with { Lines = [ValidLines[0], null!] }).Validated().ShouldBe(Invalid.Field("Lines[1]", "Required"));
    }

    [Fact]
    public void Arrays_And_Lists_Of_Ids_Strings_And_Value_Objects()
    {
#pragma warning disable TVAL006
        var basket = new Basket([Id<Item>.New(), Id<Item>.Empty], ["ok", null!], [new Quantity(5), new Quantity(500)]);
#pragma warning restore TVAL006
        basket.Validated().ShouldBe(new Invalid(
            new FieldError("Items[1]", "Required"),
            new FieldError("Notes[1]", "Required"),
            new FieldError("Quantities[1]", "Range", "1..100")));
    }

    [Fact]
    public void Rules_On_Primitive_Parameters_For_Per_Request_Limits()
    {
        new SearchOrders("lamp", 20, Option.None).Validated().IsSuccess.ShouldBeTrue();
        new SearchOrders(new string('x', 101), 0, Id<Order>.Empty).Validated().ShouldBe(new Invalid(
            new FieldError("Text", "Length", "..100"),
            new FieldError("PageSize", "Range", "1..50"),
            new FieldError("After", "Required")));
    }

    [Fact]
    public void Nullable_Value_Types_Are_Optional()
    {
        new Signup(Code.Create("ABC").GetValueOrThrow(), "a@b.cd", null).Validated().IsSuccess.ShouldBeTrue();
        new Signup(Code.Create("ABC").GetValueOrThrow(), "a@b.cd", 12).Validated().ShouldBe(Invalid.Field("Age", "Range", "18..130"));
    }

    [Fact]
    public void Validated_Rejects_Null()
        => Should.Throw<ArgumentNullException>(() => ((PlaceOrder)null!).Validated());
}
