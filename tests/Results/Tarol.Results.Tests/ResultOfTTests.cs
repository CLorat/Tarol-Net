namespace Tarol.Results.Tests;

// Ported from Tarol ResultT.UnitTests.cs (29 tests).
public class ResultOfTTests
{
    private static readonly Error NotFound = Error.NotFound("User.NotFound");
    private static readonly Invalid NameRequired = Invalid.Field("Name", "Name.Required");

    [Fact] // Tarol: Success_ShouldExposeValueAndFlags
    public void Success_Should_ExposeValueAndFlags()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.GetValueOrThrow().ShouldBe(42);
        (result is 42).ShouldBeTrue();
    }

    [Fact] // Tarol: Failure_ShouldExposeErrorAndThrowOnValueAccess
    public void Failure_Should_ExposeViolation_And_ThrowOnGetValueOrThrow()
    {
        var result = Result<int>.Failure(NotFound);

        result.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => result.GetValueOrThrow()).Message.ShouldContain("User.NotFound");
    }

    [Fact] // Tarol: ImplicitConversionFromValue_ShouldCreateSuccess
    public void ImplicitConversion_FromValue_Should_CreateSuccess()
    {
        Result<string> result = "hello";

        (result is "hello").ShouldBeTrue();
    }

    [Fact] // Tarol: ImplicitConversionFromError_ShouldCreateFailure
    public void ImplicitConversion_FromInvalidOrError_Should_CreateFailure()
    {
        Result<string> invalid = NameRequired;
        Result<string> error = NotFound;

        (invalid is Invalid).ShouldBeTrue();
        (error is Error { Type: ErrorType.NotFound }).ShouldBeTrue();
    }

    [Fact] // Tarol: ToResult_ShouldReturnNonGenericState
    public void ToResult_Should_ReturnNonGenericState()
    {
        Result<int>.Success(1).ToResult().IsSuccess.ShouldBeTrue();
        Result<int>.Failure(NotFound).ToResult().ShouldBe(Result.Failure(NotFound));
    }

    [Fact] // Tarol: TryGetValue_ShouldReturnTrueAndValueOnSuccess
    public void TryGetValue_Should_ReturnTrueAndValue_On_Success()
    {
        Result<int>.Success(7).TryGetValue(out int value).ShouldBeTrue();
        value.ShouldBe(7);
    }

    [Fact] // Tarol: TryGetValue_ShouldReturnFalseOnFailure
    public void TryGetValue_Should_ReturnFalse_On_Failure() => Result<int>.Failure(NotFound).TryGetValue(out int _).ShouldBeFalse();

    [Fact] // Tarol: TryGetError_ShouldReturnErrorOnFailure (now the TryGetValue(out Violation) overload)
    public void TryGetValue_Violation_Should_ReturnViolation_On_Failure()
    {
        Result<int>.Failure(NotFound).TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBe(NotFound);
        Result<int>.Success(1).TryGetValue(out violation).ShouldBeFalse();
        violation.ShouldBeNull();
    }

    [Fact] // Tarol: Map_ShouldTransformValueOnSuccess
    public void Map_Should_TransformValue_On_Success() => Result<int>.Success(2).Map(static v => v * 10).GetValueOrThrow().ShouldBe(20);

    [Fact] // Tarol: Map_ShouldSkipMapperOnFailure
    public void Map_Should_SkipMapper_On_Failure()
    {
        var called = false;
        var mapped = Result<int>.Failure(NotFound).Map(v => { called = true; return v; });

        called.ShouldBeFalse();
        mapped.ShouldBe(Result<int>.Failure(NotFound));
    }

    [Fact] // Tarol: MapAsync_ShouldTransformValueOnSuccess
    public async Task MapAsync_Should_TransformValue_On_Success()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        var mapped = await Result<int>.Success(2).MapAsync((v, ct) => { seen = ct; return ValueTask.FromResult(v + 1); }, cts.Token);

        mapped.GetValueOrThrow().ShouldBe(3);
        seen.ShouldBe(cts.Token);
    }

    [Fact] // Tarol: MapAsync_ShouldSkipMapperOnFailure
    public async Task MapAsync_Should_SkipMapper_On_Failure()
        => (await Result<int>.Failure(NotFound).MapAsync<int>(static (_, _) => throw new InvalidOperationException("must not run"), TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();

    [Fact] // Tarol: Bind_ShouldReturnBinderResultOnSuccess
    public void Bind_Should_ReturnBinderResult_On_Success()
    {
        Result<int>.Success(1).Bind(static v => Result<string>.Success($"#{v}")).GetValueOrThrow().ShouldBe("#1");
        Result<int>.Success(1).Bind(static _ => Result.Failure(NotFound)).ShouldBe(Result.Failure(NotFound));
    }

    [Fact] // Tarol: Bind_ShouldSkipBinderOnFailure
    public void Bind_Should_SkipBinder_On_Failure()
    {
        var called = false;
        Result<int>.Failure(NotFound).Bind(v => { called = true; return Result<int>.Success(v); }).IsFailure.ShouldBeTrue();
        called.ShouldBeFalse();
    }

    [Fact] // Tarol: BindAsync_ShouldRespectState
    public async Task BindAsync_Should_RespectState()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Result<int>.Success(1).BindAsync(static (v, _) => ValueTask.FromResult(Result<int>.Success(v + 1)), ct)).GetValueOrThrow().ShouldBe(2);
        (await Result<int>.Failure(NotFound).BindAsync(static (v, _) => ValueTask.FromResult(Result<int>.Success(v)), ct)).IsFailure.ShouldBeTrue();
        (await Result<int>.Success(1).BindAsync(static (_, _) => ValueTask.FromResult(Result.Success()), ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact] // Tarol: Match_ShouldReturnBranchValue
    public void Match_Should_ReturnBranchValue()
    {
        Result<int>.Success(3).Match(static v => v * 2, static _ => -1).ShouldBe(6);
        Result<int>.Failure(NotFound).Match(static v => v, static _ => -1).ShouldBe(-1);
    }

    [Fact] // Tarol: MatchAsync_ShouldReturnBranchValue (MatchAsync removed: await, then switch)
    public async Task AwaitThenSwitch_Should_ReplaceMatchAsync()
    {
        var text = await Load(5) switch
        {
            int v => $"value {v}",
            Violation v => v.ToString(),
        };

        text.ShouldBe("value 5");

        static async ValueTask<Result<int>> Load(int v)
        {
            await Task.Yield();
            return v;
        }
    }

    [Fact] // Tarol: Switch_ShouldInvokeCorrectAction (Switch removed: statement switch)
    public void StatementSwitch_Should_ReplaceSwitch()
    {
        var seen = "";
        switch (Result<int>.Success(1))
        {
            case int v:
                seen = $"ok {v}";
                break;
            case Violation:
                seen = "failure";
                break;
        }

        seen.ShouldBe("ok 1");
    }

    [Fact] // Tarol: Tap_ShouldExecuteActionOnlyOnSuccess
    public void Tap_Should_RunOnlyOnSuccess()
    {
        var sum = 0;
        Result<int>.Success(2).Tap(v => sum += v);
        Result<int>.Failure(NotFound).Tap(v => sum += v);

        sum.ShouldBe(2);
    }

    [Fact] // Tarol: TapError_ShouldExecuteActionOnlyOnFailure (renamed TapFailure)
    public void TapFailure_Should_RunOnlyOnFailure()
    {
        Violation? seen = null;
        Result<int>.Success(1).TapFailure(v => seen = v);
        seen.ShouldBeNull();
        Result<int>.Failure(NotFound).TapFailure(v => seen = v);
        seen.ShouldBe(NotFound);
    }

    [Fact]
    public async Task TapAsync_Should_RunOnlyOnSuccess()
    {
        var sum = 0;
        await Result<int>.Success(2).TapAsync((v, _) => { sum += v; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);
        await Result<int>.Failure(NotFound).TapAsync((v, _) => { sum += v; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);

        sum.ShouldBe(2);
    }

    [Fact] // Tarol: GetValueOrDefault_ShouldReturnValueOnSuccess
    public void GetValueOrDefault_Should_ReturnValue_On_Success() => Result<int>.Success(4).GetValueOrDefault(0).ShouldBe(4);

    [Fact] // Tarol: GetValueOrDefault_ShouldReturnFallbackOnFailure
    public void GetValueOrDefault_Should_ReturnFallback_On_Failure() => Result<int>.Failure(NotFound).GetValueOrDefault(9).ShouldBe(9);

    [Fact] // Tarol: GetValueOrDefault_WithFactory_ShouldUseFactoryOnFailure
    public void GetValueOrDefault_WithFactory_Should_UseFactory_On_Failure()
        => Result<string>.Failure(NotFound).GetValueOrDefault(static v => v.ToString()).ShouldBe("Error { NotFound, User.NotFound }");

    [Fact] // Tarol: Ensure_ShouldReturnFailureWhenPredicateFails
    public void Ensure_Should_ReturnFailure_When_PredicateFails()
        => Result<int>.Success(1).Ensure(static v => v > 1, NameRequired).ShouldBe(Result<int>.Failure(NameRequired));

    [Fact] // Tarol: Ensure_ShouldReturnOriginalWhenPredicatePasses
    public void Ensure_Should_ReturnOriginal_When_PredicatePasses() => Result<int>.Success(2).Ensure(static v => v > 1, NameRequired).ShouldBe(Result<int>.Success(2));

    [Fact] // Tarol: Ensure_ShouldKeepOriginalFailure
    public void Ensure_Should_KeepOriginalFailure() => Result<int>.Failure(NotFound).Ensure(static _ => false, NameRequired).ShouldBe(Result<int>.Failure(NotFound));

    [Fact] // Tarol: EnsureAsync_ShouldHandleFailedPredicate
    public async Task EnsureAsync_Should_HandleFailedPredicate()
        => (await Result<int>.Success(1).EnsureAsync(static (_, _) => ValueTask.FromResult(false), NameRequired, TestContext.Current.CancellationToken))
            .ShouldBe(Result<int>.Failure(NameRequired));

    [Fact] // Tarol: EnsureAsync_ShouldReturnOriginalOnSuccess
    public async Task EnsureAsync_Should_ReturnOriginal_On_Success()
        => (await Result<int>.Success(1).EnsureAsync(static (_, _) => ValueTask.FromResult(true), NameRequired, TestContext.Current.CancellationToken))
            .ShouldBe(Result<int>.Success(1));

    [Fact] // Tarol: EnsureAsync_ShouldKeepOriginalFailure
    public async Task EnsureAsync_Should_KeepOriginalFailure()
        => (await Result<int>.Failure(NotFound).EnsureAsync(static (_, _) => ValueTask.FromResult(false), NameRequired, TestContext.Current.CancellationToken))
            .ShouldBe(Result<int>.Failure(NotFound));

    [Fact]
    public void Equality_And_ToString()
    {
        Result<int>.Success(1).ShouldBe(Result<int>.Success(1));
        Result<int>.Success(1).ShouldNotBe(Result<int>.Success(2));
        (Result<int>.Failure(NotFound) == Result<int>.Failure(Error.NotFound("User.NotFound"))).ShouldBeTrue();
        (Result<int>.Success(1) != Result<int>.Failure(NotFound)).ShouldBeTrue();
        Result<int>.Success(1).GetHashCode().ShouldBe(Result<int>.Success(1).GetHashCode());
        Result<int>.Success(1).ToString().ShouldBe("Success(1)");
        Result<int>.Failure(NameRequired).ToString().ShouldBe("Failure(Invalid { Name: Name.Required })");
    }

    [Fact]
    public void Delegates_Should_BeRequired()
    {
        var r = Result<int>.Success(1);
        Should.Throw<ArgumentNullException>(() => r.Map<int>(null!));
        Should.Throw<ArgumentNullException>(() => r.Bind<int>(null!));
        Should.Throw<ArgumentNullException>(() => r.Ensure(null!, NotFound));
        Should.Throw<ArgumentNullException>(() => r.Ensure(static _ => true, null!));
        Should.Throw<ArgumentNullException>(() => new Result<int>((Violation)null!));
    }
}
