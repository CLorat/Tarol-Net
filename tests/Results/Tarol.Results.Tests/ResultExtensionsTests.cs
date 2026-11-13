namespace Tarol.Results.Tests;

// Ported from Tarol ResultExtensions.UnitTests.cs (16 tests).
public class ResultExtensionsTests
{
    private static readonly Error NotFound = Error.NotFound("User.NotFound");

    [Fact] // Tarol: Combine_TwoSuccess_ShouldReturnTuple
    public void Combine_Two_Should_ReturnTuple_When_BothSucceed()
        => Result.Combine(Result<int>.Success(1), Result<string>.Success("a")).GetValueOrThrow().ShouldBe((1, "a"));

    [Fact] // Tarol: Combine_Two_ShouldReturnFirstErrorWhenFirstFails (R-7: it now combines like every other Combine)
    public void Combine_Two_Should_CombineFailures()
    {
        Result.Combine(Result<int>.Failure(NotFound), Result<string>.Failure(Error.Conflict("c")))
            .ShouldBe(Result<(int, string)>.Failure(NotFound));
        Result.Combine(Result<int>.Failure(Invalid.Field("A", "A.X")), Result<string>.Failure(Invalid.Field("B", "B.X")))
            .ShouldBe(Result<(int, string)>.Failure(new Invalid(new FieldError("A", "A.X"), new FieldError("B", "B.X"))));
    }

    [Fact] // Tarol: Combine_ThreeSuccess_ShouldReturnTuple
    public void Combine_Three_Should_ReturnTuple_When_AllSucceed()
        => Result.Combine(Result<int>.Success(1), Result<int>.Success(2), Result<int>.Success(3)).GetValueOrThrow().ShouldBe((1, 2, 3));

    [Fact] // Tarol: Combine_Three_ShouldReturnFailingResult
    public void Combine_Three_Should_ReturnFailure_When_OneFails()
        => Result.Combine(Result<int>.Success(1), Result<int>.Failure(NotFound), Result<int>.Success(3)).ShouldBe(Result<(int, int, int)>.Failure(NotFound));

    [Fact] // Tarol: Sequence_ShouldReturnValuesWhenAllSuccess
    public void Sequence_Should_ReturnValues_When_AllSucceed()
        => new[] { Result<int>.Success(1), Result<int>.Success(2) }.Sequence().GetValueOrThrow().ShouldBe([1, 2]);

    [Fact] // Tarol: Sequence_ShouldAggregateErrorsWhenAnyFail
    public void Sequence_Should_CombineFailures_When_AnyFails()
    {
        var invalid = new[] { Result<int>.Success(1), Invalid.Field("A", "A.X"), Invalid.Field("B", "B.X") }.Sequence();
        invalid.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Invalid>().Errors.Count.ShouldBe(2);

        new[] { Invalid.Field("A", "A.X"), (Result<int>)NotFound }.Sequence().ShouldBe(Result<IReadOnlyList<int>>.Failure(NotFound));
    }

    [Fact] // Tarol: ToResult_Class_ShouldReturnSuccessWhenValuePresent
    public void ToResult_Class_Should_ReturnSuccess_When_ValuePresent() => "x".ToResult(NotFound).GetValueOrThrow().ShouldBe("x");

    [Fact] // Tarol: ToResult_Class_ShouldReturnErrorWhenNull
    public void ToResult_Class_Should_ReturnViolation_When_Null() => ((string?)null).ToResult(NotFound).ShouldBe(Result<string>.Failure(NotFound));

    [Fact] // Tarol: ToResult_Struct_ShouldReturnSuccessWhenHasValue
    public void ToResult_Struct_Should_ReturnSuccess_When_HasValue() => ((int?)5).ToResult(NotFound).GetValueOrThrow().ShouldBe(5);

    [Fact] // Tarol: ToResult_Struct_ShouldReturnErrorWhenNull
    public void ToResult_Struct_Should_ReturnViolation_When_Null() => ((int?)null).ToResult(NotFound).ShouldBe(Result<int>.Failure(NotFound));

    [Fact] // Tarol: MapTask_ShouldTransformValueOnSuccess
    public async Task Map_OnValueTask_Should_TransformValue_On_Success() => (await Pending(2).Map(static v => v * 3)).GetValueOrThrow().ShouldBe(6);

    [Fact] // Tarol: MapTask_ShouldPropagateFailure
    public async Task Map_OnValueTask_Should_PropagateFailure() => (await Pending(NotFound).Map(static v => v * 3)).ShouldBe(Result<int>.Failure(NotFound));

    [Fact] // Tarol: BindTask_ShouldInvokeBinderOnSuccess
    public async Task Bind_OnValueTask_Should_InvokeBinder_On_Success()
        => (await Pending(2).Bind(static v => Result<string>.Success(v.ToString(System.Globalization.CultureInfo.InvariantCulture)))).GetValueOrThrow().ShouldBe("2");

    [Fact] // Tarol: BindTask_ShouldReturnFailureWhenSourceFails
    public async Task Bind_OnValueTask_Should_ReturnFailure_When_SourceFails()
        => (await Pending(NotFound).Bind(static v => Result<int>.Success(v))).IsFailure.ShouldBeTrue();

    [Fact] // Tarol: BindAsyncTask_ShouldAwaitBinder
    public async Task BindAsync_OnValueTask_Should_AwaitBinder()
    {
        var result = await Pending(2).BindAsync(static async (v, ct) =>
        {
            await Task.Delay(1, ct);
            return Result<int>.Success(v + 1);
        }, TestContext.Current.CancellationToken);

        result.GetValueOrThrow().ShouldBe(3);
    }

    [Fact] // Tarol: BindAsyncTask_ShouldPropagateFailure
    public async Task BindAsync_OnValueTask_Should_PropagateFailure()
        => (await Pending(NotFound).BindAsync(static (v, _) => ValueTask.FromResult(Result<int>.Success(v)), TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();

    [Fact]
    public async Task Pipeline_OnValueTask_Should_ChainEnsureTapAndMatch()
    {
        var log = new List<string>();
        var text = await Pending(5)
            .Ensure(static v => v > 0, Invalid.Field("Value", "Value.Negative"))
            .Tap(v => log.Add($"ok {v}"))
            .TapFailure(v => log.Add(v.ToString()))
            .Match(static v => $"#{v}", static v => v.ToString());

        text.ShouldBe("#5");
        log.ShouldBe(["ok 5"]);
        (await PendingUnit(Result.Success()).Bind(static () => Result.Failure(NotFound)).Match(static () => "ok", static _ => "failed")).ShouldBe("failed");
    }

    private static async ValueTask<Result<int>> Pending(Result<int> result)
    {
        await Task.Yield();
        return result;
    }

    private static async ValueTask<Result> PendingUnit(Result result)
    {
        await Task.Yield();
        return result;
    }
}
