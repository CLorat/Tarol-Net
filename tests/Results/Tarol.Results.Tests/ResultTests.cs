namespace Tarol.Results.Tests;

// Ported from Tarol Result.UnitTests.cs (17 tests).
public class ResultTests
{
    private static readonly Error NotFound = Error.NotFound("User.NotFound");

    [Fact] // Tarol: Success_ShouldSetFlagsAndReturnNoneError
    public void Success_Should_SetFlagsAndHaveNoViolation()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.TryGetValue(out Violation? violation).ShouldBeFalse();
        violation.ShouldBeNull();
        (result is Success).ShouldBeTrue();
    }

    [Fact] // Tarol: Failure_ShouldExposeErrorAndFailureFlags
    public void Failure_Should_ExposeViolationAndFlags()
    {
        var result = Result.Failure(NotFound);

        result.IsFailure.ShouldBeTrue();
        result.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBe(NotFound);
    }

    [Fact] // Tarol: ImplicitConversionFromError_ShouldCreateFailure
    public void ImplicitConversion_FromViolation_Should_CreateFailure()
    {
        Result result = NotFound;

        (result is Error { Code: "User.NotFound" }).ShouldBeTrue();
    }

    [Fact] // Tarol: Combine_Params_ShouldReturnSuccessWhenAllSuccess
    public void Combine_Params_Should_ReturnSuccess_When_AllSucceed()
        => Result.Combine(Result.Success(), Result.Success()).IsSuccess.ShouldBeTrue();

    [Fact] // Tarol: Combine_Params_ShouldAggregateErrorsWhenFailuresExist
    public void Combine_Params_Should_ConcatenateFieldErrors_When_AllFailuresAreInvalid()
    {
        var combined = Result.Combine(Invalid.Field("Email", "Email.Required"), Result.Success(), Invalid.Field("Name", "Name.Required"));

        combined.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Invalid>().Errors.Select(static e => e.Field).ShouldBe(["Email", "Name"]);
    }

    [Fact] // Tarol: Combine_Enumerable_ShouldBehaveLikeParams
    public void Combine_Enumerable_Should_BehaveLikeParams()
    {
        List<Result> results = [Result.Success(), Invalid.Field("A", "A.X"), Invalid.Field("B", "B.X")];

        Result.Combine(results).ShouldBe(Result.Combine([.. results]));
        Result.Combine(new List<Result> { Result.Success() }).IsSuccess.ShouldBeTrue();
    }

    [Fact] // R-6, R-7: an Error is not turned into a validation failure
    public void Combine_Should_ReturnTheFirstError_When_AnyFailureIsAnError()
    {
        var combined = Result.Combine(Invalid.Field("A", "A.X"), Error.Forbidden(), NotFound);

        combined.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBe(Error.Forbidden());
        violation!.ToHttpStatusCode().ShouldBe(403);
    }

    [Fact] // Tarol: Map_ShouldInvokeMapperWhenSuccessful
    public void Map_Should_InvokeMapper_When_Successful() => Result.Success().Map(static () => 42).GetValueOrThrow().ShouldBe(42);

    [Fact] // Tarol: Map_ShouldSkipMapperWhenFailure
    public void Map_Should_SkipMapper_When_Failure()
    {
        var called = false;
        var mapped = Result.Failure(NotFound).Map(() => { called = true; return 1; });

        called.ShouldBeFalse();
        (mapped is Error { Code: "User.NotFound" }).ShouldBeTrue();
    }

    [Fact] // Tarol: MapAsync_ShouldInvokeMapperOnSuccess
    public async Task MapAsync_Should_InvokeMapper_On_Success()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        var mapped = await Result.Success().MapAsync(ct => { seen = ct; return ValueTask.FromResult("ok"); }, cts.Token);

        mapped.GetValueOrThrow().ShouldBe("ok");
        seen.ShouldBe(cts.Token);
    }

    [Fact] // Tarol: MapAsync_ShouldBypassMapperOnFailure
    public async Task MapAsync_Should_BypassMapper_On_Failure()
    {
        var mapped = await Result.Failure(NotFound).MapAsync<string>(static _ => throw new InvalidOperationException("must not run"), TestContext.Current.CancellationToken);

        mapped.IsFailure.ShouldBeTrue();
    }

    [Fact] // Tarol: Bind_ShouldReturnBinderResultOnSuccess
    public void Bind_Should_ReturnBinderResult_On_Success()
    {
        Result.Success().Bind(static () => Result.Failure(NotFound)).IsFailure.ShouldBeTrue();
        Result.Success().Bind(static () => Result.Success(5)).GetValueOrThrow().ShouldBe(5);
    }

    [Fact] // Tarol: Bind_ShouldShortCircuitOnFailure
    public void Bind_Should_ShortCircuit_On_Failure()
    {
        var called = false;
        var result = Result.Failure(NotFound).Bind(() => { called = true; return Result.Success(); });

        called.ShouldBeFalse();
        result.ShouldBe(Result.Failure(NotFound));
    }

    [Fact] // Tarol: BindAsync_ShouldRespectState
    public async Task BindAsync_Should_RespectState()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Result.Success().BindAsync(static _ => ValueTask.FromResult(Result.Failure(NotFound)), ct)).IsFailure.ShouldBeTrue();
        (await Result.Failure(NotFound).BindAsync(static _ => ValueTask.FromResult(Result.Success()), ct)).IsFailure.ShouldBeTrue();
        (await Result.Success().BindAsync(static _ => ValueTask.FromResult(Result.Success(1)), ct)).GetValueOrThrow().ShouldBe(1);
    }

    [Fact] // Tarol: Match_ShouldRouteToCorrespondingBranch
    public void Match_Should_RouteToCorrespondingBranch()
    {
        Result.Success().Match(static () => "ok", static v => v.ToString()).ShouldBe("ok");
        Result.Failure(NotFound).Match(static () => "ok", static v => ((Error)v).Code).ShouldBe("User.NotFound");
    }

    [Fact] // Tarol: Switch_ShouldInvokeProperAction (Switch was removed: a statement switch does the same)
    public void StatementSwitch_Should_ReplaceSwitch()
    {
        string? seen = null;
        switch (Result.Failure(NotFound))
        {
            case Success:
                seen = "success";
                break;
            case Error error:
                seen = error.Code;
                break;
        }

        seen.ShouldBe("User.NotFound");
    }

    [Fact] // Tarol: Tap_ShouldExecuteSideEffectOnlyOnSuccess
    public void Tap_Should_RunOnlyOnSuccess()
    {
        var count = 0;
        Result.Success().Tap(() => count++);
        Result.Failure(NotFound).Tap(() => count++);

        count.ShouldBe(1);
    }

    [Fact] // Tarol: TapError_ShouldExecuteOnlyOnFailure (renamed TapFailure)
    public void TapFailure_Should_RunOnlyOnFailure()
    {
        Violation? seen = null;
        Result.Success().TapFailure(v => seen = v);
        seen.ShouldBeNull();
        Result.Failure(NotFound).TapFailure(v => seen = v);
        seen.ShouldBe(NotFound);
    }

    [Fact]
    public async Task TapAsync_Should_RunOnlyOnSuccess()
    {
        var count = 0;
        await Result.Success().TapAsync(_ => { count++; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);
        await Result.Failure(NotFound).TapAsync(_ => { count++; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);

        count.ShouldBe(1);
    }

    [Fact]
    public void Ensure_Should_FailOnlyWhenPredicateIsFalse()
    {
        Result.Success().Ensure(static () => true, NotFound).IsSuccess.ShouldBeTrue();
        Result.Success().Ensure(static () => false, NotFound).ShouldBe(Result.Failure(NotFound));
    }

    [Fact]
    public void Equality_And_ToString()
    {
        Result.Success().ShouldBe(new Result(default(Success)));
        Result.Success().ShouldNotBe(Result.Failure(NotFound));
        (Result.Failure(NotFound) == Result.Failure(Error.NotFound("User.NotFound"))).ShouldBeTrue();
        Result.Success().ToString().ShouldBe("Success");
        Result.Failure(NotFound).ToString().ShouldBe("Failure(Error { NotFound, User.NotFound })");
    }
}
