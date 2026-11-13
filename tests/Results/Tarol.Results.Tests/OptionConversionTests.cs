using Tarol.Options;

namespace Tarol.Results.Tests;

// Option<T> (Tarol.Options) to and from Result<T>: the conversions that stay in Tarol.Results (docs/migration/options.md Q-6).
public class OptionConversionTests
{
    public sealed record User(string Name, int Age);

    [Fact]
    public void ToResult_Should_TurnNoneIntoTheViolation()
    {
        var notFound = Error.NotFound("User.NotFound");

        Option.Some(new User("Ada", 1)).ToResult(notFound).IsSuccess.ShouldBeTrue();
        default(Option<User>).ToResult(notFound).ShouldBe(Result<User>.Failure(notFound));
        (default(Option<int>).ToResult(static () => Error.NotFound("Int.NotFound")) is Error { Code: "Int.NotFound" }).ShouldBeTrue();
        Option.Some(1).ToResult(static () => throw new InvalidOperationException("not called")).ShouldBe(Result.Success(1));
    }

    [Fact]
    public void ToOption_Should_DropTheViolation()
    {
        Result.Success(3).ToOption().ShouldBe(Option.Some(3));
        Result<int>.Failure(Invalid.Field("A", "A.X")).ToOption().IsNone.ShouldBeTrue();
        default(Result<int>).ToOption().IsNone.ShouldBeTrue();
    }
}
