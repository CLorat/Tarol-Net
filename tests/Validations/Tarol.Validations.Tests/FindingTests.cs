using System.Diagnostics;
using Tarol.Results;
using Tarol.Validations.Tests.Fixtures;

namespace Tarol.Validations.Tests;

/// <summary>One test per old review finding fixed in step 5 (docs/migration/validations.md §2).</summary>
public class FindingTests
{
    [Fact]
    public void Finding_4_Several_Rules_On_One_Value_Give_One_Error()
    {
        Code.Create(" ").ShouldBe(Invalid.Field("", ValidationCodes.Required));
        Code.Create("ab").ShouldBe(Invalid.Field("", ValidationCodes.Length, "3..5"));
        Code.Create("ab1").ShouldBe(Invalid.Field("", ValidationCodes.Pattern));
        Code.Create("abc").IsSuccess.ShouldBeTrue();                      // IgnoreCase
    }

    [Fact]
    public void Finding_7_Internal_Value_Objects_Are_Generated()
        => InternalCode.Create("DE").IsSuccess.ShouldBeTrue();

    [Fact]
    public void Finding_8_Nested_Value_Objects_Are_Generated_In_Their_Containing_Type()
        => Catalog.Tag.Create("").ShouldBe(Invalid.Field("", ValidationCodes.Length, "1..20"));

    [Fact]
    public void Finding_9_Same_Name_In_Two_Namespaces_Does_Not_Collide()
    {
        Bidders.Name.Create("Ada").IsSuccess.ShouldBeTrue();
        Experts.Name.Create(new string('x', 100)).IsSuccess.ShouldBeTrue();
        Bidders.Name.Create(new string('x', 100)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Finding_16_A_Catastrophic_Pattern_Times_Out_Into_A_Field_Error()
    {
        var watch = Stopwatch.StartNew();

        var result = Evil.Create(new string('a', 30) + "!");

        watch.Stop();
        result.ShouldBe(Invalid.Field("", ValidationCodes.Pattern));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));         // 50 ms timeout; without one: minutes
    }

    [Fact]
    public void Finding_21_NotEmpty_On_A_Guid_Compiles_And_Works()
    {
        ExternalRef.Create(Guid.Empty).ShouldBe(Invalid.Field("", ValidationCodes.Required));
        ExternalRef.Create(Guid.NewGuid()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Finding_22_A_Null_Nested_Input_Is_Required_Not_A_NullReferenceException()
        => (InputTests.ValidOrder() with { Shipping = null! }).Validated().ShouldBe(Invalid.Field("Shipping", ValidationCodes.Required));

    [Fact]
    public void Finding_19_20_Decimal_Bounds_And_One_Sided_Limits_Are_Usable()
    {
        Shop.Price.Create(9999.99m).IsSuccess.ShouldBeTrue();
        Shop.Weight.Create(1e9).IsSuccess.ShouldBeTrue();
    }
}
