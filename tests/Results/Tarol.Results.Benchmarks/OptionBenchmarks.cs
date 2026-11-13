using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace Tarol.Results.Benchmarks;

// Option<T> against plain T? (docs/migration/options.md §3.4): create + check, chains, Match vs switch, Result conversions.
[MemoryDiagnoser]
[ShortRunJob]
public class OptionBenchmarks
{
    private static readonly Error NotFound = Error.NotFound("X.NotFound");
    private readonly int _i = 2;
    private readonly Guid _id = Guid.NewGuid();
    private readonly string _name = "Ada";

    [Benchmark(Baseline = true)]
    public int Nullable_Int_Pattern() => NullableInt() is { } v ? v : 0;

    [Benchmark]
    public int Option_Int_Switch() => OptionInt() switch { int v => v, None => 0 };

    [Benchmark]
    public int Option_Guid_Switch() => OptionGuid() switch { Guid => 1, None => 0 };

    [Benchmark]
    public int Nullable_String_Pattern() => NullableString() is { } s ? s.Length : 0;

    [Benchmark]
    public int Option_String_Switch() => OptionString() switch { string s => s.Length, None => 0 };

    [Benchmark]
    public int Option_Chain() => OptionInt().Map(static v => v + 1).Bind(static v => v > 3 ? Option.Some(v) : Option.None)
        .Filter(static v => v != 7).GetValueOrDefault(0);

    [Benchmark]
    public int Option_Match_Capturing()
    {
        var k = _i;
        return OptionInt().Match(v => v + k, () => k);
    }

    [Benchmark]
    public int Option_Switch_SameLogic()
    {
        var k = _i;
        return OptionInt() switch { int v => v + k, None => k };
    }

    [Benchmark]
    public int Option_ToResult() => OptionInt().ToResult(NotFound) switch { int v => v, Violation => 0 };

    [Benchmark]
    public int Result_ToOption() => ResultInt().ToOption() switch { int v => v, None => 0 };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int? NullableInt() => _i % 2 == 0 ? _i : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Option<int> OptionInt() => _i % 2 == 0 ? _i : Option.None;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Option<Guid> OptionGuid() => _i % 2 == 0 ? _id : Option.None;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? NullableString() => _i % 2 == 0 ? _name : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Option<string> OptionString() => _i % 2 == 0 ? _name : Option.None;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Result<int> ResultInt() => _i % 2 == 0 ? _i : NotFound;
}
