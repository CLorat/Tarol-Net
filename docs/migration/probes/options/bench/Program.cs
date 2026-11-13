using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Probe;
using Probe.B;
using Tarol.Results;
BenchmarkSwitcher.FromAssembly(typeof(Bench).Assembly).Run(args);

[MemoryDiagnoser, ShortRunJob]
public class Bench
{
    private int _i = 2; private readonly Id<User> _id = new(Guid.NewGuid()); private readonly User _u = new("Ada");
    private static readonly Error NotFound = Error.NotFound("X.NotFound");
    [MethodImpl(MethodImplOptions.NoInlining)] int? AInt() => _i % 2 == 0 ? _i : null;
    [MethodImpl(MethodImplOptions.NoInlining)] User? AUser() => _i % 2 == 0 ? _u : null;
    [MethodImpl(MethodImplOptions.NoInlining)] Option<int> CInt() => _i % 2 == 0 ? _i : Option.None;
    [MethodImpl(MethodImplOptions.NoInlining)] Option<Id<User>> CId() => _i % 2 == 0 ? _id : Option.None;
    [MethodImpl(MethodImplOptions.NoInlining)] Option<User> CUser() => _i % 2 == 0 ? _u : Option.None;
    [MethodImpl(MethodImplOptions.NoInlining)] DOption<int> DInt() => _i % 2 == 0 ? _i : new None();
    [MethodImpl(MethodImplOptions.NoInlining)] DOption<Id<User>> DId() => _i % 2 == 0 ? _id : new None();
    [MethodImpl(MethodImplOptions.NoInlining)] Result<int> EInt() => _i % 2 == 0 ? _i : NotFound;

    [Benchmark(Baseline = true)] public int A_Int_Pattern() => AInt() is { } v ? v : 0;
    [Benchmark] public int B_Int_Or() => AInt().Or(0);
    [Benchmark] public int C_Int_Switch() => CInt() switch { int v => v, None => 0 };
    [Benchmark] public int D_Int_Switch() => DInt() switch { int v => v, None => 0 };
    [Benchmark] public int E_Int_Switch() => EInt() switch { int v => v, Violation => 0 };
    [Benchmark] public int C_Id_Switch() => CId() switch { Id<User> v => 1, None => 0 };
    [Benchmark] public int D_Id_Switch() => DId() switch { Id<User> v => 1, None => 0 };
    [Benchmark] public int A_User_Pattern() => AUser() is { } u ? u.Name.Length : 0;
    [Benchmark] public int C_User_Switch() => CUser() switch { User u => u.Name.Length, None => 0 };
    [Benchmark] public int A_Chain() { var a = AInt(); int? m = a is { } x ? x + 1 : null; int? b = m is { } y && y > 3 ? y : null; return b is { } z && z != 7 ? z : 0; }
    [Benchmark] public int B_Chain() => AInt().Map(static v => v + 1).Match(static v => v > 3 && v != 7 ? v : 0, static () => 0);
    [Benchmark] public int C_Chain() => CInt().Map(static v => v + 1).Bind(static v => v > 3 ? Option.Some(v) : Option.None).Filter(static v => v != 7).GetValueOrDefault(0);
    [Benchmark] public int C_Match_Capturing() { var k = _i; return CInt().Match(v => v + k, () => k); }
    [Benchmark] public int C_Switch_SameLogic() { var k = _i; return CInt() switch { int v => v + k, None => k }; }
    [Benchmark] public int C_ToResult() => CInt().ToResult(NotFound) switch { int v => v, Violation => 0 };
    [Benchmark] public int E_ToOption() => EInt().ToOption() switch { int v => v, None => 0 };
    [Benchmark] public int A_ToResult() => AInt().ToResult(NotFound) switch { int v => v, Violation => 0 };
}
