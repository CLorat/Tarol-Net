using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
long a = GC.GetAllocatedBytesForCurrentThread(); int s = 0;
for (int i = 0; i < 1000; i++) { R<int> r = new(i); s += r switch { int v => v, F f => -1 }; }
Console.WriteLine($"V1 explicit IUnion.Value + public T Value: switch ok, allocated {GC.GetAllocatedBytesForCurrentThread() - a} B, r.Value is int: {new R<int>(3).Value.GetType().Name}");
R<int> d = default; Console.WriteLine($"V1 default switch: {d switch { int v => "ok", F f => f.Code }}");
Console.WriteLine($"V1 is: {(new R<string>("x") is string str ? str : "-")}");
public sealed class F(string code) { public string Code => code; public static F Uninit { get; } = new("Uninit"); }
[Union]
public readonly struct R<T> : IUnion
{
    private static readonly F Ok = new("<ok>");
    private readonly T _v; private readonly F? _s;
    public R(T value) { _v = value; _s = Ok; }
    public R(F f) { _v = default!; _s = f; }
    object? IUnion.Value => ReferenceEquals(_s, Ok) ? _v : _s ?? F.Uninit;
    public T Value => ReferenceEquals(_s, Ok) ? _v : throw new InvalidOperationException();
    public bool HasValue => true;
    public bool TryGetValue([MaybeNullWhen(false)] out T value) { value = _v; return ReferenceEquals(_s, Ok); }
    public bool TryGetValue([NotNullWhen(true)] out F? value) { value = ReferenceEquals(_s, Ok) ? null : _s ?? F.Uninit; return value is not null; }
}
