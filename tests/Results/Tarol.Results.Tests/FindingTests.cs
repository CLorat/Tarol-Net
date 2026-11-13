namespace Tarol.Results.Tests;

// One test per review finding fixed in Tarol-Net (docs/migration/results.md §2).
public class FindingTests
{
    [Fact] // R-1: default(Result) was a failure with Code == null
    public void R1_Default_Should_HaveACode()
    {
        default(Result).TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Error>().Code.ShouldBe("Result.Uninitialized");
        default(Result<int>).GetValueOrDefault(static v => ((Error)v).Code.Length).ShouldBe("Result.Uninitialized".Length);
    }

    [Fact] // R-2: Violation.None mapped to 400 and could become a failure
    public void R2_There_Should_BeNoNoneViolation()
    {
        typeof(Violation).GetProperty("None").ShouldBeNull();
        default(ErrorType).ToHttpStatusCode().ShouldBe(500);
    }

    [Fact] // R-3: a null success value was possible
    public void R3_NullSuccess_Should_Throw() => Should.Throw<ArgumentNullException>(static () => { Result<string> r = (string)null!; return r; });

    [Fact] // R-4: a failed result could not be serialized, and nothing round-tripped
    public void R4_Failure_Should_RoundTrip() => ResultJsonTests.RoundTrip(Result<int>.Failure(Error.NotFound("x"))).ShouldBe(Result<int>.Failure(Error.NotFound("x")));

    [Fact] // R-6: Combine(NotFound, Forbidden) was a 400 validation failure
    public void R6_Combine_Should_KeepTheErrorType()
    {
        var combined = Result.Combine(Error.NotFound("a"), Error.Forbidden());

        combined.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation!.ToHttpStatusCode().ShouldBe(404);
    }

    [Fact] // R-7: Combine<T1,T2> returned the first failure while Combine(params) aggregated
    public void R7_All_Combines_Should_FollowOneRule()
    {
        var a = Invalid.Field("A", "A.X");
        var b = Invalid.Field("B", "B.X");
        var expected = a.Combine(b);

        Result.Combine(a, b).ShouldBe(Result.Failure(expected));
        Result.Combine(Result<int>.Failure(a), Result<int>.Failure(b)).ShouldBe(Result<(int, int)>.Failure(expected));
        new[] { Result<int>.Failure(a), Result<int>.Failure(b) }.Sequence().ShouldBe(Result<IReadOnlyList<int>>.Failure(expected));
    }

    [Fact] // R-8: async combinators take a CancellationToken and use ValueTask only
    public async Task R8_Async_Should_FlowTheCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            _ = await Result<int>.Success(1).BindAsync(static async (v, ct) =>
            {
                await Task.Delay(1000, ct);
                return Result<int>.Success(v);
            }, cts.Token));

        typeof(Result<int>).GetMethods().Where(static m => m.Name.EndsWith("Async", StringComparison.Ordinal))
            .ShouldAllBe(static m => m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
    }

    [Fact] // R-9: no IResult interface
    public void R9_There_Should_BeNoIResult() => typeof(Result).Assembly.GetType("Tarol.Results.IResult").ShouldBeNull();

    [Fact] // R-10: NotFound<TEntity>(object id) boxed the id
    public void R10_NotFound_Should_NotBoxTheId()
    {
        var id = Guid.NewGuid();
        _ = Error.NotFound<FindingTests, Guid>(id);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Error.NotFound<FindingTests, Guid>(id);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        error.Detail.ShouldNotBeNull().ShouldContain(id.ToString());
        // two strings (code, detail) + the Error object; Tarol's version also boxed the Guid (304 B in total)
        allocated.ShouldBeLessThan(304);
    }

    [Fact] // R-11: no dependency on StronglyTypedId
    public void R11_Should_NotReferenceStronglyTypedId()
        => typeof(Result).Assembly.GetReferencedAssemblies().ShouldNotContain(static a => a.Name == "Tarol.StronglyTypedId");
}
