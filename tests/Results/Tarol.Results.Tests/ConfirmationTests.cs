namespace Tarol.Results.Tests;

/// <summary>TN-37 (Tatalu FB-F04.2-2, FB-F06.2-2): a warning the caller confirms.</summary>
public sealed class ConfirmationTests
{
    [Fact]
    public void ConfirmationRequired_is_a_confirmable_conflict_with_arguments_compared_by_value()
    {
        var warning = Error.ConfirmationRequired("Absence.QuotaExceeded", new Dictionary<string, string> { ["office"] = "Mitte", ["days"] = "3" });

        warning.Type.ShouldBe(ErrorType.Conflict);
        warning.Confirmable.ShouldBeTrue();
        warning.Arguments!["days"].ShouldBe("3");
        warning.ShouldBe(Error.ConfirmationRequired("Absence.QuotaExceeded", new Dictionary<string, string> { ["days"] = "3", ["office"] = "Mitte" }));
        warning.ShouldNotBe(Error.ConfirmationRequired("Absence.QuotaExceeded", new Dictionary<string, string> { ["days"] = "4", ["office"] = "Mitte" }));
        warning.ShouldNotBe(Error.Conflict("Absence.QuotaExceeded"));
        Error.Conflict("X").Confirmable.ShouldBeFalse();
    }
}
