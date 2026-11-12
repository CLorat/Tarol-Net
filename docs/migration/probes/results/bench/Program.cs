using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Probe;
BenchmarkRunner.Run<ResultBench>(args: args);

[MemoryDiagnoser, ShortRunJob]
public class ResultBench
{
    private readonly Id<User> _id = new(Guid.NewGuid());
    private static readonly Failure Fail = Failure.NotFound("User.NotFound");
    private static readonly Violation Viol = new() { Code = "User.NotFound", Type = ViolationType.NotFound };
    private int _n = 7;

    [Benchmark(Baseline = true)] public int A_Create_Switch() { AResult<Id<User>> r = _id; return r.IsSuccess ? 1 : r.Violation.Code.Length; }
    [Benchmark] public int B_Create_Switch() { BResult<Id<User>> r = _id; return r switch { Id<User> => 1, Failure f => f.Code.Length }; }
    [Benchmark] public int C_Create_Switch() { C2Result<Id<User>> r = _id; return r switch { Id<User> => 1, Failure f => f.Code.Length }; }
    [Benchmark] public int A_Fail_Match() { AResult<Id<User>> r = Viol; return r.Match(static _ => 1, static v => v.Code.Length); }
    [Benchmark] public int C_Fail_Switch() { C2Result<Id<User>> r = Fail; return r switch { Id<User> => 1, Failure f => f.Code.Length }; }
    [Benchmark] public int C_Fail_Match() { C2Result<Id<User>> r = Fail; return r.Match(static _ => 1, static f => f.Code.Length); }
    [Benchmark] public int A_Chain() { AResult<int> r = _n; return r.Map(static v => v + 1).Bind(static v => (AResult<Id<User>>)new Id<User>(Guid.Empty)).Map(static id => id.Value.GetHashCode()).Match(static v => v, static _ => 0); }
    [Benchmark] public int C_Chain() { C2Result<int> r = _n; return r.Map(static v => v + 1).Bind(static v => (C2Result<Id<User>>)new Id<User>(Guid.Empty)).Map(static id => id.Value.GetHashCode()).Match(static v => v, static _ => 0); }
    [Benchmark] public int C_Match_Capturing() { C2Result<int> r = _n; int k = _n; return r.Match(v => v + k, _ => k); }
    [Benchmark] public int C_Switch_Capturing() { C2Result<int> r = _n; int k = _n; return r switch { int v => v + k, Failure => k }; }
    [Benchmark] public async ValueTask<int> A_AsyncPipeline() { AResult<int> r = _n; var x = await r.BindAsync(static v => ValueTask.FromResult((AResult<Id<User>>)new Id<User>(Guid.Empty))); var y = await x.BindAsync(static v => ValueTask.FromResult((AResult<int>)1)); return y.IsSuccess ? y.Value : 0; }
    [Benchmark] public async ValueTask<int> C_AsyncPipeline() { C2Result<int> r = _n; var x = await r.BindAsync(static v => ValueTask.FromResult((C2Result<Id<User>>)new Id<User>(Guid.Empty))); var y = await x.BindAsync(static v => ValueTask.FromResult((C2Result<int>)1)); return y switch { int v => v, Failure => 0 }; }
    [Benchmark] public async ValueTask<int> A_AsyncYield() { AResult<int> r = _n; var x = await r.BindAsync(static async v => { await Task.Yield(); return (AResult<Id<User>>)new Id<User>(Guid.Empty); }); return x.IsSuccess ? 1 : 0; }
    [Benchmark] public async ValueTask<int> C_AsyncYield() { C2Result<int> r = _n; var x = await r.BindAsync(static async v => { await Task.Yield(); return (C2Result<Id<User>>)new Id<User>(Guid.Empty); }); return x switch { Id<User> => 1, Failure => 0 }; }
}
