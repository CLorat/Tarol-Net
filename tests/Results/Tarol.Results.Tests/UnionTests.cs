using System.Runtime.CompilerServices;

namespace Tarol.Results.Tests;

// The C# 15 union behaviours Tarol.Results relies on (docs/migration/results.md §3).
public class UnionTests
{
    private static readonly Error NotFound = Error.NotFound("User.NotFound");

    [Fact]
    public void Types_Should_BeUnions() => typeof(IUnion).IsAssignableFrom(typeof(Result<int>)).ShouldBeTrue();

    [Fact] // §3.1: value + one reference
    public void Size_Should_BeTheValuePlusOneReference()
    {
        Unsafe.SizeOf<Result>().ShouldBe(8);
        Unsafe.SizeOf<Result<int>>().ShouldBe(16);
        Unsafe.SizeOf<Result<string>>().ShouldBe(16);
        Unsafe.SizeOf<Result<Guid>>().ShouldBe(24);
    }

    [Fact] // §3.2: the non-boxing pattern: switching over a value-type success allocates nothing
    public void Switch_Should_NotAllocate()
    {
        static int Run()
        {
            var sum = 0;
            for (var i = 0; i < 1000; i++)
            {
                Result<int> result = i % 2 == 0 ? new Result<int>(i) : new Result<int>(NotFound);
                sum += result switch { int v => v, Invalid => -2, Error => -1 };
            }

            return sum;
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        total.ShouldBe(249_000); // 0 + 2 + … + 998 = 249,500, minus 500 errors
        allocated.ShouldBe(0);
    }

    [Fact] // closed Violation: T | Invalid | Error is exhaustive, so is T | Violation
    public void Switch_Should_DecomposeTheClosedViolation()
    {
        static string Describe(Result<int> result) => result switch
        {
            int v => $"value {v}",
            Invalid invalid => $"invalid {invalid.Errors.Count}",
            Error error => $"error {error.Code}",
        };

        static Result<string> PassOn(Result<int> result) => result switch
        {
            int v => $"#{v}",
            Violation violation => violation,
        };

        Describe(1).ShouldBe("value 1");
        Describe(Invalid.Field("A", "A.X")).ShouldBe("invalid 1");
        Describe(NotFound).ShouldBe("error User.NotFound");
        PassOn(NotFound).ShouldBe(Result<string>.Failure(NotFound));
    }

    [Fact] // Q-3 / R-1: default is the Uninitialized failure and lands in the Error arm
    public void Default_Should_BeTheUninitializedError()
    {
        Result<int> generic = default;
        Result plain = default;

        generic.IsSuccess.ShouldBeFalse();
        (generic switch { int => "value", Invalid => "invalid", Error e => e.Code }).ShouldBe("Result.Uninitialized");
        (plain switch { Success => "success", Violation v => v.ToString() }).ShouldBe(Error.Uninitialized.ToString());
        generic.ShouldBe(Result<int>.Failure(Error.Uninitialized));
        plain.ShouldBe(Result.Failure(Error.Uninitialized));
        (generic is null).ShouldBeFalse();
    }

    [Fact]
    public async Task Default_Should_BeAFailure_InArraysAndValueTasks()
    {
        var array = new Result<Guid>[2];
        array[0].IsFailure.ShouldBeTrue();
        (await default(ValueTask<Result>)).IsFailure.ShouldBeTrue();
    }

    [Fact] // R-3: a null success is impossible
    public void NullValue_Should_BeRejected()
    {
        Should.Throw<ArgumentNullException>(static () => new Result<string>((string)null!));
        Should.Throw<ArgumentNullException>(static () => Result.Success<string>(null!));
    }

    [Fact]
    public void IsPattern_Should_NarrowTheValue()
    {
        Result<string> result = "abc";

        (result is string text && text.Length == 3).ShouldBeTrue();
        (result is Violation).ShouldBeFalse();
    }

    [Fact]
    public void Value_Should_ExposeTheActiveCaseAsObject()
    {
#pragma warning disable TRES003 // the union protocol member is what this test checks
        Result<int>.Success(3).Value.ShouldBe(3);
        Result<int>.Failure(NotFound).Value.ShouldBe(NotFound);
        default(Result<int>).Value.ShouldBe(Error.Uninitialized);
        Result.Success().Value.ShouldBe(default(Success));
#pragma warning restore TRES003
        Result.Success().HasValue.ShouldBeTrue();
        default(Result<int>).HasValue.ShouldBeTrue();
    }

    [Fact]
    public void Success_Should_MatchAsAPattern()
    {
        Result result = default(Success);

        (result is Success).ShouldBeTrue();
        default(Success).ShouldBe(new Success());
        (default(Success) == new Success()).ShouldBeTrue();
        (default(Success) != new Success()).ShouldBeFalse();
        default(Success).ToString().ShouldBe("Success");
    }
}
