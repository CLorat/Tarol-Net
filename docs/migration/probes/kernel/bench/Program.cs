using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Time.Testing;

BenchmarkSwitcher.FromAssembly(typeof(ClockBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class ClockBenchmarks
{
    private static readonly AsyncLocal<TimeProvider?> Ambient = new();
    private static TimeProvider _static = TimeProvider.System;
    private TimeProvider _injected = TimeProvider.System;
    private readonly FakeTimeProvider _fake = new();
    private IDisposable? _scope;

    [GlobalSetup(Targets = [nameof(Ambient_Set_Fake)])]
    public void SetAmbient() => Ambient.Value = _fake;

    [GlobalCleanup]
    public void Clear() { Ambient.Value = null; _scope?.Dispose(); }

    [Benchmark(Baseline = true)] public DateTimeOffset DateTimeOffset_UtcNow() => DateTimeOffset.UtcNow;
    [Benchmark] public DateTimeOffset TimeProvider_System() => TimeProvider.System.GetUtcNow();
    [Benchmark] public DateTimeOffset Injected_System() => _injected.GetUtcNow();
    [Benchmark] public DateTimeOffset Static_Field_DomainClock() => _static.GetUtcNow();
    [Benchmark] public DateTimeOffset Ambient_Unset_FallsBackToSystem() => (Ambient.Value ?? TimeProvider.System).GetUtcNow();
    [Benchmark] public DateTimeOffset Ambient_Set_Fake() => (Ambient.Value ?? TimeProvider.System).GetUtcNow();
    [Benchmark] public DateTimeOffset Injected_Fake() => _fake.GetUtcNow();
    [Benchmark] public TimeProvider? AsyncLocal_Read_Only() => Ambient.Value;
    [Benchmark] public long AsyncLocal_SetAndReset_Scope() { var prev = Ambient.Value; Ambient.Value = _fake; Ambient.Value = prev; return 0; }
}
