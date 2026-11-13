using BenchmarkDotNet.Attributes;

namespace Tarol.Results.Benchmarks;

// Creation, Map/Bind chains, switch versus Match, async ValueTask pipelines; Tarol shape (Baseline_*) against Tarol-Net.
[MemoryDiagnoser]
[ShortRunJob]
public class ResultBenchmarks
{
    private readonly Error _notFound = Error.NotFound("User.NotFound");
    private readonly BaselineViolation _baselineNotFound = new() { Code = "User.NotFound", Type = BaselineViolationType.NotFound };
    private readonly Guid _id = Guid.NewGuid();
    private readonly int _n = 7;

    [Benchmark(Baseline = true)]
    public int Baseline_Create_Success()
    {
        BaselineResult<Guid> result = _id;
        return result.IsSuccess ? 1 : result.Violation.Code.Length;
    }

    [Benchmark]
    public int Create_Success_Switch()
    {
        Result<Guid> result = _id;
        return result switch { Guid => 1, Violation v => v.ToHttpStatusCode() };
    }

    [Benchmark]
    public int Baseline_Create_Failure_Match()
    {
        BaselineResult<Guid> result = _baselineNotFound;
        return result.Match(static _ => 1, static v => v.Code.Length);
    }

    [Benchmark]
    public int Create_Failure_Switch()
    {
        Result<Guid> result = _notFound;
        return result switch { Guid => 1, Invalid => 400, Error e => e.Code.Length };
    }

    [Benchmark]
    public int Create_Failure_Match()
    {
        Result<Guid> result = _notFound;
        return result.Match(static _ => 1, static v => v.ToHttpStatusCode());
    }

    [Benchmark]
    public int Baseline_Chain()
    {
        BaselineResult<int> result = _n;
        return result.Map(static v => v + 1).Bind(static _ => (BaselineResult<Guid>)Guid.Empty).Map(static id => id.GetHashCode()).Match(static v => v, static _ => 0);
    }

    [Benchmark]
    public int Chain()
    {
        Result<int> result = _n;
        return result.Map(static v => v + 1).Bind(static _ => (Result<Guid>)Guid.Empty).Map(static id => id.GetHashCode()).Match(static v => v, static _ => 0);
    }

    [Benchmark]
    public int Match_CapturingLambda()
    {
        Result<int> result = _n;
        var k = _n;
        return result.Match(v => v + k, _ => k);
    }

    [Benchmark]
    public int Switch_SameLogic()
    {
        Result<int> result = _n;
        var k = _n;
        return result switch { int v => v + k, Violation => k };
    }

    [Benchmark]
    public async ValueTask<int> Baseline_AsyncPipeline()
    {
        BaselineResult<int> result = _n;
        var loaded = await result.BindAsync(static _ => ValueTask.FromResult((BaselineResult<Guid>)Guid.Empty)).ConfigureAwait(false);
        var saved = await loaded.BindAsync(static _ => ValueTask.FromResult((BaselineResult<int>)1)).ConfigureAwait(false);
        return saved.IsSuccess ? saved.Value : 0;
    }

    [Benchmark]
    public async ValueTask<int> AsyncPipeline()
    {
        Result<int> result = _n;
        var loaded = await result.BindAsync(static (_, _) => ValueTask.FromResult((Result<Guid>)Guid.Empty)).ConfigureAwait(false);
        var saved = await loaded.BindAsync(static (_, _) => ValueTask.FromResult((Result<int>)1)).ConfigureAwait(false);
        return saved switch { int v => v, Violation => 0 };
    }

    [Benchmark]
    public async ValueTask<int> Baseline_AsyncYield()
    {
        BaselineResult<int> result = _n;
        var loaded = await result.BindAsync(static async _ =>
        {
            await Task.Yield();
            return (BaselineResult<Guid>)Guid.Empty;
        }).ConfigureAwait(false);
        return loaded.IsSuccess ? 1 : 0;
    }

    [Benchmark]
    public async ValueTask<int> AsyncYield()
    {
        Result<int> result = _n;
        var loaded = await result.BindAsync(static async (_, _) =>
        {
            await Task.Yield();
            return (Result<Guid>)Guid.Empty;
        }).ConfigureAwait(false);
        return loaded switch { Guid => 1, Violation => 0 };
    }
}
