using Tarol.Results;

namespace Tarol.Validations.Tests;

public class ValidationErrorsTests
{
    [Fact]
    public void Default_Is_Valid_And_Converts_To_Success()
    {
        var errors = default(ValidationErrors);

        errors.IsValid.ShouldBeTrue();
        errors.Count.ShouldBe(0);
        errors.ToInvalid().ShouldBeNull();
        errors.ToResult().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Builds_Input_Paths_From_Members_And_Indexes()
    {
        var errors = default(ValidationErrors);
        errors.Add("Root");
        errors.Enter("Lines");
        errors.EnterIndex(2);
        errors.AddAt("Quantity", ValidationCodes.Range, "1..100");
        errors.Enter("Price");
        errors.Add(ValidationCodes.Range);
        errors.CurrentPath.ShouldBe("Lines[2].Price");
        errors.Exit();
        errors.Exit();
        errors.Add(ValidationCodes.Count, "1..50");
        errors.Exit();

        errors.ToInvalid().ShouldBe(new Invalid(
            new FieldError("", "Root"),
            new FieldError("Lines[2].Quantity", "Range", "1..100"),
            new FieldError("Lines[2].Price", "Range"),
            new FieldError("Lines", "Count", "1..50")));
    }

    [Fact]
    public void Paths_Deeper_Than_The_Inline_Buffer_Still_Work()
    {
        var errors = default(ValidationErrors);
        for (var i = 0; i < 12; i++)
            errors.Enter("L" + i);
        errors.Add("Deep");
        for (var i = 0; i < 12; i++)
            errors.Exit();
        errors.Add("Top");

        errors.ToInvalid()!.Errors.Select(e => e.Field).ShouldBe(["L0.L1.L2.L3.L4.L5.L6.L7.L8.L9.L10.L11", ""]);
    }

    [Fact]
    public void Exit_Without_Enter_Throws()
    {
        var errors = default(ValidationErrors);
        Should.Throw<InvalidOperationException>(() => errors.Exit());
    }

    [Fact]
    public void Rejects_Empty_Members_And_Negative_Indexes()
    {
        var errors = default(ValidationErrors);
        Should.Throw<ArgumentException>(() => errors.Enter(""));
        Should.Throw<ArgumentOutOfRangeException>(() => errors.EnterIndex(-1));
        Should.Throw<ArgumentException>(() => errors.AddAt("", "Code"));
    }
}
