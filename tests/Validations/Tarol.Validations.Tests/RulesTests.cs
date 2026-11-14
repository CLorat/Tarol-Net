using System.Text.RegularExpressions;

namespace Tarol.Validations.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("ada@example.com", true)]
    [InlineData("a@b.cd", true)]
    [InlineData("first.last+tag@sub.example.org", true)]
    [InlineData("", false)]
    [InlineData("nope", false)]
    [InlineData("@example.com", false)]
    [InlineData("ada@", false)]
    [InlineData("ada@example", false)]
    [InlineData("ada@.example.com", false)]
    [InlineData("ada@example.com.", false)]
    [InlineData("ada@exa..mple.com", false)]
    [InlineData("ada@@example.com", false)]
    [InlineData("a da@example.com", false)]
    public void Email_Checks_The_Shape(string text, bool expected) => Rules.Email(text).ShouldBe(expected);

    [Fact]
    public void Email_Rejects_Overlong_Parts()
    {
        Rules.Email(new string('a', 65) + "@example.com").ShouldBeFalse();
        Rules.Email("a@" + new string('b', 250) + ".com").ShouldBeFalse();
    }

    [Fact]
    public void InRange_Is_Inclusive_And_Rejects_NaN()
    {
        Rules.InRange(1, 1, 3).ShouldBeTrue();
        Rules.InRange(3, 1, 3).ShouldBeTrue();
        Rules.InRange(4, 1, 3).ShouldBeFalse();
        Rules.InRange(double.NaN, 0d, 1d).ShouldBeFalse();
    }

    [Fact]
    public void Length_And_NotBlank_Treat_Null_As_Missing()
    {
        Rules.Length(null, 0, 10).ShouldBeFalse();
        Rules.Length("abc", 3, 3).ShouldBeTrue();
        Rules.NotBlank("  ").ShouldBeFalse();
        Rules.NotBlank(null).ShouldBeFalse();
        Rules.NotBlank(" x ").ShouldBeTrue();
    }

    [Fact]
    public void Matches_Turns_A_Timeout_Into_False()
    {
        var regex = new Regex("^(a+)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

        Rules.Matches(regex, new string('a', 30) + "!").ShouldBeFalse();
        Rules.Matches(regex, "aaa").ShouldBeTrue();
        Rules.Matches(regex, null).ShouldBeFalse();
    }
}
