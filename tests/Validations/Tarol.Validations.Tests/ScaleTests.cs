using Tarol.Results;

namespace Tarol.Validations.Tests;

/// <summary>An amount of money: euro and cents (TN-53, Tatalu E08 billing, E09 invoices, E10 finances).</summary>
public readonly partial record struct Euro([Range(Min = 0), Scale(2)] decimal Value);

/// <summary>A rate per hour to a tenth of a cent, as an input member.</summary>
public sealed partial record SetRate([Scale(3)] decimal PerHour, Euro Cap) : IValidatable;

public sealed class ScaleTests
{
    [Theory]
    [InlineData("12.5")]
    [InlineData("12.50")]
    [InlineData("12.500")]
    [InlineData("0")]
    public void At_most_two_digits_after_the_point_pass_trailing_zeros_do_not_count(string value)
        => Euro.Create(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_third_digit_is_refused_with_code_Scale_and_the_digits()
    {
        Euro.Create(12.505m).ShouldBe(Invalid.Field("", ValidationCodes.Scale, "2"));
        Euro.Create(-1m).ShouldBe(Invalid.Field("", ValidationCodes.Range, "0.."));                 // the first failing rule wins
    }

    [Fact]
    public void An_input_member_and_a_nested_value_object_report_their_paths()
    {
#pragma warning disable TVAL006 // an invalid fixture on purpose: the constructor trusts, Validated must catch it
        var input = new SetRate(0.1234m, new Euro(1.001m));
#pragma warning restore TVAL006

        input.Validated().ShouldBe(new Invalid([new FieldError("PerHour", ValidationCodes.Scale, "3"), new FieldError("Cap", ValidationCodes.Scale, "2")]));
    }

    [Fact]
    public void Rules_Scale_rounds_to_the_digits()
    {
        Rules.Scale(1.005m, 2).ShouldBeFalse();
        Rules.Scale(1.10m, 1).ShouldBeTrue();
        Rules.Scale(7m, 0).ShouldBeTrue();
    }
}
