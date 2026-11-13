using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Tarol.Options;

namespace Tarol.Results.Benchmarks;

// Option<T> (Tarol.Options) to and from Result<T> (docs/migration/options.md §3.4).
[MemoryDiagnoser]
[ShortRunJob]
public class OptionConversionBenchmarks
{
    private static readonly Error NotFound = Error.NotFound("X.NotFound");
    private readonly int _i = 2;

    [Benchmark]
    public int Option_ToResult() => OptionInt().ToResult(NotFound) switch { int v => v, Violation => 0 };

    [Benchmark]
    public int Result_ToOption() => ResultInt().ToOption() switch { int v => v, None => 0 };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Option<int> OptionInt() => _i % 2 == 0 ? _i : Option.None;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Result<int> ResultInt() => _i % 2 == 0 ? _i : NotFound;
}
